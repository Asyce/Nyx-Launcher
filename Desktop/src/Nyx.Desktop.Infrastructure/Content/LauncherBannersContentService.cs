using System.Collections.ObjectModel;
using Nyx.Desktop.Core.Content;

namespace Nyx.Desktop.Infrastructure.Content;

public sealed class LauncherBannersContentService : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly ILauncherBannersTransport transport;
    private readonly LauncherBannersCache cache;
    private readonly byte[] bundledPayload;
    private readonly LauncherBannersManifest bundledManifest;
    private readonly string bundledAssetsDirectory;
    private readonly Uri endpoint;
    private readonly Uri codesEndpoint;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource shutdown = new();
    private LauncherBannersManifest current;
    private LauncherCodesManifest? currentCodes;
    private Task? refresh;
    private Task<CodesRefreshResult>? codesRefresh;
    private Task? pump;
    private bool automaticRefreshEnabled;
    private bool disposed;

    public LauncherBannersContentService(
        byte[] bundledPayload,
        string cacheDirectory,
        Uri? endpoint = null,
        ILauncherBannersTransport? transport = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? interval = null,
        string? bundledAssetsDirectory = null,
        Uri? codesEndpoint = null)
        : this(
            bundledPayload,
            new LauncherBannersCache(cacheDirectory),
            endpoint,
            transport,
            clock,
            interval,
            bundledAssetsDirectory,
            codesEndpoint)
    {
    }

    internal LauncherBannersContentService(
        byte[] bundledPayload,
        LauncherBannersCache cache,
        Uri? endpoint,
        ILauncherBannersTransport? transport,
        Func<DateTimeOffset>? clock,
        TimeSpan? interval,
        string? bundledAssetsDirectory,
        Uri? codesEndpoint = null)
    {
        this.bundledPayload = bundledPayload?.ToArray() ?? throw new ArgumentNullException(nameof(bundledPayload));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.bundledAssetsDirectory = Path.GetFullPath(bundledAssetsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Content", "launcher-art"));
        this.endpoint = endpoint ?? new Uri(LauncherBannersTransport.ProductionEndpoint);
        LauncherBannersTransport.ValidateEndpoint(this.endpoint, allowConfigured: true, requireJson: true);
        this.codesEndpoint = codesEndpoint ?? new Uri(LauncherBannersTransport.ProductionCodesEndpoint);
        LauncherBannersTransport.ValidateEndpoint(this.codesEndpoint, allowConfigured: true, requireJson: true);
        this.transport = transport ?? new LauncherBannersTransport();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.interval = interval ?? TimeSpan.FromHours(6);
        if (this.interval < TimeSpan.FromMinutes(15)) throw new ArgumentOutOfRangeException(nameof(interval));
        var observedAt = this.clock();
        bundledManifest = LauncherBannersManifestParser.Parse(this.bundledPayload, fallback: true, observedAt);
        var cached = cache.TryLoadLastKnownGood(observedAt, this.bundledAssetsDirectory);
        current = ApplyBundledUpcomingFallback(
            cached is not null && cached.GeneratedAt >= bundledManifest.GeneratedAt
                ? cached
                : bundledManifest,
            bundledManifest);
        currentCodes = cache.TryLoadLastKnownGoodCodes(this.clock());
        if (currentCodes is not null) current = ApplyCodes(current, currentCodes);
    }

    public LauncherBannersManifest Current
    {
        get
        {
            LauncherBannersManifest snapshot;
            lock (sync) snapshot = current;
            return snapshot.ForDisplayAt(clock());
        }
    }

    public event EventHandler? Updated;

    public string? TryResolveManagedAsset(LauncherBannersAsset asset) =>
        cache.TryResolveBundledAsset(asset, bundledAssetsDirectory) ?? cache.TryResolveManagedAsset(asset);

    public void Start()
        => SetAutomaticRefreshEnabled(true);

    public void SetAutomaticRefreshEnabled(bool enabled)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            automaticRefreshEnabled = enabled;
            if (enabled) pump ??= PumpAsync();
        }
        if (enabled) _ = RefreshAsync();
    }

    public Task RefreshOnReactivationAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);
    public Task RefreshManualAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task<bool> RefreshCodesManualAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CodesRefreshResult> request;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            request = StartCodesRefreshAsync(publishEvent: true);
        }
        var result = await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            refresh ??= RunRefreshAsync();
            return refresh.WaitAsync(cancellationToken);
        }
    }

    private async Task RunRefreshAsync()
    {
        var changed = false;
        try
        {
            LauncherBannersManifest selected;
            lock (sync) selected = current;
            try
            {
                changed |= await cache.HydrateAssetsAsync(
                    selected,
                    transport,
                    bundledAssetsDirectory,
                    shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !shutdown.IsCancellationRequested)
            {
                // Stale art must not prevent fetching a newer manifest.
            }

            var payload = await transport.GetManifestAsync(endpoint, LauncherBannersTransport.MaximumManifestBytes, shutdown.Token).ConfigureAwait(false);
            var manifest = ApplyBundledUpcomingFallback(
                LauncherBannersManifestParser.Parse(payload, fallback: false, clock()),
                bundledManifest);
            var promote = true;
            lock (sync)
            {
                if (manifest.GeneratedAt < current.GeneratedAt)
                    throw new InvalidDataException("Launcher banner generation moved backwards.");
                if (manifest.GeneratedAt == current.GeneratedAt)
                {
                    if (!string.Equals(manifest.Revision, current.Revision, StringComparison.Ordinal))
                        throw new InvalidDataException("Launcher banner revision changed without a newer generation.");
                }
            }
            if (promote)
            {
                await cache.PromoteAsync(manifest, payload, transport, bundledAssetsDirectory, shutdown.Token).ConfigureAwait(false);
                if (!shutdown.IsCancellationRequested)
                {
                    lock (sync) current = currentCodes is null ? manifest : ApplyCodes(manifest, currentCodes);
                    changed = true;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !shutdown.IsCancellationRequested)
        {
            // Keep the current snapshot. If startup loaded a corrupt cache, the
            // bundled parser already supplied the complete last-resort payload.
        }
        try
        {
            var codesResult = await StartCodesRefreshAsync(publishEvent: false).ConfigureAwait(false);
            changed |= codesResult.Changed;
        }
        finally
        {
            if (changed) Updated?.Invoke(this, EventArgs.Empty);
            lock (sync) refresh = null;
        }
    }

    private Task<CodesRefreshResult> StartCodesRefreshAsync(bool publishEvent)
    {
        lock (sync) return codesRefresh ??= RunCodesRefreshAsync(publishEvent);
    }

    private async Task<CodesRefreshResult> RunCodesRefreshAsync(bool publishEvent)
    {
        var changed = false;
        try
        {
            var payload = await transport.GetManifestAsync(codesEndpoint, LauncherBannersTransport.MaximumManifestBytes, shutdown.Token).ConfigureAwait(false);
            var codes = LauncherBannersManifestParser.ParseCodes(payload, fallback: false, clock());
            if (shutdown.IsCancellationRequested) return new(false, false);
            var unchanged = false;
            lock (sync)
            {
                if (currentCodes is not null)
                {
                    if (codes.GeneratedAt < currentCodes.GeneratedAt)
                        throw new InvalidDataException("Launcher codes generation moved backwards.");
                    if (codes.GeneratedAt == currentCodes.GeneratedAt)
                    {
                        if (!string.Equals(codes.Revision, currentCodes.Revision, StringComparison.Ordinal))
                            throw new InvalidDataException("Launcher codes revision changed without a newer generation.");
                        unchanged = true;
                    }
                }
            }
            if (unchanged) return new(true, false);
            await cache.PromoteCodesAsync(codes, payload, shutdown.Token).ConfigureAwait(false);
            if (shutdown.IsCancellationRequested) return new(false, false);
            lock (sync)
            {
                if (currentCodes is not null)
                {
                    if (codes.GeneratedAt < currentCodes.GeneratedAt)
                        throw new InvalidDataException("Launcher codes generation moved backwards.");
                    if (codes.GeneratedAt == currentCodes.GeneratedAt)
                    {
                        if (!string.Equals(codes.Revision, currentCodes.Revision, StringComparison.Ordinal))
                            throw new InvalidDataException("Launcher codes revision changed without a newer generation.");
                        return new(true, false);
                    }
                }
                currentCodes = codes;
                current = ApplyCodes(current, codes);
            }
            changed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !shutdown.IsCancellationRequested)
        {
            // Keep the bundled or last successfully refreshed codes.
        }
        finally
        {
            lock (sync) codesRefresh = null;
        }
        if (changed && publishEvent) Updated?.Invoke(this, EventArgs.Empty);
        return new(changed, changed);
    }

    private sealed record CodesRefreshResult(bool Succeeded, bool Changed);

    private static LauncherBannersManifest ApplyCodes(LauncherBannersManifest bannerManifest, LauncherCodesManifest codesManifest)
    {
        var games = bannerManifest.Games.ToDictionary(
            pair => pair.Key,
            pair => new LauncherBannersGame(
                pair.Value.GameId,
                pair.Value.Region,
                pair.Value.Current,
                pair.Value.News,
                pair.Value.Upcoming,
                codesManifest.Games[pair.Key]),
            StringComparer.Ordinal);
        return new LauncherBannersManifest(
            bannerManifest.SchemaVersion,
            bannerManifest.Revision,
            bannerManifest.GeneratedAt,
            bannerManifest.Health,
            games);
    }

    internal static LauncherBannersManifest ApplyBundledUpcomingFallback(
        LauncherBannersManifest manifest,
        LauncherBannersManifest bundled)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bundled);

        var changed = false;
        var games = manifest.Games.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var remoteGame = pair.Value;
                if (!bundled.Games.TryGetValue(pair.Key, out var bundledGame))
                {
                    return remoteGame;
                }

                var useBundledUpcoming = manifest.GeneratedAt <= bundled.GeneratedAt
                    && remoteGame.Upcoming.Count == 0
                    && bundledGame.Upcoming.Count > 0;
                if (!useBundledUpcoming) return remoteGame;

                changed = true;
                return new LauncherBannersGame(
                    remoteGame.GameId,
                    remoteGame.Region,
                    remoteGame.Current,
                    remoteGame.News,
                    useBundledUpcoming ? bundledGame.Upcoming : remoteGame.Upcoming,
                    remoteGame.Codes);
            },
            StringComparer.Ordinal);

        return changed
            ? new LauncherBannersManifest(
                manifest.SchemaVersion,
                manifest.Revision,
                manifest.GeneratedAt,
                manifest.Health,
                games)
            : manifest;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                LauncherBannersManifest snapshot;
                lock (sync) snapshot = current;
                await Task.Delay(CalculateNextRefreshDelay(snapshot, clock(), interval), shutdown.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (!automaticRefreshEnabled) continue;
                }
                await RefreshAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    internal static TimeSpan CalculateNextRefreshDelay(
        LauncherBannersManifest manifest,
        DateTimeOffset now,
        TimeSpan regularInterval)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (regularInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(regularInterval));

        var nextExpiry = manifest.Games.Values
            .SelectMany(static game => game.Upcoming.Select(phase => (DateTimeOffset?)phase.Start)
                .Prepend(game.Current?.EffectiveEnd))
            .Where(static boundary => boundary.HasValue)
            .Select(static boundary => boundary!.Value)
            .Where(boundary => boundary > now)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        if (nextExpiry == DateTimeOffset.MaxValue) return regularInterval;

        // A short grace period lets the upstream banner feed finish switching
        // phases instead of repeatedly fetching at the exact boundary.
        var untilExpiry = nextExpiry - now + TimeSpan.FromSeconds(30);
        return untilExpiry < regularInterval ? untilExpiry : regularInterval;
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingRefresh;
        Task? pendingCodesRefresh;
        Task? pendingPump;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            pendingRefresh = refresh;
            pendingCodesRefresh = codesRefresh;
            pendingPump = pump;
        }
        try { await Task.WhenAll(pendingRefresh ?? Task.CompletedTask, pendingCodesRefresh ?? Task.CompletedTask, pendingPump ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        shutdown.Dispose();
        if (transport is IDisposable disposable) disposable.Dispose();
    }
}
