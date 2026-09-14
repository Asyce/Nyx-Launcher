using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Hoyo;
using System.Runtime.Versioning;

namespace Nyx.Desktop.Infrastructure.Sessions;

/// <summary>
/// Connects one sealed HSR or ZZZ profile to the shared app-lifetime coordinator.
/// Discovery is repeated at dispatch, followed by the launch service's immediate
/// exact identity revalidation and argument-free process admission.
/// </summary>
public sealed class HoyoGameSessionAdapter : IGameSessionAdapter
{
    private readonly Func<HoyoGameInspectionResult> discover;
    private readonly Func<string, HoyoGameLaunchResult> check;
    private readonly Func<string, IReadOnlyList<string>, HoyoGameLaunchResult> launch;
    private readonly Func<IReadOnlyList<string>> readLaunchArguments;
    private readonly object stateSync = new();
    private string? activeRoot;
    private string? pendingRoot;
    private string? version;

    [SupportedOSPlatform("windows")]
    public HoyoGameSessionAdapter(
        string gameId,
        HoyoCurrentUserDiscovery discovery,
        HoyoGameLaunchService launchService)
        : this(gameId, discovery, launchService, null, null)
    {
    }

    [SupportedOSPlatform("windows")]
    public HoyoGameSessionAdapter(
        string gameId,
        HoyoCurrentUserDiscovery discovery,
        HoyoGameLaunchService launchService,
        Func<string?>? locateManualRoot,
        HoyoGameIdentityAdapter? identityAdapter,
        Func<HoyoGameRenderingMode>? renderingMode = null,
        Func<IReadOnlyList<string>>? launchArguments = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(launchService);
        if (locateManualRoot is not null) ArgumentNullException.ThrowIfNull(identityAdapter);

        GameId = RequireSupportedGame(gameId);
        var record = GameId == "hsr"
            ? HoyoCurrentGameRecord.HsrGlobal
            : HoyoCurrentGameRecord.ZzzGlobal;
        discover = () =>
        {
            var manual = locateManualRoot?.Invoke();
            return string.IsNullOrWhiteSpace(manual)
                ? discovery.Discover(record)
                : identityAdapter!.Inspect(GameId, manual);
        };
        check = root => launchService.CheckGame(
            GameId,
            root,
            renderingMode?.Invoke() ?? HoyoGameRenderingMode.PublisherDefault);
        launch = (root, arguments) => launchService.LaunchGame(
            GameId,
            root,
            renderingMode?.Invoke() ?? HoyoGameRenderingMode.PublisherDefault,
            arguments);
        readLaunchArguments = launchArguments ?? EmptyLaunchArguments;
    }

    internal HoyoGameSessionAdapter(
        string gameId,
        Func<HoyoGameInspectionResult> discover,
        Func<string, HoyoGameLaunchResult> check,
        Func<string, HoyoGameLaunchResult> launch)
    {
        GameId = RequireSupportedGame(gameId);
        this.discover = discover ?? throw new ArgumentNullException(nameof(discover));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        ArgumentNullException.ThrowIfNull(launch);
        this.launch = (root, _) => launch(root);
        readLaunchArguments = EmptyLaunchArguments;
    }

    internal HoyoGameSessionAdapter(
        string gameId,
        Func<HoyoGameInspectionResult> discover,
        Func<string, HoyoGameLaunchResult> check,
        Func<string, IReadOnlyList<string>, HoyoGameLaunchResult> launch,
        Func<IReadOnlyList<string>> readLaunchArguments)
    {
        GameId = RequireSupportedGame(gameId);
        this.discover = discover ?? throw new ArgumentNullException(nameof(discover));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
        this.readLaunchArguments = readLaunchArguments ?? throw new ArgumentNullException(nameof(readLaunchArguments));
    }

    public string GameId { get; }

    public string? Version
    {
        get
        {
            lock (stateSync)
            {
                return version;
            }
        }
    }

    public async ValueTask<GameSessionEvidence> ObserveSessionAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(Observe, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(Launch, cancellationToken).ConfigureAwait(false);
    }

    private GameSessionEvidence Observe()
    {
        try
        {
            var inspection = discover();
            var roots = ReadRoots();
            if (!IsExactReadyInspection(inspection))
            {
                return ObserveUnavailableDiscovery(inspection, roots.Active);
            }

            var discoveredRoot = inspection.CanonicalRoot!;
            if (roots.Active is not null
                && !string.Equals(roots.Active, discoveredRoot, StringComparison.OrdinalIgnoreCase))
            {
                var previous = check(roots.Active);
                if (previous.Status is HoyoGameLaunchStatus.Running)
                {
                    StoreActiveRoot(roots.Active, roots.Version);
                    return RunningEvidence;
                }

                if (previous.Status is not HoyoGameLaunchStatus.Ready)
                {
                    ClearPendingRoot();
                    return ReviewEvidence;
                }

                // A changed registry target cannot replace the root we were observing
                // after one absence sample. Keep the old exact path active and require
                // the same new root plus another old-root absence observation.
                if (!string.Equals(roots.Pending, discoveredRoot, StringComparison.OrdinalIgnoreCase))
                {
                    StorePendingRoot(discoveredRoot);
                    return ReviewEvidence;
                }
            }

            var result = check(discoveredRoot);
            switch (result.Status)
            {
                case HoyoGameLaunchStatus.Ready:
                    StoreActiveRoot(discoveredRoot, inspection.Version);
                    return GameSessionEvidence.ReadyAndAbsent;
                case HoyoGameLaunchStatus.Running:
                    StoreActiveRoot(discoveredRoot, inspection.Version);
                    return RunningEvidence;
                default:
                    return ReviewEvidence;
            }
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return ReviewEvidence;
        }
    }

