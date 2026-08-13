using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

/// <summary>
/// Stores only the user's explicit HoYo role choice. The payload is protected
/// for the current Windows user before it reaches disk; publisher cookies remain
/// solely in the isolated WebView profile.
/// </summary>
public sealed class PublisherRoleBindingStore
{
    private const int MaximumCiphertextBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string publisherProfilesRoot;
    private readonly string root;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IPublisherRoleBindingFileBoundary files;
    private readonly object mutationSync = new();
    private readonly string mutationMutexName;

    public PublisherRoleBindingStore(string publisherProfilesRoot)
        : this(publisherProfilesRoot, new WindowsCurrentUserRoleBindingProtector())
    {
    }

    internal PublisherRoleBindingStore(
        string publisherProfilesRoot,
        IPublisherRoleBindingProtector protector)
        : this(publisherProfilesRoot, protector, new SystemPublisherRoleBindingFileBoundary())
    {
    }

    internal PublisherRoleBindingStore(
        string publisherProfilesRoot,
        IPublisherRoleBindingProtector protector,
        IPublisherRoleBindingFileBoundary files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherProfilesRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.publisherProfilesRoot = Path.GetFullPath(publisherProfilesRoot);
        root = Path.GetFullPath(Path.Combine(this.publisherProfilesRoot, ".protected-role-bindings"));
        if (!IsContained(root))
            throw new ArgumentException("Protected role binding root escaped its configured root.", nameof(publisherProfilesRoot));
        mutationMutexName = "Local\\Pengo.Nyx.Desktop.RoleBindings."
            + Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(
                root.ToUpperInvariant())));
    }

    public PublisherRoleBinding? TryLoad(string gameId) => TryLoadRecord(gameId)?.Binding;

    public PublisherRoleRecord? TryLoadRecord(string gameId) =>
        SerializeMutation<PublisherRoleRecord?>(() => TryLoadRecordCore(gameId), null);

    private PublisherRoleRecord? TryLoadRecordCore(string gameId)
    {
        if (!IsSupportedGame(gameId)) return null;
        try
        {
            EnsureRoot();
            var path = BindingPath(gameId);
            if (!ValidateExistingComponents(path)) return null;
            if (!files.Exists(path)) return null;
            if ((files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = files.OpenRead(path);
            if (stream.Length is <= 0 or > MaximumCiphertextBytes) return null;
            var ciphertext = new byte[stream.Length];
            stream.ReadExactly(ciphertext);
            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(ciphertext);
                return ParseRecord(gameId, plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or InvalidDataException)
        {
            return null;
        }
    }

    public bool Save(string gameId, PublisherRoleBinding binding) => SerializeMutation(
        () => SaveCore(gameId, binding),
        false);

    private bool SaveCore(string gameId, PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsSupportedGame(gameId)
            || !PublisherAccountCatalog.IsValidRoleBinding(gameId, binding))
            return false;
        var existingRecord = TryLoadRecordCore(gameId);
        if (existingRecord is not null
            && existingRecord.Binding == binding)
            return true;

        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            EnsureRoot();
            var destination = BindingPath(gameId);
            if (files.Exists(destination)
                && (files.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                return false;

            plaintext = SerializeV1(gameId, binding);
            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes) return false;
            temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = files.CreateNewWriteThrough(temporary))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            files.MoveOverwrite(temporary, destination);
            temporary = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { files.Delete(temporary); } catch (Exception) { }
            }
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public bool SaveRecord(string gameId, PublisherRoleRecord record) => SerializeMutation(
        () => SaveRecordPublicCore(gameId, record),
        false);

    private bool SaveRecordPublicCore(string gameId, PublisherRoleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsSupportedGame(gameId)
            || !PublisherRoleRecordRules.IsValid(gameId, record))
            return false;
        return SaveRecordCore(gameId, record);
    }

    public bool Delete(string gameId) => SerializeMutation(
        () => DeleteCore(gameId),
        false);

    private bool DeleteCore(string gameId)
    {
        if (!IsSupportedGame(gameId)) return false;
        try
        {
            EnsureRoot();
            var path = BindingPath(gameId);
            if (!ValidateExistingComponents(path)) return false;
            if (!files.Exists(path)) return true;
            if ((files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            files.Delete(path);
            return !files.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool DeleteProvider(string provider)
    {
        if (!string.Equals(provider, "HoYoLAB", StringComparison.Ordinal))
            return string.Equals(provider, "SKPORT", StringComparison.Ordinal);
        var deleted = true;
        foreach (var gameId in new[] { "gi", "hsr", "zzz" })
            deleted &= Delete(gameId);
        return deleted;
    }

    private void EnsureRoot()
    {
        if (!ValidateExistingComponents(publisherProfilesRoot)
            || !ValidateExistingComponents(root))
            throw new IOException("Protected role binding path cannot contain a reparse point.");
        files.CreateDirectory(root);
        if (!ValidateExistingComponents(root)
            || (files.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Protected role binding root cannot be a reparse point.");
    }

    private string BindingPath(string gameId) => Path.Combine(root, gameId + ".bin");

    private static bool IsSupportedGame(string gameId) => gameId is "gi" or "hsr" or "zzz";

    private bool ValidateExistingComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsContained(fullPath)) return false;
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot)) return false;
        var current = volumeRoot;
        foreach (var component in fullPath[volumeRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!files.EntryExists(current)) continue;
            if ((files.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private bool IsContained(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, publisherProfilesRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                publisherProfilesRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private bool SaveRecordCore(string gameId, PublisherRoleRecord record)
    {
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            EnsureRoot();
            var destination = BindingPath(gameId);
            if (!ValidateExistingComponents(destination)) return false;
            if (!ValidateExistingComponents(destination)) return false;
            if (files.Exists(destination)
                && (files.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                return false;

            plaintext = SerializeV2(gameId, record);
            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes) return false;
            temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = files.CreateNewWriteThrough(temporary))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            files.MoveOverwrite(temporary, destination);
            temporary = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or EncoderFallbackException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { files.Delete(temporary); } catch (Exception) { }
            }
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] SerializeV1(string gameId, PublisherRoleBinding binding) =>
        StrictUtf8.GetBytes($"1\n{gameId}\n{binding.RoleId}\n{binding.Server}");

    private static byte[] SerializeV2(string gameId, PublisherRoleRecord record) =>
        StrictUtf8.GetBytes(string.Join(
            '\n',
            "2",
            gameId,
            record.Binding.RoleId,
            record.Binding.Server,
            record.Nickname ?? string.Empty,
            record.ReadableRegion));

    private static PublisherRoleRecord? ParseRecord(string expectedGameId, byte[] plaintext)
    {
        if (plaintext.Length is <= 0 or > 512) return null;
        string[] fields;
        try
        {
            fields = StrictUtf8.GetString(plaintext).Split('\n');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        if (fields.Length is not (4 or 6)
            || fields[0] is not ("1" or "2")
            || (fields[0] == "1" && fields.Length != 4)
            || (fields[0] == "2" && fields.Length != 6)
            || !string.Equals(fields[1], expectedGameId, StringComparison.Ordinal))
            return null;
        var binding = new PublisherRoleBinding(fields[2], fields[3]);
        if (!PublisherAccountCatalog.IsValidRoleBinding(expectedGameId, binding)) return null;
        var record = fields[0] == "1"
            ? new PublisherRoleRecord(
                binding,
                null,
                PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server))
            : new PublisherRoleRecord(
                binding,
                fields[4].Length == 0 ? null : fields[4],
                fields[5]);
        return PublisherRoleRecordRules.IsValid(expectedGameId, record) ? record : null;
    }

    private T SerializeMutation<T>(Func<T> mutation, T failure)
    {
        lock (mutationSync)
        {
            using var mutex = new Mutex(initiallyOwned: false, mutationMutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                return acquired ? mutation() : failure;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or WaitHandleCannotBeOpenedException)
            {
                return failure;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
                }
            }
        }
    }
}

internal interface IPublisherRoleBindingProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

internal interface IPublisherRoleBindingFileBoundary
{
    void CreateDirectory(string path);
    bool EntryExists(string path);
    bool Exists(string path);
    FileAttributes GetAttributes(string path);
    FileStream OpenRead(string path);
    FileStream CreateNewWriteThrough(string path);
    void MoveOverwrite(string source, string destination);
    void Delete(string path);
}

internal sealed class SystemPublisherRoleBindingFileBoundary : IPublisherRoleBindingFileBoundary
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool Exists(string path) => File.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        4096,
        FileOptions.SequentialScan);

    public FileStream CreateNewWriteThrough(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        4096,
        FileOptions.WriteThrough);

    public void MoveOverwrite(string source, string destination) =>
        File.Move(source, destination, overwrite: true);

    public void Delete(string path) => File.Delete(path);
}

internal sealed class WindowsCurrentUserRoleBindingProtector : IPublisherRoleBindingProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows user protection is required.");

        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            DataBlob outputBlob;
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Pointer, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Pointer != IntPtr.Zero)
                {
                    ZeroMemory(outputBlob.Pointer, (nuint)Math.Max(0, outputBlob.Length));
                    LocalFree(outputBlob.Pointer);
                }
            }
        }
        finally
        {
            ZeroMemory(inputPointer, (nuint)input.Length);
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Pointer;

        public DataBlob(int length, IntPtr pointer)
        {
            Length = length;
            Pointer = pointer;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    private static extern void ZeroMemory(IntPtr destination, nuint length);
}
