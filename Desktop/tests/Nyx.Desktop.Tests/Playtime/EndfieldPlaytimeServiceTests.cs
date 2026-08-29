using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Playtime;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Playtime;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Playtime;

public sealed class EndfieldPlaytimeServiceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public async Task Same_lifetime_absent_present_absent_records_one_complete_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            saves: saves,
            evidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var started = await rig.RefreshAsync();
        var exactStart = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.True(started.IsRunning);
        Assert.True(started.HasPendingSession);
        Assert.False(closed.IsRunning);
        Assert.False(closed.HasPendingSession);
        Assert.Equal(0, closed.IncompleteSessions);
        var interval = Assert.Single(saves[^1].Intervals);
        Assert.Equal(exactStart, interval.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
    }

    [Fact]
    public async Task Startup_present_is_excluded_until_absent_then_a_later_session_is_tracked()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            saves: saves,
            evidence: [
                RuntimeEvidence,
                AbsentEvidence,
                AbsentEvidence,
                RuntimeEvidence,
                AbsentEvidence,
                AbsentEvidence,
            ],
            timeProvider: clock);

        var startupRunning = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var startupClosed = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var trackedClosed = await rig.RefreshAsync();

        Assert.True(startupRunning.IsRunning);
        Assert.False(startupRunning.HasPendingSession);
        Assert.Equal(0, startupClosed.Statistics.Gameplay.Sessions);
        Assert.Equal(1, trackedClosed.Statistics.Gameplay.Sessions);
        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(saves[^1].Intervals).Duration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prior_lifetime_pending_becomes_one_incomplete_on_first_exact_observation(
        bool processIsPresent)
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            saves: saves,
            initialState: PendingState(clock.GetUtcNow().AddMinutes(-5), incomplete: 2),
            evidence: processIsPresent
                ? [RuntimeEvidence, RuntimeEvidence]
                : [AbsentEvidence, AbsentEvidence],
            timeProvider: clock);

        var first = await rig.RefreshAsync();
        var second = await rig.RefreshAsync();

        Assert.Equal(3, first.IncompleteSessions);
        Assert.Equal(3, second.IncompleteSessions);
        Assert.False(first.HasPendingSession);
        Assert.False(second.HasPendingSession);
        Assert.Single(saves);
        Assert.Empty(saves[0].Intervals);
    }

    [Fact]
    public async Task Failed_prior_pending_save_retries_without_double_counting()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            initialState: PendingState(clock.GetUtcNow().AddMinutes(-5)),
            evidence: [AbsentEvidence, AbsentEvidence],
            timeProvider: clock,
            persist: state =>
            {
                attempts++;
                if (attempts == 1) return false;
                saves.Add(state);
                return true;
            });

        var failed = await rig.RefreshAsync();
        var recovered = await rig.RefreshAsync();

        Assert.True(failed.SaveFailed);
        Assert.False(failed.HasPendingSession);
        Assert.Equal(1, failed.IncompleteSessions);
        Assert.False(recovered.SaveFailed);
        Assert.False(recovered.HasPendingSession);
        Assert.Equal(1, recovered.IncompleteSessions);
        Assert.Equal(2, attempts);
        Assert.Equal(1, Assert.Single(saves).IncompleteSessions);
    }

    [Fact]
    public async Task Failed_prior_pending_cleanup_preserves_the_absent_boundary_for_the_next_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            initialState: PendingState(clock.GetUtcNow().AddMinutes(-5)),
            evidence: [AbsentEvidence, RuntimeEvidence, AbsentEvidence, AbsentEvidence],
            timeProvider: clock,
            persist: state =>
            {
                attempts++;
                if (attempts == 1) return false;
                saves.Add(state);
                return true;
            });

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var exactStart = clock.GetUtcNow();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        var interval = Assert.Single(saves[^1].Intervals);
        Assert.Equal(exactStart, interval.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
        Assert.Equal(1, closed.IncompleteSessions);
        Assert.False(closed.SaveFailed);
    }

    [Fact]
    public async Task Failed_start_save_retries_the_exact_boundary()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            evidence: [AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock,
            persist: state =>
            {
                attempts++;
                if (attempts == 1) return false;
                saves.Add(state);
                return true;
            });

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var exactStart = clock.GetUtcNow();
        var failed = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        var recovered = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(8));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.True(failed.SaveFailed);
        Assert.True(failed.HasPendingSession);
        Assert.False(recovered.SaveFailed);
        Assert.Equal(exactStart, saves[0].PendingStart!.StartedAt);
        var interval = Assert.Single(saves[^1].Intervals);
        Assert.Equal(exactStart, interval.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
        Assert.False(closed.HasPendingSession);
    }

    [Fact]
    public async Task Failed_end_save_retries_the_same_confirmed_boundary()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            evidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock,
            persist: state =>
            {
                attempts++;
                if (attempts == 2) return false;
                saves.Add(state);
                return true;
            });

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        var recovered = await rig.RefreshAsync();

        Assert.True(failed.SaveFailed);
        Assert.False(failed.HasPendingSession);
        Assert.False(recovered.SaveFailed);
        Assert.False(recovered.HasPendingSession);
        Assert.Equal(UtcAt(2026, 8, 29, 12, 10), Assert.Single(saves[^1].Intervals).EndUtc);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Failed_end_save_preserves_a_new_session_while_retries_continue()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            evidence: [
                AbsentEvidence,
                RuntimeEvidence,
                AbsentEvidence,
                AbsentEvidence,
                RuntimeEvidence,
                AbsentEvidence,
                AbsentEvidence,
            ],
            timeProvider: clock,
            persist: state =>
            {
                attempts++;
                if (attempts is 2 or 3) return false;
                saves.Add(state);
                return true;
            });

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var firstStart = clock.GetUtcNow();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var firstFailedClose = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        var secondStart = clock.GetUtcNow();
        var secondFailedStart = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.True(firstFailedClose.SaveFailed);
        Assert.True(secondFailedStart.SaveFailed);
        Assert.False(closed.SaveFailed);
        Assert.Equal(5, attempts);
        Assert.Collection(
            saves[^1].Intervals,
            interval =>
            {
                Assert.Equal(firstStart, interval.StartUtc);
                Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
            },
            interval =>
            {
                Assert.Equal(secondStart, interval.StartUtc);
                Assert.Equal(TimeSpan.FromMinutes(5), interval.Duration);
            });
    }

    [Fact]
    public async Task Invalid_observed_interval_is_counted_incomplete_instead_of_invented()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 1, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = TestRig.Create(
            saves: saves,
            evidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromDays(8));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.Empty(saves[^1].Intervals);
        Assert.Equal(1, closed.IncompleteSessions);
        Assert.False(closed.HasPendingSession);
    }

    [Fact]
    public async Task Uncertain_evidence_neither_establishes_a_baseline_nor_tracks_a_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            evidence: [UncertainEvidence, RuntimeEvidence, AbsentEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.Equal(0, closed.Statistics.Gameplay.Sessions);
        Assert.Equal(0, closed.IncompleteSessions);
    }

    [Fact]
    public async Task Uncertain_evidence_during_a_tracked_session_marks_it_incomplete()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            evidence: [AbsentEvidence, RuntimeEvidence, UncertainEvidence, AbsentEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        var uncertain = await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.Equal(1, uncertain.IncompleteSessions);
        Assert.False(uncertain.HasPendingSession);
        Assert.Equal(0, closed.Statistics.Gameplay.Sessions);
        Assert.Equal(1, closed.IncompleteSessions);
    }

    [Fact]
    public async Task Incomplete_counter_saturates()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            initialState: PendingState(
                clock.GetUtcNow().AddMinutes(-5),
                incomplete: int.MaxValue),
            evidence: [AbsentEvidence],
            timeProvider: clock);

        var snapshot = await rig.RefreshAsync();

        Assert.Equal(int.MaxValue, snapshot.IncompleteSessions);
        Assert.False(snapshot.HasPendingSession);
    }

    private static EndfieldPlaytimeState PendingState(
        DateTimeOffset startedAt,
        int incomplete = 0) => new()
        {
            PendingStart = new()
            {
                StartedAt = startedAt,
                TimeZoneId = Utc.Id,
            },
            IncompleteSessions = incomplete,
        };

    private static DateTimeOffset UtcAt(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0,
        int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static readonly GameSessionEvidence RuntimeEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Present);

    private static readonly GameSessionEvidence UncertainEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Absent);

    private static readonly GameSessionEvidence AbsentEvidence =
        GameSessionEvidence.ReadyAndAbsent;

    private sealed class TestRig : IDisposable
    {
        private readonly GameSessionCoordinator coordinator;
        private readonly GameSessionRefreshPump pump;
        private readonly EndfieldPlaytimeService service;

        private TestRig(
            GameSessionCoordinator coordinator,
            GameSessionRefreshPump pump,
            EndfieldPlaytimeService service)
        {
            this.coordinator = coordinator;
            this.pump = pump;
            this.service = service;
        }

        public static TestRig Create(
            List<EndfieldPlaytimeState>? saves = null,
            EndfieldPlaytimeState? initialState = null,
            Func<EndfieldPlaytimeState, bool>? persist = null,
            GameSessionEvidence[]? evidence = null,
            TimeProvider? timeProvider = null)
        {
            persist ??= state =>
            {
                saves?.Add(state);
                return true;
            };
            var adapters = GameCatalog.All
                .Select(game => game.Id == "ae"
                    ? new SequenceAdapter(game.Id, evidence ?? [])
                    : new SequenceAdapter(game.Id, []))
                .Cast<IGameSessionAdapter>()
                .ToArray();
            var coordinator = new GameSessionCoordinator(
                adapters,
                timeProvider,
                startupTimeout: TimeSpan.FromSeconds(10),
                adapterCallTimeout: TimeSpan.FromSeconds(2),
                absenceConfirmationInterval: TimeSpan.FromSeconds(1));
            var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
            var service = new EndfieldPlaytimeService(
                initialState ?? new(),
                persist,
                pump,
                timeProvider,
                Utc);
            return new(coordinator, pump, service);
        }

        public async Task<EndfieldPlaytimeSnapshot> RefreshAsync()
        {
            await pump.RefreshNowAsync();
            return service.Current;
        }

        public void Dispose()
        {
            service.Dispose();
            pump.DisposeAsync().AsTask().GetAwaiter().GetResult();
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class SequenceAdapter(
        string gameId,
        IEnumerable<GameSessionEvidence> observations) : IGameSessionAdapter
    {
        private readonly Queue<GameSessionEvidence> observations = new(observations);

        public string GameId { get; } = gameId;

        public ValueTask<GameSessionEvidence> ObserveSessionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(observations.Count > 0
                ? observations.Dequeue()
                : GameSessionEvidence.ReadyAndAbsent);
        }

        public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GameLaunchDispatchResult.Accepted);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
