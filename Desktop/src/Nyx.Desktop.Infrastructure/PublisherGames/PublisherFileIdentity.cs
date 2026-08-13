using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal static class PublisherFileIdentity
{
    private const long MaximumComparableExecutableBytes = 256L * 1024 * 1024;

    internal static bool FixedTimeEquals(byte[] left, byte[] right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    internal static byte[] GetSha256(FileStream stream)
        => GetHash(stream, HashAlgorithmName.SHA256);

    internal static byte[] GetMd5(FileStream stream)
        => GetHash(stream, HashAlgorithmName.MD5);

    private static byte[] GetHash(FileStream stream, HashAlgorithmName algorithm)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static bool IsComparableLength(long length) =>
        length > 0 && length <= MaximumComparableExecutableBytes;
}

internal readonly record struct PublisherNtfsFileIdentity(
    uint VolumeSerialNumber,
    ulong FileId,
    uint NumberOfLinks);

internal interface IPublisherFileIdentityReader
{
    PublisherNtfsFileIdentity Read(SafeFileHandle handle);
}

internal interface IPublisherExecutableEntryOpener
{
    SafeFileHandle Open(string path);
}

internal sealed class WindowsPublisherExecutableEntryOpener : IPublisherExecutableEntryOpener
{
    public SafeFileHandle Open(string path) => PublisherPathIdentity.OpenNonReparseEntry(path);
}

internal sealed class WindowsPublisherFileIdentityReader : IPublisherFileIdentityReader
{
    public PublisherNtfsFileIdentity Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "Unable to bind publisher evidence to its NTFS file identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks);
    }
}

internal static class PublisherPathIdentity
{
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public static void EnsurePathMatches(
        string path,
        PublisherNtfsFileIdentity expectedIdentity,
        IPublisherFileIdentityReader identityReader)
    {
        using var handle = OpenNonReparseEntry(path);
        var actualIdentity = identityReader.Read(handle);
        if (actualIdentity != expectedIdentity)
        {
            throw new IOException("Executable path no longer names the protected NTFS file.");
        }
    }

    public static SafeFileHandle OpenNonReparseEntry(string path)
    {
        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                "Unable to open protected executable evidence.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            handle.Dispose();
            throw new IOException(
                "Unable to inspect protected executable evidence.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new PublisherReparsePointException();
        }

        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0)
        {
            handle.Dispose();
            throw new IOException("Executable evidence resolved to a directory.");
        }

        return handle;
    }
}

internal sealed class PublisherReparsePointException()
    : IOException("Executable evidence became a reparse point during inspection.");

