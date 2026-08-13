namespace Nyx.Desktop.Core.PublisherGames;

public static class BoundedMaintenanceObservation
{
    public static async Task<T> ObserveAsync<T>(
        Func<CancellationToken, Task<T>> observeAsync,
        Func<T, bool> isConclusive,
        int maximumObservations,
        TimeSpan interval,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(observeAsync);
        ArgumentNullException.ThrowIfNull(isConclusive);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumObservations);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);

        delayAsync ??= Task.Delay;
        T? latest = default;
        for (var observation = 0; observation < maximumObservations; observation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await delayAsync(interval, cancellationToken).ConfigureAwait(false);
            latest = await observeAsync(cancellationToken).ConfigureAwait(false);
            if (isConclusive(latest))
            {
                return latest;
            }
        }

        return latest!;
    }
}
