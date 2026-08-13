namespace Nyx.Desktop.Core.AccountStatus;

public enum PublisherProtectedStateAuthority
{
    LoginRequired,
    NeedsReview,
    ExplicitConsentOff,
    DisconnectOrProfileDeletion,
    ProvenAccountOrRoleReplacement,
    Quarantine,
}

public static class PublisherProtectedStateRetentionPolicy
{
    public static bool RetainsVerifiedState(PublisherProtectedStateAuthority authority) =>
        authority is PublisherProtectedStateAuthority.LoginRequired
            or PublisherProtectedStateAuthority.NeedsReview;

    public static bool ClearsVerifiedState(PublisherProtectedStateAuthority authority) =>
        authority is PublisherProtectedStateAuthority.ExplicitConsentOff
            or PublisherProtectedStateAuthority.DisconnectOrProfileDeletion
            or PublisherProtectedStateAuthority.ProvenAccountOrRoleReplacement
            or PublisherProtectedStateAuthority.Quarantine;

    public static PublisherResourceState ProjectTransientResourceState(
        PublisherProtectedStateAuthority authority,
        bool hasVerifiedSnapshot)
    {
        if (!RetainsVerifiedState(authority))
            throw new ArgumentOutOfRangeException(nameof(authority));
        if (hasVerifiedSnapshot) return PublisherResourceState.Stale;
        return authority == PublisherProtectedStateAuthority.LoginRequired
            ? PublisherResourceState.LoginRequired
            : PublisherResourceState.NeedsReview;
    }
}

public static class PublisherProtectedStateDeletionPolicy
{
    public static bool TryDeleteGameState(
        Func<bool> deleteSnapshot,
        Func<bool> deleteRole) =>
        TryDeleteBoth(deleteSnapshot, deleteRole);

    public static bool TryDeleteProviderState(
        Func<bool> deleteSnapshots,
        Func<bool> deleteRoles) =>
        TryDeleteBoth(deleteSnapshots, deleteRoles);

    private static bool TryDeleteBoth(Func<bool> first, Func<bool> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var firstDeleted = first();
        var secondDeleted = second();
        return firstDeleted && secondDeleted;
    }
}
