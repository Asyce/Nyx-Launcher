using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public enum AchievementExportHandoffOutcome
{
    NotAvailable,
    Delivered,
    Fallback,
}

public interface IAchievementExportHandoffLauncher
{
    ValueTask<bool> OpenBrowserAsync(Uri browserUri, CancellationToken cancellationToken);
    ValueTask<bool> OpenFallbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns only fixed native GI/HSR achievement deliveries already admitted by
/// the export coordinator. Each registration ends with that job, its timeout,
/// or the one-use bridge expiry; it cannot launch arbitrary processes or URLs.
/// </summary>
public sealed class BoundedAchievementExportHandoffOwner : IAsyncDisposable
{
    private readonly ExportCoordinator exports;
    private readonly AchievementImportBridge bridge;
    private readonly IAchievementExportHandoffLauncher launcher;
    private readonly TimeSpan maximumLifetime;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Registration> active = new();
    private Task? disposalTask;
    private int closed;

    public BoundedAchievementExportHandoffOwner(
        ExportCoordinator exports,
        AchievementImportBridge bridge,
        IAchievementExportHandoffLauncher launcher,
        TimeSpan? maximumLifetime = null)
    {
        this.exports = exports ?? throw new ArgumentNullException(nameof(exports));
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.maximumLifetime = maximumLifetime ?? TimeSpan.FromMinutes(7);
        if (this.maximumLifetime <= TimeSpan.Zero
            || this.maximumLifetime > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime));
    }

    public Task<AchievementExportHandoffOutcome> TrackAsync(string gameId, Guid jobId)
    {
        Registration? registration = null;
        Registration? duplicate = null;
        lock (sync)
        {
            if (active.TryGetValue(jobId, out var existing))
            {
                if (existing.GameId != gameId)
                    throw new InvalidOperationException(
                        "Only an admitted fixed native achievement job can own a launcher-close handoff.");
                duplicate = existing;
            }
            else
            {
                ObjectDisposedException.ThrowIf(closed != 0, this);
            }
        }
        if (duplicate is not null)
            return duplicate.Work.Value;

        ExportJobSnapshot snapshot;
        try
        {
            snapshot = exports.GetSnapshot(jobId);
        }
        catch (KeyNotFoundException)
        {
            throw new InvalidOperationException(
                "Only an admitted fixed native achievement job can own a launcher-close handoff.");
        }
        if (gameId is not ("gi" or "hsr")
            || snapshot.GameId != gameId
            || !exports.IsLauncherIndependentAchievementJob(jobId))
            throw new InvalidOperationException(
                "Only an admitted fixed native achievement job can own a launcher-close handoff.");

        Registration? admitted;
        lock (sync)
        {
            if (active.TryGetValue(jobId, out var existing))
            {
                if (existing.GameId != gameId)
                    throw new InvalidOperationException(
                        "Only an admitted fixed native achievement job can own a launcher-close handoff.");
                admitted = existing;
            }
            else
            {
                ObjectDisposedException.ThrowIf(closed != 0, this);
                registration = new(this, gameId, jobId);
                active.Add(jobId, registration);
                admitted = registration;
            }
        }
        return admitted.Work.Value;
    }

    public Task WaitForActiveAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync()
    {
        Task task;
        Registration[]? registrations = null;
        TaskCompletionSource? starter = null;
        lock (sync)
        {
            if (disposalTask is null)
            {
                closed = 1;
                registrations = active.Values.ToArray();
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                disposalTask = starter.Task;
            }
            task = disposalTask;
        }
        if (starter is not null)
            _ = DrainAsync(registrations!, starter);
        return new ValueTask(task);
    }

    private async Task DrainAsync(
        Registration[] registrations,
        TaskCompletionSource completion)
    {
        try
        {
            if (registrations.Length != 0)
                await Task.WhenAll(registrations.Select(static registration => registration.Work.Value))
                    .ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private sealed class Registration
    {
        private readonly BoundedAchievementExportHandoffOwner owner;

        public Registration(
            BoundedAchievementExportHandoffOwner owner,
            string gameId,
            Guid jobId)
        {
            this.owner = owner;
            GameId = gameId;
            JobId = jobId;
            Work = new(
                () => owner.TrackSafelyAsync(gameId, jobId, this),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string GameId { get; }
        public Guid JobId { get; }
        public Lazy<Task<AchievementExportHandoffOutcome>> Work { get; }

        public void Remove()
        {
            lock (owner.sync)
            {
                if (owner.active.TryGetValue(JobId, out var current)
                    && ReferenceEquals(current, this))
                    owner.active.Remove(JobId);
            }
        }
    }

    private async Task<AchievementExportHandoffOutcome> TrackCoreAsync(
        string gameId,
        Guid jobId)
    {
        using var lifetime = new CancellationTokenSource(maximumLifetime);
        ExportJobSnapshot snapshot;
        try
        {
            snapshot = await exports.WaitForCompletionAsync(
                jobId,
                lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            exports.Cancel(jobId);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await exports.WaitForCompletionAsync(
                    jobId,
                    cleanup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }
            return AchievementExportHandoffOutcome.NotAvailable;
        }

        if (snapshot.Achievements.State is not ExportTaskState.Succeeded
            || snapshot.Achievements.Artifact is not
            {
                IsHandoffCurrent: true,
                OutputPath: { Length: > 0 } outputPath,
            })
            return AchievementExportHandoffOutcome.NotAvailable;

        AchievementImportBridgeSession session;
        try
        {
            session = await bridge.StartAsync(
                gameId,
                outputPath,
                lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ExportProviderException
            or IOException
            or UnauthorizedAccessException)
        {
            await TryOpenFallbackAsync(lifetime.Token).ConfigureAwait(false);
            return AchievementExportHandoffOutcome.Fallback;
        }

        await using (session.ConfigureAwait(false))
        {
            await TryOpenFallbackAsync(lifetime.Token).ConfigureAwait(false);
            bool opened;
            try
            {
                opened = await launcher.OpenBrowserAsync(
                    session.BrowserUri,
                    lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                opened = false;
            }
            if (!opened) return AchievementExportHandoffOutcome.Fallback;

            try
            {
                var result = await session.Completion
                    .WaitAsync(lifetime.Token).ConfigureAwait(false);
                return result == AchievementImportDeliveryState.Delivered
                    ? AchievementExportHandoffOutcome.Delivered
                    : AchievementExportHandoffOutcome.Fallback;
            }
            catch (OperationCanceledException)
            {
                return AchievementExportHandoffOutcome.Fallback;
            }
        }
    }

    private async Task<AchievementExportHandoffOutcome> TrackSafelyAsync(
        string gameId,
        Guid jobId,
        Registration registration)
    {
        try
        {
            return await TrackCoreAsync(gameId, jobId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exports.Cancel(jobId);
            return AchievementExportHandoffOutcome.NotAvailable;
        }
        catch (Exception)
        {
            return AchievementExportHandoffOutcome.NotAvailable;
        }
        finally
        {
            registration.Remove();
        }
    }

    private async ValueTask TryOpenFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await launcher.OpenFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
