namespace Nyx.Desktop.Core.AccountStatus;

public enum WuWaAccountStatusFailure
{
    None,
    CacheNotFound,
    CacheMalformed,
    MultipleAccounts,
    PlayerInfoRejected,
    RoleRejected,
    InvalidResponse,
    ResponseTooLarge,
    Timeout,
    Network,
    Canceled,
    RateLimited,
    Shutdown,
}

public sealed record WuWaAccountStatusSnapshot(
    int Energy,
    int MaxEnergy,
    int StoreEnergy,
    long StoreEnergyRecoverTime,
    long EnergyRecoverTime,
    int Liveness,
    int LivenessMaxCount);

public sealed record WuWaAccountIdentity(string PlayerId, string Region)
{
    public string DisplayText => $"{PlayerId} · {Region}";

    public override string ToString() => nameof(WuWaAccountIdentity);
}

public static class WuWaAccountStatusRules
{
    // Protected publisher recovery snapshots already use a fourteen-day trust
    // window. WuWa recovery normally completes in hours, so this remains
    // generous while bounding every duration conversion and UI projection.
    public const long MaximumRecoverySeconds = 14L * 24 * 60 * 60;

    public static bool IsValidRecoverySeconds(long value) =>
        value is >= 0 and <= MaximumRecoverySeconds;
}

public sealed record WuWaAccountStatusResult(
    DateTimeOffset CheckedAt,
    WuWaAccountStatusFailure Failure,
    WuWaAccountStatusSnapshot? Snapshot,
    DateTimeOffset? SuccessfulAt,
    bool IsStale)
{
    public WuWaAccountIdentity? Identity { get; init; }

    public bool IsSuccess => Failure is WuWaAccountStatusFailure.None && Snapshot is not null;

    public override string ToString() => nameof(WuWaAccountStatusResult);
}
