using System.Collections.ObjectModel;
using System.Text.Json;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Core.Exports;

[Flags]
public enum ExportKind
{
    None = 0,
    Pulls = 1,
    Achievements = 2,
}

public enum ExportProviderStatus
{
    Unsupported,
    Ready,
    Completed,
    Failed,
    Canceled,
}

public sealed record ExportProviderCapability(string GameId, ExportKind SupportedKinds)
{
    public bool Supports(ExportKind kind) => (SupportedKinds & kind) == kind;
}

public static partial class AchievementCatalogVersions
{
    public static string Get(string gameId) => gameId switch
    {
        "gi" => Genshin,
        "hsr" => StarRail,
        _ => throw new ExportProviderException("output-invalid"),
    };
}

/// <summary>Capability slots are explicit; unsupported lanes never invoke a provider.</summary>
public static class ExportProviderCatalog
{
    private static readonly IReadOnlyDictionary<string, ExportProviderCapability> Slots =
        new ReadOnlyDictionary<string, ExportProviderCapability>(new Dictionary<string, ExportProviderCapability>(StringComparer.Ordinal)
        {
            ["gi"] = new("gi", ExportKind.Pulls | ExportKind.Achievements),
            ["hsr"] = new("hsr", ExportKind.Pulls | ExportKind.Achievements),
            ["zzz"] = new("zzz", ExportKind.Pulls),
            ["wuwa"] = new("wuwa", ExportKind.Pulls),
            ["ae"] = new("ae", ExportKind.Pulls),
        });

    public static IReadOnlyList<ExportProviderCapability> All { get; } = Slots.Values.ToArray();

    public static ExportProviderCapability Get(string gameId) =>
        Slots.TryGetValue(gameId, out var slot)
            ? slot
            : new ExportProviderCapability(gameId, ExportKind.None);

    public static ExportProviderCapability GetEnabled(
        string gameId,
        LauncherFeatureFlags flags,
        string? achievementSource)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var available = Get(gameId);
        var normalizedAchievementSource = AchievementExportSources.Normalize(
            gameId,
            achievementSource);
        var enabled = gameId switch
        {
            "gi" => (flags.GiPulls ? ExportKind.Pulls : ExportKind.None)
                | (flags.GiAchievements && flags.AchievementHelperReady ? ExportKind.Achievements : ExportKind.None),
            "hsr" => (flags.HsrPulls ? ExportKind.Pulls : ExportKind.None)
                | (flags.HsrAchievements
                    && (normalizedAchievementSource == AchievementExportSources.HoyoLab
                        ? flags.HoyoLabAccountAccess
                            && !flags.HoyoLabAccountCleanupPending
                        : flags.AchievementHelperReady)
                            ? ExportKind.Achievements
                            : ExportKind.None),
            "zzz" => (flags.ZzzPulls ? ExportKind.Pulls : ExportKind.None)
                | (flags.ZzzAchievements ? ExportKind.Achievements : ExportKind.None),
            "wuwa" => (flags.WuWaPulls ? ExportKind.Pulls : ExportKind.None)
                | (flags.WuWaAchievements ? ExportKind.Achievements : ExportKind.None),
            "ae" => (flags.EndfieldPulls ? ExportKind.Pulls : ExportKind.None)
                | (flags.EndfieldAchievements ? ExportKind.Achievements : ExportKind.None),
            _ => ExportKind.None,
        };
        return available with { SupportedKinds = available.SupportedKinds & enabled };
    }
}

public sealed record ExportArmSnapshot(
    string GameId,
    bool PullsArmed,
    bool AchievementsArmed)
{
    public ExportKind RequestedKinds =>
        (PullsArmed ? ExportKind.Pulls : ExportKind.None)
        | (AchievementsArmed ? ExportKind.Achievements : ExportKind.None);

    // The working game-capture flow arms at the final login screen, immediately
    // before the game enters the world. The process is already running then.
    // A late attempt remains fail-closed: the bounded helper times out and writes
    // no partial file.
    public bool CanStartWhileGameRunning => RequestedKinds != ExportKind.None;

    public static ExportArmSnapshot From(
        ExportArmingState state,
        string gameId,
        LauncherFeatureFlags featureFlags)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(featureFlags);
        var armed = state.Games.TryGetValue(gameId, out var game)
            ? game
            : new ExportGameArming { PullsArmed = state.IsArmed, AchievementsArmed = state.IsArmed };
        var capability = ExportProviderCatalog.GetEnabled(
            gameId,
            featureFlags,
            armed.AchievementSource);
        return new(
            gameId,
            armed.PullsArmed && capability.Supports(ExportKind.Pulls),
            armed.AchievementsArmed && capability.Supports(ExportKind.Achievements));
    }
}

