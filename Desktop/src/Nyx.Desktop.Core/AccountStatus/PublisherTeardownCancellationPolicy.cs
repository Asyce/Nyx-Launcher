namespace Nyx.Desktop.Core.AccountStatus;

public static class PublisherTeardownCancellationPolicy
{
    public static void ThrowIfCanceled(
        CancellationToken cancellationToken,
        Exception teardownException)
    {
        ArgumentNullException.ThrowIfNull(teardownException);
        if (!cancellationToken.IsCancellationRequested) return;

        throw new OperationCanceledException(
            "The publisher connection was canceled during browser teardown.",
            teardownException,
            cancellationToken);
    }
}
