using System.Collections.ObjectModel;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Infrastructure.Playtime;

public sealed record GamePlaytimeSnapshot(
    TimeSpan Total,
    bool IsTracking,
    bool SaveFailed)
{
    /// <summary>The exact persisted whole-second total, without TimeSpan clamping.</summary>
    public long TotalSeconds { get; init; }

    public bool TrackingAvailable { get; init; } = true;

    public override string ToString() => nameof(GamePlaytimeSnapshot);
}

/// <summary>
/// Tracks only runtime sessions that followed an accepted Nyx launch during
/// this app lifetime. Totals are persisted as whole seconds per game.
/// </summary>
public sealed class GamePlaytimeService : IDisposable
{
    private readonly object sync = new();
    private readonly Func<IReadOnlyDictionary<string, long>, bool> persist;
    private readonly GameSessionRefreshPump sessionRefresh;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, long> totals;
    private readonly Dictionary<string, RuntimeState> runtimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingGameIds = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, long>? pendingTotals;
    private bool trackingAvailable = true;
    private bool suspended;
    private bool disposed;

    public GamePlaytimeService(
        IReadOnlyDictionary<string, long> initialTotals,
        Func<IReadOnlyDictionary<string, long>, bool> persist,
        GameSessionRefreshPump sessionRefresh,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(initialTotals);
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        this.sessionRefresh = sessionRefresh ?? throw new ArgumentNullException(nameof(sessionRefresh));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        totals = NormalizeTotals(initialTotals);
        sessionRefresh.Refreshed += SessionRefresh_Refreshed;
        sessionRefresh.SystemSuspending += SessionRefresh_SystemSuspending;
    }

