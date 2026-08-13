namespace Nyx.Desktop.Core.AccountStatus;

public enum PublisherProfileCleanupScope
{
    PasswordsOnly,
    FullProfile,
}

public sealed record PublisherPasswordStorageSnapshot(
    bool PasswordSavingEnabled,
    PublisherProfileCleanupScope? PendingCleanup)
{
    public bool CanOpenPublisherPage =>
        PendingCleanup is not PublisherProfileCleanupScope.FullProfile
        && (PasswordSavingEnabled || PendingCleanup is null);
}

/// <summary>
/// Tracks password-saving consent separately from publisher session cookies.
/// The policy contains no credential values and performs no profile I/O.
/// </summary>
public sealed class PublisherPasswordStoragePolicy
{
    private readonly object sync = new();
    private PublisherPasswordStorageSnapshot snapshot;

    public PublisherPasswordStoragePolicy(
        bool passwordSavingEnabled = false,
        bool profileExists = false)
    {
        snapshot = new(
            passwordSavingEnabled,
            !passwordSavingEnabled && profileExists
                ? PublisherProfileCleanupScope.PasswordsOnly
                : null);
    }

    public PublisherPasswordStorageSnapshot Snapshot
    {
        get
        {
            lock (sync) return snapshot;
        }
    }

    public PublisherPasswordStorageSnapshot ApplyPreference(
        bool enabled,
        bool profileExists)
    {
        lock (sync)
        {
            if (snapshot.PendingCleanup is PublisherProfileCleanupScope.FullProfile)
            {
                snapshot = snapshot with { PasswordSavingEnabled = enabled };
                return snapshot;
            }

            snapshot = new(
                enabled,
                !enabled && profileExists
                    ? PublisherProfileCleanupScope.PasswordsOnly
                    : null);
            return snapshot;
        }
    }

    public PublisherPasswordStorageSnapshot RequireFullProfileCleanup()
    {
        lock (sync)
        {
            snapshot = snapshot with
            {
                PendingCleanup = PublisherProfileCleanupScope.FullProfile,
            };
            return snapshot;
        }
    }

    public PublisherPasswordStorageSnapshot CompleteCleanup(
        PublisherProfileCleanupScope scope,
        bool succeeded)
    {
        lock (sync)
        {
            if (!succeeded)
            {
                if (snapshot.PendingCleanup is not PublisherProfileCleanupScope.FullProfile)
                {
                    snapshot = snapshot with { PendingCleanup = scope };
                }
                return snapshot;
            }

            if (scope is PublisherProfileCleanupScope.FullProfile
                || snapshot.PendingCleanup == scope)
            {
                snapshot = snapshot with { PendingCleanup = null };
            }
            return snapshot;
        }
    }
}
