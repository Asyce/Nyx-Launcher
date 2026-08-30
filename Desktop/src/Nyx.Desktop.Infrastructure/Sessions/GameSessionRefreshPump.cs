using Nyx.Desktop.Core.Sessions;

namespace Nyx.Desktop.Infrastructure.Sessions;

public enum SystemSuspendResumeEvent
{
    Ignore,
    Suspend,
    AutomaticResume,
}

public sealed class GameSessionsRefreshedEventArgs(
    IReadOnlyDictionary<string, GameSessionSnapshot> snapshots,
    bool resetsAfterSystemResume = false) : EventArgs
{
    public IReadOnlyDictionary<string, GameSessionSnapshot> Snapshots { get; } =
        snapshots ?? throw new ArgumentNullException(nameof(snapshots));

    public bool ResetsAfterSystemResume { get; } = resetsAfterSystemResume;
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
    private long publicationEpoch;
    private bool suspended;
    private bool resumeRequested;
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

    public event EventHandler? SystemSuspending;

    public static SystemSuspendResumeEvent ClassifyPowerBroadcast(uint eventType) =>
        eventType switch
        {
            4 => SystemSuspendResumeEvent.Suspend,
            0x12 => SystemSuspendResumeEvent.AutomaticResume,
            _ => SystemSuspendResumeEvent.Ignore,
        };

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
        await RunAdmittedRefreshAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>>
        ResetAfterResumeAndRefreshAsync(CancellationToken cancellationToken = default)
    {
        _ = RequestSystemSuspend();
        _ = RequestSystemResume();
        return await RunAdmittedRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes publication at the exact native suspend boundary.</summary>
    public bool RequestSystemSuspend()
    {
        lock (publicationSync)
        {
            if (stopped != 0 || suspended)
            {
                return false;
            }

            suspended = true;
            resumeRequested = false;
            publicationEpoch++;
            var handlers = SystemSuspending;
            if (handlers is not null)
            {
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                        // A subscriber cannot reopen publication across sleep.
                    }
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Requests the coordinator ownership reset before any refresh can wait on
    /// the publication gate. One automatic resume is accepted per suspend.
    /// </summary>
    public bool RequestSystemResume()
    {
        lock (publicationSync)
        {
            if (stopped != 0 || !suspended || resumeRequested)
            {
                return false;
            }

            resumeRequested = true;
            publicationEpoch++;
        }

        try
        {
            coordinator.ResetAfterSystemResumeAsync(lifetimeToken)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (Exception)
        {
            // Remain suspended and fail closed if the reset cannot be requested.
            lock (publicationSync)
            {
                if (suspended)
                {
                    resumeRequested = false;
                    publicationEpoch++;
                }
            }

            return false;
        }
    }

    /// <summary>Excludes refresh publication while one state/adapter swap commits.</summary>
    public async ValueTask<IDisposable?> TryAcquireExclusivePublicationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryAdmitInvocation())
        {
            return null;
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
            ReleaseInvocation();
            return null;
        }

        if (Volatile.Read(ref stopped) != 0)
        {
            refreshGate.Release();
            ReleaseInvocation();
            return null;
        }

        return new ExclusivePublicationLease(this);
    }

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

            long epoch;
            bool resetsAfterSystemResume;
            lock (publicationSync)
            {
                epoch = publicationEpoch;
                resetsAfterSystemResume = suspended && resumeRequested;
            }

            var snapshots = await coordinator
                .RefreshAllAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            Publish(snapshots, epoch, resetsAfterSystemResume);
            return snapshots;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, GameSessionSnapshot>> RunAdmittedRefreshAsync(
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

            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
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

    private void Publish(
        IReadOnlyDictionary<string, GameSessionSnapshot> snapshots,
        long epoch,
        bool resetsAfterSystemResume)
    {
        lock (publicationSync)
        {
            if (stopped != 0 || epoch != publicationEpoch)
            {
                return;
            }

            if (resetsAfterSystemResume)
            {
                if (!suspended || !resumeRequested)
                {
                    return;
                }

                suspended = false;
                resumeRequested = false;
            }
            else if (suspended)
            {
                return;
            }

            var handlers = Refreshed;
            if (handlers is null)
            {
                return;
            }

            var args = new GameSessionsRefreshedEventArgs(
                snapshots,
                resetsAfterSystemResume);
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
        refreshGate.Dispose();
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

    private void ReleaseExclusivePublication()
    {
        refreshGate.Release();
        ReleaseInvocation();
    }

    private sealed class ExclusivePublicationLease(GameSessionRefreshPump owner) : IDisposable
    {
        private GameSessionRefreshPump? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.ReleaseExclusivePublication();
        }
    }
}
