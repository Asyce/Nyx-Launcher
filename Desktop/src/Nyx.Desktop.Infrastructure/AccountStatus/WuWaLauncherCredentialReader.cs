using System.Security.Cryptography;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

internal sealed class WuWaLauncherCredential(string oauthCode)
{
    public string OAuthCode { get; } = oauthCode;

    public override string ToString() => nameof(WuWaLauncherCredential);
}

internal sealed record WuWaCredentialReadResult(
    WuWaLauncherCredential? Credential,
    WuWaAccountStatusFailure Failure);

internal sealed class WuWaLauncherCredentialReader
{
    internal const int MaximumCacheBytes = 256 * 1024;
    private const int MaximumCredentialCharacters = 4096;
    private readonly string cachePath;
    private readonly string lastLoginCachePath;

    public WuWaLauncherCredentialReader()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    internal WuWaLauncherCredentialReader(string roamingAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);
        var appCacheDirectory = Path.GetFullPath(Path.Combine(
            roamingAppData,
            "KR_G153",
            "A1730"));
        cachePath = Path.Combine(appCacheDirectory, "KRSDKUserLauncherCache.json");
        lastLoginCachePath = Path.Combine(appCacheDirectory, "KRSDKUserCache.json");
    }

    public async Task<WuWaCredentialReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(cachePath))
        {
            return new(null, WuWaAccountStatusFailure.CacheNotFound);
        }

        var accounts = new List<CachedAccount>();
        var sawMalformed = false;
        byte[]? bytes = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(cachePath);
            if (info.Length is <= 0 or > MaximumCacheBytes
                || IsReparsePoint(cachePath)
                || IsReparsePoint(Path.GetDirectoryName(cachePath)!)
                || IsReparsePoint(Path.GetDirectoryName(Path.GetDirectoryName(cachePath)!)!))
            {
                return new(null, WuWaAccountStatusFailure.CacheMalformed);
            }

            bytes = new byte[MaximumCacheBytes + 1];
            var length = 0;
            await using (var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
            }
            if (length is <= 0 or > MaximumCacheBytes)
                return new(null, WuWaAccountStatusFailure.CacheMalformed);
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            CollectAccounts(document.RootElement, accounts, depth: 0);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            sawMalformed = true;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }

        var distinct = accounts
            .DistinctBy(static account => (account.Cuid, account.ObfuscatedOAuthCode))
            .ToArray();
        if (distinct.Length == 0)
        {
            return new(null, sawMalformed
                ? WuWaAccountStatusFailure.CacheMalformed
                : WuWaAccountStatusFailure.CacheNotFound);
        }

        var lastLogin = await ReadLastLoginCuidAsync(cancellationToken).ConfigureAwait(false);
        if (lastLogin.IsMalformed)
            return new(null, WuWaAccountStatusFailure.CacheMalformed);

        CachedAccount? chosen;
        if (lastLogin.Cuid is not null)
        {
            var matches = distinct
                .Where(account => string.Equals(account.Cuid, lastLogin.Cuid, StringComparison.Ordinal))
                .ToArray();
            chosen = matches.Length == 1 ? matches[0] : null;
        }
        else
        {
            var selected = distinct.Where(static account => account.IsSelected).ToArray();
            chosen = selected.Length == 1
                ? selected[0]
                : selected.Length == 0 && distinct.Length == 1
                    ? distinct[0]
                    : null;
        }
        if (chosen is null)
        {
            return new(null, WuWaAccountStatusFailure.MultipleAccounts);
        }

        var decoded = DecodeOAuthCode(chosen.ObfuscatedOAuthCode);
        return string.IsNullOrWhiteSpace(decoded)
            ? new(null, WuWaAccountStatusFailure.CacheMalformed)
            : new(new WuWaLauncherCredential(decoded), WuWaAccountStatusFailure.None);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private async Task<LastLoginReadResult> ReadLastLoginCuidAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(lastLoginCachePath)) return new(null, false);

        byte[]? bytes = null;
        try
        {
            var info = new FileInfo(lastLoginCachePath);
            if (info.Length is <= 0 or > MaximumCacheBytes
                || IsReparsePoint(lastLoginCachePath)
                || IsReparsePoint(Path.GetDirectoryName(lastLoginCachePath)!)
                || IsReparsePoint(Path.GetDirectoryName(Path.GetDirectoryName(lastLoginCachePath)!)!))
            {
                return new(null, true);
            }

            bytes = new byte[MaximumCacheBytes + 1];
            var length = 0;
            await using (var stream = new FileStream(
                lastLoginCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
            }
            if (length is <= 0 or > MaximumCacheBytes) return new(null, true);

            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var values = new List<string>();
            CollectLastLoginCuids(document.RootElement, values, depth: 0);
            var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
            return distinct.Length switch
            {
                0 => new(null, false),
                1 => new(distinct[0], false),
                _ => new(null, true),
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(null, true);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string DecodeOAuthCode(string obfuscated)
    {
        ArgumentNullException.ThrowIfNull(obfuscated);
        return string.Create(obfuscated.Length, obfuscated, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = (char)(source[index] ^ 5);
            }
        });
    }

    private static void CollectAccounts(JsonElement element, List<CachedAccount> accounts, int depth)
    {
        if (depth > 24) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadAccount(element, out var account)) accounts.Add(account!);
                foreach (var property in element.EnumerateObject())
                {
                    CollectAccounts(property.Value, accounts, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAccounts(item, accounts, depth + 1);
                }
                break;
        }
    }

    private static void CollectLastLoginCuids(JsonElement element, List<string> values, int depth)
    {
        if (depth > 24) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetBoundedString(element, "last_login_cuid", 256, out var cuid)) values.Add(cuid!);
                foreach (var property in element.EnumerateObject())
                {
                    CollectLastLoginCuids(property.Value, values, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectLastLoginCuids(item, values, depth + 1);
                }
                break;
        }
    }

    private static bool TryReadAccount(JsonElement element, out CachedAccount? account)
    {
        account = null;
        if (!TryGetBoundedString(element, "cuid", 256, out var cuid)
            || !TryGetBoundedString(element, "oauthCode", MaximumCredentialCharacters, out var oauthCode))
        {
            return false;
        }

        var selected = IsTrue(element, "selected")
            || IsTrue(element, "isSelected")
            || IsTrue(element, "current")
            || IsTrue(element, "isCurrent")
            || IsTrue(element, "login");
        account = new(cuid!, oauthCode!, selected);
        return true;
    }

    private static bool TryGetBoundedString(
        JsonElement element,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > maximumLength
            || candidate.Any(char.IsControl))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && (property.ValueKind is JsonValueKind.True
            || property.ValueKind is JsonValueKind.Number && property.TryGetInt32(out var number) && number == 1
            || property.ValueKind is JsonValueKind.String
                && property.GetString() is "1" or "true" or "True");

    private sealed record CachedAccount(string Cuid, string ObfuscatedOAuthCode, bool IsSelected);

    private sealed record LastLoginReadResult(string? Cuid, bool IsMalformed);
}
