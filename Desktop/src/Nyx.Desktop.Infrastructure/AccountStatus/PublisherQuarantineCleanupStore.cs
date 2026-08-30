namespace Nyx.Desktop.Infrastructure.AccountStatus;

public static class PublisherQuarantineCleanupStore
{
    public static bool TryClean(
        string provider,
        PublisherConsentRevocationStore revocations,
        PublisherRoleBindingStore roleBindings,
        PublisherResourceSnapshotStore resourceSnapshots,
        Func<string, bool, bool>? persistCleanupPending = null,
        Func<bool>? deleteHoyoGameBundle = null)
    {
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(roleBindings);
        ArgumentNullException.ThrowIfNull(resourceSnapshots);

        // Both channels are write-ahead. When the launcher-state callback is
        // supplied, cleanup is complete only after that independent pending bit
        // and the profile-root marker were durably recorded. A later process
        // can therefore suppress consent and cache restoration if either marker
        // filename was temporarily unusable.
        var pendingBitRecorded = persistCleanupPending is null
            || persistCleanupPending(provider, true);
        var revocationRecorded = revocations.MarkPending(provider);
        var roleBindingsCleared = roleBindings.DeleteProvider(provider);
        var resourceSnapshotsCleared = resourceSnapshots.DeleteProvider(provider);
        var gameBundleCleared = provider != "HoYoLAB"
            || deleteHoyoGameBundle?.Invoke() == true;
        if (!pendingBitRecorded
            || !revocationRecorded
            || !roleBindingsCleared
            || !resourceSnapshotsCleared
            || !gameBundleCleared)
            return false;

        if (!revocations.ClearCleanupPending(provider))
            return false;

        // Clearing the independent bit is last. A failed clear is conservative:
        // startup retries cleanup while account access remains disabled.
        return persistCleanupPending is null
            || persistCleanupPending(provider, false);
    }
}
