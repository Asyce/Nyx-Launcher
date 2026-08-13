using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class GameSessionRefreshPumpTests
{
    [Fact]
    public async Task Concurrent_manual_refreshes_never_overlap_adapter_observation()
    {
        var adapter = new ControlledAdapter("gi");
        var adapters = CreateAdapters(adapter);
        await using var coordinator = CreateCoordinator(adapters);
        await using var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
        var events = 0;
        pump.Refreshed += (_, _) => Interlocked.Increment(ref events);

        var first = pump.RefreshNowAsync().AsTask();
        await adapter.FirstObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = pump.RefreshNowAsync().AsTask();
        adapter.ReleaseFirstObservation.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, adapter.MaximumConcurrentObservations);
        Assert.Equal(2, events);
    }

    [Fact]
    public async Task Resume_reset_discards_stale_absence_before_close_confirmation()
    {
        var time = new MutableTimeProvider();
        var adapter = new SequenceAdapter(
            "gi",
            new(
                LocalReadinessEvidence.Ready,
                ExactProcessPresence.Absent,
                ExactProcessPresence.Present),
            GameSessionEvidence.ReadyAndAbsent,
            GameSessionEvidence.ReadyAndAbsent);
        await using var coordinator = CreateCoordinator(CreateAdapters(adapter), time);
        await using var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));

        Assert.Equal(LocalGameStatus.Running, (await pump.RefreshNowAsync())["gi"].Status);
        var firstAbsence = (await pump.ResetAfterResumeAndRefreshAsync())["gi"];
        time.Advance(TimeSpan.FromSeconds(1));
        var closed = (await pump.RefreshNowAsync())["gi"];

        Assert.Equal(LocalGameStatus.Running, firstAbsence.Status);
        Assert.Equal(1, firstAbsence.RequestedResumeGeneration);
        Assert.Equal(1, firstAbsence.AppliedResumeGeneration);
        Assert.Equal(LocalGameStatus.Ready, closed.Status);
        Assert.Equal(0, adapter.LaunchCount);
    }

    [Fact]
    public async Task Stop_and_coordinator_shutdown_cancel_observation_and_reject_new_work()
    {
        var adapter = new CancelableAdapter("gi");
        await using var coordinator = CreateCoordinator(CreateAdapters(adapter));
        var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromMilliseconds(10));
        pump.Start();
        await adapter.ObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        pump.Stop();
        coordinator.Shutdown();
        await pump.DisposeAsync();
        var observationsAtStop = adapter.ObserveCount;
        await Task.Delay(40);

        Assert.Equal(observationsAtStop, adapter.ObserveCount);
        Assert.True(coordinator.GetSnapshot("gi").CoordinatorStopped);
        Assert.Equal(
            GameLaunchRequestOutcome.CoordinatorStopped,
            (await coordinator.RequestLaunchAsync("gi")).Outcome);
        Assert.Equal(0, adapter.LaunchCount);
    }

    [Fact]
    public async Task Stop_during_manual_refresh_suppresses_late_event_and_dispose_drains_refresh()
    {
        var adapter = new ControlledAdapter("gi");
        await using var coordinator = CreateCoordinator(CreateAdapters(adapter));
        var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
        var events = 0;
        pump.Refreshed += (_, _) => Interlocked.Increment(ref events);

        var refresh = pump.RefreshNowAsync().AsTask();
        await adapter.FirstObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        pump.Stop();
        var disposal = pump.DisposeAsync().AsTask();
        adapter.ReleaseFirstObservation.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, events);
        Assert.True(refresh.IsCompleted);
    }

    [Fact]
    public async Task Stop_is_a_barrier_for_publication_already_in_progress()
    {
        await using var coordinator = CreateCoordinator(
            CreateAdapters(new SequenceAdapter("gi", GameSessionEvidence.ReadyAndAbsent)));
        await using var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Refreshed += (_, _) =>
        {
            publicationEntered.TrySetResult();
            releasePublication.Task.GetAwaiter().GetResult();
        };

        var refresh = Task.Run(async () => await pump.RefreshNowAsync());
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = Task.Run(pump.Stop);
        await Task.Delay(40);
        Assert.False(stop.IsCompleted);

        releasePublication.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Dispose_waits_for_manual_refresh_publication_to_drain()
    {
        await using var coordinator = CreateCoordinator(
            CreateAdapters(new SequenceAdapter("gi", GameSessionEvidence.ReadyAndAbsent)));
        var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Refreshed += (_, _) =>
        {
            publicationEntered.TrySetResult();
            releasePublication.Task.GetAwaiter().GetResult();
        };

        var refresh = Task.Run(async () => await pump.RefreshNowAsync());
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disposal = Task.Run(async () => await pump.DisposeAsync());
        await Task.Delay(40);
        Assert.False(disposal.IsCompleted);

        releasePublication.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        await refresh.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(refresh.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_closes_admission_and_drains_pre_gate_invocation_exactly_once()
    {
        await using var coordinator = CreateCoordinator(
            CreateAdapters(new SequenceAdapter("gi", GameSessionEvidence.ReadyAndAbsent)));
        var admitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionCount = 0;
        var pump = new GameSessionRefreshPump(
            coordinator,
            TimeSpan.FromHours(1),
            async () =>
            {
                Interlocked.Increment(ref admissionCount);
                admitted.TrySetResult();
                await releaseAdmission.Task;
            });
        var publications = 0;
        pump.Refreshed += (_, _) => Interlocked.Increment(ref publications);

        var preGateRefresh = pump.RefreshNowAsync().AsTask();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disposals = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () => await pump.DisposeAsync()))
            .ToArray();
        await Task.Delay(40);
        Assert.All(disposals, task => Assert.False(task.IsCompleted));

        releaseAdmission.TrySetResult();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(1));
        await preGateRefresh.WaitAsync(TimeSpan.FromSeconds(1));
        await pump.RefreshNowAsync();
        await pump.ResetAfterResumeAndRefreshAsync();

        Assert.Equal(1, admissionCount);
        Assert.Equal(0, publications);
        Assert.All(disposals, task => Assert.True(task.IsCompletedSuccessfully));
    }

    private static IReadOnlyList<IGameSessionAdapter> CreateAdapters(IGameSessionAdapter selected) =>
        GameCatalog.All.Select(game => game.Id == selected.GameId
            ? selected
            : new SequenceAdapter(game.Id, GameSessionEvidence.ReadyAndAbsent))
        .ToArray();

    private static GameSessionCoordinator CreateCoordinator(
        IEnumerable<IGameSessionAdapter> adapters,
        TimeProvider? timeProvider = null) =>
        new(
            adapters,
            timeProvider,
            startupTimeout: TimeSpan.FromSeconds(10),
            adapterCallTimeout: TimeSpan.FromSeconds(2),
            absenceConfirmationInterval: TimeSpan.FromSeconds(1));

    private class SequenceAdapter(string gameId, params GameSessionEvidence[] evidence)
        : IGameSessionAdapter
    {
        private readonly Queue<GameSessionEvidence> evidence = new(evidence);
        private int launchCount;

        public string GameId { get; } = gameId;

        public int LaunchCount => Volatile.Read(ref launchCount);

        public virtual ValueTask<GameSessionEvidence> ObserveSessionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(evidence.Count > 0
                ? evidence.Dequeue()
                : GameSessionEvidence.ReadyAndAbsent);
        }

        public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref launchCount);
            return ValueTask.FromResult(GameLaunchDispatchResult.Accepted);
        }
    }

    private sealed class ControlledAdapter(string gameId) : SequenceAdapter(gameId)
    {
        private int activeObservations;
        private int observeCount;
        private int maximumConcurrentObservations;

        public TaskCompletionSource FirstObservationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstObservation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentObservations => Volatile.Read(ref maximumConcurrentObservations);

        public override async ValueTask<GameSessionEvidence> ObserveSessionAsync(
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeObservations);
            UpdateMaximum(active);
            try
            {
                if (Interlocked.Increment(ref observeCount) == 1)
                {
                    FirstObservationEntered.TrySetResult();
                    await ReleaseFirstObservation.Task.WaitAsync(cancellationToken);
                }

                return GameSessionEvidence.ReadyAndAbsent;
            }
            finally
            {
                Interlocked.Decrement(ref activeObservations);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrentObservations);
                if (value <= current
                    || Interlocked.CompareExchange(ref maximumConcurrentObservations, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class CancelableAdapter(string gameId) : SequenceAdapter(gameId)
    {
        private int observeCount;

        public TaskCompletionSource ObservationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ObserveCount => Volatile.Read(ref observeCount);

        public override async ValueTask<GameSessionEvidence> ObserveSessionAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref observeCount);
            ObservationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return GameSessionEvidence.ReadyAndAbsent;
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
