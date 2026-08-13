using Nyx.Desktop.Core.PublisherMaintenance;

namespace Nyx.Desktop.Infrastructure.PublisherMaintenance;

/// <summary>
/// Owns one conservative, non-overlapping app-lifetime publisher-status stream.
/// It is advisory only and has no launch, updater, package, or file capability.
/// </summary>
public sealed class HoyoPublisherStatusSource : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    private readonly object sync = new();
    private readonly HoyoPublisherStatusService service;
    private readonly Func<HoyoLocalVersions> localVersions;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource shutdown = new();
    private Task? refresh;
    private Task? pump;
    private HoyoPublisherStatusResult? current;
    private bool disposed;

    public HoyoPublisherStatusSource(Func<HoyoLocalVersions> localVersions)
        : this(new HoyoPublisherStatusService(), localVersions, DefaultInterval)
    {
    }

    internal HoyoPublisherStatusSource(
        HoyoPublisherStatusService service,
        Func<HoyoLocalVersions> localVersions,
        TimeSpan interval)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.localVersions = localVersions ?? throw new ArgumentNullException(nameof(localVersions));
        this.interval = interval;
        if (interval < TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public HoyoPublisherStatusResult? Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public event EventHandler? Updated;

    public void Start()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pump ??= PumpAsync();
        }

        _ = RefreshAsync();
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
        await Task.Yield();
        try
        {
            HoyoLocalVersions versions;
            try
            {
                versions = localVersions();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return;
            }

            var result = await service.RefreshAsync(
                versions,
                PublisherRefreshIntent.Automatic,
                shutdown.Token).ConfigureAwait(false);
            if (shutdown.IsCancellationRequested)
            {
                return;
            }

            lock (sync)
            {
                current = result;
            }

            Publish();
        }
        finally
        {
            lock (sync)
            {
                refresh = null;
            }
        }
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(shutdown.Token).ConfigureAwait(false))
            {
                await RefreshAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private void Publish()
    {
        var handlers = Updated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Publisher status is optional; a view cannot stop its refresh loop.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingRefresh;
        Task? pendingPump;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            shutdown.Cancel();
            pendingRefresh = refresh;
            pendingPump = pump;
        }

        try
        {
            await Task.WhenAll(
                pendingRefresh ?? Task.CompletedTask,
                pendingPump ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await service.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }
}
