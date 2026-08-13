namespace Nyx.Desktop.Infrastructure.Sessions;

public sealed class SessionUiLease
{
    internal SessionUiLease(long generation, CancellationToken cancellationToken)
    {
        Generation = generation;
        CancellationToken = cancellationToken;
    }

    internal long Generation { get; }

    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// A small lifetime barrier for queued view work. Deactivation cancels outstanding
/// operations and waits for any mutation already admitted by <see cref="TryRun"/>.
/// A callback carrying an older lease can never mutate a later page lifetime.
/// </summary>
public sealed class SessionUiLifetime
{
    private readonly object sync = new();
    private CancellationTokenSource? current;
    private long generation;
    private bool active;
    private bool terminated;

    public SessionUiLease Activate()
    {
        CancellationTokenSource? previous;
        SessionUiLease lease;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(terminated, this);
            previous = current;
            current = new CancellationTokenSource();
            active = true;
            lease = new(++generation, current.Token);
        }

        CancelAndDispose(previous);
        return lease;
    }

    public bool TryRun(SessionUiLease lease, Action action)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(action);
        lock (sync)
        {
            if (terminated
                || !active
                || current is null
                || lease.Generation != generation
                || lease.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public void Deactivate(SessionUiLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        CancellationTokenSource? previous;
        lock (sync)
        {
            if (terminated
                || !active
                || current is null
                || lease.Generation != generation)
            {
                return;
            }

            active = false;
            previous = current;
            current = null;
            generation++;
        }

        CancelAndDispose(previous);
    }

    public void Terminate()
    {
        CancellationTokenSource? previous;
        lock (sync)
        {
            if (terminated)
            {
                return;
            }

            terminated = true;
            active = false;
            previous = current;
            current = null;
            generation++;
        }

        CancelAndDispose(previous);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (Exception)
        {
            // The lifetime is already invalid. A linked operation's cancellation
            // callback cannot reopen it or block window/page teardown.
        }
        finally
        {
            source.Dispose();
        }
    }
}
