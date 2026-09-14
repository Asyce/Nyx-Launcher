namespace Nyx.Desktop.Core.Sessions;

public enum LocalReadinessEvidence
{
    Unknown,
    NotFound,
    Ready,
    NeedsReview,
}

/// <summary>
/// The result of checking one adapter-owned, exact executable identity.
/// A name-only process match must never be reported as <see cref="Present"/>.
/// </summary>
public enum ExactProcessPresence
{
    Absent,
    Present,
    Uncertain,
}

/// <summary>
/// One atomic adapter observation. Local installation readiness and exact process
/// identity are independent evidence and must both be reported explicitly.
/// </summary>
public sealed record GameSessionEvidence
{
    public GameSessionEvidence(
        LocalReadinessEvidence readiness,
        ExactProcessPresence bootstrap,
        ExactProcessPresence runtime)
    {
        if (!Enum.IsDefined(readiness))
        {
            throw new ArgumentOutOfRangeException(nameof(readiness));
        }

        if (!Enum.IsDefined(bootstrap))
        {
            throw new ArgumentOutOfRangeException(nameof(bootstrap));
        }

        if (!Enum.IsDefined(runtime))
        {
            throw new ArgumentOutOfRangeException(nameof(runtime));
        }

        Readiness = readiness;
        Bootstrap = bootstrap;
        Runtime = runtime;
    }

    public LocalReadinessEvidence Readiness { get; }

    public ExactProcessPresence Bootstrap { get; }

    public ExactProcessPresence Runtime { get; }

    public ExactProcessPresence Overall =>
        Bootstrap is ExactProcessPresence.Present || Runtime is ExactProcessPresence.Present
            ? ExactProcessPresence.Present
            : Bootstrap is ExactProcessPresence.Uncertain || Runtime is ExactProcessPresence.Uncertain
                ? ExactProcessPresence.Uncertain
                : ExactProcessPresence.Absent;

    public static GameSessionEvidence ReadyAndAbsent { get; } = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Absent);
}

public enum LocalGameStatus
{
    NotFound,
    NeedsReview,
    Ready,
    Starting,
    Running,
    LaunchFailed,
}

public enum PublisherMaintenanceStatus
{
    NotChecked,
    Checking,
    Current,
    UpdateAvailable,
    PreDownloadAvailable,
    UpdateAndPreDownloadAvailable,
    CheckInOfficialLauncher,
    CouldNotCheck,
}

/// <summary>
/// Publisher maintenance is deliberately independent from local launch state.
/// A failed or unavailable publisher check must not make a validated local game unlaunchable.
/// </summary>
public sealed record PublisherMaintenanceSnapshot(
    PublisherMaintenanceStatus Status,
    DateTimeOffset? CheckedAt = null);

public sealed record GameOperationalSnapshot(
    GameSessionSnapshot Local,
    PublisherMaintenanceSnapshot Publisher);

public enum GameSessionFailureReason
{
    None,
    LocalReadinessUnavailable,
    EvidenceUnavailable,
    EvidenceConflict,
    LaunchNeedsReview,
    LaunchDispatchFailed,
    LaunchOutcomeUncertain,
    StartupTimedOut,
}

public sealed record GameSessionSnapshot(
    string GameId,
    LocalReadinessEvidence Readiness,
    LocalGameStatus Status,
    ExactProcessPresence LastProcessEvidence,
    bool WasBootstrapObserved,
    bool WasRuntimeObserved,
    int ConsecutiveAbsentSamples,
    long ObservationGeneration,
    long? FirstAbsentGeneration,
    DateTimeOffset? FirstAbsentAt,
    DateTimeOffset? LaunchRequestedAt,
    DateTimeOffset? BootstrapObservedAt,
    long RequestedResumeGeneration,
    long AppliedResumeGeneration,
    GameSessionFailureReason FailureReason,
    bool CoordinatorStopped)
{
    public bool WasObservedRunning => WasBootstrapObserved || WasRuntimeObserved;

    public bool ResumeResetPending => RequestedResumeGeneration > AppliedResumeGeneration;

    /// <summary>The latest exact runtime-process observation, independent of bootstrap state.</summary>
    public ExactProcessPresence CurrentRuntimeEvidence { get; init; } = ExactProcessPresence.Uncertain;

    /// <summary>Monotonic timestamp captured when the coordinator applied the latest adapter evidence.</summary>
    public long? LastExactObservationTimestamp { get; init; }

    /// <summary>True only while the current runtime session followed an accepted Nyx launch.</summary>
    public bool CurrentSessionLaunchedByNyx { get; init; }

    public TimeSpan? LastLaunchDetectionDuration { get; init; }

    public TimeSpan? LastCloseDetectionDuration { get; init; }
}

public enum GameLaunchDispatchStatus
{
    Accepted,
    AlreadyRunning,
    Failed,
    NeedsReview,
}

public readonly record struct GameLaunchDispatchResult(GameLaunchDispatchStatus Status)
{
    public static GameLaunchDispatchResult Accepted { get; } = new(GameLaunchDispatchStatus.Accepted);

    public static GameLaunchDispatchResult AlreadyRunning { get; } = new(GameLaunchDispatchStatus.AlreadyRunning);

    public static GameLaunchDispatchResult Failed { get; } = new(GameLaunchDispatchStatus.Failed);

    public static GameLaunchDispatchResult NeedsReview { get; } = new(GameLaunchDispatchStatus.NeedsReview);
}

/// <summary>
/// Pure Core boundary. Implementations live outside Core and are responsible for proving
/// local readiness, exact process identity, and a sealed validated launch action.
/// Methods must return their ValueTask promptly; cancellation is cooperative, not forceful.
/// </summary>
public interface IGameSessionAdapter
{
    string GameId { get; }

    TimeSpan? LaunchDispatchTimeout => null;

    ValueTask<GameSessionEvidence> ObserveSessionAsync(CancellationToken cancellationToken);

    ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(CancellationToken cancellationToken);
}

public enum GameLaunchRequestOutcome
{
    Accepted,
    AlreadyStarting,
    AlreadyRunning,
    NotReady,
    NeedsReview,
    Failed,
    Canceled,
    Reconciling,
    CoordinatorStopped,
}

public sealed record GameLaunchRequestResult(
    GameLaunchRequestOutcome Outcome,
    GameSessionSnapshot Snapshot);