public enum ExportTaskState
{
    NotRequested,
    Preparing,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Unsupported,
}

public enum ExportJobState
{
    PendingLaunch,
    Running,
    Completed,
    Failed,
    Canceled,
    Unsupported,
}

public sealed record ExportTaskSnapshot(
    ExportTaskState State,
    string? ErrorCode = null,
    ExportArtifactMetadata? Artifact = null);

public sealed record ExportJobSnapshot(
    Guid JobId,
    string GameId,
    ExportJobState State,
    ExportTaskSnapshot Pulls,
    ExportTaskSnapshot Achievements,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null)
{
    public bool IsFinished => State is ExportJobState.Completed or ExportJobState.Failed
        or ExportJobState.Canceled or ExportJobState.Unsupported;
}

public interface IExportArtifactHandoffAuthority
{
    bool IsCurrent { get; }
}

public sealed record ExportArtifactMetadata(
    string Kind,
    long ItemCount,
    long ByteCount,
    string Format,
    DateTimeOffset CreatedAt,
    string? OutputPath = null,
    IExportArtifactHandoffAuthority? HandoffAuthority = null)
{
    public bool IsHandoffCurrent =>
        HandoffAuthority?.IsCurrent ?? true;
}

public sealed class ExportProviderException : Exception
{
    public ExportProviderException(string code) : base("The export provider could not complete the job.")
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A safe error code is required.", nameof(code));
        Code = code;
    }

    public string Code { get; }
}

public sealed record ExportLaunchResult(
    bool LaunchAdmitted,
    Guid JobId,
    ExportJobSnapshot Snapshot);

public interface IPullExportProvider
{
    ValueTask<IPullExportSession> PrepareAsync(
        string gameId,
        CancellationToken cancellationToken);
}

public interface IPullExportSession : IAsyncDisposable
{
    ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken);
}

public interface IAchievementExportProvider
{
    ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken);
}

public interface IAchievementExportSession : IAsyncDisposable
{
    Task Ready { get; }
    Task<ExportArtifactMetadata> Completion { get; }
}

/// <summary>
/// Marks a bounded native capture session that has its own timeout and cleanup
/// and may finish after the launcher process exits once Ready has completed.
/// Other achievement providers remain launcher-owned.
/// </summary>
public interface ILauncherIndependentAchievementExportSession : IAchievementExportSession
{
}

public sealed record ExportStatusEvent(
    Guid JobId,
    string GameId,
    string Kind,
    string State,
    string? ErrorCode,
    DateTimeOffset At)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string ToNdjson() => JsonSerializer.Serialize(this, JsonOptions);
}

public interface IExportStatusSink
{
    ValueTask PublishAsync(ExportStatusEvent status, CancellationToken cancellationToken);
}