    /// <summary>Closes and forgets a tracked runtime before its adapter is replaced or removed.</summary>
    public void CloseRuntime(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            if (runtimes.Remove(gameId, out var runtime))
            {
                CloseAtLastConfirmed(runtime);
            }

            TryPersistPending();
        }
    }

    /// <summary>Closes and permanently forgets playtime for a removed game.</summary>
    public void ForgetRemovedGame(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            if (runtimes.Remove(gameId, out var runtime))
            {
                CloseAtLastConfirmed(runtime);
            }

            totals.Remove(gameId);
            pendingGameIds.Remove(gameId);
            if (pendingTotals is not null)
            {
                pendingTotals = pendingGameIds.Count == 0 ? null : Freeze(totals);
            }

            TryPersistPending();
        }
    }

    /// <summary>Returns a locked, read-only copy of the newest in-memory totals.</summary>
    public IReadOnlyDictionary<string, long> SnapshotTotals()
    {
        lock (sync)
        {
            return Freeze(totals);
        }
    }

    public GamePlaytimeSnapshot Current(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (sync)
        {
            totals.TryGetValue(gameId, out var seconds);
            var isTracking = runtimes.TryGetValue(gameId, out var runtime)
                && runtime.StartedAtTimestamp is not null;
            var displayedSeconds = isTracking
                && runtime!.StartedAtTimestamp is { } startedAt
                && runtime.LastConfirmedAtTimestamp is { } lastConfirmedAt
                    ? SaturatingAdd(seconds, ElapsedWholeSeconds(startedAt, lastConfirmedAt))
                    : seconds;
            return new(ToTimeSpan(displayedSeconds), isTracking, pendingGameIds.Contains(gameId))
            {
                TotalSeconds = displayedSeconds,
                TrackingAvailable = trackingAvailable,
            };
        }
    }

    /// <summary>Fails closed when Windows sleep boundaries cannot be observed.</summary>
    public void DisableTracking()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            trackingAvailable = false;
            suspended = true;
            foreach (var runtime in runtimes.Values)
            {
                CloseAtLastConfirmed(runtime);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (!disposed)
            {
                disposed = true;
                foreach (var runtime in runtimes.Values)
                {
                    if (runtime.StartedAtTimestamp is { } startedAt
                        && runtime.LastConfirmedAtTimestamp is { } lastConfirmedAt)
                    {
                        AddElapsed(runtime.GameId, startedAt, lastConfirmedAt);
                    }

                    runtime.StartedAtTimestamp = null;
                }
            }

            TryPersistPending();
        }

        sessionRefresh.Refreshed -= SessionRefresh_Refreshed;
        sessionRefresh.SystemSuspending -= SessionRefresh_SystemSuspending;
    }

    private void SessionRefresh_SystemSuspending(object? sender, EventArgs e)
    {
        lock (sync)
        {
            if (disposed || !trackingAvailable)
            {
                return;
            }

            suspended = true;
            foreach (var runtime in runtimes.Values)
            {
                CloseAtLastConfirmed(runtime);
            }
        }
    }

    private void SessionRefresh_Refreshed(object? sender, GameSessionsRefreshedEventArgs e)
    {
        lock (sync)
        {
            if (disposed || !trackingAvailable)
            {
                return;
            }

            if (suspended)
            {
                if (!e.ResetsAfterSystemResume)
                {
                    return;
                }

                suspended = false;
            }

            TryPersistPending();
            var presentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in e.Snapshots)
            {
                presentIds.Add(pair.Key);
                HandleSnapshot(pair.Key, pair.Value);
            }

            // A removed or missing snapshot cannot prove the process is still
            // alive. Close any tracked session at its last confirmed sample.
            foreach (var pair in runtimes)
            {
                if (presentIds.Contains(pair.Key))
                {
                    continue;
                }

                CloseAtLastConfirmed(pair.Value);
            }

            TryPersistPending();
        }
    }

    private void HandleSnapshot(
        string gameId,
        GameSessionSnapshot snapshot)
    {
        if (!IsSupportedGameId(gameId))
        {
            return;
        }

        if (!runtimes.TryGetValue(gameId, out var runtime))
        {
            runtime = new RuntimeState(gameId);
            runtimes.Add(gameId, runtime);
        }

        switch (snapshot.CurrentRuntimeEvidence)
        {
            case ExactProcessPresence.Uncertain:
                // Do not count time we cannot confirm.
                CloseAtLastConfirmed(runtime);
                return;

            case ExactProcessPresence.Present:
                if (!snapshot.CurrentSessionLaunchedByNyx
                    || snapshot.LastExactObservationTimestamp is not { } presentAt)
                {
                    CloseAtLastConfirmed(runtime);
                    return;
                }

                runtime.LastConfirmedAtTimestamp = presentAt;
                if (runtime.StartedAtTimestamp is null)
                {
                    runtime.StartedAtTimestamp = presentAt;
                }

                return;

            case ExactProcessPresence.Absent:
                if (snapshot.LastExactObservationTimestamp is not { } absentAt)
                {
                    CloseAtLastConfirmed(runtime);
                    return;
                }

                runtime.LastConfirmedAtTimestamp = absentAt;
                if (runtime.StartedAtTimestamp is { } startedAt)
                {
                    AddElapsed(gameId, startedAt, absentAt);
                    runtime.StartedAtTimestamp = null;
                }

                return;
        }
    }

    private void CloseAtLastConfirmed(RuntimeState runtime)
    {
        if (runtime.StartedAtTimestamp is { } startedAt
            && runtime.LastConfirmedAtTimestamp is { } lastConfirmedAt)
        {
            AddElapsed(runtime.GameId, startedAt, lastConfirmedAt);
            runtime.StartedAtTimestamp = null;
        }
    }

    private void AddElapsed(string gameId, long startedAt, long endedAt)
    {
        var seconds = ElapsedWholeSeconds(startedAt, endedAt);
        if (seconds <= 0)
        {
            return;
        }

        totals.TryGetValue(gameId, out var current);
        totals[gameId] = SaturatingAdd(current, seconds);
        pendingGameIds.Add(gameId);
        pendingTotals = Freeze(totals);
    }

    private void TryPersistPending()
    {
        if (pendingTotals is null)
        {
            return;
        }

        var candidate = pendingTotals;
        var saved = false;
        try
        {
            saved = persist(candidate);
        }
        catch (Exception)
        {
            // Persistence is outside the observation boundary. Keep the
            // in-memory total and retry on the next refresh or close.
        }

        if (!saved)
        {
            return;
        }

        if (ReferenceEquals(pendingTotals, candidate))
        {
            pendingTotals = null;
            pendingGameIds.Clear();
        }
    }

    private long ElapsedWholeSeconds(long startedAt, long endedAt)
    {
        var elapsed = timeProvider.GetElapsedTime(startedAt, endedAt);
        return elapsed <= TimeSpan.Zero
            ? 0
            : elapsed.Ticks / TimeSpan.TicksPerSecond;
    }

    private static long SaturatingAdd(long current, long seconds) =>
        current > long.MaxValue - seconds ? long.MaxValue : current + seconds;

    private static Dictionary<string, long> NormalizeTotals(
        IReadOnlyDictionary<string, long> values)
    {
        var normalized = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!IsSupportedGameId(pair.Key))
            {
                continue;
            }

            normalized[pair.Key] = Math.Max(0, pair.Value);
        }

        return normalized;
    }

    private static bool IsSupportedGameId(string gameId) =>
        GameCatalog.TryGet(gameId, out _)
        || CustomGameId.IsValid(gameId);

    private static IReadOnlyDictionary<string, long> Freeze(
        IReadOnlyDictionary<string, long> values) =>
        new ReadOnlyDictionary<string, long>(
            values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static TimeSpan ToTimeSpan(long seconds)
    {
        if (seconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var maximumWholeSeconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
        return TimeSpan.FromTicks(
            Math.Min(seconds, maximumWholeSeconds) * TimeSpan.TicksPerSecond);
    }

    private sealed class RuntimeState(string gameId)
    {
        public string GameId { get; } = gameId;

        public long? StartedAtTimestamp { get; set; }

        public long? LastConfirmedAtTimestamp { get; set; }
    }
}