internal sealed class PublisherAncestorDirectoryBinding : IDisposable
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly List<SafeFileHandle> handles;
    private bool disposed;

    private PublisherAncestorDirectoryBinding(List<SafeFileHandle> handles)
    {
        this.handles = handles;
    }

    public static PublisherAncestorDirectoryBinding Open(string bindingRoot, string filePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bindingRoot));
        var parent = Path.GetDirectoryName(Path.GetFullPath(filePath))
            ?? throw new IOException("Executable has no protected parent directory.");
        var relative = Path.GetRelativePath(root, parent);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Executable escaped its protected install root.");
        }

        var paths = new List<string> { root };
        if (relative != ".")
        {
            var current = root;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                paths.Add(current);
            }
        }

        var handles = new List<SafeFileHandle>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                var handle = NativeMethods.CreateFileW(
                    path,
                    desiredAccess: 0,
                    FileShare.Read,
                    IntPtr.Zero,
                    FileMode.Open,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException(
                        "Unable to protect an executable ancestor directory.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (!NativeMethods.GetFileInformationByHandle(handle, out var information)
                    || (information.FileAttributes & (uint)FileAttributes.Directory) == 0
                    || (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    handle.Dispose();
                    throw new IOException("Executable ancestor identity is unsafe.");
                }

                handles.Add(handle);
            }

            return new(handles);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

/// <summary>
/// Keeps one executable and each in-root ancestor directory open without write/delete
/// sharing while its hash, signature, signer and PE metadata are observed. Every path
/// reopen must match the protected handle's NTFS volume serial and file ID. This binds
/// every observation to one immutable file instead of trusting path-only opens.
/// </summary>
internal sealed class ProtectedPublisherExecutableObservation : IDisposable
{
    private readonly FileStream stream;
    private readonly PublisherAncestorDirectoryBinding ancestorBinding;
    private readonly IPublisherFileIdentityReader identityReader;
    private bool disposed;

    private ProtectedPublisherExecutableObservation(
        string path,
        FileStream stream,
        PublisherAncestorDirectoryBinding ancestorBinding,
        IPublisherFileIdentityReader identityReader,
        PublisherNtfsFileIdentity fileIdentity,
        PublisherExecutableMetadata metadata,
        PublisherFileSnapshot snapshot,
        byte[] digest,
        byte[] md5Digest)
    {
        Path = path;
        this.stream = stream;
        this.ancestorBinding = ancestorBinding;
        this.identityReader = identityReader;
        FileIdentity = fileIdentity;
        Metadata = metadata;
        Snapshot = snapshot;
        Digest = digest;
        Md5Digest = md5Digest;
    }

    public string Path { get; }

    public PublisherExecutableMetadata Metadata { get; }

    public PublisherNtfsFileIdentity FileIdentity { get; }

    public PublisherFileSnapshot Snapshot { get; }

    public byte[] Digest { get; }

    public byte[] Md5Digest { get; }

    public static ProtectedPublisherExecutableObservation Open(
        string path,
        string bindingRoot,
        IPublisherExecutableMetadataReader metadataReader,
        IPublisherFileIdentityReader identityReader,
        IPublisherExecutableEntryOpener entryOpener)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(identityReader);
        ArgumentNullException.ThrowIfNull(entryOpener);
        var ancestorBinding = PublisherAncestorDirectoryBinding.Open(bindingRoot, path);
        SafeFileHandle? entryHandle = null;
        FileStream? stream = null;
        try
        {
            entryHandle = entryOpener.Open(path);
            stream = new FileStream(
                entryHandle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            entryHandle = null;
            var fileIdentity = identityReader.Read(stream.SafeFileHandle);
            if (fileIdentity.NumberOfLinks != 1)
            {
                throw new IOException("Hard-linked executable evidence is not accepted.");
            }

            PublisherPathIdentity.EnsurePathMatches(path, fileIdentity, identityReader);
            var before = PublisherFileSnapshot.Capture(path);
            if (!PublisherFileIdentity.IsComparableLength(stream.Length)
                || before.Length != stream.Length)
            {
                throw new IOException("Executable is outside the bounded proof size.");
            }

            var digest = PublisherFileIdentity.GetSha256(stream);
            var md5Digest = PublisherFileIdentity.GetMd5(stream);
            var metadata = metadataReader.Read(path, fileIdentity, identityReader);
            PublisherPathIdentity.EnsurePathMatches(path, fileIdentity, identityReader);
            var digestAfterMetadata = PublisherFileIdentity.GetSha256(stream);
            var after = PublisherFileSnapshot.Capture(path);
            if (before != after
                || after.Length != stream.Length
                || !PublisherFileIdentity.FixedTimeEquals(digest, digestAfterMetadata))
            {
                throw new IOException("Executable changed while its identity was read.");
            }

            return new(
                path,
                stream,
                ancestorBinding,
                identityReader,
                fileIdentity,
                metadata,
                after,
                digest,
                md5Digest);
        }
        catch
        {
            stream?.Dispose();
            entryHandle?.Dispose();
            ancestorBinding.Dispose();
            throw;
        }
    }

    public bool RemainsBound(IPublisherExecutableMetadataReader metadataReader)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var before = PublisherFileIdentity.GetSha256(stream);
        PublisherPathIdentity.EnsurePathMatches(Path, FileIdentity, identityReader);
        var currentMetadata = metadataReader.Read(Path, FileIdentity, identityReader);
        PublisherPathIdentity.EnsurePathMatches(Path, FileIdentity, identityReader);
        var after = PublisherFileIdentity.GetSha256(stream);
        return PublisherFileSnapshot.Capture(Path) == Snapshot
            && stream.Length == Snapshot.Length
            && currentMetadata == Metadata
            && PublisherFileIdentity.FixedTimeEquals(Digest, before)
            && PublisherFileIdentity.FixedTimeEquals(before, after);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stream.Dispose();
        ancestorBinding.Dispose();
    }
}
