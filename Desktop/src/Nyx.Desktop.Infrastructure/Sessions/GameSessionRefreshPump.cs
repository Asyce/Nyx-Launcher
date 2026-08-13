using Nyx.Desktop.Core.Sessions;

namespace Nyx.Desktop.Infrastructure.Sessions;

public sealed class GameSessionsRefreshedEventArgs(
    IReadOnlyDictionary<string, GameSessionSnapshot> snapshots) : EventArgs
{
    public IReadOnlyDictionary<string, GameSessionSnapshot> Snapshots { get; } =
        snapshots ?? throw new ArgumentNullException(nameof(snapshots));
}

/// <summary>
/// Runs one non-overlapping, cancellable refresh stream for an app-lifetime coordinator.
/// It has no launch capability and never turns an observed close into a launch request.
/// </summary>
public sealed class GameSessionRefreshPump : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly GameSessionCoordinator coordinator;
    private readonly TimeSpan refreshInterval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object publicationSync = new();
    private readonly object disposalSync = new();
    private readonly object admissionSync = new();
    private readonly Func<ValueTask>? afterAdmission;
    private Task? runner;
    private Task? disposal;
    private TaskCompletionSource? invocationsDrained;
    private int activeInvocations;
    private bool admissionClosed;
    private int started;
    private int stopped;

    public GameSessionRefreshPump(
        GameSessionCoordinator coordinator,
        TimeSpan? refreshInterval = null)
        : this(coordinator, refreshInterval, afterAdmission: null)
    {
    }

    internal GameSessionRefreshPump(
        GameSessionCoordinator coordinator,
        TimeSpan? refreshInterval,
        Func<ValueTask>? afterAdmission)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.afterAdmission = afterAdmission;
        lifetimeToken = lifetime.Token;
        this.refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        if (this.refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }
    }

    public event EventHandler<GameSessionsRefreshedEventArgs>? Refreshed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        runner = RunAsync();
    }

    public async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>> RefreshNowAsync(
        CancellationToken cancellationToken = default) =>
        await RunAdmittedRefreshAsync(resetAfterResume: false, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>>
        ResetAfterResumeAndRefreshAsync(CancellationToken cancellationToken = default) =>
        await RunAdmittedRefreshAsync(resetAfterResume: true, cancellationToken).ConfigureAwait(false);

    public void Stop()
    {
        lock (publicationSync)
        {
            if (stopped != 0)
            {
                return;
            }

            Volatile.Write(ref stopped, 1);
        }

        try
        {
            lifetime.Cancel();
        }
        catch (Exception)
        {
            // State is already stopped. Untrusted linked cancellation callbacks
            // cannot reopen publication or prevent disposal from draining work.
        }

        _ = CloseAdmission();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>> RefreshCoreAsync(
        bool resetAfterResume,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref stopped) != 0)
        {
            return coordinator.GetAllSnapshots();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        try
        {
            await refreshGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return coordinator.GetAllSnapshots();
        }

        try
        {
            if (Volatile.Read(ref stopped) != 0)
            {
                return coordinator.GetAllSnapshots();
            }

            if (resetAfterResume)
            {
                await coordinator
                    .ResetAfterSystemResumeAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

            var snapshots = await coordinator
                .RefreshAllAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            Publish(snapshots);
            return snapshots;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>> RunAdmittedRefreshAsync(
        bool resetAfterResume,
        CancellationToken cancellationToken)
    {
        if (!TryAdmitInvocation())
        {
            return coordinator.GetAllSnapshots();
        }

        try
        {
            if (afterAdmission is not null)
            {
                await afterAdmission().ConfigureAwait(false);
            }

            return await RefreshCoreAsync(resetAfterResume, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseInvocation();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshNowAsync(lifetimeToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(lifetimeToken).ConfigureAwait(false))
            {
                await RefreshNowAsync(lifetimeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void Publish(IReadOnlyDictionary<string, GameSessionSnapshot> snapshots)
    {
        lock (publicationSync)
        {
            if (stopped != 0)
            {
                return;
            }

            var handlers = Refreshed;
            if (handlers is null)
            {
                return;
            }

            var args = new GameSessionsRefreshedEventArgs(snapshots);
            foreach (EventHandler<GameSessionsRefreshedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception)
                {
                    // A view subscriber cannot stop session observation for the app.
                }
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        var drain = CloseAdmission();
        if (runner is not null)
        {
            try
            {
                await runner.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await drain.ConfigureAwait(false);
        lifetime.Dispose();
    }

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
}
