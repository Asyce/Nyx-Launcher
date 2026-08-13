namespace Nyx.Desktop.Core.AccountStatus;

public static class PublisherSessionProofRetryPolicy
{
    private const int MaximumAttempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<PublisherSessionProof> RunAsync(
        Func<CancellationToken, Task<PublisherSessionProof>> proveAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proveAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proof = await proveAsync(cancellationToken);
            if (proof != PublisherSessionProof.LoginRequired
                || attempt + 1 == MaximumAttempts)
                return proof;

            await delayAsync(RetryDelay, cancellationToken);
        }

        throw new InvalidOperationException("Publisher session proof retry bounds were not enforced.");
    }
}
