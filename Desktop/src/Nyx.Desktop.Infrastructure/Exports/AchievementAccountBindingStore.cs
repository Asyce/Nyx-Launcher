using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Infrastructure.Exports;

public sealed class AchievementAccountBindingStore
{
    private const int SecretLength = 32;
    private const int MaximumCiphertextBytes = 4096;
    private readonly object sync = new();
    private readonly string root;
    private readonly string keyPath;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly Func<byte[]> createSecret;

    public AchievementAccountBindingStore(string root)
        : this(
            root,
            new WindowsCurrentUserRoleBindingProtector(),
            () => RandomNumberGenerator.GetBytes(SecretLength))
    {
    }

    internal AchievementAccountBindingStore(
        string root,
        IPublisherRoleBindingProtector protector,
        Func<byte[]> createSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        keyPath = Path.Combine(this.root, "achievement-account-binding-key.bin");
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.createSecret = createSecret ?? throw new ArgumentNullException(nameof(createSecret));
    }

    public AchievementAccountBinding Derive(string gameId, PublisherRoleBinding role)
    {
        if (gameId is not ("gi" or "hsr" or "zzz" or "wuwa" or "ae"))
            throw new ExportProviderException("achievement-binding-unavailable");
        ArgumentNullException.ThrowIfNull(role);
        if (gameId is "gi" or "hsr" or "zzz"
            && !PublisherAccountCatalog.IsValidRoleBinding(gameId, role))
            throw new ExportProviderException("achievement-binding-unavailable");
        if (string.IsNullOrWhiteSpace(role.RoleId)
            || role.RoleId.Length > 64
            || string.IsNullOrWhiteSpace(role.Server)
            || role.Server.Length > 64)
            throw new ExportProviderException("achievement-binding-unavailable");

        byte[] secret;
        lock (sync)
        {
            secret = LoadOrCreateSecret();
        }
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
            Append(hmac, AchievementAccountBinding.CurrentScheme);
            Append(hmac, gameId);
            Append(hmac, role.Server);
            Append(hmac, role.RoleId);
            var digest = hmac.GetHashAndReset();
            try
            {
                var value = Convert.ToBase64String(digest)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                return new(AchievementAccountBinding.CurrentScheme, value, role.Server);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private byte[] LoadOrCreateSecret()
    {
        EnsureRoot();
        if (File.Exists(keyPath)) return LoadSecret();

        var secret = createSecret();
        if (secret.Length != SecretLength)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new ExportProviderException("achievement-binding-unavailable");
        }
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            ciphertext = protector.Protect(secret);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes)
                throw new ExportProviderException("achievement-binding-unavailable");
            temporary = keyPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, keyPath, overwrite: false);
                temporary = null;
                return secret.ToArray();
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                return LoadSecret();
            }
        }
        catch (ExportProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            throw new ExportProviderException("achievement-binding-unavailable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private byte[] LoadSecret()
    {
        if ((File.GetAttributes(keyPath) & FileAttributes.ReparsePoint) != 0)
            throw new ExportProviderException("achievement-binding-unavailable");
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            using var stream = new FileStream(
                keyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumCiphertextBytes)
                throw new ExportProviderException("achievement-binding-unavailable");
            ciphertext = new byte[stream.Length];
            stream.ReadExactly(ciphertext);
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length != SecretLength)
                throw new ExportProviderException("achievement-binding-unavailable");
            return plaintext.ToArray();
        }
        catch (ExportProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            throw new ExportProviderException("achievement-binding-unavailable");
        }
        finally
        {
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ExportProviderException("achievement-binding-unavailable");
    }

    private static void Append(IncrementalHash hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        try
        {
            hmac.AppendData(length);
            hmac.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(length);
        }
    }
}
