using System.Collections.Concurrent;
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
public sealed class BoundedAchievementExportHandoffOwner
{
    private readonly ExportCoordinator exports;
    private readonly AchievementImportBridge bridge;
    private readonly IAchievementExportHandoffLauncher launcher;
    private readonly TimeSpan maximumLifetime;
    private readonly ConcurrentDictionary<
        Guid,
        Lazy<Task<AchievementExportHandoffOutcome>>> active = new();

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
        return active.GetOrAdd(
            jobId,
            _ => new(
                () => TrackSafelyAsync(gameId, jobId),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public async Task WaitForActiveAsync()
    {
        var pending = active.Values.Select(static registration => registration.Value).ToArray();
        if (pending.Length != 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
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
        Guid jobId)
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
