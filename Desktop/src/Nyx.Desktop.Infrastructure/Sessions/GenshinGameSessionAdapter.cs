using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.Sessions;

/// <summary>
/// Connects the shared session coordinator to the already sealed Genshin discovery,
/// inspection, exact-process, direct-start, and narrow UAC boundaries.
/// </summary>
public sealed class GenshinGameSessionAdapter : IGameSessionAdapter
{
    private readonly Func<GenshinDiscoveryResult> discover;
    private readonly Func<string, GenshinInspectionResult> inspect;
    private readonly Func<string, GenshinLaunchResult> check;
    private readonly Func<string, IReadOnlyList<string>, GenshinLaunchResult> launch;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, GenshinLaunchResult> launch120Fps;
    private readonly Func<IReadOnlyList<string>> readLaunchArguments;
    private readonly Func<bool> read120FpsOnLaunch;
    private readonly object stateSync = new();
    private string? version;
    private GenshinLaunchFailureReason lastLaunchFailureReason;
    private bool lastLaunchUsed120Fps;

    public GenshinGameSessionAdapter(
        WindowsGenshinCandidateDiscovery discovery,
        GenshinInspectionAdapter inspectionAdapter,
        GenshinLaunchService launchService,
        Func<string?>? locateManualRoot = null,
        Func<IReadOnlyList<string>>? launchArguments = null,
        Func<bool>? fps120OnLaunch = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(inspectionAdapter);
        ArgumentNullException.ThrowIfNull(launchService);

        discover = () =>
        {
            var automatic = discovery.Discover();
            var manual = locateManualRoot?.Invoke();
            return string.IsNullOrWhiteSpace(manual)
                ? automatic
                : new GenshinDiscoveryResult(manual, automatic.UpdaterRoot);
        };
        inspect = root => inspectionAdapter.InspectGame(root, GenshinPathOrigin.PreviouslySaved);
        check = root => launchService.CheckGame(root);
        launch = launchService.LaunchGame;
        launch120Fps = launchService.LaunchGameWith120Fps;
        readLaunchArguments = launchArguments ?? EmptyLaunchArguments;
        read120FpsOnLaunch = fps120OnLaunch ?? Disabled120Fps;
    }

    internal GenshinGameSessionAdapter(
        Func<GenshinDiscoveryResult> discover,
        Func<string, GenshinInspectionResult> inspect,
        Func<string, GenshinLaunchResult> check,
        Func<string, GenshinLaunchResult> launch)
    {
        this.discover = discover ?? throw new ArgumentNullException(nameof(discover));
        this.inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        ArgumentNullException.ThrowIfNull(launch);
        this.launch = (root, _) => launch(root);
        launch120Fps = (root, _, _) => launch(root);
        readLaunchArguments = EmptyLaunchArguments;
        read120FpsOnLaunch = Disabled120Fps;
    }

    internal GenshinGameSessionAdapter(
        Func<GenshinDiscoveryResult> discover,
        Func<string, GenshinInspectionResult> inspect,
        Func<string, GenshinLaunchResult> check,
        Func<string, IReadOnlyList<string>, GenshinLaunchResult> launch,
        Func<IReadOnlyList<string>> readLaunchArguments,
        Func<bool>? read120FpsOnLaunch = null,
        Func<string, IReadOnlyList<string>, CancellationToken, GenshinLaunchResult>? launch120Fps = null)
    {
        this.discover = discover ?? throw new ArgumentNullException(nameof(discover));
        this.inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
        this.readLaunchArguments = readLaunchArguments ?? throw new ArgumentNullException(nameof(readLaunchArguments));
        this.read120FpsOnLaunch = read120FpsOnLaunch ?? Disabled120Fps;
        this.launch120Fps = launch120Fps ?? ((root, arguments, _) => launch(root, arguments));
    }

    public string GameId => "gi";

    public TimeSpan? LaunchDispatchTimeout => Timeout.InfiniteTimeSpan;

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

