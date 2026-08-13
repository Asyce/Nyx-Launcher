using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

/// <summary>
/// Persists only a bounded display snapshot together with its exact protected
/// role binding. Publisher credentials remain solely in the isolated WebView
/// profile.
/// </summary>
public sealed class PublisherResourceSnapshotStore
{
    private const int MaximumCiphertextBytes = 16 * 1024;
    private readonly string publisherProfilesRoot;
    private readonly string root;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IPublisherRoleBindingFileBoundary files;

    public PublisherResourceSnapshotStore(string publisherProfilesRoot)
        : this(publisherProfilesRoot, new WindowsCurrentUserRoleBindingProtector())
    {
    }

    internal PublisherResourceSnapshotStore(
        string publisherProfilesRoot,
        IPublisherRoleBindingProtector protector)
        : this(publisherProfilesRoot, protector, new SystemPublisherRoleBindingFileBoundary())
    {
    }

    internal PublisherResourceSnapshotStore(
        string publisherProfilesRoot,
        IPublisherRoleBindingProtector protector,
        IPublisherRoleBindingFileBoundary files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherProfilesRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.publisherProfilesRoot = Path.GetFullPath(publisherProfilesRoot);
        root = Path.GetFullPath(Path.Combine(
            this.publisherProfilesRoot,
            ".protected-resource-snapshots"));
        if (!IsContained(root))
            throw new ArgumentException("Protected resource snapshot root escaped its configured root.", nameof(publisherProfilesRoot));
    }

    public PublisherResourceSnapshot? TryLoad(
        string gameId,
        PublisherRoleBinding expectedBinding)
    {
        if (!IsSupportedGame(gameId)
            || !PublisherAccountCatalog.IsValidRoleBinding(gameId, expectedBinding))
            return null;
        try
        {
            EnsureRoot();
            var path = SnapshotPath(gameId);
            if (!ValidateExistingComponents(path)) return null;
            if (!files.Exists(path)
                || (files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return null;
            using var stream = files.OpenRead(path);
            if (stream.Length is <= 0 or > MaximumCiphertextBytes) return null;
            var ciphertext = new byte[stream.Length];
            stream.ReadExactly(ciphertext);
            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(ciphertext);
                return Parse(gameId, expectedBinding, plaintext);
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

    public bool Save(PublisherResourceSnapshot snapshot, PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsValid(snapshot, binding)) return false;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            EnsureRoot();
            var destination = SnapshotPath(snapshot.GameId);
            if (!ValidateExistingComponents(destination)) return false;
            if (files.Exists(destination)
                && (files.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                return false;
            plaintext = Serialize(snapshot, binding);
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

    public bool Delete(string gameId)
    {
        if (!IsSupportedGame(gameId)) return false;
        try
        {
            EnsureRoot();
            var path = SnapshotPath(gameId);
            if (!ValidateExistingComponents(path)) return false;
            if (!files.Exists(path)) return true;
            if ((files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
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
            throw new IOException("Protected resource snapshot path cannot contain a reparse point.");
        files.CreateDirectory(root);
        if (!ValidateExistingComponents(root)
            || (files.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Protected resource snapshot root cannot be a reparse point.");
    }

    private string SnapshotPath(string gameId) => Path.Combine(root, gameId + ".bin");

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
            try
            {
                if (!files.EntryExists(current)) continue;
                var attributes = files.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (IOException)
            {
                return false;
            }
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

    private static bool IsValid(
        PublisherResourceSnapshot snapshot,
        PublisherRoleBinding binding) =>
        IsSupportedGame(snapshot.GameId)
        && PublisherAccountCatalog.IsValidRoleBinding(snapshot.GameId, binding)
        && snapshot.ResourceName is { Length: > 0 and <= 64 }
        && snapshot.Current >= 0
        && snapshot.Maximum > 0
        && snapshot.Current <= snapshot.Maximum
        && snapshot.RecoverySeconds is >= 0 and <= 14 * 24 * 60 * 60
        && snapshot.Reserve is null or >= 0;

    private static byte[] Serialize(
        PublisherResourceSnapshot snapshot,
        PublisherRoleBinding binding) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\n',
            "1",
            snapshot.GameId,
            binding.RoleId,
            binding.Server,
            snapshot.ResourceName,
            snapshot.Current,
            snapshot.Maximum,
            snapshot.ObservedAt.ToUnixTimeSeconds(),
            snapshot.RecoverySeconds,
            snapshot.Reserve?.ToString() ?? string.Empty));

    private static PublisherResourceSnapshot? Parse(
        string expectedGameId,
        PublisherRoleBinding expectedBinding,
        byte[] plaintext)
    {
        if (plaintext.Length is <= 0 or > 1024) return null;
        var fields = Encoding.UTF8.GetString(plaintext).Split('\n');
        if (fields.Length != 10
            || fields[0] != "1"
            || !string.Equals(fields[1], expectedGameId, StringComparison.Ordinal)
            || !string.Equals(fields[2], expectedBinding.RoleId, StringComparison.Ordinal)
            || !string.Equals(fields[3], expectedBinding.Server, StringComparison.Ordinal)
            || fields[4].Length is <= 0 or > 64
            || !int.TryParse(fields[5], out var current)
            || !int.TryParse(fields[6], out var maximum)
            || !long.TryParse(fields[7], out var observedAt)
            || !int.TryParse(fields[8], out var recovery)
            || (fields[9].Length > 0 && !int.TryParse(fields[9], out _)))
            return null;
        int? reserve = fields[9].Length == 0 ? null : int.Parse(fields[9]);
        DateTimeOffset observed;
        try
        {
            observed = DateTimeOffset.FromUnixTimeSeconds(observedAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        var snapshot = new PublisherResourceSnapshot(
            expectedGameId,
            fields[4],
            current,
            maximum,
            observed,
            IsStale: true,
            RecoverySeconds: recovery,
            Reserve: reserve);
        return IsValid(snapshot, expectedBinding) ? snapshot : null;
    }
}
