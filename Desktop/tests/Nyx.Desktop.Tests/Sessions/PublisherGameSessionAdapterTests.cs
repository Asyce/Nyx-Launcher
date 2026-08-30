using System.Collections.Concurrent;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class PublisherGameSessionAdapterTests
{
    private const string WuWaRoot = @"C:\Games\Wuthering Waves";
    private const string EndfieldRoot = @"C:\Games\GRYPHLINK";

    [Fact]
    public async Task WuWa_maps_bootstrap_runtime_handoff_and_exact_absence()
    {
        var results = new Queue<PublisherGameDirectLaunchResult>([
            Result(PublisherGameLaunchStatus.Running, RunningProcessStatus.Running, RunningProcessStatus.NotRunning),
            Result(PublisherGameLaunchStatus.Running, RunningProcessStatus.NotRunning, RunningProcessStatus.Running),
            Result(PublisherGameLaunchStatus.Ready),
        ]);
        var adapter = CreateAdapter("wuwa", WuWaRoot, check: _ => results.Dequeue());

        var bootstrap = await adapter.ObserveSessionAsync(default);
        var runtime = await adapter.ObserveSessionAsync(default);
        var absent = await adapter.ObserveSessionAsync(default);

        Assert.Equal(ExactProcessPresence.Present, bootstrap.Bootstrap);
        Assert.Equal(ExactProcessPresence.Absent, bootstrap.Runtime);
        Assert.Equal(ExactProcessPresence.Absent, runtime.Bootstrap);
        Assert.Equal(ExactProcessPresence.Present, runtime.Runtime);
        Assert.Equal(GameSessionEvidence.ReadyAndAbsent, absent);
    }

    [Fact]
    public async Task Endfield_uses_only_runtime_evidence_and_uncertain_fails_closed()
    {
        var running = CreateAdapter(
            "ae",
            EndfieldRoot,
            check: _ => Result(
                PublisherGameLaunchStatus.Running,
                RunningProcessStatus.NotRunning,
                RunningProcessStatus.Running));
        var uncertain = CreateAdapter(
            "ae",
            EndfieldRoot,
            check: _ => Result(
                PublisherGameLaunchStatus.NeedsReview,
                RunningProcessStatus.NotRunning,
                RunningProcessStatus.Uncertain));

        var runningEvidence = await running.ObserveSessionAsync(default);
        var uncertainEvidence = await uncertain.ObserveSessionAsync(default);

        Assert.Equal(ExactProcessPresence.Absent, runningEvidence.Bootstrap);
        Assert.Equal(ExactProcessPresence.Present, runningEvidence.Runtime);
        Assert.Equal(LocalReadinessEvidence.NeedsReview, uncertainEvidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, uncertainEvidence.Overall);
    }

    [Theory]
    [InlineData(PublisherGameLaunchStatus.Running, GameLaunchDispatchStatus.Accepted)]
    [InlineData(PublisherGameLaunchStatus.LaunchFailed, GameLaunchDispatchStatus.Failed)]
    [InlineData(PublisherGameLaunchStatus.NeedsReview, GameLaunchDispatchStatus.NeedsReview)]
    public async Task Dispatch_maps_only_sealed_launch_outcomes(
        PublisherGameLaunchStatus status,
        GameLaunchDispatchStatus expected)
    {
        string? launchedRoot = null;
        var adapter = CreateAdapter("wuwa", WuWaRoot, launch: root =>
        {
            launchedRoot = root;
            return Result(status);
        });

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(WuWaRoot, launchedRoot);
    }

    [Fact]
    public async Task Dispatch_returns_already_running_when_service_did_not_start_process()
    {
        var adapter = CreateAdapter(
            "ae",
            EndfieldRoot,
            launch: _ => Result(
                PublisherGameLaunchStatus.Running,
                StartedByThisCall: false));

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.AlreadyRunning, result.Status);
    }

    [Fact]
    public async Task Missing_locator_is_not_found_and_never_dispatches()
    {
        var launchCount = 0;
        var adapter = CreateAdapter(
            "ae",
            EndfieldRoot,
            locate: () => null,
            launch: _ =>
            {
                launchCount++;
                return Result(PublisherGameLaunchStatus.Running);
            });

        var evidence = await adapter.ObserveSessionAsync(default);
        var launch = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(LocalReadinessEvidence.NotFound, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Overall);
        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, launch.Status);
        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task Dispatch_reads_and_freezes_current_arguments_once_per_launch()
    {
        IReadOnlyList<string> current = ["--first"];
        var resolverCalls = 0;
        var starts = new List<string[]>();
        var adapter = new PublisherGameSessionAdapter(
            "wuwa",
            () => WuWaRoot,
            _ => Result(PublisherGameLaunchStatus.Ready),
            (_, arguments) =>
            {
                starts.Add(arguments.ToArray());
                return Result(PublisherGameLaunchStatus.Running);
            },
            () =>
            {
                resolverCalls++;
                return current;
            });

        await adapter.RequestValidatedLaunchAsync(default);
        current = ["--second", "two"];
        await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(2, resolverCalls);
        Assert.Equal(["--first"], starts[0]);
        Assert.Equal(["--second", "two"], starts[1]);
    }

    [Fact]
    public async Task Root_change_between_observation_and_dispatch_is_rejected()
    {
        var root = WuWaRoot;
        var launchCount = 0;
        var adapter = CreateAdapter(
            "wuwa",
            WuWaRoot,
            locate: () => root,
            launch: _ =>
            {
                launchCount++;
                return Result(PublisherGameLaunchStatus.Running);
            });

        Assert.Equal(LocalReadinessEvidence.Ready, (await adapter.ObserveSessionAsync(default)).Readiness);
        root = @"C:\Games\WuWa Replacement";

        Assert.Equal(
            GameLaunchDispatchStatus.NeedsReview,
            (await adapter.RequestValidatedLaunchAsync(default)).Status);
        Assert.Equal(0, launchCount);
        Assert.Equal(
            ExactProcessPresence.Uncertain,
            (await adapter.ObserveSessionAsync(default)).Overall);
        Assert.Equal(
            ExactProcessPresence.Absent,
            (await adapter.ObserveSessionAsync(default)).Overall);
    }

    [Fact]
    public async Task Old_exact_running_root_wins_over_a_changed_locator()
    {
        var root = WuWaRoot;
        var oldRunning = true;
        var adapter = CreateAdapter(
            "wuwa",
            WuWaRoot,
            locate: () => root,
            check: checkedRoot => checkedRoot == WuWaRoot && oldRunning
                ? Result(
                    PublisherGameLaunchStatus.Running,
                    RunningProcessStatus.NotRunning,
                    RunningProcessStatus.Running)
                : Result(PublisherGameLaunchStatus.Ready));

        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);
        root = @"C:\Games\WuWa Replacement";
        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);

        oldRunning = false;
        Assert.Equal(ExactProcessPresence.Uncertain, (await adapter.ObserveSessionAsync(default)).Overall);
        Assert.Equal(ExactProcessPresence.Absent, (await adapter.ObserveSessionAsync(default)).Overall);
    }

    [Fact]
    public async Task Moved_valid_root_is_staged_then_promoted_when_old_root_is_uninspectable()
    {
        var replacement = @"C:\Games\WuWa Replacement";
        var discoveredRoot = WuWaRoot;
        var oldInspectable = true;
        var checkedRoots = new List<string>();
        var adapter = CreateAdapter(
            "wuwa",
            WuWaRoot,
            locate: () => discoveredRoot,
            check: root =>
            {
                checkedRoots.Add(root);
                if (root == WuWaRoot && !oldInspectable)
                {
                    return Result(
                        PublisherGameLaunchStatus.NeedsReview,
                        RunningProcessStatus.Uncertain,
                        RunningProcessStatus.Uncertain);
                }

                return Result(PublisherGameLaunchStatus.Ready);
            });

        Assert.Equal(ExactProcessPresence.Absent, (await adapter.ObserveSessionAsync(default)).Overall);
        discoveredRoot = replacement;
        oldInspectable = false;

        var staged = await adapter.ObserveSessionAsync(default);
        var promoted = await adapter.ObserveSessionAsync(default);

        Assert.Equal(LocalReadinessEvidence.NeedsReview, staged.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, staged.Overall);
        Assert.Equal(LocalReadinessEvidence.Ready, promoted.Readiness);
        Assert.Equal(ExactProcessPresence.Absent, promoted.Overall);
        Assert.Equal(
            [WuWaRoot, WuWaRoot, replacement, WuWaRoot, replacement],
            checkedRoots);
    }

    [Fact]
    public async Task Failed_second_replacement_proof_discards_staging_and_never_promotes()
    {
        var replacement = @"C:\Games\WuWa Replacement";
        var discoveredRoot = WuWaRoot;
        var replacementChecks = 0;
        var adapter = CreateAdapter(
            "wuwa",
            WuWaRoot,
            locate: () => discoveredRoot,
            check: root =>
            {
                if (root == replacement
                    && Interlocked.Increment(ref replacementChecks) == 2)
                {
                    return Result(
                        PublisherGameLaunchStatus.NeedsReview,
                        RunningProcessStatus.Uncertain,
                        RunningProcessStatus.Uncertain);
                }

                return Result(PublisherGameLaunchStatus.Ready);
            });

        Assert.Equal(ExactProcessPresence.Absent, (await adapter.ObserveSessionAsync(default)).Overall);
        discoveredRoot = replacement;

        var staged = await adapter.ObserveSessionAsync(default);
        var rejected = await adapter.ObserveSessionAsync(default);
        var restaged = await adapter.ObserveSessionAsync(default);
        var promoted = await adapter.ObserveSessionAsync(default);

        Assert.Equal(LocalReadinessEvidence.NeedsReview, staged.Readiness);
        Assert.Equal(LocalReadinessEvidence.NeedsReview, rejected.Readiness);
        Assert.Equal(LocalReadinessEvidence.NeedsReview, restaged.Readiness);
        Assert.Equal(LocalReadinessEvidence.Ready, promoted.Readiness);
        Assert.Equal(4, replacementChecks);
    }

    [Fact]
    public async Task Coordinator_confirms_two_absences_then_allows_explicit_relaunch()
    {
        var checks = new ConcurrentQueue<PublisherGameDirectLaunchResult>([
            Result(PublisherGameLaunchStatus.Ready),
            Result(
                PublisherGameLaunchStatus.Running,
                RunningProcessStatus.NotRunning,
                RunningProcessStatus.Running),
            Result(PublisherGameLaunchStatus.Ready),
            Result(PublisherGameLaunchStatus.Ready),
            Result(PublisherGameLaunchStatus.Ready),
        ]);
        var launches = 0;
        var adapter = CreateAdapter(
            "wuwa",
            WuWaRoot,
            check: _ => checks.TryDequeue(out var result)
                ? result
                : Result(PublisherGameLaunchStatus.Ready),
            launch: _ =>
            {
                Interlocked.Increment(ref launches);
                return Result(PublisherGameLaunchStatus.Running);
            });
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(time, adapter);

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("wuwa")).Outcome);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("wuwa")).Status);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("wuwa")).Status);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("wuwa")).Status);
        Assert.Equal(1, launches);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("wuwa")).Outcome);
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task Same_game_is_suppressed_while_WuWa_and_Endfield_dispatch_concurrently()
    {
        var entered = new CountdownEvent(2);
        var release = new ManualResetEventSlim();
        var wuwaCount = 0;
        var endfieldCount = 0;
        PublisherGameDirectLaunchResult BlockingLaunch(string gameId)
        {
            if (gameId == "wuwa")
            {
                Interlocked.Increment(ref wuwaCount);
            }
            else
            {
                Interlocked.Increment(ref endfieldCount);
            }

            entered.Signal();
            release.Wait(TimeSpan.FromSeconds(10));
            return Result(PublisherGameLaunchStatus.Running);
        }

        var wuwa = CreateAdapter("wuwa", WuWaRoot, launch: _ => BlockingLaunch("wuwa"));
        var endfield = CreateAdapter("ae", EndfieldRoot, launch: _ => BlockingLaunch("ae"));
        await using var coordinator = CreateCoordinator(null, wuwa, endfield);

        var firstWuWa = coordinator.RequestLaunchAsync("wuwa").AsTask();
        var firstEndfield = coordinator.RequestLaunchAsync("ae").AsTask();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var duplicateWuWa = coordinator.RequestLaunchAsync("wuwa").AsTask();
        release.Set();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await firstWuWa).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await firstEndfield).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, (await duplicateWuWa).Outcome);
        Assert.Equal(1, wuwaCount);
        Assert.Equal(1, endfieldCount);
    }

    [Fact]
    public async Task Failure_is_isolated_to_one_game()
    {
        var wuwa = CreateAdapter(
            "wuwa",
            WuWaRoot,
            launch: _ => Result(PublisherGameLaunchStatus.LaunchFailed));
        var endfield = CreateAdapter(
            "ae",
            EndfieldRoot,
            launch: _ => Result(PublisherGameLaunchStatus.Running));
        await using var coordinator = CreateCoordinator(null, wuwa, endfield);

        var failed = await coordinator.RequestLaunchAsync("wuwa");
        var accepted = await coordinator.RequestLaunchAsync("ae");

        Assert.Equal(GameLaunchRequestOutcome.Failed, failed.Outcome);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, accepted.Outcome);
    }

    [Fact]
    public async Task Precancelled_call_never_enters_locator()
    {
        var calls = 0;
        var adapter = CreateAdapter("wuwa", WuWaRoot, locate: () =>
        {
            calls++;
            return WuWaRoot;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await adapter.RequestValidatedLaunchAsync(cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    [InlineData("zzz")]
    public void Constructor_has_no_generic_profile(string gameId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublisherGameSessionAdapter(
            gameId,
            () => WuWaRoot,
            _ => Result(PublisherGameLaunchStatus.Ready),
            _ => Result(PublisherGameLaunchStatus.Running)));
    }

    private static PublisherGameSessionAdapter CreateAdapter(
        string gameId,
        string root,
        Func<string?>? locate = null,
        Func<string, PublisherGameDirectLaunchResult>? check = null,
        Func<string, PublisherGameDirectLaunchResult>? launch = null) =>
        new(
            gameId,
            locate ?? (() => root),
            check ?? (_ => Result(PublisherGameLaunchStatus.Ready)),
            launch ?? (_ => Result(PublisherGameLaunchStatus.Running)));

    private static PublisherGameDirectLaunchResult Result(
        PublisherGameLaunchStatus status,
        RunningProcessStatus bootstrap = RunningProcessStatus.NotRunning,
        RunningProcessStatus runtime = RunningProcessStatus.NotRunning,
        bool StartedByThisCall = true) =>
        new(status, Bootstrap: bootstrap, Runtime: runtime, StartedByThisCall: StartedByThisCall);

    private static GameSessionCoordinator CreateCoordinator(
        TimeProvider? timeProvider,
        params IGameSessionAdapter[] liveAdapters)
    {
        var liveById = liveAdapters.ToDictionary(adapter => adapter.GameId, StringComparer.Ordinal);
        return new(
            GameCatalog.All.Select(game => liveById.TryGetValue(game.Id, out var adapter)
                ? adapter
                : new StaticSessionAdapter(game.Id)),
            timeProvider,
            startupTimeout: TimeSpan.FromSeconds(10),
            adapterCallTimeout: TimeSpan.FromSeconds(10),
            absenceConfirmationInterval: TimeSpan.FromSeconds(1));
    }

    private sealed class StaticSessionAdapter(string gameId) : IGameSessionAdapter
    {
        public string GameId { get; } = gameId;

        public ValueTask<GameSessionEvidence> ObserveSessionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(GameSessionEvidence.ReadyAndAbsent);

        public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GameLaunchDispatchResult.NeedsReview);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
