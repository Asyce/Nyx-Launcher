using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;

namespace Nyx.Desktop.Infrastructure.Sessions;

/// <summary>
/// Connects one sealed WuWa or Endfield profile to the shared coordinator. The
/// locator supplies only a root hint; the launch service repeats the complete
/// protected identity proof for every observation and dispatch.
/// </summary>
public sealed class PublisherGameSessionAdapter : IGameSessionAdapter
{
    private readonly Func<string?> locateRoot;
    private readonly Func<string, PublisherGameDirectLaunchResult> check;
    private readonly Func<string, IReadOnlyList<string>, PublisherGameDirectLaunchResult> launch;
    private readonly Func<IReadOnlyList<string>> readLaunchArguments;
    private readonly object stateSync = new();
    private string? activeRoot;
    private string? pendingRoot;

    public PublisherGameSessionAdapter(
        string gameId,
        Func<string?> locateRoot,
        PublisherGameDirectLaunchService launchService,
        Func<PublisherGameRenderingMode>? renderingMode = null,
        Func<IReadOnlyList<string>>? launchArguments = null)
    {
        ArgumentNullException.ThrowIfNull(launchService);
        GameId = RequireSupportedGame(gameId);
        this.locateRoot = locateRoot ?? throw new ArgumentNullException(nameof(locateRoot));
        check = root => launchService.CheckGame(
            GameId,
            root,
            renderingMode?.Invoke() ?? PublisherGameRenderingMode.PublisherDefault);
        launch = (root, arguments) => launchService.LaunchGame(
            GameId,
            root,
            renderingMode?.Invoke() ?? PublisherGameRenderingMode.PublisherDefault,
            arguments);
        readLaunchArguments = launchArguments ?? EmptyLaunchArguments;
    }

    internal PublisherGameSessionAdapter(
        string gameId,
        Func<string?> locateRoot,
        Func<string, PublisherGameDirectLaunchResult> check,
        Func<string, PublisherGameDirectLaunchResult> launch)
    {
        GameId = RequireSupportedGame(gameId);
        this.locateRoot = locateRoot ?? throw new ArgumentNullException(nameof(locateRoot));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        ArgumentNullException.ThrowIfNull(launch);
        this.launch = (root, _) => launch(root);
        readLaunchArguments = EmptyLaunchArguments;
    }

    internal PublisherGameSessionAdapter(
        string gameId,
        Func<string?> locateRoot,
        Func<string, PublisherGameDirectLaunchResult> check,
        Func<string, IReadOnlyList<string>, PublisherGameDirectLaunchResult> launch,
        Func<IReadOnlyList<string>> readLaunchArguments)
    {
        GameId = RequireSupportedGame(gameId);
        this.locateRoot = locateRoot ?? throw new ArgumentNullException(nameof(locateRoot));
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
        this.readLaunchArguments = readLaunchArguments ?? throw new ArgumentNullException(nameof(readLaunchArguments));
    }

    public string GameId { get; }

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
            var discoveredRoot = locateRoot();
            var roots = ReadRoots();
            if (string.IsNullOrWhiteSpace(discoveredRoot))
            {
                return ObserveMissingRoot(roots.Active);
            }

            discoveredRoot = Path.TrimEndingDirectorySeparator(discoveredRoot);
            if (roots.Active is not null
                && !string.Equals(roots.Active, discoveredRoot, StringComparison.OrdinalIgnoreCase))
            {
                var previous = check(roots.Active);
                if (previous.Status is PublisherGameLaunchStatus.Running)
                {
                    ClearPendingRoot();
                    return Evidence(previous);
                }

                // A moved installation may make the old root uninspectable. The
                // replacement must pass one complete protected check before it is
                // staged and a second complete protected check before promotion.
                var replacement = check(discoveredRoot);
                if (replacement.Status is not PublisherGameLaunchStatus.Ready
                    and not PublisherGameLaunchStatus.Running)
                {
                    ClearPendingRoot();
                    return ReviewEvidence;
                }

                if (!string.Equals(roots.Pending, discoveredRoot, StringComparison.OrdinalIgnoreCase))
                {
                    StorePendingRoot(discoveredRoot);
                    return ReviewEvidence;
                }

                StoreActiveRoot(discoveredRoot);
                return Evidence(replacement);
            }

            var result = check(discoveredRoot);
            if (result.Status is PublisherGameLaunchStatus.Ready
                or PublisherGameLaunchStatus.Running)
            {
                StoreActiveRoot(discoveredRoot);
                return Evidence(result);
            }

            return ReviewEvidence;
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
            var discoveredRoot = locateRoot();
            if (string.IsNullOrWhiteSpace(discoveredRoot))
            {
                return GameLaunchDispatchResult.NeedsReview;
            }

            discoveredRoot = Path.TrimEndingDirectorySeparator(discoveredRoot);
            var roots = ReadRoots();
            if (roots.Active is not null
                && !string.Equals(roots.Active, discoveredRoot, StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingRoot();
                return GameLaunchDispatchResult.NeedsReview;
            }

            if (!TryCaptureLaunchArguments(out var launchArguments))
                return GameLaunchDispatchResult.NeedsReview;

            var result = launch(discoveredRoot, launchArguments);
            return result.Status switch
            {
                PublisherGameLaunchStatus.Running when result.StartedByThisCall => GameLaunchDispatchResult.Accepted,
                PublisherGameLaunchStatus.Running => GameLaunchDispatchResult.AlreadyRunning,
                PublisherGameLaunchStatus.LaunchFailed => GameLaunchDispatchResult.Failed,
                _ => GameLaunchDispatchResult.NeedsReview,
            };
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return GameLaunchDispatchResult.Failed;
        }
    }

    private GameSessionEvidence ObserveMissingRoot(string? previousRoot)
    {
        if (previousRoot is null)
        {
            return MissingEvidence;
        }

        var previous = check(previousRoot);
        if (previous.Status is PublisherGameLaunchStatus.Running)
        {
            ClearPendingRoot();
            return Evidence(previous);
        }

        ClearPendingRoot();
        return previous.Status is PublisherGameLaunchStatus.Ready
            ? new(
                LocalReadinessEvidence.NotFound,
                ToPresence(previous.Bootstrap),
                ToPresence(previous.Runtime))
            : ReviewEvidence;
    }

    private static GameSessionEvidence Evidence(PublisherGameDirectLaunchResult result) =>
        new(
            LocalReadinessEvidence.Ready,
            ToPresence(result.Bootstrap),
            ToPresence(result.Runtime));

    private static ExactProcessPresence ToPresence(RunningProcessStatus status) => status switch
    {
        RunningProcessStatus.NotRunning => ExactProcessPresence.Absent,
        RunningProcessStatus.Running => ExactProcessPresence.Present,
        _ => ExactProcessPresence.Uncertain,
    };

    private (string? Active, string? Pending) ReadRoots()
    {
        lock (stateSync)
        {
            return (activeRoot, pendingRoot);
        }
    }

    private void StoreActiveRoot(string root)
    {
        lock (stateSync)
        {
            activeRoot = root;
            pendingRoot = null;
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
        "wuwa" => gameId,
        "ae" => gameId,
        _ => throw new ArgumentOutOfRangeException(
            nameof(gameId),
            "Only WuWa and Endfield sessions are supported."),
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

    private static GameSessionEvidence MissingEvidence { get; } = new(
        LocalReadinessEvidence.NotFound,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Uncertain);
}
