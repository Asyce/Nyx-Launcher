using System.Collections.Concurrent;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Playtime;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class GenshinGameSessionAdapterTests
{
    private const string GameRoot = @"C:\Games\Genshin Impact Game";

    [Fact]
    public async Task Observe_maps_missing_candidate_to_not_found_with_uncertain_process_evidence()
    {
        var adapter = CreateAdapter(
            discover: () => new(null, null));

        var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.NotFound, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Bootstrap);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Runtime);
        Assert.Null(adapter.Version);
    }

    [Fact]
    public async Task Discovery_loss_after_exact_running_cannot_false_close_or_reenable_launch()
    {
        var discoveryCount = 0;
        var adapter = CreateAdapter(
            discover: () => Interlocked.Increment(ref discoveryCount) == 1
                ? new(GameRoot, null)
                : new(null, null),
            check: _ => new(GenshinLaunchStatus.Running));
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(adapter, time);

        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        var firstLoss = await coordinator.RefreshAsync("gi");
        time.Advance(TimeSpan.FromSeconds(5));
        var repeatedLoss = await coordinator.RefreshAsync("gi");
        var launch = await coordinator.RequestLaunchAsync("gi");

        Assert.Equal(ExactProcessPresence.Uncertain, firstLoss.LastProcessEvidence);
        Assert.Equal(0, firstLoss.ConsecutiveAbsentSamples);
        Assert.Equal(LocalGameStatus.Running, repeatedLoss.Status);
        Assert.Equal(0, repeatedLoss.ConsecutiveAbsentSamples);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyRunning, launch.Outcome);
    }

    [Fact]
    public async Task Observe_maps_identity_review_to_uncertain_and_clears_version()
    {
        var adapter = CreateAdapter(
            inspect: _ => new(
                GenshinInspectionStatus.NeedsReview,
                GenshinInspectionReason.SignatureInvalid,
                GameRoot));

        var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.NeedsReview, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Bootstrap);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Runtime);
        Assert.Null(adapter.Version);
    }

    [Fact]
    public async Task Observe_maps_exact_ready_and_absent_with_version()
    {
        var adapter = CreateAdapter();

        var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.Ready, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Absent, evidence.Overall);
        Assert.Equal("6.7.0", adapter.Version);
    }

    [Fact]
    public async Task Observe_maps_only_exact_running_result_to_runtime_present()
    {
        var adapter = CreateAdapter(
            check: _ => new(GenshinLaunchStatus.Running));

        var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.Ready, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Absent, evidence.Bootstrap);
        Assert.Equal(ExactProcessPresence.Present, evidence.Runtime);
    }

    [Fact]
    public async Task Observe_maps_ambiguous_process_check_to_review_not_absence()
    {
        var adapter = CreateAdapter(
            check: _ => new(GenshinLaunchStatus.NeedsReview));

        var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.NeedsReview, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Overall);
    }

    [Theory]
    [InlineData(GenshinLaunchStatus.Running, GenshinLaunchFailureReason.None, GameLaunchDispatchStatus.Accepted)]
    [InlineData(GenshinLaunchStatus.LaunchFailed, GenshinLaunchFailureReason.WindowsStartFailed, GameLaunchDispatchStatus.Failed)]
    [InlineData(GenshinLaunchStatus.LaunchFailed, GenshinLaunchFailureReason.ElevationCancelled, GameLaunchDispatchStatus.Failed)]
    [InlineData(GenshinLaunchStatus.LaunchFailed, GenshinLaunchFailureReason.ElevatedStartFailed, GameLaunchDispatchStatus.Failed)]
    [InlineData(GenshinLaunchStatus.NeedsReview, GenshinLaunchFailureReason.None, GameLaunchDispatchStatus.NeedsReview)]
    public async Task Dispatch_maps_only_sealed_Genshin_launch_outcomes(
        GenshinLaunchStatus status,
        GenshinLaunchFailureReason reason,
        GameLaunchDispatchStatus expected)
    {
        string? launchedRoot = null;
        var adapter = CreateAdapter(
            launch: root =>
            {
                launchedRoot = root;
                return new(status, FailureReason: reason, StartedByThisCall: true);
            });

        var result = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(GameRoot, launchedRoot);
        Assert.Equal(reason, adapter.LastLaunchFailureReason);
    }

    [Fact]
    public async Task Dispatch_returns_already_running_when_service_did_not_start_process()
    {
        var adapter = CreateAdapter(
            launch: _ => new(
                GenshinLaunchStatus.Running,
                StartedByThisCall: false));

        var result = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);

        Assert.Equal(GameLaunchDispatchStatus.AlreadyRunning, result.Status);
    }

    [Fact]
    public async Task Root_change_requires_an_uncertain_sample_before_the_new_root_can_be_observed()
    {
        var root = GameRoot;
        var oldRunning = true;
        var newRunning = false;
        var replacement = @"C:\Games\Genshin Replacement";
        var adapter = CreateAdapter(
            discover: () => new(root, null),
            check: checkedRoot => new(
                checkedRoot == GameRoot
                    ? (oldRunning ? GenshinLaunchStatus.Running : GenshinLaunchStatus.Ready)
                    : (newRunning ? GenshinLaunchStatus.Running : GenshinLaunchStatus.Ready),
                StartedByThisCall: false));

        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);
        root = replacement;
        oldRunning = false;

        var transition = await adapter.ObserveSessionAsync(default);
        Assert.Equal(ExactProcessPresence.Uncertain, transition.Overall);

        newRunning = true;
        var newRoot = await adapter.ObserveSessionAsync(default);
        Assert.Equal(ExactProcessPresence.Present, newRoot.Overall);
    }

    [Fact]
    public async Task External_process_at_a_new_root_never_inherits_Nyx_playtime_ownership()
    {
        var root = GameRoot;
        var oldRunning = false;
        var newRunning = false;
        var replacement = @"C:\Games\Genshin Replacement";
        var adapter = CreateAdapter(
            discover: () => new(root, null),
            check: checkedRoot => new(
                checkedRoot == GameRoot
                    ? (oldRunning ? GenshinLaunchStatus.Running : GenshinLaunchStatus.Ready)
                    : (newRunning ? GenshinLaunchStatus.Running : GenshinLaunchStatus.Ready),
                StartedByThisCall: false),
            launch: _ => new(GenshinLaunchStatus.Running, StartedByThisCall: true));
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(adapter, time);
        await using var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
        using var playtime = new GamePlaytimeService(
            new Dictionary<string, long>(),
            _ => true,
            pump,
            time);

        await pump.RefreshNowAsync();
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("gi")).Outcome);
        oldRunning = true;
        await pump.RefreshNowAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        await pump.RefreshNowAsync();

        root = replacement;
        oldRunning = false;
        newRunning = true;
        await pump.RefreshNowAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), playtime.Current("gi").Total);

        time.Advance(TimeSpan.FromMinutes(5));
        await pump.RefreshNowAsync();

        Assert.Equal(TimeSpan.FromMinutes(5), playtime.Current("gi").Total);
        Assert.False((await coordinator.RefreshAsync("gi")).CurrentSessionLaunchedByNyx);
    }

    [Fact]
    public async Task Dispatch_rejects_candidate_drift_before_live_launch_boundary()
    {
        var calls = 0;
        var adapter = CreateAdapter(
            discover: () => Interlocked.Increment(ref calls) == 1
                ? new(GameRoot, null)
                : new(null, null));

        Assert.Equal(LocalReadinessEvidence.Ready, (await adapter.ObserveSessionAsync(default)).Readiness);
        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task Pre_cancelled_dispatch_never_enters_live_launch_boundary()
    {
        var launchCount = 0;
        var adapter = CreateAdapter(
            launch: _ =>
            {
                Interlocked.Increment(ref launchCount);
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await adapter.RequestValidatedLaunchAsync(cancellation.Token));
        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task Dispatch_reads_and_freezes_current_arguments_once_per_launch()
    {
        IReadOnlyList<string> current = ["--first"];
        var resolverCalls = 0;
        var starts = new List<string[]>();
        var adapter = new GenshinGameSessionAdapter(
            () => new(GameRoot, null),
            root => new(GenshinInspectionStatus.Ready, GenshinInspectionReason.None, root, "6.7.0"),
            _ => new(GenshinLaunchStatus.Ready),
            (_, arguments) =>
            {
                starts.Add(arguments.ToArray());
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
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
    public async Task Dispatch_captures_120_fps_preference_once_and_uses_only_enabled_path()
    {
        var preferenceReads = 0;
        var directStarts = 0;
        var helperStarts = 0;
        var adapter = new GenshinGameSessionAdapter(
            () => new(GameRoot, null),
            root => new(GenshinInspectionStatus.Ready, GenshinInspectionReason.None, root, "6.7.0"),
            _ => new(GenshinLaunchStatus.Ready),
            (_, _) =>
            {
                directStarts++;
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            },
            () => ["--first", "two"],
            () =>
            {
                preferenceReads++;
                return true;
            },
            (_, arguments, _) =>
            {
                helperStarts++;
                Assert.Equal(["--first", "two"], arguments);
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            });

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.Accepted, result.Status);
        Assert.Equal(1, preferenceReads);
        Assert.Equal(0, directStarts);
        Assert.Equal(1, helperStarts);
        Assert.True(adapter.LastLaunchUsed120Fps);
    }

    [Fact]
    public async Task Dispatch_with_120_fps_off_preserves_direct_path_and_never_calls_helper()
    {
        var directStarts = 0;
        var helperStarts = 0;
        var adapter = new GenshinGameSessionAdapter(
            () => new(GameRoot, null),
            root => new(GenshinInspectionStatus.Ready, GenshinInspectionReason.None, root, "6.7.0"),
            _ => new(GenshinLaunchStatus.Ready),
            (_, _) =>
            {
                directStarts++;
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            },
            EmptyArguments,
            () => false,
            (_, _, _) =>
            {
                helperStarts++;
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            });

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.Accepted, result.Status);
        Assert.Equal(1, directStarts);
        Assert.Equal(0, helperStarts);
        Assert.False(adapter.LastLaunchUsed120Fps);
    }

    [Fact]
    public async Task Coordinator_observes_close_then_requires_explicit_second_launch()
    {
        var checks = new ConcurrentQueue<GenshinLaunchStatus>([
            GenshinLaunchStatus.Ready,
            GenshinLaunchStatus.Running,
            GenshinLaunchStatus.Ready,
            GenshinLaunchStatus.Ready,
            GenshinLaunchStatus.Ready,
        ]);
        var launchCount = 0;
        var adapter = CreateAdapter(
            check: _ => new(checks.TryDequeue(out var status) ? status : GenshinLaunchStatus.Ready),
            launch: _ =>
            {
                Interlocked.Increment(ref launchCount);
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            });
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(adapter, time);

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("gi")).Outcome);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("gi")).Status);
        await coordinator.RefreshAsync("gi");
        Assert.Equal(1, launchCount);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("gi")).Outcome);
        Assert.Equal(2, launchCount);
    }

    [Fact]
    public async Task Same_game_repeat_with_production_adapter_dispatches_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchCount = 0;
        var adapter = CreateAdapter(
            launch: _ =>
            {
                Interlocked.Increment(ref launchCount);
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new(GenshinLaunchStatus.Running, StartedByThisCall: true);
            });
        await using var coordinator = CreateCoordinator(adapter);

        var first = coordinator.RequestLaunchAsync("gi").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.RequestLaunchAsync("gi").AsTask();
        release.TrySetResult();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await first).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, (await second).Outcome);
        Assert.Equal(1, launchCount);
    }

    private static GenshinGameSessionAdapter CreateAdapter(
        Func<GenshinDiscoveryResult>? discover = null,
        Func<string, GenshinInspectionResult>? inspect = null,
        Func<string, GenshinLaunchResult>? check = null,
        Func<string, GenshinLaunchResult>? launch = null) =>
        new(
            discover ?? (() => new(GameRoot, null)),
            inspect ?? (root => new(
                GenshinInspectionStatus.Ready,
                GenshinInspectionReason.None,
                root,
                "6.7.0")),
            check ?? (_ => new(GenshinLaunchStatus.Ready)),
            launch ?? (_ => new(GenshinLaunchStatus.Running, StartedByThisCall: true)));

    private static GameSessionCoordinator CreateCoordinator(
        IGameSessionAdapter genshin,
        TimeProvider? timeProvider = null)
    {
        var adapters = GameCatalog.All.Select(game => game.Id == "gi"
            ? genshin
            : new StaticSessionAdapter(game.Id));
        return new(
            adapters,
            timeProvider,
            startupTimeout: TimeSpan.FromSeconds(10),
            adapterCallTimeout: TimeSpan.FromSeconds(2),
            absenceConfirmationInterval: TimeSpan.FromSeconds(1));
    }

    private static IReadOnlyList<string> EmptyArguments() => Array.Empty<string>();

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
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration)
        {
            now += duration;
            timestamp += duration.Ticks;
        }
    }
}