    private GameLaunchDispatchResult Launch()
    {
        try
        {
            var inspection = discover();
            if (!IsExactReadyInspection(inspection))
            {
                return GameLaunchDispatchResult.NeedsReview;
            }

            var roots = ReadRoots();
            if (roots.Active is not null
                && !string.Equals(
                    roots.Active,
                    inspection.CanonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                // A target that changes after the coordinator's observation must go
                // through the two-observation root transition before it can launch.
                ClearPendingRoot();
                return GameLaunchDispatchResult.NeedsReview;
            }

            if (!TryCaptureLaunchArguments(out var launchArguments))
                return GameLaunchDispatchResult.NeedsReview;

            var result = launch(inspection.CanonicalRoot!, launchArguments);
            return result.Status switch
            {
                HoyoGameLaunchStatus.Running when result.StartedByThisCall => GameLaunchDispatchResult.Accepted,
                HoyoGameLaunchStatus.Running => GameLaunchDispatchResult.AlreadyRunning,
                HoyoGameLaunchStatus.LaunchFailed => GameLaunchDispatchResult.Failed,
                _ => GameLaunchDispatchResult.NeedsReview,
            };
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return GameLaunchDispatchResult.Failed;
        }
    }

    private bool IsExactReadyInspection(HoyoGameInspectionResult inspection) =>
        inspection.Status is HoyoInspectionStatus.Ready
        && string.Equals(inspection.GameId, GameId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(inspection.CanonicalRoot);

    private static bool IsMissingCurrentRecord(HoyoGameInspectionResult inspection) =>
        inspection.Status is not HoyoInspectionStatus.Ready
        && inspection.Reason is HoyoInspectionReason.CurrentRecordMissing;

    private GameSessionEvidence ObserveUnavailableDiscovery(
        HoyoGameInspectionResult inspection,
        string? previousRoot)
    {
        if (previousRoot is null)
        {
            StoreActiveRoot(null, null);
            return IsMissingCurrentRecord(inspection)
                ? MissingUncertainEvidence
                : ReviewEvidence;
        }

        var previous = check(previousRoot);
        if (previous.Status is HoyoGameLaunchStatus.Running)
        {
            ClearPendingRoot();
            return RunningEvidence;
        }

        if (previous.Status is not HoyoGameLaunchStatus.Ready)
        {
            ClearPendingRoot();
            return ReviewEvidence;
        }

        ClearPendingRoot();

        return IsMissingCurrentRecord(inspection)
            ? new(
                LocalReadinessEvidence.NotFound,
                ExactProcessPresence.Absent,
                ExactProcessPresence.Absent)
            : new(
                LocalReadinessEvidence.NeedsReview,
                ExactProcessPresence.Absent,
                ExactProcessPresence.Absent);
    }

    private (string? Active, string? Pending, string? Version) ReadRoots()
    {
        lock (stateSync)
        {
            return (activeRoot, pendingRoot, version);
        }
    }

    private void StoreActiveRoot(string? root, string? observedVersion)
    {
        lock (stateSync)
        {
            activeRoot = root;
            pendingRoot = null;
            version = observedVersion;
        }
    }

    private void StorePendingRoot(string root)
    {
        lock (stateSync)
        {
            pendingRoot = root;
        }
    }

    private void ClearPendingRoot()
    {
        lock (stateSync)
        {
            pendingRoot = null;
        }
    }

    private bool TryCaptureLaunchArguments(out IReadOnlyList<string> arguments)
    {
        var current = readLaunchArguments();
        if (!CustomArgumentParser.IsValid(current))
        {
            arguments = Array.Empty<string>();
            return false;
        }
        arguments = current.Count == 0 ? Array.Empty<string>() : Array.AsReadOnly(current.ToArray());
        return true;
    }

    private static IReadOnlyList<string> EmptyLaunchArguments() => Array.Empty<string>();

    private static string RequireSupportedGame(string? gameId) => gameId switch
    {
        "hsr" => gameId,
        "zzz" => gameId,
        _ => throw new ArgumentOutOfRangeException(nameof(gameId), "Only HSR and ZZZ sessions are supported."),
    };

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception;

    private static GameSessionEvidence ReviewEvidence { get; } = new(
        LocalReadinessEvidence.NeedsReview,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Uncertain);

    private static GameSessionEvidence MissingUncertainEvidence { get; } = new(
        LocalReadinessEvidence.NotFound,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Uncertain);

    private static GameSessionEvidence RunningEvidence { get; } = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Present);
}
