using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Sessions;

/// <summary>
/// Coordinates app-lifetime session state. It has no process, network, filesystem,
/// updater, or elevation implementation; all evidence and launch dispatch arrive
/// through narrow adapter contracts.
/// </summary>
public sealed class GameSessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultAdapterCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultAbsenceConfirmationInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, SessionEntry> entries;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan adapterCallTimeout;
    private readonly TimeSpan launchDispatchTimeout;
    private readonly TimeSpan absenceConfirmationInterval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object admissionSync = new();
    private readonly object disposalSync = new();
    private readonly List<SessionEntry> retiredEntries = [];
    private readonly HashSet<string> customMutationReservations = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource lifetimeCancellationCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IGameSessionCoordinatorHooks? hooks;
    private Task? disposal;
    private TaskCompletionSource? invocationsDrained;
    private int activeInvocations;
    private bool admissionClosed;
    private int cancellationStarted;
    private int stopped;

    public GameSessionCoordinator(
        IEnumerable<IGameSessionAdapter> adapters,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? adapterCallTimeout = null,
        TimeSpan? absenceConfirmationInterval = null,
        TimeSpan? launchDispatchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.startupTimeout = RequirePositive(
            startupTimeout ?? DefaultStartupTimeout,
            nameof(startupTimeout));
        this.adapterCallTimeout = RequirePositive(
            adapterCallTimeout ?? DefaultAdapterCallTimeout,
            nameof(adapterCallTimeout));
        this.launchDispatchTimeout = RequirePositive(
            launchDispatchTimeout ?? this.adapterCallTimeout,
            nameof(launchDispatchTimeout));
        this.absenceConfirmationInterval = RequirePositive(
            absenceConfirmationInterval ?? DefaultAbsenceConfirmationInterval,
            nameof(absenceConfirmationInterval));

        var adapterById = new Dictionary<string, IGameSessionAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!GameCatalog.TryGet(adapter.GameId, out _)
                && !CustomGameId.IsValid(adapter.GameId))
            {
                GameCatalog.GetRequired(adapter.GameId);
            }
            if (!adapterById.TryAdd(adapter.GameId, adapter))
            {
                throw new ArgumentException(
                    $"More than one session adapter was supplied for '{adapter.GameId}'.",
                    nameof(adapters));
            }
        }

        var missingIds = GameCatalog.All
            .Select(game => game.Id)
            .Where(gameId => !adapterById.ContainsKey(gameId))
            .ToArray();
        if (missingIds.Length > 0)
        {
            throw new ArgumentException(
                $"Session adapters are required for: {string.Join(", ", missingIds)}.",
                nameof(adapters));
        }

        entries = new ConcurrentDictionary<string, SessionEntry>(
            adapterById.ToDictionary(
                static pair => pair.Key,
                static pair => new SessionEntry(pair.Key, pair.Value),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    internal GameSessionCoordinator(
        IEnumerable<IGameSessionAdapter> adapters,
        TimeProvider? timeProvider,
        TimeSpan? startupTimeout,
        TimeSpan? adapterCallTimeout,
        TimeSpan? absenceConfirmationInterval,
        IGameSessionCoordinatorHooks hooks)
        : this(
            adapters,
            timeProvider,
            startupTimeout,
            adapterCallTimeout,
            absenceConfirmationInterval)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public GameSessionSnapshot GetSnapshot(string gameId) => Read(GetEntry(gameId));

    public bool TryGetSnapshot(string gameId, out GameSessionSnapshot? snapshot)
    {
        lock (admissionSync)
        {
            if (entries.TryGetValue(gameId, out var entry))
            {
                snapshot = Read(entry);
                return true;
            }
        }

        snapshot = null;
        return false;
    }

    public IReadOnlyDictionary<string, GameSessionSnapshot> GetAllSnapshots()
    {
        lock (admissionSync)
        {
            return new ReadOnlyDictionary<string, GameSessionSnapshot>(
                entries.ToDictionary(
                    static pair => pair.Key,
                    static pair => Read(pair.Value),
                    StringComparer.Ordinal));
        }
    }

    public bool TryRegisterCustomAdapter(IGameSessionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!CustomGameId.IsValid(adapter.GameId))
        {
            return false;
        }

        lock (admissionSync)
        {
            return !admissionClosed
                && !IsStopped
                && !customMutationReservations.Contains(adapter.GameId)
                && entries.TryAdd(adapter.GameId, new SessionEntry(adapter.GameId, adapter));
        }
    }

    public bool TryRemoveCustomAdapter(string gameId)
    {
        if (!CustomGameId.IsValid(gameId))
        {
            return false;
        }

        lock (admissionSync)
        {
            if (admissionClosed
                || IsStopped
                || customMutationReservations.Contains(gameId)
                || !entries.TryGetValue(gameId, out var entry))
            {
                return false;
            }

            lock (entry.Sync)
            {
                if (entry.ActiveOperations > 0
                    || entry.ResumeWorkerRunning
                    || entry.OutstandingObservation is { IsCompleted: false }
                    || entry.OutstandingDispatch is { IsCompleted: false })
                {
                    return false;
                }

                entry.Retiring = true;
            }

            if (!entries.TryRemove(gameId, out _))
            {
                lock (entry.Sync)
                {
                    entry.Retiring = false;
                }

                return false;
            }

            retiredEntries.Add(entry);
            return true;
        }
    }

    /// <summary>
    /// Reserves one exact set of custom-adapter changes without hiding the old
    /// entries. Dispose rolls the reservation back; Commit publishes the whole
    /// replacement set while coordinator admission is exclusively locked.
    /// </summary>
    public bool TryReserveCustomAdapterMutations(
        IReadOnlyDictionary<string, IGameSessionAdapter?> mutations,
        out CustomAdapterMutationLease? lease)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        lease = null;
        if (mutations.Count == 0)
        {
            return false;
        }

        var frozen = mutations.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (frozen.Any(static pair => !CustomGameId.IsValid(pair.Key)
            || pair.Value is not null
                && !string.Equals(pair.Key, pair.Value.GameId, StringComparison.Ordinal)))
        {
            return false;
        }

        lock (admissionSync)
        {
            if (admissionClosed
                || IsStopped
                || frozen.Keys.Any(customMutationReservations.Contains))
            {
                return false;
            }

            var originals = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
            foreach (var gameId in frozen.Keys)
            {
                if (!entries.TryGetValue(gameId, out var entry))
                {
                    continue;
                }

                lock (entry.Sync)
                {
                    if (entry.Retiring
                        || entry.ActiveOperations > 0
                        || entry.ResumeWorkerRunning
                        || entry.OutstandingObservation is { IsCompleted: false }
                        || entry.OutstandingDispatch is { IsCompleted: false })
                    {
                        return false;
                    }
                }

                originals.Add(gameId, entry);
            }

            foreach (var pair in originals)
            {
                lock (pair.Value.Sync)
                {
                    pair.Value.Retiring = true;
                }
            }

            customMutationReservations.UnionWith(frozen.Keys);
            lease = new CustomAdapterMutationLease(
                () => CommitCustomAdapterMutations(frozen, originals),
                () => ReleaseCustomAdapterMutations(frozen, originals));
            return true;
        }
    }

    public async ValueTask<GameLaunchRequestResult> RequestLaunchAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(gameId);
        if (!TryAdmitInvocation())
        {
            return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
        }

        try
        {
            if (IsStopped)
            {
                return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token);
            var gateEntered = await TryEnterGateAsync(entry, linkedCancellation.Token).ConfigureAwait(false);
            if (!gateEntered)
            {
                return Result(CancellationOutcome(cancellationToken), entry);
            }

            try
            {
                if (IsStopped)
                {
                    return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
                }

                ApplyPendingResumeReset(entry);
                var observationResumeGeneration = GetRequestedResumeGeneration(entry);

                var current = Read(entry);
                if (current.Status is LocalGameStatus.Running)
                {
                    return new(GameLaunchRequestOutcome.AlreadyRunning, current);
                }

                if (current.Status is LocalGameStatus.Starting)
                {
                    return new(GameLaunchRequestOutcome.AlreadyStarting, current);
                }

                var observation = await ObserveAsync(
                    entry,
                    linkedCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
                if (observation.Status is ObservationAttemptStatus.CallerCanceled)
                {
                    return Result(GameLaunchRequestOutcome.Canceled, entry);
                }

                if (observation.Status is ObservationAttemptStatus.CoordinatorStopped)
                {
                    return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
                }

                if (observation.Status is ObservationAttemptStatus.Unavailable)
                {
                    if (!TryApplyUnavailableEvidence(
                            entry,
                            observationResumeGeneration,
                            out var unavailable))
                    {
                        ApplyPendingResumeReset(entry);
                        return Result(GameLaunchRequestOutcome.NeedsReview, entry);
                    }

                    return new(GameLaunchRequestOutcome.NeedsReview, unavailable);
                }

                if (!TryApplyEvidence(
                        entry,
                        observation.Evidence!,
                        observationResumeGeneration,
                        out var observed))
                {
                    ApplyPendingResumeReset(entry);
                    return Result(GameLaunchRequestOutcome.NeedsReview, entry);
                }

                if (observation.Evidence!.Overall is ExactProcessPresence.Present)
                {
                    return new(GameLaunchRequestOutcome.AlreadyRunning, observed);
                }

                if (observation.Evidence.Overall is ExactProcessPresence.Uncertain
                    || observed.Status is LocalGameStatus.NeedsReview)
                {
                    return new(GameLaunchRequestOutcome.NeedsReview, observed);
                }

                if (observed.Status is LocalGameStatus.NotFound)
                {
                    return new(GameLaunchRequestOutcome.NotReady, observed);
                }

                if (observed.Readiness is not LocalReadinessEvidence.Ready)
                {
                    return new(GameLaunchRequestOutcome.NotReady, observed);
                }

                if (hooks is not null)
                {
                    await hooks.BeforeDispatchAdmissionAsync().ConfigureAwait(false);
                }

                var admission = AdmitDispatchAtomically(
                    entry,
                    observationResumeGeneration,
                    linkedCancellation.Token,
                    cancellationToken);
                if (!admission.Admitted)
                {
                    ApplyPendingResumeReset(entry);
                    return Result(admission.Outcome, entry);
                }

                return await AwaitAdmittedDispatchAsync(
                    entry,
                    admission.DispatchTask,
                    linkedCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntryOperation(entry);
            }
        }
        finally
        {
            ReleaseInvocation();
        }
    }

    public async ValueTask<GameSessionSnapshot> RefreshAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(gameId);
        if (!TryAdmitInvocation())
        {
            return Read(entry);
        }

        try
        {
            if (IsStopped)
            {
                return Read(entry);
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token);
            var gateEntered = await TryEnterGateAsync(entry, linkedCancellation.Token).ConfigureAwait(false);
            if (!gateEntered)
            {
                return Read(entry);
            }

            try
            {
                if (IsStopped)
                {
                    return Read(entry);
                }

                ApplyPendingResumeReset(entry);
                var observationResumeGeneration = GetRequestedResumeGeneration(entry);

                var observation = await ObserveAsync(
                    entry,
                    linkedCancellation.Token,
                    cancellationToken).ConfigureAwait(false);
                if (observation.Status is ObservationAttemptStatus.Succeeded && !IsStopped)
                {
                    if (TryApplyEvidence(
                            entry,
                            observation.Evidence!,
                            observationResumeGeneration,
                            out var observed))
                    {
                        return observed;
                    }

                    ApplyPendingResumeReset(entry);
                    return Read(entry);
                }

                if (observation.Status is ObservationAttemptStatus.Unavailable && !IsStopped)
                {
                    if (TryApplyUnavailableEvidence(
                            entry,
                            observationResumeGeneration,
                            out var unavailable))
                    {
                        return unavailable;
                    }

                    ApplyPendingResumeReset(entry);
                }

                return Read(entry);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntryOperation(entry);
            }
        }
        finally
        {
            ReleaseInvocation();
        }
    }

    /// <summary>
    /// Refreshes all games concurrently. Each adapter wait and gate wait is bounded,
    /// so one non-cooperative game returns an unavailable snapshot instead of starving
    /// the other games or the aggregate result.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>> RefreshAllAsync(
        CancellationToken cancellationToken = default)
    {
        string[] gameIds;
        lock (admissionSync)
        {
            gameIds = entries.Keys.ToArray();
        }

        var refreshes = gameIds
            .Select(async gameId => new KeyValuePair<string, GameSessionSnapshot>(
                gameId,
                await RefreshAsync(gameId, cancellationToken).ConfigureAwait(false)))
            .ToArray();
        var results = await Task.WhenAll(refreshes).ConfigureAwait(false);
        return new ReadOnlyDictionary<string, GameSessionSnapshot>(
            results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public ValueTask ResetAfterSystemResumeAsync(CancellationToken cancellationToken = default)
    {
        if (IsStopped || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var entry in entries.Values)
        {
            RequestResumeReset(entry);
        }

        return ValueTask.CompletedTask;
    }

    public void Shutdown()
    {
        var startCancellation = false;
        lock (admissionSync)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            admissionClosed = true;
            foreach (var entry in GetEntriesUnsafe())
            {
                lock (entry.Sync)
                {
                    entry.Snapshot = entry.Snapshot with { CoordinatorStopped = true };
                }
            }

            startCancellation = true;
        }

        if (startCancellation && Interlocked.Exchange(ref cancellationStarted, 1) == 0)
        {
            _ = CancelLifetimeAndSignalAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private bool IsStopped => Volatile.Read(ref stopped) != 0;

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private async Task CancelLifetimeAndSignalAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Adapter cancellation callbacks are outside Core's trust boundary.
            // State is already stopped, and callback faults must not escape Shutdown.
        }
        finally
        {
            lifetimeCancellationCompleted.TrySetResult();
        }
    }

    private async Task DisposeCoreAsync()
    {
        Shutdown();
        var drain = CloseAdmission();

        await lifetimeCancellationCompleted.Task.ConfigureAwait(false);
        await drain.ConfigureAwait(false);

        var entriesToDispose = GetEntriesForDisposal();
        var resumeWorkers = entriesToDispose
            .Select(static entry => entry.ResumeWorkerTask)
            .Where(static task => task is not null)
            .Select(static task => task!)
            .ToArray();
        if (resumeWorkers.Length > 0)
        {
            await Task.WhenAll(resumeWorkers).ConfigureAwait(false);
        }

        foreach (var entry in entriesToDispose)
        {
            entry.Gate.Dispose();
        }

        lifetime.Dispose();
    }

    private SessionEntry[] GetEntriesForDisposal()
    {
        lock (admissionSync)
        {
            return GetEntriesUnsafe().ToArray();
        }
    }

    private IEnumerable<SessionEntry> GetEntriesUnsafe() =>
        entries.Values.Concat(retiredEntries);

    private bool TryAdmitInvocation()
    {
        lock (admissionSync)
        {
            if (admissionClosed)
            {
                return false;
            }

            activeInvocations++;
            return true;
        }
    }

    private void ReleaseInvocation()
    {
        TaskCompletionSource? drained = null;
        lock (admissionSync)
        {
            activeInvocations--;
            if (activeInvocations == 0 && admissionClosed)
            {
                drained = invocationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private Task CloseAdmission()
    {
        lock (admissionSync)
        {
            admissionClosed = true;
            if (activeInvocations == 0)
            {
                return Task.CompletedTask;
            }

            invocationsDrained ??= new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return invocationsDrained.Task;
        }
    }

    private async ValueTask<bool> TryEnterGateAsync(
        SessionEntry entry,
        CancellationToken cancellationToken)
    {
        if (!TryReserveEntryOperation(entry))
        {
            return false;
        }

        try
        {
            var entered = await entry.Gate
                .WaitAsync(adapterCallTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!entered)
            {
                ReleaseEntryOperation(entry);
            }

            return entered;
        }
        catch (OperationCanceledException)
        {
            ReleaseEntryOperation(entry);
            return false;
        }
    }

    private bool TryReserveEntryOperation(SessionEntry entry)
    {
        lock (admissionSync)
        {
            lock (entry.Sync)
            {
                if (entry.Retiring)
                {
                    return false;
                }

                entry.ActiveOperations++;
                return true;
            }
        }
    }

    private void ReleaseEntryOperation(SessionEntry entry)
    {
        lock (admissionSync)
        {
            lock (entry.Sync)
            {
                entry.ActiveOperations = Math.Max(0, entry.ActiveOperations - 1);
            }
        }
    }

    private GameLaunchRequestOutcome CancellationOutcome(CancellationToken callerToken) =>
        IsStopped || lifetime.IsCancellationRequested
            ? GameLaunchRequestOutcome.CoordinatorStopped
            : callerToken.IsCancellationRequested
                ? GameLaunchRequestOutcome.Canceled
                : GameLaunchRequestOutcome.NeedsReview;

    private async ValueTask<ObservationAttempt> ObserveAsync(
        SessionEntry entry,
        CancellationToken linkedCancellation,
        CancellationToken callerCancellation)
    {
        Task<GameSessionEvidence>? observationTask = null;
        lock (entry.Sync)
        {
            if (entry.OutstandingObservation is { } outstanding)
            {
                if (!outstanding.IsCompleted) return ObservationAttempt.Unavailable;
                observationTask = outstanding;
                entry.OutstandingObservation = null;
            }
        }

        if (observationTask is null)
        {
            try
            {
                observationTask = entry.Adapter.ObserveSessionAsync(linkedCancellation).AsTask();
            }
            catch (OperationCanceledException) when (IsStopped || linkedCancellation.IsCancellationRequested)
            {
                return IsStopped
                    ? ObservationAttempt.CoordinatorStopped
                    : callerCancellation.IsCancellationRequested
                        ? ObservationAttempt.CallerCanceled
                        : ObservationAttempt.Unavailable;
            }
            catch (OperationCanceledException)
            {
                return ObservationAttempt.Unavailable;
            }
            catch (Exception)
            {
                return ObservationAttempt.Unavailable;
            }

            TrackOutstandingObservation(entry, observationTask);
        }

        try
        {
            var evidence = await observationTask
                .WaitAsync(adapterCallTimeout, linkedCancellation)
                .ConfigureAwait(false);
            ClearOutstandingObservation(entry, observationTask);
            return ObservationAttempt.Succeeded(evidence);
        }
        catch (TimeoutException)
        {
            return ObservationAttempt.Unavailable;
        }
        catch (OperationCanceledException) when (IsStopped)
        {
            ClearOutstandingObservation(entry, observationTask);
            return ObservationAttempt.CoordinatorStopped;
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            ClearOutstandingObservation(entry, observationTask);
            return ObservationAttempt.CallerCanceled;
        }
        catch (OperationCanceledException)
        {
            ClearOutstandingObservation(entry, observationTask);
            return ObservationAttempt.Unavailable;
        }
        catch (Exception)
        {
            ClearOutstandingObservation(entry, observationTask);
            return ObservationAttempt.Unavailable;
        }
    }

    private static void TrackOutstandingObservation(
        SessionEntry entry,
        Task<GameSessionEvidence> observationTask)
    {
        lock (entry.Sync)
        {
            entry.OutstandingObservation = observationTask;
        }

        _ = observationTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ClearOutstandingObservation(
        SessionEntry entry,
        Task<GameSessionEvidence> observationTask)
    {
        lock (entry.Sync)
        {
            if (ReferenceEquals(entry.OutstandingObservation, observationTask))
                entry.OutstandingObservation = null;
        }
    }

    private DispatchAdmission AdmitDispatchAtomically(
        SessionEntry entry,
        long expectedResumeGeneration,
        CancellationToken linkedCancellation,
        CancellationToken callerCancellation)
    {
        lock (admissionSync)
        {
            if (IsStopped || lifetime.IsCancellationRequested)
            {
                return DispatchAdmission.Rejected(GameLaunchRequestOutcome.CoordinatorStopped);
            }

            if (linkedCancellation.IsCancellationRequested)
            {
                return DispatchAdmission.Rejected(
                    callerCancellation.IsCancellationRequested
                        ? GameLaunchRequestOutcome.Canceled
                        : GameLaunchRequestOutcome.NeedsReview);
            }

            if (GetRequestedResumeGeneration(entry) != expectedResumeGeneration)
            {
                return DispatchAdmission.Rejected(GameLaunchRequestOutcome.NeedsReview);
            }

            SetDispatchReconciliation(entry);
            try
            {
                hooks?.DispatchAdmissionCommitted(entry.Snapshot.GameId);
                var dispatchTask = entry.Adapter
                    .RequestValidatedLaunchAsync(linkedCancellation)
                    .AsTask();
                TrackOutstandingDispatch(entry, dispatchTask);
                return DispatchAdmission.Accepted(dispatchTask);
            }
            catch (Exception)
            {
                // Adapter entry occurred. Even a synchronous exception cannot prove
                // that the sealed dispatch produced no external side effect.
                return DispatchAdmission.Accepted(dispatchTask: null);
            }
        }
    }

    private async ValueTask<GameLaunchRequestResult> AwaitAdmittedDispatchAsync(
        SessionEntry entry,
        Task<GameLaunchDispatchResult>? dispatchTask,
        CancellationToken linkedCancellation,
        CancellationToken callerCancellation)
    {
        if (dispatchTask is null)
        {
            return IsStopped
                ? Result(GameLaunchRequestOutcome.CoordinatorStopped, entry)
                : Result(GameLaunchRequestOutcome.Reconciling, entry);
        }

        try
        {
            var dispatchTimeout = entry.Adapter.LaunchDispatchTimeout ?? launchDispatchTimeout;
            var dispatch = await dispatchTask
                .WaitAsync(dispatchTimeout, linkedCancellation)
                .ConfigureAwait(false);
            if (IsStopped)
            {
                return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
            }

            return dispatch.Status switch
            {
                GameLaunchDispatchStatus.Accepted => SetDispatchAccepted(entry),
                GameLaunchDispatchStatus.AlreadyRunning => SetAlreadyRunning(entry),
                GameLaunchDispatchStatus.NeedsReview => SetNeedsReview(entry),
                GameLaunchDispatchStatus.Failed => SetLaunchFailed(entry),
                _ => SetLaunchFailed(entry),
            };
        }
        catch (TimeoutException)
        {
            return Result(GameLaunchRequestOutcome.Reconciling, entry);
        }
        catch (OperationCanceledException) when (IsStopped)
        {
            return Result(GameLaunchRequestOutcome.CoordinatorStopped, entry);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            return Result(GameLaunchRequestOutcome.Reconciling, entry);
        }
        catch (OperationCanceledException)
        {
            return Result(GameLaunchRequestOutcome.Reconciling, entry);
        }
        catch (Exception)
        {
            return Result(GameLaunchRequestOutcome.Reconciling, entry);
        }
    }

    private static void TrackOutstandingDispatch(
        SessionEntry entry,
        Task<GameLaunchDispatchResult> dispatchTask)
    {
        lock (entry.Sync)
        {
            entry.OutstandingDispatch = dispatchTask;
        }

        _ = dispatchTask.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (entry.Sync)
                {
                    if (ReferenceEquals(entry.OutstandingDispatch, completed))
                    {
                        entry.OutstandingDispatch = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private SessionEntry GetEntry(string gameId)
    {
        lock (admissionSync)
        {
            if (entries.TryGetValue(gameId, out var entry))
            {
                return entry;
            }
        }

        GameCatalog.GetRequired(gameId);
        throw new InvalidOperationException($"No session adapter exists for '{gameId}'.");
    }

    private static GameSessionSnapshot Read(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            return entry.Snapshot;
        }
    }

    private static GameLaunchRequestResult Result(
        GameLaunchRequestOutcome outcome,
        SessionEntry entry) => new(outcome, Read(entry));

    private void SetDispatchReconciliation(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Snapshot = ClearAbsence(entry.Snapshot) with
            {
                Status = LocalGameStatus.Starting,
                WasBootstrapObserved = false,
                WasRuntimeObserved = false,
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
                LaunchRequestedAt = timeProvider.GetUtcNow(),
                BootstrapObservedAt = null,
                LastLaunchDetectionDuration = null,
                FailureReason = GameSessionFailureReason.LaunchOutcomeUncertain,
            };
        }
    }

    private static GameLaunchRequestResult SetDispatchAccepted(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Snapshot = entry.Snapshot with
            {
                Status = LocalGameStatus.Starting,
                CurrentSessionLaunchedByNyx = true,
                FailureReason = GameSessionFailureReason.None,
            };
            return new(GameLaunchRequestOutcome.Accepted, entry.Snapshot);
        }
    }

    private static GameLaunchRequestResult SetAlreadyRunning(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Snapshot = ClearAbsence(entry.Snapshot) with
            {
                Status = LocalGameStatus.Running,
                LastProcessEvidence = ExactProcessPresence.Uncertain,
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                WasBootstrapObserved = false,
                WasRuntimeObserved = false,
                CurrentSessionLaunchedByNyx = false,
                LaunchRequestedAt = null,
                BootstrapObservedAt = null,
                FailureReason = GameSessionFailureReason.EvidenceUnavailable,
            };
            return new(GameLaunchRequestOutcome.AlreadyRunning, entry.Snapshot);
        }
    }

    private static GameLaunchRequestResult SetNeedsReview(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Snapshot = ClearAbsence(entry.Snapshot) with
            {
                Status = LocalGameStatus.NeedsReview,
                WasBootstrapObserved = false,
                WasRuntimeObserved = false,
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
                LaunchRequestedAt = null,
                BootstrapObservedAt = null,
                FailureReason = GameSessionFailureReason.LaunchNeedsReview,
            };
            return new(GameLaunchRequestOutcome.NeedsReview, entry.Snapshot);
        }
    }

    private static GameLaunchRequestResult SetLaunchFailed(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Snapshot = ClearAbsence(entry.Snapshot) with
            {
                Status = LocalGameStatus.LaunchFailed,
                WasBootstrapObserved = false,
                WasRuntimeObserved = false,
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
                LaunchRequestedAt = null,
                BootstrapObservedAt = null,
                FailureReason = GameSessionFailureReason.LaunchDispatchFailed,
            };
            return new(GameLaunchRequestOutcome.Failed, entry.Snapshot);
        }
    }

    private static long GetRequestedResumeGeneration(SessionEntry entry)
    {
        lock (entry.Sync)
        {
            return entry.RequestedResumeGeneration;
        }
    }

    private bool TryApplyEvidence(
        SessionEntry entry,
        GameSessionEvidence evidence,
        long expectedResumeGeneration,
        out GameSessionSnapshot snapshot)
    {
        lock (entry.Sync)
        {
            if (entry.RequestedResumeGeneration != expectedResumeGeneration)
            {
                snapshot = entry.Snapshot;
                return false;
            }

            var current = entry.Snapshot with
            {
                Readiness = evidence.Readiness,
                ObservationGeneration = ++entry.ObservationGeneration,
                LastExactObservationTimestamp = timeProvider.GetTimestamp(),
            };
            entry.Snapshot = evidence.Overall switch
            {
                ExactProcessPresence.Present => ApplyPresentEvidence(current, evidence),
                ExactProcessPresence.Uncertain => ApplyUncertainEvidence(current),
                ExactProcessPresence.Absent => ApplyAbsentEvidence(entry, current),
                _ => ApplyUncertainEvidence(current),
            };
            snapshot = entry.Snapshot;
            return true;
        }
    }

    private GameSessionSnapshot ApplyPresentEvidence(
        GameSessionSnapshot current,
        GameSessionEvidence evidence)
    {
        var runtimeFirstConfirmed = !current.WasRuntimeObserved
            && evidence.Runtime is ExactProcessPresence.Present;
        var runtimeObserved = current.WasRuntimeObserved || runtimeFirstConfirmed;
        var bootstrapObserved = current.WasBootstrapObserved
            || evidence.Bootstrap is ExactProcessPresence.Present;
        var sessionLaunchedByNyx = current.CurrentSessionLaunchedByNyx
            && (!current.WasRuntimeObserved
                || evidence.Runtime is ExactProcessPresence.Present);
        var failureReason = current.FailureReason is GameSessionFailureReason.LaunchNeedsReview
            ? current.FailureReason
            : evidence.Readiness is LocalReadinessEvidence.Ready
                ? GameSessionFailureReason.None
                : evidence.Readiness is LocalReadinessEvidence.NotFound
                    ? GameSessionFailureReason.EvidenceConflict
                    : GameSessionFailureReason.LocalReadinessUnavailable;

        return ClearAbsence(current) with
        {
            Status = LocalGameStatus.Running,
            LastProcessEvidence = ExactProcessPresence.Present,
            CurrentRuntimeEvidence = evidence.Runtime,
            CurrentSessionLaunchedByNyx = sessionLaunchedByNyx,
            WasBootstrapObserved = bootstrapObserved,
            WasRuntimeObserved = runtimeObserved,
            LaunchRequestedAt = runtimeObserved ? null : current.LaunchRequestedAt,
            BootstrapObservedAt = bootstrapObserved && !runtimeObserved
                ? current.BootstrapObservedAt ?? timeProvider.GetUtcNow()
                : current.BootstrapObservedAt,
            LastLaunchDetectionDuration = runtimeFirstConfirmed
                && current.LaunchRequestedAt is { } requestedAt
                    ? timeProvider.GetUtcNow() - requestedAt
                    : current.LastLaunchDetectionDuration,
            FailureReason = failureReason,
        };
    }

    private static GameSessionSnapshot ApplyUncertainEvidence(GameSessionSnapshot current)
    {
        var status = current.Status is LocalGameStatus.Starting or LocalGameStatus.Running
            ? current.Status
            : LocalGameStatus.NeedsReview;
        return ClearAbsence(current) with
        {
            Status = status,
            LastProcessEvidence = ExactProcessPresence.Uncertain,
            CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
            LastExactObservationTimestamp = null,
            CurrentSessionLaunchedByNyx = false,
            FailureReason = current.FailureReason is GameSessionFailureReason.LaunchNeedsReview
                ? current.FailureReason
                : GameSessionFailureReason.EvidenceUnavailable,
        };
    }

    private static bool TryApplyUnavailableEvidence(
        SessionEntry entry,
        long expectedResumeGeneration,
        out GameSessionSnapshot snapshot)
    {
        lock (entry.Sync)
        {
            if (entry.RequestedResumeGeneration != expectedResumeGeneration)
            {
                snapshot = entry.Snapshot;
                return false;
            }

            var current = entry.Snapshot;
            var status = current.Status is LocalGameStatus.Starting or LocalGameStatus.Running
                ? current.Status
                : LocalGameStatus.NeedsReview;
            entry.Snapshot = ClearAbsence(current) with
            {
                Status = status,
                LastProcessEvidence = ExactProcessPresence.Uncertain,
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
                FailureReason = current.FailureReason is GameSessionFailureReason.LaunchNeedsReview
                    ? current.FailureReason
                    : GameSessionFailureReason.EvidenceUnavailable,
            };
            snapshot = entry.Snapshot;
            return true;
        }
    }

    private GameSessionSnapshot ApplyAbsentEvidence(
        SessionEntry entry,
        GameSessionSnapshot current)
    {
        current = current with { LastProcessEvidence = ExactProcessPresence.Absent, CurrentRuntimeEvidence = ExactProcessPresence.Absent };
        var now = timeProvider.GetUtcNow();

        if (current.Status is LocalGameStatus.Starting)
        {
            var timedOut = current.LaunchRequestedAt is { } requestedAt
                && now - requestedAt >= startupTimeout;
            if (timedOut && !HasOutstandingDispatch(entry))
            {
                return ClearAbsence(current) with
                {
                    Status = LocalGameStatus.LaunchFailed,
                    WasBootstrapObserved = false,
                    WasRuntimeObserved = false,
                    CurrentSessionLaunchedByNyx = false,
                    LaunchRequestedAt = null,
                    BootstrapObservedAt = null,
                    FailureReason = GameSessionFailureReason.StartupTimedOut,
                };
            }

            return ClearAbsence(current);
        }

        if (current.Status is LocalGameStatus.Running)
        {
            if (current.WasRuntimeObserved)
            {
                return ApplyRuntimeAbsence(current, now);
            }

            if (current.WasBootstrapObserved)
            {
                var handoffTimedOut = current.BootstrapObservedAt is { } bootstrapAt
                    && now - bootstrapAt >= startupTimeout;
                return handoffTimedOut && !HasOutstandingDispatch(entry)
                    ? ClearAbsence(current) with
                    {
                        Status = LocalGameStatus.LaunchFailed,
                        WasBootstrapObserved = false,
                        CurrentSessionLaunchedByNyx = false,
                        LaunchRequestedAt = null,
                        BootstrapObservedAt = null,
                        FailureReason = GameSessionFailureReason.StartupTimedOut,
                    }
                    : ClearAbsence(current);
            }
        }

        if (current.FailureReason is GameSessionFailureReason.LaunchNeedsReview)
        {
            return ClearAbsence(current) with { Status = LocalGameStatus.NeedsReview };
        }

        if (current.Readiness is LocalReadinessEvidence.NotFound)
        {
            return ResetToIdle(current, LocalGameStatus.NotFound);
        }

        if (current.Readiness is LocalReadinessEvidence.NeedsReview
            or LocalReadinessEvidence.Unknown)
        {
            return ResetToIdle(current, LocalGameStatus.NeedsReview) with
            {
                FailureReason = GameSessionFailureReason.LocalReadinessUnavailable,
            };
        }

        if (current.Status is LocalGameStatus.LaunchFailed)
        {
            return ClearAbsence(current);
        }

        return ResetToIdle(current, LocalGameStatus.Ready);
    }

    private GameSessionSnapshot ApplyRuntimeAbsence(
        GameSessionSnapshot current,
        DateTimeOffset observedAt)
    {
        if (current.FirstAbsentAt is null || current.FirstAbsentGeneration is null)
        {
            return current with
            {
                ConsecutiveAbsentSamples = 1,
                FirstAbsentAt = observedAt,
                FirstAbsentGeneration = current.ObservationGeneration,
                CurrentSessionLaunchedByNyx = false,
            };
        }

        var separatedByGeneration = current.ObservationGeneration
            > current.FirstAbsentGeneration.Value;
        var separatedByTime = observedAt - current.FirstAbsentAt.Value
            >= absenceConfirmationInterval;
        if (!separatedByGeneration || !separatedByTime)
        {
            return current with { ConsecutiveAbsentSamples = 1 };
        }

        var idleStatus = current.FailureReason is GameSessionFailureReason.LaunchNeedsReview
            ? LocalGameStatus.NeedsReview
            : current.Readiness switch
            {
                LocalReadinessEvidence.Ready => LocalGameStatus.Ready,
                LocalReadinessEvidence.NotFound => LocalGameStatus.NotFound,
                _ => LocalGameStatus.NeedsReview,
            };
        var idle = ResetToIdle(current, idleStatus) with
        {
            LastCloseDetectionDuration = observedAt - current.FirstAbsentAt.Value,
        };
        return current.FailureReason is GameSessionFailureReason.LaunchNeedsReview
            ? idle with { FailureReason = GameSessionFailureReason.LaunchNeedsReview }
            : idle;
    }

    private static GameSessionSnapshot ResetToIdle(
        GameSessionSnapshot current,
        LocalGameStatus status) => ClearAbsence(current) with
        {
            Status = status,
            WasBootstrapObserved = false,
            WasRuntimeObserved = false,
            CurrentRuntimeEvidence = ExactProcessPresence.Absent,
            CurrentSessionLaunchedByNyx = false,
            LaunchRequestedAt = null,
            BootstrapObservedAt = null,
            FailureReason = status is LocalGameStatus.NeedsReview
                ? current.FailureReason
                : GameSessionFailureReason.None,
        };

    private static GameSessionSnapshot ClearAbsence(GameSessionSnapshot current) => current with
    {
        ConsecutiveAbsentSamples = 0,
        FirstAbsentGeneration = null,
        FirstAbsentAt = null,
    };

    private static bool HasOutstandingDispatch(SessionEntry entry) =>
        entry.OutstandingDispatch is { IsCompleted: false };

    private void RequestResumeReset(SessionEntry entry)
    {
        hooks?.BeforeResumeAdmission(entry.Snapshot.GameId);
        if (!TryAdmitInvocation())
        {
            return;
        }

        TaskCompletionSource? workerCompletion = null;
        try
        {
            lock (admissionSync)
            {
                lock (entry.Sync)
                {
                    if (IsStopped || entry.Retiring)
                    {
                        return;
                    }

                    var requested = entry.RequestedResumeGeneration + 1;
                    entry.RequestedResumeGeneration = requested;
                    entry.Snapshot = entry.Snapshot with { RequestedResumeGeneration = requested };
                    if (!entry.ResumeWorkerRunning)
                    {
                        entry.ResumeWorkerRunning = true;
                        workerCompletion = new(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        entry.ResumeWorkerTask = workerCompletion.Task;
                    }
                }
            }

            if (workerCompletion is not null)
            {
                _ = ProcessPendingResumeResetsAsync(entry, workerCompletion);
            }
        }
        finally
        {
            ReleaseInvocation();
        }
    }

    private void CommitCustomAdapterMutations(
        IReadOnlyDictionary<string, IGameSessionAdapter?> mutations,
        IReadOnlyDictionary<string, SessionEntry> originals)
    {
        lock (admissionSync)
        {
            foreach (var pair in mutations)
            {
                if (originals.TryGetValue(pair.Key, out var original))
                {
                    _ = entries.TryRemove(
                        new KeyValuePair<string, SessionEntry>(pair.Key, original));
                    retiredEntries.Add(original);
                }

                if (pair.Value is not null)
                {
                    entries[pair.Key] = new SessionEntry(pair.Key, pair.Value);
                }
            }

            customMutationReservations.ExceptWith(mutations.Keys);
        }
    }

    private void ReleaseCustomAdapterMutations(
        IReadOnlyDictionary<string, IGameSessionAdapter?> mutations,
        IReadOnlyDictionary<string, SessionEntry> originals)
    {
        lock (admissionSync)
        {
            foreach (var pair in originals)
            {
                lock (pair.Value.Sync)
                {
                    pair.Value.Retiring = false;
                }
            }

            customMutationReservations.ExceptWith(mutations.Keys);
        }
    }

    private async Task ProcessPendingResumeResetsAsync(
        SessionEntry entry,
        TaskCompletionSource workerCompletion)
    {
        try
        {
            while (!IsStopped)
            {
                if (!TryReserveEntryOperation(entry))
                {
                    break;
                }

                var entered = false;
                try
                {
                    await entry.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    entered = true;
                }
                catch (OperationCanceledException)
                {
                    ReleaseEntryOperation(entry);
                    break;
                }

                try
                {
                    ApplyPendingResumeReset(entry);
                }
                finally
                {
                    if (entered)
                    {
                        entry.Gate.Release();
                    }

                    ReleaseEntryOperation(entry);
                }

                lock (entry.Sync)
                {
                    if (entry.AppliedResumeGeneration >= entry.RequestedResumeGeneration)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception)
        {
            // The durable request remains visible. A later foreground operation or
            // resume event can apply it; background worker faults never escape.
        }
        finally
        {
            lock (entry.Sync)
            {
                entry.ResumeWorkerRunning = false;
            }

            workerCompletion.TrySetResult();
        }
    }

    private void ApplyPendingResumeReset(SessionEntry entry)
    {
        long appliedGeneration;
        lock (entry.Sync)
        {
            if (entry.AppliedResumeGeneration >= entry.RequestedResumeGeneration)
            {
                return;
            }

            var current = entry.Snapshot;
            var generationDelta = entry.RequestedResumeGeneration - entry.AppliedResumeGeneration;
            entry.ObservationGeneration += generationDelta;
            entry.AppliedResumeGeneration = entry.RequestedResumeGeneration;
            appliedGeneration = entry.AppliedResumeGeneration;
            entry.Snapshot = ClearAbsence(current) with
            {
                LastProcessEvidence = current.Status is LocalGameStatus.Starting or LocalGameStatus.Running
                    ? ExactProcessPresence.Uncertain
                    : current.LastProcessEvidence,
                CurrentRuntimeEvidence = current.Status is LocalGameStatus.Starting or LocalGameStatus.Running
                    ? ExactProcessPresence.Uncertain
                    : current.CurrentRuntimeEvidence,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
                ObservationGeneration = entry.ObservationGeneration,
                RequestedResumeGeneration = entry.RequestedResumeGeneration,
                AppliedResumeGeneration = entry.AppliedResumeGeneration,
                LaunchRequestedAt = current.Status is LocalGameStatus.Starting
                    ? timeProvider.GetUtcNow()
                    : current.LaunchRequestedAt,
                BootstrapObservedAt = current.Status is LocalGameStatus.Running
                    && current.WasBootstrapObserved
                    && !current.WasRuntimeObserved
                        ? timeProvider.GetUtcNow()
                        : current.BootstrapObservedAt,
            };
        }

        try
        {
            hooks?.ResumeResetApplied(entry.Snapshot.GameId, appliedGeneration);
        }
        catch (Exception)
        {
            // Internal verification hooks cannot alter Core state semantics.
        }
    }

    private enum ObservationAttemptStatus
    {
        Succeeded,
        Unavailable,
        CallerCanceled,
        CoordinatorStopped,
    }

    private sealed record ObservationAttempt(
        ObservationAttemptStatus Status,
        GameSessionEvidence? Evidence = null)
    {
        public static ObservationAttempt Unavailable { get; } = new(ObservationAttemptStatus.Unavailable);

        public static ObservationAttempt CallerCanceled { get; } = new(ObservationAttemptStatus.CallerCanceled);

        public static ObservationAttempt CoordinatorStopped { get; } = new(ObservationAttemptStatus.CoordinatorStopped);

        public static ObservationAttempt Succeeded(GameSessionEvidence evidence) =>
            new(ObservationAttemptStatus.Succeeded, evidence);
    }

    private sealed record DispatchAdmission(
        bool Admitted,
        GameLaunchRequestOutcome Outcome,
        Task<GameLaunchDispatchResult>? DispatchTask)
    {
        public static DispatchAdmission Accepted(Task<GameLaunchDispatchResult>? dispatchTask) =>
            new(true, GameLaunchRequestOutcome.Reconciling, dispatchTask);

        public static DispatchAdmission Rejected(GameLaunchRequestOutcome outcome) =>
            new(false, outcome, DispatchTask: null);
    }

    public sealed class CustomAdapterMutationLease : IDisposable
    {
        private readonly object sync = new();
        private readonly Action commit;
        private readonly Action release;
        private bool completed;

        internal CustomAdapterMutationLease(Action commit, Action release)
        {
            this.commit = commit;
            this.release = release;
        }

        public void Commit()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(completed, this);
                commit();
                completed = true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (completed)
                {
                    return;
                }

                release();
                completed = true;
            }
        }
    }

    private sealed class SessionEntry
    {
        public SessionEntry(string gameId, IGameSessionAdapter adapter)
        {
            Adapter = adapter;
            Snapshot = new(
                gameId,
                LocalReadinessEvidence.Unknown,
                LocalGameStatus.NeedsReview,
                ExactProcessPresence.Uncertain,
                WasBootstrapObserved: false,
                WasRuntimeObserved: false,
                ConsecutiveAbsentSamples: 0,
                ObservationGeneration: 0,
                FirstAbsentGeneration: null,
                FirstAbsentAt: null,
                LaunchRequestedAt: null,
                BootstrapObservedAt: null,
                RequestedResumeGeneration: 0,
                AppliedResumeGeneration: 0,
                GameSessionFailureReason.LocalReadinessUnavailable,
                CoordinatorStopped: false)
            {
                CurrentRuntimeEvidence = ExactProcessPresence.Uncertain,
                LastExactObservationTimestamp = null,
                CurrentSessionLaunchedByNyx = false,
            };
        }

        public object Sync { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IGameSessionAdapter Adapter { get; }

        public GameSessionSnapshot Snapshot { get; set; }

        public long ObservationGeneration { get; set; }

        public Task<GameSessionEvidence>? OutstandingObservation { get; set; }

        public Task<GameLaunchDispatchResult>? OutstandingDispatch { get; set; }

        public bool Retiring { get; set; }

        public int ActiveOperations { get; set; }

        public long RequestedResumeGeneration { get; set; }

        public long AppliedResumeGeneration { get; set; }

        public bool ResumeWorkerRunning { get; set; }

        public Task? ResumeWorkerTask { get; set; }
    }
}

internal interface IGameSessionCoordinatorHooks
{
    ValueTask BeforeDispatchAdmissionAsync();

    void DispatchAdmissionCommitted(string gameId);

    void BeforeResumeAdmission(string gameId);

    void ResumeResetApplied(string gameId, long generation);
}
