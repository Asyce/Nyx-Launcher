using System.Collections.Concurrent;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Launching;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class HoyoGameSessionAdapterTests
{
    private const string HsrRoot = @"C:\Games\Star Rail Games";
    private const string ZzzRoot = @"C:\Games\ZenlessZoneZero Game";

    [Fact]
    public async Task Observe_maps_exact_ready_absent_and_running_evidence()
    {
        var absent = CreateAdapter();
        var running = CreateAdapter(check: _ => new(HoyoGameLaunchStatus.Running));

        var absentEvidence = await absent.ObserveSessionAsync(default);
        var runningEvidence = await running.ObserveSessionAsync(default);

        Assert.Equal(LocalReadinessEvidence.Ready, absentEvidence.Readiness);
        Assert.Equal(ExactProcessPresence.Absent, absentEvidence.Overall);
        Assert.Equal("4.3.0", absent.Version);
        Assert.Equal(LocalReadinessEvidence.Ready, runningEvidence.Readiness);
        Assert.Equal(ExactProcessPresence.Present, runningEvidence.Runtime);
    }

    [Fact]
    public async Task Missing_current_record_is_not_found_but_process_presence_stays_uncertain()
    {
        var adapter = CreateAdapter(discover: () =>
            new("hsr", HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.CurrentRecordMissing));

        var evidence = await adapter.ObserveSessionAsync(default);

        Assert.Equal(LocalReadinessEvidence.NotFound, evidence.Readiness);
        Assert.Equal(ExactProcessPresence.Uncertain, evidence.Overall);
        Assert.Null(adapter.Version);
    }

    [Fact]
    public async Task Wrong_game_ready_result_and_ambiguous_process_fail_closed()
    {
        var wrongGame = CreateAdapter(discover: () =>
            new("zzz", HoyoInspectionStatus.Ready, HoyoInspectionReason.None, HsrRoot, "4.3.0"));
        var uncertain = CreateAdapter(check: _ => new(HoyoGameLaunchStatus.NeedsReview));

        Assert.Equal(
            LocalReadinessEvidence.NeedsReview,
            (await wrongGame.ObserveSessionAsync(default)).Readiness);
        Assert.Equal(
            ExactProcessPresence.Uncertain,
            (await uncertain.ObserveSessionAsync(default)).Overall);
    }

    [Theory]
    [InlineData(HoyoGameLaunchStatus.Running, GameLaunchDispatchStatus.Accepted)]
    [InlineData(HoyoGameLaunchStatus.LaunchFailed, GameLaunchDispatchStatus.Failed)]
    [InlineData(HoyoGameLaunchStatus.NeedsReview, GameLaunchDispatchStatus.NeedsReview)]
    public async Task Dispatch_maps_only_sealed_launch_outcomes(
        HoyoGameLaunchStatus status,
        GameLaunchDispatchStatus expected)
    {
        string? launchedRoot = null;
        var adapter = CreateAdapter(launch: root =>
        {
            launchedRoot = root;
            return new(status, StartedByThisCall: true);
        });

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(HsrRoot, launchedRoot);
    }

    [Fact]
    public async Task Dispatch_returns_already_running_when_service_did_not_start_process()
    {
        var adapter = CreateAdapter(
            launch: _ => new(
                HoyoGameLaunchStatus.Running,
                StartedByThisCall: false));

        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.AlreadyRunning, result.Status);
    }

    [Fact]
    public async Task Dispatch_rediscovers_and_rejects_identity_drift()
    {
        var calls = 0;
        var launchCount = 0;
        var adapter = CreateAdapter(
            discover: () => Interlocked.Increment(ref calls) == 1
                ? Ready()
                : new("hsr", HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.TargetChangedDuringInspection),
            launch: _ =>
            {
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });

        Assert.Equal(LocalReadinessEvidence.Ready, (await adapter.ObserveSessionAsync(default)).Readiness);
        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, result.Status);
        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task Dispatch_reads_and_freezes_current_arguments_once_per_launch()
    {
        IReadOnlyList<string> current = ["--first"];
        var resolverCalls = 0;
        var starts = new List<string[]>();
        var adapter = new HoyoGameSessionAdapter(
            "hsr",
            Ready,
            _ => new(HoyoGameLaunchStatus.Ready),
            (_, arguments) =>
            {
                starts.Add(arguments.ToArray());
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
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
    public async Task Old_root_running_again_resets_a_pending_root_transition()
    {
        var replacement = @"C:\Games\Star Rail Replacement";
        var discoveredRoot = HsrRoot;
        var oldStatus = HoyoGameLaunchStatus.Running;
        var adapter = CreateAdapter(
            discover: () => Ready("hsr", discoveredRoot),
            check: root => new(root == HsrRoot ? oldStatus : HoyoGameLaunchStatus.Ready));

        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);
        discoveredRoot = replacement;
        oldStatus = HoyoGameLaunchStatus.Ready;
        Assert.Equal(ExactProcessPresence.Uncertain, (await adapter.ObserveSessionAsync(default)).Overall);

        oldStatus = HoyoGameLaunchStatus.Running;
        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);

        oldStatus = HoyoGameLaunchStatus.Ready;
        Assert.Equal(ExactProcessPresence.Uncertain, (await adapter.ObserveSessionAsync(default)).Overall);
        Assert.Equal(ExactProcessPresence.Absent, (await adapter.ObserveSessionAsync(default)).Overall);
    }

    [Fact]
    public async Task Valid_registry_root_drift_keeps_observing_the_old_exact_running_process()
    {
        var discoveredRoot = HsrRoot;
        var oldRunning = true;
        var checkedRoots = new List<string>();
        var launchCount = 0;
        var adapter = CreateAdapter(
            discover: () => Ready("hsr", discoveredRoot),
            check: root =>
            {
                checkedRoots.Add(root);
                return new(root == HsrRoot && oldRunning
                    ? HoyoGameLaunchStatus.Running
                    : HoyoGameLaunchStatus.Ready);
            },
            launch: _ =>
            {
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });
        await using var coordinator = CreateCoordinator(null, adapter);

        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        discoveredRoot = @"C:\Games\Star Rail Replacement";

        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyRunning, (await coordinator.RequestLaunchAsync("hsr")).Outcome);
        Assert.Equal(0, launchCount);
        Assert.All(checkedRoots.Skip(1), root => Assert.Equal(HsrRoot, root));
    }

    [Fact]
    public async Task Root_transition_requires_two_old_root_absence_checks_before_new_root_can_close()
    {
        var replacement = @"C:\Games\Star Rail Replacement";
        var discoveredRoot = HsrRoot;
        var oldRunning = true;
        var oldChecks = 0;
        var launchCount = 0;
        var adapter = CreateAdapter(
            discover: () => Ready("hsr", discoveredRoot),
            check: root =>
            {
                if (root == HsrRoot)
                {
                    oldChecks++;
                    return new(oldRunning
                        ? HoyoGameLaunchStatus.Running
                        : HoyoGameLaunchStatus.Ready);
                }

                return new(HoyoGameLaunchStatus.Ready);
            },
            launch: root =>
            {
                Assert.Equal(replacement, root);
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(time, adapter);

        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        discoveredRoot = replacement;
        oldRunning = false;

        var transitionPending = await coordinator.RefreshAsync("hsr");
        var firstNewRootAbsence = await coordinator.RefreshAsync("hsr");
        time.Advance(TimeSpan.FromSeconds(1));
        var confirmedClosed = await coordinator.RefreshAsync("hsr");

        Assert.Equal(LocalGameStatus.Running, transitionPending.Status);
        Assert.Equal(ExactProcessPresence.Uncertain, transitionPending.LastProcessEvidence);
        Assert.Equal(LocalGameStatus.Running, firstNewRootAbsence.Status);
        Assert.Equal(LocalGameStatus.Ready, confirmedClosed.Status);
        Assert.True(oldChecks >= 3);
        Assert.Equal(0, launchCount);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("hsr")).Outcome);
        Assert.Equal(1, launchCount);
    }

    [Fact]
    public async Task Root_change_between_coordinator_observation_and_dispatch_is_rejected()
    {
        var discoveredRoot = HsrRoot;
        var launchCount = 0;
        var adapter = CreateAdapter(
            discover: () => Ready("hsr", discoveredRoot),
            launch: _ =>
            {
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });

        Assert.Equal(LocalReadinessEvidence.Ready, (await adapter.ObserveSessionAsync(default)).Readiness);
        discoveredRoot = @"C:\Games\Star Rail Replacement";
        var result = await adapter.RequestValidatedLaunchAsync(default);

        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, result.Status);
        Assert.Equal(0, launchCount);
        Assert.Equal(
            ExactProcessPresence.Uncertain,
            (await adapter.ObserveSessionAsync(default)).Overall);
        Assert.Equal(
            ExactProcessPresence.Absent,
            (await adapter.ObserveSessionAsync(default)).Overall);
    }

    [Fact]
    public async Task Missing_registry_record_does_not_hide_the_previous_exact_running_process()
    {
        var recordPresent = true;
        var oldRunning = true;
        var adapter = CreateAdapter(
            discover: () => recordPresent
                ? Ready()
                : new("hsr", HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.CurrentRecordMissing),
            check: _ => new(oldRunning
                ? HoyoGameLaunchStatus.Running
                : HoyoGameLaunchStatus.Ready));

        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);
        recordPresent = false;
        Assert.Equal(ExactProcessPresence.Present, (await adapter.ObserveSessionAsync(default)).Overall);
        oldRunning = false;
        var missingAndClosed = await adapter.ObserveSessionAsync(default);
        Assert.Equal(LocalReadinessEvidence.NotFound, missingAndClosed.Readiness);
        Assert.Equal(ExactProcessPresence.Absent, missingAndClosed.Overall);
    }

    [Fact]
    public async Task Fresh_adapter_after_root_move_blocks_when_same_name_process_path_is_uncertain()
    {
        var launchCount = 0;
        var freshAdapter = CreateAdapter(
            root: @"C:\Games\Star Rail Replacement",
            check: _ => new(HoyoGameLaunchStatus.NeedsReview),
            launch: _ =>
            {
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });
        await using var coordinator = CreateCoordinator(null, freshAdapter);

        var observed = await coordinator.RefreshAsync("hsr");
        var launch = await coordinator.RequestLaunchAsync("hsr");

        Assert.Equal(LocalGameStatus.NeedsReview, observed.Status);
        Assert.Equal(ExactProcessPresence.Uncertain, observed.LastProcessEvidence);
        Assert.Equal(GameLaunchRequestOutcome.NeedsReview, launch.Outcome);
        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task Precancelled_calls_never_enter_discovery_or_launch()
    {
        var calls = 0;
        var adapter = CreateAdapter(discover: () =>
        {
            calls++;
            return Ready();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await adapter.RequestValidatedLaunchAsync(cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Coordinator_detects_close_then_requires_an_explicit_second_launch()
    {
        var checks = new ConcurrentQueue<HoyoGameLaunchStatus>([
            HoyoGameLaunchStatus.Ready,
            HoyoGameLaunchStatus.Running,
            HoyoGameLaunchStatus.Ready,
            HoyoGameLaunchStatus.Ready,
            HoyoGameLaunchStatus.Ready,
        ]);
        var launchCount = 0;
        var adapter = CreateAdapter(
            check: _ => new(checks.TryDequeue(out var status) ? status : HoyoGameLaunchStatus.Ready),
            launch: _ =>
            {
                launchCount++;
                return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
            });
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(time, adapter);

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("hsr")).Outcome);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("hsr")).Status);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("hsr")).Status);
        await coordinator.RefreshAsync("hsr");
        Assert.Equal(1, launchCount);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("hsr")).Outcome);
        Assert.Equal(2, launchCount);
    }

    [Fact]
    public async Task Normal_user_exact_path_observation_then_activation_refresh_preserves_close_proof()
    {
        var expectedPath = Path.Combine(HsrRoot, "StarRail.exe");
        var pathQuery = new SequencedPathQuery(
            [expectedPath],
            [],
            []);
        var inspector = new WindowsRunningProcessInspector(pathQuery);
        var validator = new ReadyLaunchValidator();
        var service = new HoyoGameLaunchService(validator, inspector, new NeverStartProcess());
        var adapter = new HoyoGameSessionAdapter(
            "hsr",
            () => Ready(),
            root => service.CheckGame("hsr", root),
            root => service.LaunchGame("hsr", root));
        var time = new MutableTimeProvider();
        await using var coordinator = CreateCoordinator(time, adapter);
        await using var pump = new GameSessionRefreshPump(
            coordinator,
            refreshInterval: TimeSpan.FromHours(1));

        var elevatedObserved = (await pump.RefreshNowAsync())["hsr"];
        var activationAbsence = (await pump.RefreshNowAsync())["hsr"];
        time.Advance(TimeSpan.FromSeconds(1));
        var confirmedClosed = (await pump.RefreshNowAsync())["hsr"];

        Assert.Equal(LocalGameStatus.Running, elevatedObserved.Status);
        Assert.Equal(LocalGameStatus.Running, activationAbsence.Status);
        Assert.Equal(1, activationAbsence.ConsecutiveAbsentSamples);
        Assert.Equal(LocalGameStatus.Ready, confirmedClosed.Status);
        Assert.Equal(0, confirmedClosed.ConsecutiveAbsentSamples);
        Assert.Equal(3, pathQuery.QueryCount);
    }

    [Fact]
    public async Task Same_game_repeat_dispatches_once_but_hsr_and_zzz_can_dispatch_together()
    {
        var entered = new CountdownEvent(2);
        var release = new ManualResetEventSlim();
        var hsrCount = 0;
        var zzzCount = 0;
        HoyoGameLaunchResult BlockingLaunch(string gameId)
        {
            if (gameId == "hsr")
            {
                Interlocked.Increment(ref hsrCount);
            }
            else
            {
                Interlocked.Increment(ref zzzCount);
            }

            entered.Signal();
            release.Wait(TimeSpan.FromSeconds(2));
            return new(HoyoGameLaunchStatus.Running, StartedByThisCall: true);
        }

        var hsr = CreateAdapter(launch: _ => BlockingLaunch("hsr"));
        var zzz = CreateAdapter(
            gameId: "zzz",
            root: ZzzRoot,
            launch: _ => BlockingLaunch("zzz"));
        await using var coordinator = CreateCoordinator(null, hsr, zzz);

        var firstHsr = coordinator.RequestLaunchAsync("hsr").AsTask();
        var firstZzz = coordinator.RequestLaunchAsync("zzz").AsTask();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        var duplicateHsr = coordinator.RequestLaunchAsync("hsr").AsTask();
        release.Set();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await firstHsr).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await firstZzz).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, (await duplicateHsr).Outcome);
        Assert.Equal(1, hsrCount);
        Assert.Equal(1, zzzCount);
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("wuwa")]
    [InlineData("ae")]
    public void Constructor_has_no_generic_game_profile(string gameId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoyoGameSessionAdapter(
            gameId,
            () => Ready(),
            _ => new(HoyoGameLaunchStatus.Ready),
            _ => new(HoyoGameLaunchStatus.Running)));
    }

    private static HoyoGameSessionAdapter CreateAdapter(
        string gameId = "hsr",
        string root = HsrRoot,
        Func<HoyoGameInspectionResult>? discover = null,
        Func<string, HoyoGameLaunchResult>? check = null,
        Func<string, HoyoGameLaunchResult>? launch = null) =>
        new(
            gameId,
            discover ?? (() => Ready(gameId, root)),
            check ?? (_ => new(HoyoGameLaunchStatus.Ready)),
            launch ?? (_ => new(HoyoGameLaunchStatus.Running, StartedByThisCall: true)));

    private static HoyoGameInspectionResult Ready() => Ready("hsr", HsrRoot);

    private static HoyoGameInspectionResult Ready(string gameId, string root) =>
        new(gameId, HoyoInspectionStatus.Ready, HoyoInspectionReason.None, root, "4.3.0");

    private static GameSessionCoordinator CreateCoordinator(
        TimeProvider? timeProvider,
        params IGameSessionAdapter[] liveAdapters)
    {
        var liveById = liveAdapters.ToDictionary(adapter => adapter.GameId, StringComparer.Ordinal);
        var adapters = GameCatalog.All.Select(game => liveById.TryGetValue(game.Id, out var adapter)
            ? adapter
            : new StaticSessionAdapter(game.Id));
        return new(
            adapters,
            timeProvider,
            startupTimeout: TimeSpan.FromSeconds(10),
            adapterCallTimeout: TimeSpan.FromSeconds(2),
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

    private sealed class SequencedPathQuery(params IReadOnlyList<string?>[] observations)
        : IWindowsProcessPathQuery
    {
        private readonly Queue<IReadOnlyList<string?>> remaining = new(observations);

        public int QueryCount { get; private set; }

        public IReadOnlyList<string?> QueryExecutablePaths(string processName)
        {
            Assert.Equal("StarRail", processName);
            QueryCount++;
            return remaining.Dequeue();
        }
    }

    private sealed class ReadyLaunchValidator : IHoyoGameLaunchIdentityValidator
    {
        public HoyoGameInspectionResult Validate(string gameId, string? root)
        {
            Assert.Equal("hsr", gameId);
            Assert.Equal(HsrRoot, root);
            return Ready();
        }
    }

    private sealed class NeverStartProcess : ILaunchProcessStarter
    {
        public void Start(LaunchSpecification specification) =>
            throw new Xunit.Sdk.XunitException("Observation must never start a process.");
    }
}