    public GenshinLaunchFailureReason LastLaunchFailureReason
    {
        get
        {
            lock (stateSync)
            {
                return lastLaunchFailureReason;
            }
        }
    }

    public bool LastLaunchUsed120Fps
    {
        get
        {
            lock (stateSync)
            {
                return lastLaunchUsed120Fps;
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
        return await Task.Run(() => Launch(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private GameSessionEvidence Observe()
    {
        try
        {
            var roots = discover();
            if (roots.GameRoot is null)
            {
                StoreObservation(version: null);
                return new(
                    LocalReadinessEvidence.NotFound,
                    ExactProcessPresence.Uncertain,
                    ExactProcessPresence.Uncertain);
            }

            var inspection = inspect(roots.GameRoot);
            if (inspection.Status is not GenshinInspectionStatus.Ready
                || string.IsNullOrWhiteSpace(inspection.CanonicalRoot)
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(roots.GameRoot),
                    inspection.CanonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                StoreObservation(version: null);
                return ReviewEvidence;
            }

            var result = check(inspection.CanonicalRoot);
            switch (result.Status)
            {
                case GenshinLaunchStatus.Ready:
                    StoreObservation(inspection.Version);
                    return GameSessionEvidence.ReadyAndAbsent;
                case GenshinLaunchStatus.Running:
                    StoreObservation(inspection.Version);
                    return new(
                        LocalReadinessEvidence.Ready,
                        ExactProcessPresence.Absent,
                        ExactProcessPresence.Present);
                default:
                    StoreObservation(version: null);
                    return ReviewEvidence;
            }
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            StoreObservation(version: null);
            return ReviewEvidence;
        }
    }

    private GameLaunchDispatchResult Launch(CancellationToken cancellationToken)
    {
        GenshinLaunchResult result;
        try
        {
            // Discovery is repeated at dispatch time. GenshinLaunchService then performs
            // its own exact revalidation immediately before any normal or elevated start.
            var roots = discover();
            if (roots.GameRoot is null)
            {
                StoreLaunchFailure(GenshinLaunchFailureReason.None);
                return GameLaunchDispatchResult.NeedsReview;
            }

            var inspection = inspect(roots.GameRoot);
            if (inspection.Status is not GenshinInspectionStatus.Ready
                || string.IsNullOrWhiteSpace(inspection.CanonicalRoot)
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(roots.GameRoot),
                    inspection.CanonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                StoreLaunchFailure(GenshinLaunchFailureReason.None);
                return GameLaunchDispatchResult.NeedsReview;
            }

            if (!TryCaptureLaunchArguments(out var launchArguments))
            {
                StoreLaunchFailure(GenshinLaunchFailureReason.None);
                return GameLaunchDispatchResult.NeedsReview;
            }
            var use120Fps = read120FpsOnLaunch();
            StoreLaunchMode(use120Fps);
            result = use120Fps
                ? launch120Fps(inspection.CanonicalRoot, launchArguments, cancellationToken)
                : launch(inspection.CanonicalRoot, launchArguments);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            StoreLaunchFailure(GenshinLaunchFailureReason.WindowsStartFailed);
            return GameLaunchDispatchResult.Failed;
        }

        StoreLaunchFailure(result.FailureReason);
        return result.Status switch
        {
            GenshinLaunchStatus.Running => GameLaunchDispatchResult.Accepted,
            GenshinLaunchStatus.LaunchFailed => GameLaunchDispatchResult.Failed,
            _ => GameLaunchDispatchResult.NeedsReview,
        };
    }

    private void StoreObservation(string? version)
    {
        lock (stateSync)
        {
            this.version = version;
        }
    }

    private void StoreLaunchFailure(GenshinLaunchFailureReason reason)
    {
        lock (stateSync)
        {
            lastLaunchFailureReason = reason;
        }
    }

    private void StoreLaunchMode(bool used120Fps)
    {
        lock (stateSync)
        {
            lastLaunchUsed120Fps = used120Fps;
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

    private static bool Disabled120Fps() => false;

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
}
