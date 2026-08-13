using System.Net;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

/// <summary>
/// Exports WuWa pull history from one caller-supplied install root. The root is
/// never discovered and no external application or clipboard is touched.
/// </summary>
public sealed class WuwaPullExportProvider : IPullExportProvider, IDisposable
{
    private const string GameId = "wuwa";
    private const string RelativeLogPath = "Wuthering Waves Game\\Client\\Saved\\Logs\\Client.log";
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string installRoot;
    private readonly string exportRootDirectory;
    private readonly PullExportSafetyLimits limits;
    private readonly IWuwaPullRequestPacer pacer;
    private readonly TimeProvider timeProvider;
    private readonly WuwaPullHistoryLinkReader linkReader;
    private int disposed;

    public WuwaPullExportProvider(string installRoot)
        : this(
            CreateHttpClient(),
            installRoot,
            WindowsDocumentsDirectory.Get(),
            new WuwaPullRequestPacer(),
            new PullExportSafetyLimits(),
            TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    internal WuwaPullExportProvider(
        HttpClient httpClient,
        string installRoot,
        string exportRootDirectory,
        IWuwaPullRequestPacer? pacer = null,
        PullExportSafetyLimits? limits = null,
        TimeProvider? timeProvider = null,
        bool ownsHttpClient = false,
        WuwaPullHistoryLinkReader? linkReader = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.installRoot = NormalizeInstallRoot(installRoot);
        this.exportRootDirectory = Path.GetFullPath(exportRootDirectory ?? throw new ArgumentNullException(nameof(exportRootDirectory)));
        this.pacer = pacer ?? new WuwaPullRequestPacer();
        this.limits = limits ?? new PullExportSafetyLimits();
        ValidateLimits(this.limits);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.linkReader = linkReader ?? new WuwaPullHistoryLinkReader(this.limits);
        this.ownsHttpClient = ownsHttpClient;
    }

    public ValueTask<IPullExportSession> PrepareAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(gameId, GameId, StringComparison.Ordinal))
            throw new PullExportException(PullExportErrorCodes.UnsupportedGame);
        try
        {
            var logPath = ResolveValidatedLogPath(installRoot);
            var observation = linkReader.Read(logPath, cancellationToken);
            return ValueTask.FromResult<IPullExportSession>(new PullExportSession(
                this,
                observation));
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception) { throw new PullExportException(PullExportErrorCodes.HistoryNotFound); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ownsHttpClient) httpClient.Dispose();
    }

    private async ValueTask<ExportArtifactMetadata> ExportAsync(
        WuwaPullHistoryObservation baseline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var totalBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalBudget.CancelAfter(limits.EffectiveTotalDuration);
        var fresh = await WaitForFreshUrlAsync(
            baseline,
            totalBudget.Token,
            cancellationToken).ConfigureAwait(false);
        var api = new WuwaPullApiClient(httpClient, limits, pacer);
        var archive = await api.DownloadAsync(fresh, totalBudget.Token).ConfigureAwait(false);
        var writer = new WuwaPullExportWriter(exportRootDirectory, limits, timeProvider);
        var output = await writer.WriteAsync(archive, totalBudget.Token).ConfigureAwait(false);
        return new ExportArtifactMetadata(
            "pulls",
            archive.Records.Count,
            output.ByteCount,
            "WWGF JSON",
            timeProvider.GetUtcNow(),
            output.Path);
    }

    private async ValueTask<WuwaPullHistoryUrl> WaitForFreshUrlAsync(
        WuwaPullHistoryObservation baseline,
        CancellationToken totalToken,
        CancellationToken callerToken)
    {
        using var observationBudget = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
        observationBudget.CancelAfter(limits.EffectiveCacheObservationDuration);
        while (true)
        {
            callerToken.ThrowIfCancellationRequested();
            try
            {
                var currentPath = ResolveValidatedLogPath(installRoot);
                var current = linkReader.Read(currentPath, observationBudget.Token);
                var fresh = SelectFreshUrl(baseline, current);
                if (fresh is not null) return fresh;
                await Task.Delay(limits.EffectiveCachePollInterval, observationBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested
                && observationBudget.IsCancellationRequested)
            {
                throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);
            }
            catch (PullExportException exception) when (
                exception.ErrorCode is PullExportErrorCodes.HistoryNotFound
                    or PullExportErrorCodes.InvalidHistoryLink
                    or PullExportErrorCodes.HistoryNotUpdated)
            {
                try
                {
                    await Task.Delay(limits.EffectiveCachePollInterval, observationBudget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested
                    && observationBudget.IsCancellationRequested)
                {
                    throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);
                }
            }
        }
    }

    private WuwaPullHistoryUrl? SelectFreshUrl(
        WuwaPullHistoryObservation baseline,
        WuwaPullHistoryObservation current)
    {
        if (!string.Equals(baseline.Path, current.Path, StringComparison.OrdinalIgnoreCase)
            || !baseline.Stamp.SameIdentity(current.Stamp))
            throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);

        var rolledOver = current.Stamp.Length < baseline.Stamp.Length;
        if (rolledOver
            && (!baseline.IsMasked
                || !current.IsMasked
                || current.Stamp.LastWriteTimeUtcTicks <= baseline.Stamp.LastWriteTimeUtcTicks))
            throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);

        foreach (var candidate in current.Candidates)
        {
            if (!rolledOver && candidate.StartOffset < baseline.Stamp.Length) continue;
            return candidate.Url;
        }
        return null;
    }

    private static string NormalizeInstallRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An install root is required.", nameof(path));
        try
        {
            var full = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(full)
                || full.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || full.StartsWith("\\\\.\\", StringComparison.Ordinal)
                || full.StartsWith("\\\\", StringComparison.Ordinal))
                throw new ArgumentException("A plain local install root is required.", nameof(path));
            var root = Path.GetPathRoot(full);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return root is not null
                && trimmed.Length < root.Length
                ? root
                : trimmed;
        }
        catch (ArgumentException) { throw; }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new ArgumentException("A valid install root is required.", nameof(path));
        }
    }

    private static string ResolveValidatedLogPath(string root)
    {
        try
        {
            EnsureNoReparseComponents(root, requireDirectory: true);
            var path = Path.Combine(root, RelativeLogPath);
            var expected = Path.GetFullPath(Path.Combine(root, RelativeLogPath));
            if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)
                || Path.GetRelativePath(root, expected).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    != RelativeLogPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar))
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
            EnsureNoReparseComponents(expected, requireDirectory: false);
            if (!WuwaPullHistoryLinkReader.IsRegularPlainFile(expected))
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
            return expected;
        }
        catch (PullExportException) { throw; }
        catch (Exception) { throw new PullExportException(PullExportErrorCodes.HistoryNotFound); }
    }

    private static void EnsureNoReparseComponents(string path, bool requireDirectory)
    {
        var full = Path.GetFullPath(path);
        var ancestors = new Stack<string>();
        var current = full;
        while (!string.IsNullOrWhiteSpace(current))
        {
            ancestors.Push(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        foreach (var component in ancestors)
        {
            if (!File.Exists(component) && !Directory.Exists(component))
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
            var attributes = File.GetAttributes(component);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }
        if (requireDirectory)
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void ValidateLimits(PullExportSafetyLimits value)
    {
        if (value.MaximumLogBytes is < 1 or > 4 * 1024 * 1024
            || value.MaximumSourceLogBytes < value.MaximumLogBytes
            || value.MaximumSourceLogBytes > 64 * 1024 * 1024
            || value.MaximumCandidateUrls is < 1 or > 256
            || value.MaximumResponseBytes is < 1_024 or > 8 * 1024 * 1024
            || value.MaximumRecords is < 1 or > 200_000
            || value.MaximumOutputBytes is < 1_024 or > 256L * 1024 * 1024
            || value.EffectiveTotalDuration < TimeSpan.FromSeconds(1)
            || value.EffectiveTotalDuration > TimeSpan.FromMinutes(15)
            || value.EffectiveRequestTimeout < TimeSpan.FromMilliseconds(100)
            || value.EffectiveRequestTimeout > TimeSpan.FromMinutes(1)
            || value.EffectiveCacheObservationDuration <= TimeSpan.Zero
            || value.EffectiveCacheObservationDuration > TimeSpan.FromMinutes(10)
            || value.EffectiveCachePollInterval <= TimeSpan.Zero
            || value.EffectiveCachePollInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private sealed class PullExportSession(
        WuwaPullExportProvider owner,
        WuwaPullHistoryObservation baseline) : IPullExportSession
    {
        private readonly object sync = new();
        private bool disposed;
        private bool used;

        public ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (used) throw new InvalidOperationException("This pull export session has already been used.");
                used = true;
                return owner.ExportAsync(baseline, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
            }
            return ValueTask.CompletedTask;
        }
    }
}