public sealed class NullExportStatusSink : IExportStatusSink
{
    public ValueTask PublishAsync(ExportStatusEvent status, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public static class ExportErrorSanitizer
{
    public static string Code(Exception exception) => exception switch
    {
        ExportProviderException provider => provider.Code,
        PullExportException pulls => pulls.ErrorCode,
        OperationCanceledException => "canceled",
        TimeoutException => "timed-out",
        UnauthorizedAccessException => "access-denied",
        IOException => "io-failed",
        _ => "provider-failed",
    };
}

/// <summary>
/// Coordinates export work independently from launch. A provider failure only fails its own task;
/// no exception text, path, argument, or secret is included in a status event.
/// </summary>
public sealed class ExportCoordinator : IAsyncDisposable
{
    private readonly IPullExportProvider pulls;
    private readonly IAchievementExportProvider achievements;
    private readonly IExportStatusSink statusSink;
    private readonly TimeSpan achievementPrepareTimeout;
    private readonly Dictionary<string, JobEntry> jobs = new(StringComparer.Ordinal);
    private readonly object lifetimeSync = new();
    private Task? launcherCloseTask;
    private Task? fullDisposeTask;
    private int closed;

    public ExportCoordinator(
        IPullExportProvider pulls,
        IAchievementExportProvider achievements,
        IExportStatusSink? statusSink = null,
        TimeSpan? achievementPrepareTimeout = null)
    {
        this.pulls = pulls ?? throw new ArgumentNullException(nameof(pulls));
        this.achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        this.statusSink = statusSink ?? new NullExportStatusSink();
        this.achievementPrepareTimeout = achievementPrepareTimeout ?? TimeSpan.FromSeconds(20);
        if (this.achievementPrepareTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(achievementPrepareTimeout));
    }

    public ExportJobSnapshot GetSnapshot(Guid jobId)
    {
        var entry = FindJob(jobId)
            ?? throw new KeyNotFoundException("Unknown export job.");
        return entry.Snapshot;
    }

    public bool IsLauncherIndependentAchievementJob(Guid jobId)
    {
        var entry = FindJob(jobId);
        return entry is not null && entry.IsLauncherIndependentAchievementJob;
    }

    public async ValueTask<ExportJobSnapshot> WaitForCompletionAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var entry = FindJob(jobId)
            ?? throw new KeyNotFoundException("Unknown export job.");
        await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return entry.Snapshot;
    }

    public async ValueTask<ExportLaunchResult> RunForLaunchAsync(
        ExportArmSnapshot arm,
        Func<CancellationToken, ValueTask<bool>> launchAdmission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(launchAdmission);
        var capability = ExportProviderCatalog.Get(arm.GameId);
        var requested = arm.RequestedKinds & capability.SupportedKinds;
        var unsupported = arm.RequestedKinds & ~capability.SupportedKinds;
        var retain = requested != ExportKind.None;
        JobEntry? entry = null;
        JobEntry? previous = null;
        JobEntry? active = null;
        lock (lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(closed != 0, this);
            if (retain)
            {
                if (jobs.TryGetValue(arm.GameId, out var existing)
                    && !existing.Completion.IsCompleted)
                {
                    active = existing;
                }
                else
                {
                    previous = existing;
                    entry = new JobEntry(Guid.NewGuid(), arm.GameId, arm, requested, unsupported);
                    jobs[arm.GameId] = entry;
                }
            }
        }
        if (active is not null)
        {
            return new(false, active.Snapshot.JobId, active.Snapshot);
        }
        entry ??= new JobEntry(Guid.NewGuid(), arm.GameId, arm, requested, unsupported);
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);

        try
        {
            await PublishAsync(entry, "job", ExportJobState.PendingLaunch.ToString(), null, cancellationToken).ConfigureAwait(false);

            // Pull baseline and achievement preparation are independent preflights.
            // Neither can veto launch or the other export lane.
            IPullExportSession? pullSession = null;
            if ((requested & ExportKind.Pulls) != 0)
            {
                entry.SetPulls(ExportTaskState.Preparing);
                await PublishAsync(entry, "pulls", ExportTaskState.Preparing.ToString(), null, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    pullSession = await pulls.PrepareAsync(entry.GameId, entry.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var code = ExportErrorSanitizer.Code(exception);
                    entry.SetPulls(code == "canceled" ? ExportTaskState.Canceled : ExportTaskState.Failed, errorCode: code);
                    entry.TryComplete();
                    await PublishAsync(entry, "pulls", entry.Snapshot.Pulls.State.ToString(), code, CancellationToken.None).ConfigureAwait(false);
                }
            }

            IAchievementExportSession? achievementSession = null;
            if ((requested & ExportKind.Achievements) != 0)
            {
                entry.SetAchievements(ExportTaskState.Preparing);
                await PublishAsync(entry, "achievements", ExportTaskState.Preparing.ToString(), null, CancellationToken.None).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, cancellationToken);
                timeout.CancelAfter(achievementPrepareTimeout);
                try
                {
                    achievementSession = await achievements.StartAsync(
                        entry.GameId,
                        null,
                        entry.Token).ConfigureAwait(false);
                    entry.SetAchievementLauncherIndependent(
                        achievementSession is ILauncherIndependentAchievementExportSession);
                    await achievementSession.Ready.WaitAsync(timeout.Token).ConfigureAwait(false);
                    entry.SetAchievements(ExportTaskState.Running);
                    await PublishAsync(entry, "achievements", ExportTaskState.Running.ToString(), null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (achievementSession is not null) await achievementSession.DisposeAsync().ConfigureAwait(false);
                    achievementSession = null;
                    var code = timeout.IsCancellationRequested && !entry.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        ? "timed-out"
                        : ExportErrorSanitizer.Code(exception);
                    entry.SetAchievements(code == "canceled" ? ExportTaskState.Canceled : ExportTaskState.Failed, errorCode: code);
                    entry.TryComplete();
                    await PublishAsync(entry, "achievements", entry.Snapshot.Achievements.State.ToString(), code, CancellationToken.None).ConfigureAwait(false);
                }
            }

            bool admitted;
            try
            {
                admitted = !cancellationToken.IsCancellationRequested
                    && !entry.Token.IsCancellationRequested
                    && Volatile.Read(ref closed) == 0
                    && await launchAdmission(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { admitted = false; }
            catch (Exception) { admitted = false; }
            if (!admitted)
            {
                if (pullSession is not null) await pullSession.DisposeAsync().ConfigureAwait(false);
                if (achievementSession is not null) await achievementSession.DisposeAsync().ConfigureAwait(false);
                entry.Cancel(forceComplete: true);
                await PublishAsync(entry, "job", ExportJobState.Canceled.ToString(), "launch-not-admitted", CancellationToken.None).ConfigureAwait(false);
                return new(false, entry.Snapshot.JobId, entry.Snapshot);
            }

            entry.MarkRunning();
            entry.SetLaunchSettled(
                admitted: true,
                emptyState: requested == ExportKind.None
                    ? unsupported != ExportKind.None
                        ? ExportJobState.Unsupported
                        : ExportJobState.Completed
                    : null);
            if (achievementSession is not null)
                _ = Task.Run(() => CompleteAchievementsAsync(entry, achievementSession));
            if (requested == ExportKind.None)
            {
                await PublishAsync(entry, "job", entry.Snapshot.State.ToString(), null, CancellationToken.None).ConfigureAwait(false);
                return new(true, entry.Snapshot.JobId, entry.Snapshot);
            }

            if (pullSession is not null) _ = Task.Run(() => RunPullsAsync(entry, pullSession));
            return new(true, entry.Snapshot.JobId, entry.Snapshot);
        }
        finally
        {
            if (!retain)
                await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    public bool Cancel(Guid jobId)
    {
        var entry = FindJob(jobId);
        if (entry is null || entry.Completion.IsCompleted) return false;
        entry.Cancel();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        JobEntry[]? entries = null;
        TaskCompletionSource? starter = null;
        lock (lifetimeSync)
        {
            if (fullDisposeTask is null)
            {
                closed = 1;
                entries = jobs.Values.ToArray();
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fullDisposeTask = starter.Task;
            }
            task = fullDisposeTask;
        }
        if (starter is not null)
            _ = DrainFullAsync(entries!, starter);
        return new ValueTask(task);
    }

    public ValueTask ShutDownForLauncherCloseAsync()
    {
        Task task;
        JobEntry[]? entries = null;
        TaskCompletionSource? starter = null;
        lock (lifetimeSync)
        {
            if (fullDisposeTask is not null)
            {
                task = fullDisposeTask;
            }
            else if (launcherCloseTask is null)
            {
                closed = 1;
                entries = jobs.Values.ToArray();
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                launcherCloseTask = starter.Task;
                task = starter.Task;
            }
            else
            {
                task = launcherCloseTask;
            }
        }
        if (starter is not null)
            _ = DrainCloseAsync(entries!, starter);
        return new ValueTask(task);
    }

    private async Task DrainCloseAsync(
        JobEntry[] entries,
        TaskCompletionSource completion)
    {
        try
        {
            var canceled = entries
                .Where(entry => !entry.Completion.IsCompleted && !entry.CanOutliveLauncher)
                .ToArray();
            foreach (var entry in canceled) entry.Cancel();
            if (canceled.Length != 0)
                await Task.WhenAll(canceled.Select(static entry => entry.Completion)).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DrainFullAsync(
        JobEntry[] entries,
        TaskCompletionSource completion)
    {
        try
        {
            var pending = entries.Where(static entry => !entry.Completion.IsCompleted).ToArray();
            foreach (var entry in pending) entry.Cancel();
            if (pending.Length != 0)
                await Task.WhenAll(pending.Select(static entry => entry.Completion)).ConfigureAwait(false);

            lock (lifetimeSync) jobs.Clear();
            foreach (var entry in entries)
                await entry.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private JobEntry? FindJob(Guid jobId)
    {
        lock (lifetimeSync)
            return jobs.Values.FirstOrDefault(entry => entry.JobId == jobId);
    }

    private async Task RunPullsAsync(JobEntry entry, IPullExportSession session)
    {
        try
        {
            entry.SetPulls(ExportTaskState.Running);
            await PublishAsync(entry, "pulls", ExportTaskState.Running.ToString(), null, CancellationToken.None).ConfigureAwait(false);
            var artifact = await session.ExportAsync(entry.Token).ConfigureAwait(false);
            entry.SetPulls(ExportTaskState.Succeeded, artifact);
            await PublishAsync(entry, "pulls", ExportTaskState.Succeeded.ToString(), null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var code = ExportErrorSanitizer.Code(exception);
            entry.SetPulls(code == "canceled" ? ExportTaskState.Canceled : ExportTaskState.Failed, errorCode: code);
            await PublishAsync(entry, "pulls", entry.Snapshot.Pulls.State.ToString(), code, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally { entry.TryComplete(); }
        }
    }

    private async Task CompleteAchievementsAsync(JobEntry entry, IAchievementExportSession session)
    {
        try
        {
            var artifact = await session.Completion.ConfigureAwait(false);
            if (!artifact.IsHandoffCurrent)
                throw new ExportProviderException("achievement-publish-not-authorized");
            entry.SetAchievements(ExportTaskState.Succeeded, artifact);
            await PublishAsync(
                entry,
                "achievements",
                entry.Snapshot.Achievements.State.ToString(),
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var code = ExportErrorSanitizer.Code(exception);
            entry.SetAchievements(code == "canceled" ? ExportTaskState.Canceled : ExportTaskState.Failed, errorCode: code);
            await PublishAsync(entry, "achievements", entry.Snapshot.Achievements.State.ToString(), code, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally { entry.TryComplete(); }
        }
    }

    private async ValueTask PublishAsync(JobEntry entry, string kind, string state, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await statusSink.PublishAsync(new ExportStatusEvent(entry.Snapshot.JobId, entry.GameId, kind, state, error, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { /* status reporting cannot affect launch/export */ }
    }

    private sealed class JobEntry
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource cancellation = new();
        private int remaining;
        private int cancellationRequested;
        private int disposed;
        private ExportJobSnapshot snapshot;

        public JobEntry(Guid id, string gameId, ExportArmSnapshot arm, ExportKind requested, ExportKind unsupported)
        {
            JobId = id;
            GameId = gameId;
            Arm = arm;
            remaining = BitCount(requested);
            workersStarted = remaining != 0;
            var unsupportedState = unsupported != ExportKind.None ? ExportTaskState.Unsupported : ExportTaskState.NotRequested;
            snapshot = new(id, gameId, ExportJobState.PendingLaunch,
                requested.HasFlag(ExportKind.Pulls) ? new(ExportTaskState.NotRequested) : new(unsupportedState),
                requested.HasFlag(ExportKind.Achievements) ? new(ExportTaskState.NotRequested) : new(unsupportedState), DateTimeOffset.UtcNow);
        }

        public Guid JobId { get; }
        public string GameId { get; }
        public ExportArmSnapshot Arm { get; }
        public CancellationToken Token => cancellation.Token;
        public Task Completion => completion.Task;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExportJobSnapshot Snapshot
        {
            get
            {
                ExportJobSnapshot current;
                lock (sync) current = snapshot;
                if (current.Achievements.State is ExportTaskState.Succeeded
                    && current.Achievements.Artifact is { IsHandoffCurrent: false })
                {
                    return current with
                    {
                        State = ExportJobState.Failed,
                        Achievements = new(
                            ExportTaskState.Failed,
                            "achievement-publish-not-authorized"),
                    };
                }
                return current;
            }
        }
        private bool achievementLauncherIndependent;
        public bool IsLauncherIndependentAchievementJob
        {
            get
            {
                lock (sync)
                    return achievementLauncherIndependent
                        && launchSettled
                        && snapshot.Achievements.State is not ExportTaskState.NotRequested
                            and not ExportTaskState.Unsupported;
            }
        }
        public bool CanOutliveLauncher
        {
            get
            {
                lock (sync)
                    return achievementLauncherIndependent
                        && launchSettled
                        && snapshot.State == ExportJobState.Running
                        && snapshot.Achievements.State == ExportTaskState.Running
                        && snapshot.Pulls.State is not ExportTaskState.Preparing
                            and not ExportTaskState.Running
                        && Volatile.Read(ref cancellationRequested) == 0;
            }
        }
        public void MarkRunning() { lock (sync) snapshot = snapshot with { State = ExportJobState.Running }; }
        private bool workersStarted;
        public void SetAchievementLauncherIndependent(bool value)
        {
            lock (sync) achievementLauncherIndependent = value;
        }
        private bool launchSettled;
        public void SetLaunchSettled(bool admitted, ExportJobState? emptyState = null)
        {
            lock (sync) launchSettled = true;
            if (!admitted) Finish(ExportJobState.Canceled);
            else if (emptyState is not null) Finish(emptyState.Value);
            else TryFinishIfReady();
        }
        public void Cancel(bool forceComplete = false)
        {
            if (completion.Task.IsCompleted) return;
            if (Interlocked.Exchange(ref cancellationRequested, 1) == 0)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
            }
            lock (sync)
            {
                if (completion.Task.IsCompleted) return;
                snapshot = snapshot with { State = ExportJobState.Canceled, FinishedAt = DateTimeOffset.UtcNow };
                if (forceComplete || !workersStarted) completion.TrySetResult();
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                cancellation.Dispose();
            return ValueTask.CompletedTask;
        }
        public void Finish(ExportJobState state)
        {
            lock (sync)
            {
                if (completion.Task.IsCompleted) return;
                if (Volatile.Read(ref cancellationRequested) != 0
                    && state != ExportJobState.Canceled)
                    state = ExportJobState.Canceled;
                snapshot = snapshot with { State = state, FinishedAt = DateTimeOffset.UtcNow };
                completion.TrySetResult();
            }
        }
        public void SetPulls(ExportTaskState state, ExportArtifactMetadata? artifact = null, string? errorCode = null) { lock (sync) snapshot = snapshot with { Pulls = new(state, errorCode, artifact) }; }
        public void SetAchievements(ExportTaskState state, ExportArtifactMetadata? artifact = null, string? errorCode = null) { lock (sync) snapshot = snapshot with { Achievements = new(state, errorCode, artifact) }; }
        public void TryComplete()
        {
            if (Interlocked.Decrement(ref remaining) != 0) return;
            TryFinishIfReady();
        }
        private void TryFinishIfReady()
        {
            lock (sync) if (!launchSettled || remaining != 0) return;
            var pullsFailed = Snapshot.Pulls.State is ExportTaskState.Failed or ExportTaskState.Canceled;
            var achievementsFailed = Snapshot.Achievements.State is ExportTaskState.Failed or ExportTaskState.Canceled;
            Finish(Volatile.Read(ref cancellationRequested) != 0
                ? ExportJobState.Canceled
                : pullsFailed || achievementsFailed
                    ? ExportJobState.Failed
                    : ExportJobState.Completed);
        }
        private static int BitCount(ExportKind kind) => (kind.HasFlag(ExportKind.Pulls) ? 1 : 0) + (kind.HasFlag(ExportKind.Achievements) ? 1 : 0);
    }
}
