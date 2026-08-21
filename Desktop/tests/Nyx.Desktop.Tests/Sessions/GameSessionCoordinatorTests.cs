using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class GameSessionCoordinatorTests
{
    private static readonly GameSessionEvidence ReadyAbsent = GameSessionEvidence.ReadyAndAbsent;
    private static readonly GameSessionEvidence MissingAbsent = new(
        LocalReadinessEvidence.NotFound,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Absent);
    private static readonly GameSessionEvidence ReviewAbsent = new(
        LocalReadinessEvidence.NeedsReview,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Absent);
    private static readonly GameSessionEvidence ReadyBootstrap = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Present,
        ExactProcessPresence.Absent);
    private static readonly GameSessionEvidence ReadyRuntime = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Present);
    private static readonly GameSessionEvidence ReadyUncertain = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Absent);

    public static TheoryData<string, string> DifferentGamePairs => new()
    {
        { "gi", "hsr" },
        { "gi", "zzz" },
        { "gi", "wuwa" },
        { "gi", "ae" },
        { "hsr", "zzz" },
        { "hsr", "wuwa" },
        { "hsr", "ae" },
        { "zzz", "wuwa" },
        { "zzz", "ae" },
        { "wuwa", "ae" },
    };

    [Fact]
    public async Task Coordinator_starts_unknown_until_each_adapter_reports_local_readiness()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservation(MissingAbsent);
        fixture["hsr"].EnqueueObservation(ReadyAbsent);
        fixture["zzz"].EnqueueObservation(ReviewAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        Assert.All(coordinator.GetAllSnapshots().Values, snapshot =>
        {
            Assert.Equal(LocalReadinessEvidence.Unknown, snapshot.Readiness);
            Assert.Equal(LocalGameStatus.NeedsReview, snapshot.Status);
        });

        Assert.Equal(LocalGameStatus.NotFound, (await coordinator.RefreshAsync("gi")).Status);
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("hsr")).Status);
        Assert.Equal(LocalGameStatus.NeedsReview, (await coordinator.RefreshAsync("zzz")).Status);
    }

    [Fact]
    public async Task Not_found_readiness_blocks_launch_without_dispatch()
    {
        var fixture = new SessionFixture();
        fixture["ae"].EnqueueObservation(MissingAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.RequestLaunchAsync("ae");

        Assert.Equal(GameLaunchRequestOutcome.NotReady, result.Outcome);
        Assert.Equal(LocalGameStatus.NotFound, result.Snapshot.Status);
        Assert.Equal(0, fixture["ae"].LaunchCount);
    }

    [Fact]
    public void Coordinator_requires_every_exact_canonical_adapter_and_rejects_aliases()
    {
        var fixture = new SessionFixture();
        Assert.Throws<ArgumentException>(() =>
            new GameSessionCoordinator(fixture.Adapters.Values.Where(adapter => adapter.GameId != "ae")));
        Assert.Throws<ArgumentException>(() =>
            new GameSessionCoordinator(fixture.Adapters.Values.Append(fixture["gi"])));

        using var unknown = new CancellationTokenSource();
        var coordinator = fixture.CreateCoordinator();
        Assert.Throws<UnsupportedGameException>(() => coordinator.GetSnapshot("genshin"));
        Assert.Throws<UnsupportedGameException>(() => coordinator.GetSnapshot("endfield"));
        coordinator.Shutdown();
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("custom-")]
    [InlineData("custom_bad")]
    public void Coordinator_rejects_noncanonical_custom_adapter_ids(string gameId)
    {
        var fixture = new SessionFixture();

        Assert.Throws<UnsupportedGameException>(() => new GameSessionCoordinator(
            fixture.Adapters.Values.Append(new FakeSessionAdapter(gameId))));
    }

    [Fact]
    public async Task Startup_state_registers_no_invalid_official_colliding_or_duplicate_custom_identity()
    {
        var loaded = LauncherStateMigrations.Read("""
        {"version":1,"customGames":[
          {"id":"evil","name":"Evil","executablePath":"C:\\evil.exe","iconPath":"C:\\evil.png"},
          {"id":"gi","name":"Collision","executablePath":"C:\\gi.exe","iconPath":"C:\\gi.png"},
          {"id":"custom_bad","name":"Malformed","executablePath":"C:\\bad.exe","iconPath":"C:\\bad.png"},
          {"id":"custom-duplicate","name":"First","executablePath":"C:\\one.exe","iconPath":"C:\\one.png"},
          {"id":"custom-duplicate","name":"Second","executablePath":"C:\\two.exe","iconPath":"C:\\two.png"},
          {"id":"custom-good","name":"Good","executablePath":"C:\\good.exe","iconPath":"C:\\good.png"}
        ]}
        """);
        var fixture = new SessionFixture();
        var startupAdapters = fixture.Adapters.Values.Cast<IGameSessionAdapter>()
            .Concat(loaded.State!.CustomGames.Select(game => new FakeSessionAdapter(game.Id)));

        await using var coordinator = new GameSessionCoordinator(startupAdapters);

        Assert.True(coordinator.TryGetSnapshot("custom-good", out _));
        Assert.False(coordinator.TryGetSnapshot("evil", out _));
        Assert.False(coordinator.TryGetSnapshot("custom_bad", out _));
        Assert.False(coordinator.TryGetSnapshot("custom-duplicate", out _));
        Assert.False(coordinator.TryRegisterCustomAdapter(new FakeSessionAdapter("gi")));
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    [InlineData("zzz")]
    [InlineData("wuwa")]
    [InlineData("ae")]
    public async Task Every_canonical_game_tracks_external_runtime_independently(string gameId)
    {
        var fixture = new SessionFixture();
        fixture[gameId].EnqueueObservation(ReadyRuntime);
        await using var coordinator = fixture.CreateCoordinator();

        var snapshot = await coordinator.RefreshAsync(gameId);

        Assert.Equal(LocalGameStatus.Running, snapshot.Status);
        Assert.True(snapshot.WasRuntimeObserved);
        Assert.False(snapshot.WasBootstrapObserved);
        Assert.All(
            coordinator.GetAllSnapshots().Where(pair => pair.Key != gameId),
            pair => Assert.Equal(LocalGameStatus.NeedsReview, pair.Value.Status));
    }

    [Fact]
    public async Task Same_game_double_click_dispatches_once()
    {
        var fixture = new SessionFixture();
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Launch = async cancellationToken =>
        {
            launchEntered.TrySetResult();
            await releaseLaunch.Task.WaitAsync(cancellationToken);
            return GameLaunchDispatchResult.Accepted;
        };
        await using var coordinator = fixture.CreateCoordinator();

        var first = coordinator.RequestLaunchAsync("gi").AsTask();
        await launchEntered.Task;
        var second = coordinator.RequestLaunchAsync("gi").AsTask();
        releaseLaunch.TrySetResult();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await first).Outcome);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, (await second).Outcome);
        Assert.Equal(1, fixture["gi"].LaunchCount);
    }

    [Theory]
    [MemberData(nameof(DifferentGamePairs))]
    public async Task Every_different_game_pair_can_dispatch_concurrently(string firstId, string secondId)
    {
        var fixture = new SessionFixture();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        foreach (var gameId in new[] { firstId, secondId })
        {
            fixture[gameId].Launch = async cancellationToken =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
                return GameLaunchDispatchResult.Accepted;
            };
        }

        await using var coordinator = fixture.CreateCoordinator();
        var first = coordinator.RequestLaunchAsync(firstId).AsTask();
        var second = coordinator.RequestLaunchAsync(secondId).AsTask();
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(GameLaunchRequestOutcome.Accepted, result.Outcome));
        Assert.Equal(1, fixture[firstId].LaunchCount);
        Assert.Equal(1, fixture[secondId].LaunchCount);
    }

    [Fact]
    public async Task Bootstrap_handoff_gap_waits_for_runtime_instead_of_false_close()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(
            ReadyAbsent,
            ReadyBootstrap,
            ReadyAbsent,
            ReadyAbsent,
            ReadyRuntime);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RequestLaunchAsync("gi");
        var bootstrap = await coordinator.RefreshAsync("gi");
        Assert.True(bootstrap.WasBootstrapObserved);
        Assert.False(bootstrap.WasRuntimeObserved);
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        var runtime = await coordinator.RefreshAsync("gi");

        Assert.Equal(LocalGameStatus.Running, runtime.Status);
        Assert.True(runtime.WasBootstrapObserved);
        Assert.True(runtime.WasRuntimeObserved);
    }

    [Fact]
    public async Task Bootstrap_without_runtime_fails_only_after_handoff_timeout()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(ReadyAbsent, ReadyBootstrap, ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RequestLaunchAsync("gi");
        await coordinator.RefreshAsync("gi");
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        var timedOut = await coordinator.RefreshAsync("gi");

        Assert.Equal(LocalGameStatus.LaunchFailed, timedOut.Status);
        Assert.Equal(GameSessionFailureReason.StartupTimedOut, timedOut.FailureReason);
    }

    [Fact]
    public async Task Runtime_close_requires_generation_and_time_separated_absence()
    {
        var fixture = new SessionFixture();
        fixture["hsr"].EnqueueObservations(ReadyRuntime, ReadyAbsent, ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RefreshAsync("hsr");
        var first = await coordinator.RefreshAsync("hsr");
        var tooSoon = await coordinator.RefreshAsync("hsr");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var closed = await coordinator.RefreshAsync("hsr");

        Assert.Equal(1, first.ConsecutiveAbsentSamples);
        Assert.Equal(1, tooSoon.ConsecutiveAbsentSamples);
        Assert.True(tooSoon.ObservationGeneration > first.ObservationGeneration);
        Assert.Equal(LocalGameStatus.Ready, closed.Status);
    }

    [Fact]
    public async Task Uncertain_sample_resets_absence_confirmation()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(
            ReadyRuntime,
            ReadyAbsent,
            ReadyUncertain,
            ReadyAbsent,
            ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RefreshAsync("gi");
        Assert.Equal(1, (await coordinator.RefreshAsync("gi")).ConsecutiveAbsentSamples);
        var uncertain = await coordinator.RefreshAsync("gi");
        Assert.Equal(0, uncertain.ConsecutiveAbsentSamples);
        Assert.Equal(LocalGameStatus.Running, uncertain.Status);
        await coordinator.RefreshAsync("gi");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("gi")).Status);
    }

    [Fact]
    public async Task Resume_then_closed_runtime_does_not_stay_permanently_running()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(ReadyRuntime, ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RefreshAsync("gi");
        await coordinator.ResetAfterSystemResumeAsync();
        var resumed = coordinator.GetSnapshot("gi");
        Assert.True(resumed.WasRuntimeObserved);
        Assert.Equal(0, resumed.ConsecutiveAbsentSamples);
        await coordinator.RefreshAsync("gi");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var closed = await coordinator.RefreshAsync("gi");

        Assert.Equal(LocalGameStatus.Ready, closed.Status);
    }

    [Fact]
    public async Task Never_observed_launch_times_out_without_retry()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("gi")).Outcome);
        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        var snapshot = await coordinator.RefreshAsync("gi");

        Assert.Equal(LocalGameStatus.LaunchFailed, snapshot.Status);
        Assert.Equal(GameSessionFailureReason.StartupTimedOut, snapshot.FailureReason);
        Assert.Equal(1, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Launch_needs_review_is_sticky_across_absent_evidence()
    {
        var fixture = new SessionFixture();
        fixture["zzz"].Launch = _ => ValueTask.FromResult(GameLaunchDispatchResult.NeedsReview);
        fixture["zzz"].EnqueueObservations(ReadyAbsent, ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        var rejected = await coordinator.RequestLaunchAsync("zzz");
        var refreshed = await coordinator.RefreshAsync("zzz");
        var retried = await coordinator.RequestLaunchAsync("zzz");

        Assert.Equal(GameSessionFailureReason.LaunchNeedsReview, rejected.Snapshot.FailureReason);
        Assert.Equal(LocalGameStatus.NeedsReview, refreshed.Status);
        Assert.Equal(GameSessionFailureReason.LaunchNeedsReview, refreshed.FailureReason);
        Assert.Equal(GameLaunchRequestOutcome.NeedsReview, retried.Outcome);
        Assert.Equal(1, fixture["zzz"].LaunchCount);
    }

    [Fact]
    public async Task Caller_cancellation_after_dispatch_entry_commits_reconciliation_lock()
    {
        var fixture = new SessionFixture();
        fixture["gi"].Launch = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return GameLaunchDispatchResult.Accepted;
        };
        fixture["gi"].EnqueueObservations(ReadyAbsent, ReadyRuntime);
        await using var coordinator = fixture.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var launch = coordinator.RequestLaunchAsync("gi", cancellation.Token).AsTask();
        await fixture["gi"].LaunchEntered.Task;
        cancellation.Cancel();
        var canceled = await launch;
        var duplicate = await coordinator.RequestLaunchAsync("gi");
        var reconciled = await coordinator.RefreshAsync("gi");

        Assert.Equal(GameLaunchRequestOutcome.Reconciling, canceled.Outcome);
        Assert.Equal(LocalGameStatus.Starting, canceled.Snapshot.Status);
        Assert.Equal(GameSessionFailureReason.LaunchOutcomeUncertain, canceled.Snapshot.FailureReason);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, duplicate.Outcome);
        Assert.Equal(1, fixture["gi"].LaunchCount);
        Assert.Equal(LocalGameStatus.Running, reconciled.Status);
    }

    [Fact]
    public async Task Internal_dispatch_operation_canceled_is_reconciliation_not_caller_cancellation()
    {
        var fixture = new SessionFixture();
        fixture["hsr"].Launch = _ => throw new OperationCanceledException("fake internal cancellation");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.RequestLaunchAsync("hsr");

        Assert.Equal(GameLaunchRequestOutcome.Reconciling, result.Outcome);
        Assert.Equal(LocalGameStatus.Starting, result.Snapshot.Status);
        Assert.Equal(GameSessionFailureReason.LaunchOutcomeUncertain, result.Snapshot.FailureReason);
    }

    [Fact]
    public async Task Internal_observation_operation_canceled_becomes_unavailable_not_caller_canceled()
    {
        var fixture = new SessionFixture();
        fixture["wuwa"].Observe = _ => throw new OperationCanceledException("fake internal cancellation");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.RequestLaunchAsync("wuwa");

        Assert.Equal(GameLaunchRequestOutcome.NeedsReview, result.Outcome);
        Assert.Equal(GameSessionFailureReason.EvidenceUnavailable, result.Snapshot.FailureReason);
        Assert.Equal(0, fixture["wuwa"].LaunchCount);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_stays_canceled_and_never_commits_start()
    {
        var fixture = new SessionFixture();
        await using var coordinator = fixture.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await coordinator.RequestLaunchAsync("gi", cancellation.Token);

        Assert.Equal(GameLaunchRequestOutcome.Canceled, result.Outcome);
        Assert.Equal(LocalGameStatus.NeedsReview, result.Snapshot.Status);
        Assert.Equal(0, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Non_cooperative_dispatch_times_out_locked_and_cannot_duplicate()
    {
        var fixture = new SessionFixture();
        var never = new TaskCompletionSource<GameLaunchDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Launch = _ => new ValueTask<GameLaunchDispatchResult>(never.Task);
        fixture["gi"].EnqueueObservations(ReadyAbsent, ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator.RequestLaunchAsync("gi");
        var duplicate = await coordinator.RequestLaunchAsync("gi");
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        var stillLocked = await coordinator.RefreshAsync("gi");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(GameLaunchRequestOutcome.Reconciling, result.Outcome);
        Assert.Equal(GameLaunchRequestOutcome.AlreadyStarting, duplicate.Outcome);
        Assert.Equal(LocalGameStatus.Starting, stillLocked.Status);
        Assert.Equal(1, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Longer_launch_dispatch_wait_does_not_weaken_observation_timeout()
    {
        var fixture = new SessionFixture();
        var observations = 0;
        var neverObserved = new TaskCompletionSource<GameSessionEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Observe = _ => Interlocked.Increment(ref observations) == 1
            ? ValueTask.FromResult(ReadyAbsent)
            : new ValueTask<GameSessionEvidence>(neverObserved.Task);
        fixture["gi"].Launch = async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            return GameLaunchDispatchResult.Accepted;
        };
        await using var coordinator = new GameSessionCoordinator(
            fixture.Adapters.Values,
            fixture.Time,
            startupTimeout: TimeSpan.FromSeconds(10),
            adapterCallTimeout: TimeSpan.FromMilliseconds(40),
            absenceConfirmationInterval: TimeSpan.FromSeconds(1),
            launchDispatchTimeout: TimeSpan.FromSeconds(1));

        var launch = await coordinator.RequestLaunchAsync("gi");
        var stopwatch = Stopwatch.StartNew();
        var refreshed = await coordinator.RefreshAsync("gi");
        stopwatch.Stop();

        Assert.Equal(GameLaunchRequestOutcome.Accepted, launch.Outcome);
        Assert.Equal(LocalGameStatus.Starting, refreshed.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Interactive_adapter_can_wait_for_uac_without_unbounding_other_adapters()
    {
        var fixture = new SessionFixture();
        fixture["gi"].LaunchDispatchTimeout = Timeout.InfiniteTimeSpan;
        fixture["gi"].Launch = async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            return GameLaunchDispatchResult.Accepted;
        };
        await using var coordinator = fixture.CreateCoordinator(
            adapterCallTimeout: TimeSpan.FromMilliseconds(40));

        var result = await coordinator.RequestLaunchAsync("gi");

        Assert.Equal(GameLaunchRequestOutcome.Accepted, result.Outcome);
        Assert.Equal(1, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Refresh_all_returns_bounded_partial_states_when_one_probe_never_completes()
    {
        var fixture = new SessionFixture();
        var never = new TaskCompletionSource<GameSessionEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Observe = _ => new ValueTask<GameSessionEvidence>(never.Task);
        fixture["hsr"].EnqueueObservation(ReadyRuntime);
        await using var coordinator = fixture.CreateCoordinator();

        var stopwatch = Stopwatch.StartNew();
        var results = await coordinator.RefreshAllAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.NeedsReview, results["gi"].Status);
        Assert.Equal(GameSessionFailureReason.EvidenceUnavailable, results["gi"].FailureReason);
        Assert.Equal(LocalGameStatus.Running, results["hsr"].Status);
        Assert.Equal(1, fixture["gi"].ObserveCount);
        await coordinator.RefreshAsync("gi");
        Assert.Equal(1, fixture["gi"].ObserveCount);
    }

    [Fact]
    public async Task Completed_slow_probe_is_applied_on_the_next_refresh_without_restarting_it()
    {
        var fixture = new SessionFixture();
        var completed = new TaskCompletionSource<GameSessionEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Observe = _ => new ValueTask<GameSessionEvidence>(completed.Task);
        await using var coordinator = fixture.CreateCoordinator(
            adapterCallTimeout: TimeSpan.FromMilliseconds(40));

        var timedOut = await coordinator.RefreshAsync("gi");
        completed.SetResult(ReadyRuntime);
        await Task.Delay(20);
        var recovered = await coordinator.RefreshAsync("gi");

        Assert.Equal(GameSessionFailureReason.EvidenceUnavailable, timedOut.FailureReason);
        Assert.Equal(LocalGameStatus.Running, recovered.Status);
        Assert.Equal(1, fixture["gi"].ObserveCount);
    }

    [Fact]
    public async Task Slow_probe_and_failing_probe_are_isolated_from_other_games()
    {
        var fixture = new SessionFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Observe = async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return ReadyRuntime;
        };
        fixture["zzz"].Observe = _ => throw new InvalidOperationException("fake failure");
        fixture["hsr"].EnqueueObservation(ReadyRuntime);
        await using var coordinator = fixture.CreateCoordinator();

        var slow = coordinator.RefreshAsync("gi").AsTask();
        await fixture["gi"].ObserveEntered.Task;
        var hsr = await coordinator.RefreshAsync("hsr").AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var zzz = await coordinator.RefreshAsync("zzz");
        release.TrySetResult();
        await slow;

        Assert.Equal(LocalGameStatus.Running, hsr.Status);
        Assert.Equal(LocalGameStatus.NeedsReview, zzz.Status);
    }

    [Fact]
    public async Task Throwing_lifetime_cancellation_callback_cannot_escape_shutdown()
    {
        var fixture = new SessionFixture();
        fixture["ae"].Observe = async cancellationToken =>
        {
            _ = cancellationToken.Register(() => throw new InvalidOperationException("fake callback"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadyAbsent;
        };
        await using var coordinator = fixture.CreateCoordinator();
        var refresh = coordinator.RefreshAsync("ae").AsTask();
        await fixture["ae"].ObserveEntered.Task;

        var exception = Record.Exception(coordinator.Shutdown);
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(exception);
        Assert.True(coordinator.GetSnapshot("ae").CoordinatorStopped);
        Assert.Equal(
            GameLaunchRequestOutcome.CoordinatorStopped,
            (await coordinator.RequestLaunchAsync("ae")).Outcome);
    }

    [Fact]
    public async Task Shutdown_atomically_rejects_dispatch_not_yet_admitted()
    {
        var fixture = new SessionFixture();
        var admissionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new TestHooks
        {
            BeforeAdmission = async () =>
            {
                admissionReached.TrySetResult();
                await releaseAdmission.Task;
            },
        };
        await using var coordinator = fixture.CreateCoordinator(hooks);

        var launch = coordinator.RequestLaunchAsync("gi").AsTask();
        await admissionReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.Shutdown();
        releaseAdmission.TrySetResult();
        var result = await launch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(GameLaunchRequestOutcome.CoordinatorStopped, result.Outcome);
        Assert.True(result.Snapshot.CoordinatorStopped);
        Assert.NotEqual(LocalGameStatus.Starting, result.Snapshot.Status);
        Assert.Equal(0, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Resume_before_dispatch_admission_rejects_pre_resume_launch_evidence()
    {
        var fixture = new SessionFixture();
        var admissionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new TestHooks
        {
            BeforeAdmission = async () =>
            {
                admissionReached.TrySetResult();
                await releaseAdmission.Task;
            },
        };
        await using var coordinator = fixture.CreateCoordinator(hooks);

        var launch = coordinator.RequestLaunchAsync("gi").AsTask();
        await admissionReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.ResetAfterSystemResumeAsync();
        var pending = coordinator.GetSnapshot("gi");
        Assert.True(pending.ResumeResetPending);
        releaseAdmission.TrySetResult();
        var result = await launch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(GameLaunchRequestOutcome.NeedsReview, result.Outcome);
        Assert.Equal(0, fixture["gi"].LaunchCount);
        Assert.False(result.Snapshot.ResumeResetPending);
        Assert.Equal(1, result.Snapshot.RequestedResumeGeneration);
        Assert.Equal(1, result.Snapshot.AppliedResumeGeneration);
        Assert.NotEqual(LocalGameStatus.Starting, result.Snapshot.Status);
    }

    [Fact]
    public async Task Dispatch_admission_before_resume_commits_entry_before_reset_is_ordered()
    {
        var fixture = new SessionFixture();
        var admissionCommitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAdmissionReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeApplied = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new TestHooks
        {
            OnDispatchCommitted = gameId =>
            {
                if (gameId == "gi")
                {
                    admissionCommitted.TrySetResult();
                    releaseAdmission.Task.GetAwaiter().GetResult();
                }
            },
            OnBeforeResumeAdmission = gameId =>
            {
                if (gameId == "gi")
                {
                    resumeAdmissionReached.TrySetResult();
                }
            },
            OnResumeApplied = (gameId, generation) =>
            {
                if (gameId == "gi")
                {
                    resumeApplied.TrySetResult(generation);
                }
            },
        };
        await using var coordinator = fixture.CreateCoordinator(hooks);

        var launch = Task.Run(async () => await coordinator.RequestLaunchAsync("gi"));
        await admissionCommitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var resume = Task.Run(async () => await coordinator.ResetAfterSystemResumeAsync());
        await resumeAdmissionReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(resume.IsCompleted);
        releaseAdmission.TrySetResult();

        var result = await launch.WaitAsync(TimeSpan.FromSeconds(1));
        await resume.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await resumeApplied.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var snapshot = coordinator.GetSnapshot("gi");

        Assert.Equal(GameLaunchRequestOutcome.Accepted, result.Outcome);
        Assert.Equal(1, fixture["gi"].LaunchCount);
        Assert.Equal(1, snapshot.RequestedResumeGeneration);
        Assert.Equal(1, snapshot.AppliedResumeGeneration);
        Assert.Equal(LocalGameStatus.Starting, snapshot.Status);
    }

    [Fact]
    public async Task Resume_reset_is_durable_observable_and_applied_after_inflight_work()
    {
        var fixture = new SessionFixture();
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeApplied = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture["gi"].Observe = async _ =>
        {
            await releaseObservation.Task;
            return ReadyRuntime;
        };
        var hooks = new TestHooks
        {
            OnResumeApplied = (gameId, generation) =>
            {
                if (gameId == "gi")
                {
                    resumeApplied.TrySetResult(generation);
                }
            },
        };
        await using var coordinator = fixture.CreateCoordinator(
            hooks,
            adapterCallTimeout: TimeSpan.FromSeconds(2));

        var refresh = coordinator.RefreshAsync("gi").AsTask();
        await fixture["gi"].ObserveEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        await coordinator.ResetAfterSystemResumeAsync();
        stopwatch.Stop();
        var pending = coordinator.GetSnapshot("gi");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.True(pending.ResumeResetPending);
        Assert.Equal(1, pending.RequestedResumeGeneration);
        Assert.Equal(0, pending.AppliedResumeGeneration);

        releaseObservation.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await resumeApplied.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var applied = coordinator.GetSnapshot("gi");

        Assert.False(applied.ResumeResetPending);
        Assert.Equal(1, applied.RequestedResumeGeneration);
        Assert.Equal(1, applied.AppliedResumeGeneration);
        Assert.Equal(1, applied.ObservationGeneration);
        Assert.Equal(0, applied.ConsecutiveAbsentSamples);
        Assert.Equal(ExactProcessPresence.Uncertain, applied.LastProcessEvidence);
    }

    [Fact]
    public async Task Pre_resume_absence_returning_after_resume_is_discarded_before_close()
    {
        var fixture = new SessionFixture();
        var staleAbsenceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleAbsence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observationNumber = 0;
        fixture["gi"].Observe = async _ =>
        {
            return Interlocked.Increment(ref observationNumber) switch
            {
                1 => ReadyRuntime,
                2 => ReadyAbsent,
                3 => await PausedStaleAbsenceAsync(),
                _ => ReadyAbsent,
            };

            async Task<GameSessionEvidence> PausedStaleAbsenceAsync()
            {
                staleAbsenceEntered.TrySetResult();
                await releaseStaleAbsence.Task;
                return ReadyAbsent;
            }
        };
        await using var coordinator = fixture.CreateCoordinator(
            adapterCallTimeout: TimeSpan.FromSeconds(2));

        Assert.Equal(LocalGameStatus.Running, (await coordinator.RefreshAsync("gi")).Status);
        Assert.Equal(1, (await coordinator.RefreshAsync("gi")).ConsecutiveAbsentSamples);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var staleRefresh = coordinator.RefreshAsync("gi").AsTask();
        await staleAbsenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.ResetAfterSystemResumeAsync();
        releaseStaleAbsence.TrySetResult();
        var afterStale = await staleRefresh.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(LocalGameStatus.Running, afterStale.Status);
        Assert.Equal(0, afterStale.ConsecutiveAbsentSamples);
        Assert.Equal(1, afterStale.RequestedResumeGeneration);
        Assert.Equal(1, afterStale.AppliedResumeGeneration);
        Assert.Equal(ExactProcessPresence.Uncertain, afterStale.LastProcessEvidence);

        var firstFreshAbsence = await coordinator.RefreshAsync("gi");
        Assert.Equal(LocalGameStatus.Running, firstFreshAbsence.Status);
        Assert.Equal(1, firstFreshAbsence.ConsecutiveAbsentSamples);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var secondFreshAbsence = await coordinator.RefreshAsync("gi");

        Assert.Equal(LocalGameStatus.Ready, secondFreshAbsence.Status);
        Assert.Equal(5, fixture["gi"].ObserveCount);
    }

    [Fact]
    public async Task Close_only_enables_explicit_relaunch_and_never_auto_dispatches()
    {
        var fixture = new SessionFixture();
        fixture["gi"].EnqueueObservations(
            ReadyAbsent,
            ReadyRuntime,
            ReadyAbsent,
            ReadyAbsent,
            ReadyAbsent);
        await using var coordinator = fixture.CreateCoordinator();

        await coordinator.RequestLaunchAsync("gi");
        await coordinator.RefreshAsync("gi");
        await coordinator.RefreshAsync("gi");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LocalGameStatus.Ready, (await coordinator.RefreshAsync("gi")).Status);
        Assert.Equal(1, fixture["gi"].LaunchCount);
        await coordinator.RefreshAsync("gi");
        Assert.Equal(1, fixture["gi"].LaunchCount);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, (await coordinator.RequestLaunchAsync("gi")).Outcome);
        Assert.Equal(2, fixture["gi"].LaunchCount);
    }

    [Fact]
    public async Task Definitive_dispatch_failure_is_launch_failed_not_reconciliation()
    {
        var fixture = new SessionFixture();
        fixture["gi"].Launch = _ => ValueTask.FromResult(GameLaunchDispatchResult.Failed);
        await using var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.RequestLaunchAsync("gi");

        Assert.Equal(GameLaunchRequestOutcome.Failed, result.Outcome);
        Assert.Equal(LocalGameStatus.LaunchFailed, result.Snapshot.Status);
        Assert.Equal(GameSessionFailureReason.LaunchDispatchFailed, result.Snapshot.FailureReason);
    }

    [Fact]
    public async Task Publisher_maintenance_state_is_independent_from_local_readiness()
    {
        var fixture = new SessionFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var local = await coordinator.RefreshAsync("hsr");
        var operational = new GameOperationalSnapshot(
            local,
            new(
                PublisherMaintenanceStatus.PreDownloadAvailable,
                fixture.Time.GetUtcNow()));

        Assert.Equal(LocalGameStatus.Ready, operational.Local.Status);
        Assert.Equal(PublisherMaintenanceStatus.PreDownloadAvailable, operational.Publisher.Status);
    }

    [Fact]
    public void Session_boundary_exposes_no_concrete_live_capability_type()
    {
        var sessionTypes = typeof(GameSessionCoordinator).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(GameSessionCoordinator).Namespace)
            .ToArray();
        var exposedTypes = sessionTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(GetReferencedTypes)
            .SelectMany(FlattenType)
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.DoesNotContain(exposedTypes, name => name.Contains("System.Diagnostics.Process", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, name => name.Contains("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, name => name.Contains("System.IO.File", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, name => name.Contains("Elevation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exposedTypes, name => name.Contains("Updater", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Type> GetReferencedTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event when @event.EventHandlerType is not null => [@event.EventHandlerType],
        _ => [],
    };

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(genericArgument))
            {
                yield return nested;
            }
        }
    }

    private sealed class SessionFixture
    {
        public SessionFixture()
        {
            Adapters = GameCatalog.All.ToDictionary(
                game => game.Id,
                game => new FakeSessionAdapter(game.Id),
                StringComparer.Ordinal);
        }

        public FakeTimeProvider Time { get; } = new(
            new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero));

        public IReadOnlyDictionary<string, FakeSessionAdapter> Adapters { get; }

        public FakeSessionAdapter this[string gameId] => Adapters[gameId];

        public GameSessionCoordinator CreateCoordinator(
            IGameSessionCoordinatorHooks? hooks = null,
            TimeSpan? adapterCallTimeout = null) => hooks is null
            ? new(
                Adapters.Values,
                Time,
                startupTimeout: TimeSpan.FromSeconds(10),
                adapterCallTimeout: adapterCallTimeout ?? TimeSpan.FromMilliseconds(100),
                absenceConfirmationInterval: TimeSpan.FromSeconds(1))
            : new(
                Adapters.Values,
                Time,
                startupTimeout: TimeSpan.FromSeconds(10),
                adapterCallTimeout: adapterCallTimeout ?? TimeSpan.FromMilliseconds(100),
                absenceConfirmationInterval: TimeSpan.FromSeconds(1),
                hooks);
    }

    private sealed class TestHooks : IGameSessionCoordinatorHooks
    {
        public Func<ValueTask>? BeforeAdmission { get; init; }

        public Action<string>? OnDispatchCommitted { get; init; }

        public Action<string>? OnBeforeResumeAdmission { get; init; }

        public Action<string, long>? OnResumeApplied { get; init; }

        public ValueTask BeforeDispatchAdmissionAsync() =>
            BeforeAdmission?.Invoke() ?? ValueTask.CompletedTask;

        public void DispatchAdmissionCommitted(string gameId) =>
            OnDispatchCommitted?.Invoke(gameId);

        public void BeforeResumeAdmission(string gameId) =>
            OnBeforeResumeAdmission?.Invoke(gameId);

        public void ResumeResetApplied(string gameId, long generation) =>
            OnResumeApplied?.Invoke(gameId, generation);
    }

    private sealed class FakeSessionAdapter(string gameId) : IGameSessionAdapter
    {
        private readonly ConcurrentQueue<GameSessionEvidence> observations = new();
        private int launchCount;
        private int observeCount;

        public string GameId { get; } = gameId;

        public TimeSpan? LaunchDispatchTimeout { get; set; }

        public Func<CancellationToken, ValueTask<GameSessionEvidence>>? Observe { get; set; }

        public Func<CancellationToken, ValueTask<GameLaunchDispatchResult>>? Launch { get; set; }

        public int LaunchCount => Volatile.Read(ref launchCount);

        public int ObserveCount => Volatile.Read(ref observeCount);

        public TaskCompletionSource ObserveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LaunchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueObservation(GameSessionEvidence evidence) => observations.Enqueue(evidence);

        public void EnqueueObservations(params GameSessionEvidence[] evidence)
        {
            foreach (var sample in evidence)
            {
                observations.Enqueue(sample);
            }
        }

        public async ValueTask<GameSessionEvidence> ObserveSessionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref observeCount);
            ObserveEntered.TrySetResult();
            if (Observe is not null)
            {
                return await Observe(cancellationToken);
            }

            return observations.TryDequeue(out var evidence) ? evidence : ReadyAbsent;
        }

        public async ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref launchCount);
            LaunchEntered.TrySetResult();
            return Launch is null
                ? GameLaunchDispatchResult.Accepted
                : await Launch(cancellationToken);
        }
    }

    public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
