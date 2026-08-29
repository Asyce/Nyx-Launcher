using Nyx.Desktop.Core.Playtime;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Sessions;
using NyxPlaytime = Nyx.Desktop.Core.Playtime.EndfieldPlaytime;

namespace Nyx.Desktop.Infrastructure.Playtime;

public sealed record EndfieldPlaytimeSnapshot(
    EndfieldPlaytimeStatistics Statistics,
    bool IsRunning,
    bool HasPendingSession,
    int IncompleteSessions,
    bool SaveFailed)
{
    public override string ToString() => nameof(EndfieldPlaytimeSnapshot);
}

public sealed class EndfieldPlaytimeService : IDisposable
{
    private readonly object sync = new();
    private readonly Func<EndfieldPlaytimeState, bool> persist;
    private readonly GameSessionRefreshPump sessionRefresh;
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo localTimeZone;
    private EndfieldPlaytimeState state;
    private bool priorLifetimePending;
    private bool observedConfirmedAbsence;
    private bool running;
    private bool saveFailed;
    private bool disposed;

    public EndfieldPlaytimeService(
        EndfieldPlaytimeState initialState,
        Func<EndfieldPlaytimeState, bool> persist,
        GameSessionRefreshPump sessionRefresh,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
    {
        state = LauncherStateMigrations.Normalize(
            initialState ?? throw new ArgumentNullException(nameof(initialState)));
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        this.sessionRefresh = sessionRefresh ?? throw new ArgumentNullException(nameof(sessionRefresh));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        priorLifetimePending = state.PendingStart is not null;
        sessionRefresh.Refreshed += SessionRefresh_Refreshed;
    }

    public EndfieldPlaytimeSnapshot Current
    {
        get
        {
            lock (sync) return Snapshot();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        sessionRefresh.Refreshed -= SessionRefresh_Refreshed;
    }

    private void SessionRefresh_Refreshed(object? sender, GameSessionsRefreshedEventArgs e)
    {
        if (!e.Snapshots.TryGetValue("ae", out var snapshot)) return;
        lock (sync)
        {
            if (disposed) return;
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var stateChanged = false;
            if (snapshot.LastProcessEvidence is not ExactProcessPresence.Uncertain
                && priorLifetimePending)
            {
                state = state with
                {
                    PendingStart = null,
                    IncompleteSessions = IncrementIncomplete(state.IncompleteSessions),
                };
                priorLifetimePending = false;
                stateChanged = true;
            }

            if (snapshot.LastProcessEvidence is ExactProcessPresence.Uncertain)
            {
                running = snapshot.Status is LocalGameStatus.Starting or LocalGameStatus.Running;
                if (!priorLifetimePending && state.PendingStart is not null)
                {
                    state = state with
                    {
                        PendingStart = null,
                        IncompleteSessions = IncrementIncomplete(state.IncompleteSessions),
                    };
                    observedConfirmedAbsence = false;
                    stateChanged = true;
                }
                PersistIfNeeded(stateChanged);
                return;
            }

            if (snapshot.Status is LocalGameStatus.Running
                && snapshot.LastProcessEvidence is ExactProcessPresence.Present)
            {
                running = true;
                if (observedConfirmedAbsence && state.PendingStart is null)
                {
                    state = state with
                    {
                        PendingStart = new()
                        {
                            StartedAt = now,
                            TimeZoneId = localTimeZone.Id,
                        },
                    };
                    stateChanged = true;
                }
                PersistIfNeeded(stateChanged);
                return;
            }

            if (snapshot.LastProcessEvidence is not ExactProcessPresence.Absent
                || snapshot.Status is LocalGameStatus.Running)
            {
                PersistIfNeeded(stateChanged);
                return;
            }

            running = false;
            observedConfirmedAbsence = true;
            if (state.PendingStart is not { } pending)
            {
                PersistIfNeeded(stateChanged);
                return;
            }

            var end = now - (snapshot.LastCloseDetectionDuration ?? TimeSpan.Zero);
            var interval = new EndfieldPlaytimeInterval(
                pending.StartedAt,
                end,
                pending.TimeZoneId);
            var intervals = interval.IsValid
                ? NyxPlaytime.LimitForStorage(state.Intervals.Append(interval))
                : state.Intervals;
            var recorded = interval.IsValid && intervals.Contains(interval);
            state = state with
            {
                Intervals = intervals,
                PendingStart = null,
                IncompleteSessions = recorded
                ? state.IncompleteSessions
                : IncrementIncomplete(state.IncompleteSessions),
            };
            PersistIfNeeded(stateChanged: true);
        }
    }

    private void PersistIfNeeded(bool stateChanged)
    {
        if (!stateChanged && !saveFailed) return;
        state = LauncherStateMigrations.Normalize(state);
        if (!persist(state))
        {
            saveFailed = true;
            return;
        }
        saveFailed = false;
    }

    private EndfieldPlaytimeSnapshot Snapshot() => new(
        NyxPlaytime.Calculate(state.Intervals),
        running,
        state.PendingStart is not null,
        state.IncompleteSessions,
        saveFailed);

    private static int IncrementIncomplete(int value) =>
        value == int.MaxValue ? value : value + 1;
}
