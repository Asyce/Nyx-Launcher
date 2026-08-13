using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

/// <summary>
/// Exports GI, HSR, and ZZZ pull history directly from their official HoYoverse APIs.
/// Cached authentication is held only in short-lived in-memory request state.
/// </summary>
public sealed class HoyoPullExportProvider : IPullExportProvider, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly IHoyoPullCacheLocator cacheLocator;
    private readonly IHoyoPullHistoryLinkReader linkReader;
    private readonly PullExportSafetyLimits limits;
    private readonly IPullRequestPacer pacer;
    private readonly string exportRootDirectory;
    private readonly TimeProvider timeProvider;
    private readonly byte[] fingerprintKey = RandomNumberGenerator.GetBytes(32);
    private int disposed;

    public HoyoPullExportProvider()
        : this(
            CreateHttpClient(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            WindowsDocumentsDirectory.Get(),
            new PullRequestPacer(),
            new PullExportSafetyLimits(),
            TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    internal HoyoPullExportProvider(
        HttpClient httpClient,
        string userProfile,
        string exportRootDirectory,
        IPullRequestPacer? pacer = null,
        PullExportSafetyLimits? limits = null,
        TimeProvider? timeProvider = null,
        bool ownsHttpClient = false,
        IHoyoPullCacheLocator? cacheLocator = null,
        IHoyoPullHistoryLinkReader? linkReader = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.limits = limits ?? new PullExportSafetyLimits();
        ValidateLimits(this.limits);
        this.pacer = pacer ?? new PullRequestPacer();
        this.exportRootDirectory = Path.GetFullPath(exportRootDirectory ?? throw new ArgumentNullException(nameof(exportRootDirectory)));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.cacheLocator = cacheLocator ?? new HoyoPullCacheLocator(
            Path.GetFullPath(userProfile ?? throw new ArgumentNullException(nameof(userProfile))), this.limits);
        this.linkReader = linkReader ?? new HoyoPullHistoryLinkReader(this.limits);
        this.ownsHttpClient = ownsHttpClient;
    }

    public ValueTask<IPullExportSession> PrepareAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var game = HoyoPullGameConfiguration.For(gameId);
        try
        {
            var cachePath = cacheLocator.Locate(game, cancellationToken);
            var observation = linkReader.Read(cachePath, game, cancellationToken);
            var evidence = BuildEvidence(observation.Candidates);
            return ValueTask.FromResult<IPullExportSession>(new PullExportSession(
                this,
                game,
                observation.Path,
                observation.Stamp,
                evidence));
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception) { throw new PullExportException(PullExportErrorCodes.UpstreamInvalid); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(fingerprintKey);
        if (ownsHttpClient) httpClient.Dispose();
    }

    private async ValueTask<ExportArtifactMetadata> ExportAsync(
        HoyoPullGameConfiguration game,
        string baselinePath,
        HoyoPullCacheStamp baselineStamp,
        IReadOnlyDictionary<string, BaselineOccurrence> baselineEvidence,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var totalBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalBudget.CancelAfter(limits.EffectiveTotalDuration);
        var candidates = await WaitForFreshCandidatesAsync(
            game,
            baselinePath,
            baselineStamp,
            baselineEvidence,
            totalBudget.Token,
            cancellationToken).ConfigureAwait(false);
        var api = new HoyoPullApiClient(httpClient, limits, pacer);
        var archive = await api.DownloadNewestValidAsync(game, candidates, totalBudget.Token).ConfigureAwait(false);
        var writer = new UigfPullExportWriter(exportRootDirectory, limits, timeProvider);
        var output = await writer.WriteAsync(archive, null, totalBudget.Token).ConfigureAwait(false);
        return new ExportArtifactMetadata(
            "pulls",
            archive.Records.Count,
            output.ByteCount,
            "UIGF v4.2 JSON",
            timeProvider.GetUtcNow(),
            output.Path);
    }

    private async ValueTask<IReadOnlyList<HoyoAuthQuery>> WaitForFreshCandidatesAsync(
        HoyoPullGameConfiguration game,
        string baselinePath,
        HoyoPullCacheStamp baselineStamp,
        IReadOnlyDictionary<string, BaselineOccurrence> baselineEvidence,
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
                var currentPath = cacheLocator.Locate(game, observationBudget.Token);
                var current = linkReader.Read(currentPath, game, observationBudget.Token);
                var fresh = SelectFreshCandidates(
                    baselinePath,
                    baselineStamp,
                    baselineEvidence,
                    current);
                if (fresh.Count != 0) return fresh;
                await Task.Delay(limits.EffectiveCachePollInterval, observationBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested
                && observationBudget.IsCancellationRequested)
            {
                throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);
            }
            catch (PullExportException exception) when (
                exception.ErrorCode is PullExportErrorCodes.HistoryNotFound or PullExportErrorCodes.InvalidHistoryLink)
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

    private IReadOnlyList<HoyoAuthQuery> SelectFreshCandidates(
        string baselinePath,
        HoyoPullCacheStamp baselineStamp,
        IReadOnlyDictionary<string, BaselineOccurrence> baselineEvidence,
        HoyoPullCacheObservation current)
    {
        var pathChanged = !string.Equals(baselinePath, current.Path, StringComparison.OrdinalIgnoreCase);
        var sameFile = !pathChanged && baselineStamp.SameFileAs(current.Stamp);
        var stampChanged = pathChanged
            || !sameFile
            || current.Stamp.Length != baselineStamp.Length
            || current.Stamp.LastWriteTimeUtcTicks != baselineStamp.LastWriteTimeUtcTicks;
        if (!stampChanged || current.Candidates.Count == 0) return [];

        var replaced = pathChanged || !sameFile;
        var truncated = current.Stamp.Length < baselineStamp.Length
            && current.Stamp.LastWriteTimeUtcTicks >= baselineStamp.LastWriteTimeUtcTicks;
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fresh = new List<HoyoAuthQuery>();
        foreach (var candidate in current.Candidates)
        {
            var fingerprint = Fingerprint(candidate.Query);
            currentCounts.TryGetValue(fingerprint, out var count);
            currentCounts[fingerprint] = ++count;
            var wasPresent = baselineEvidence.TryGetValue(fingerprint, out var baseline);
            if (!wasPresent
                || replaced
                || truncated
                || candidate.StartOffset >= baselineStamp.Length
                || count > baseline!.Count)
            {
                fresh.Add(candidate.Query);
            }
        }
        return fresh;
    }

    private IReadOnlyDictionary<string, BaselineOccurrence> BuildEvidence(
        IReadOnlyList<HoyoPullHistoryCandidate> candidates)
    {
        var result = new Dictionary<string, BaselineOccurrence>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var fingerprint = Fingerprint(candidate.Query);
            result.TryGetValue(fingerprint, out var occurrence);
            result[fingerprint] = new((occurrence?.Count ?? 0) + 1);
        }
        return result;
    }

    private string Fingerprint(HoyoAuthQuery query)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, fingerprintKey);
        foreach (var pair in query.Pairs)
        {
            AppendSecret(hash, pair.Key);
            hash.AppendData([0]);
            AppendSecret(hash, pair.Value);
            hash.AppendData([0xff]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSecret(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { hash.AppendData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record BaselineOccurrence(int Count);

    private sealed class PullExportSession : IPullExportSession
    {
        private readonly object sync = new();
        private readonly HoyoPullExportProvider owner;
        private readonly HoyoPullGameConfiguration game;
        private readonly HoyoPullCacheStamp baselineStamp;
        private string baselinePath;
        private IReadOnlyDictionary<string, BaselineOccurrence> baselineEvidence;
        private bool disposed;
        private bool used;

        public PullExportSession(
            HoyoPullExportProvider owner,
            HoyoPullGameConfiguration game,
            string baselinePath,
            HoyoPullCacheStamp baselineStamp,
            IReadOnlyDictionary<string, BaselineOccurrence> baselineEvidence)
        {
            this.owner = owner;
            this.game = game;
            this.baselinePath = baselinePath;
            this.baselineStamp = baselineStamp;
            this.baselineEvidence = baselineEvidence;
        }

        public ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (used) throw new InvalidOperationException("This pull export session has already been used.");
                used = true;
                return owner.ExportAsync(
                    game,
                    baselinePath,
                    baselineStamp,
                    baselineEvidence,
                    cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
                baselineEvidence = new Dictionary<string, BaselineOccurrence>();
                baselinePath = string.Empty;
            }
            return ValueTask.CompletedTask;
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
        if (value.MaximumCacheBytes is < 1 or > 64L * 1024 * 1024
            || value.MaximumLogBytes is < 1 or > 16 * 1024 * 1024
            || value.MaximumSourceLogBytes < value.MaximumLogBytes
            || value.MaximumSourceLogBytes > 64 * 1024 * 1024
            || value.MaximumCandidateUrls is < 1 or > 256
            || value.MaximumQueryBytes is < 512 or > 64 * 1024
            || value.MaximumResponseBytes is < 1_024 or > 8 * 1024 * 1024
            || value.MaximumPagesPerType is < 1 or > 2_000
            || value.MaximumRecords is < 1 or > 200_000
            || value.MaximumOutputBytes is < 1_024 or > 256L * 1024 * 1024
            || value.MaximumVersionDirectories is < 1 or > 1_024
            || value.MaximumSearchDirectories is < 1 or > 20_000
            || value.EffectiveTotalDuration < TimeSpan.FromSeconds(1)
            || value.EffectiveTotalDuration > TimeSpan.FromMinutes(15)
            || value.EffectiveRequestTimeout < TimeSpan.FromSeconds(1)
            || value.EffectiveRequestTimeout > TimeSpan.FromMinutes(1)
            || value.EffectiveCacheObservationDuration <= TimeSpan.Zero
            || value.EffectiveCacheObservationDuration > TimeSpan.FromMinutes(10)
            || value.EffectiveCachePollInterval <= TimeSpan.Zero
            || value.EffectiveCachePollInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}
