namespace Nyx_Desktop_App;

internal sealed class HoyoLabExportUiReservation
{
    private int held;

    public bool IsHeld => Volatile.Read(ref held) != 0;

    public Lease? TryAcquire() =>
        Interlocked.CompareExchange(ref held, 1, 0) == 0
            ? new Lease(this)
            : null;

    private void Release() => Volatile.Write(ref held, 0);

    internal sealed class Lease : IDisposable
    {
        private HoyoLabExportUiReservation? owner;

        internal Lease(HoyoLabExportUiReservation owner) => this.owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Release();
        }
    }
}

internal static class ExportUiJobRetention
{
    public static void RememberLatest<TState>(
        IDictionary<string, Guid> latestJobs,
        ISet<Guid> immediateJobs,
        IDictionary<Guid, TState> handoffs,
        string gameId,
        Guid jobId)
    {
        if (latestJobs.TryGetValue(gameId, out var previousJobId)
            && previousJobId != jobId)
        {
            immediateJobs.Remove(previousJobId);
            handoffs.Remove(previousJobId);
        }

        latestJobs[gameId] = jobId;
    }

    public static bool TrySetHandoff<TState>(
        IReadOnlyDictionary<string, Guid> latestJobs,
        IDictionary<Guid, TState> handoffs,
        string gameId,
        Guid jobId,
        TState state)
    {
        if (!latestJobs.TryGetValue(gameId, out var latestJobId)
            || latestJobId != jobId)
        {
            return false;
        }

        handoffs[jobId] = state;
        return true;
    }
}
