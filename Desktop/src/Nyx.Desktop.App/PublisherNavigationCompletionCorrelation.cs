using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

internal sealed class PublisherNavigationCompletionCorrelation
{
    private readonly object sync = new();
    private readonly string intendedAbsoluteUri;
    private readonly TaskCompletionSource<PublisherVisibleConnectNavigationOutcome> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong? intendedNavigationId;
    private bool policyCanceled;

    public PublisherNavigationCompletionCorrelation(Uri intendedUri)
    {
        ArgumentNullException.ThrowIfNull(intendedUri);
        if (!intendedUri.IsAbsoluteUri)
            throw new ArgumentException("An absolute URI is required.", nameof(intendedUri));
        intendedAbsoluteUri = intendedUri.AbsoluteUri;
    }

    public bool TryObserveStarting(
        string absoluteUri,
        ulong navigationId,
        bool isRedirected,
        bool wasPolicyCanceled = false)
    {
        lock (sync)
        {
            if (intendedNavigationId is ulong capturedId)
            {
                if (capturedId != navigationId)
                    return false;
                policyCanceled |= wasPolicyCanceled;
                return true;
            }
            if (isRedirected
                || !string.Equals(
                    absoluteUri,
                    intendedAbsoluteUri,
                    StringComparison.Ordinal))
                return false;

            intendedNavigationId = navigationId;
            policyCanceled = wasPolicyCanceled;
            return true;
        }
    }

    public bool TryObserveCompleted(
        ulong navigationId,
        PublisherVisibleConnectNavigationOutcome webViewOutcome)
    {
        lock (sync)
        {
            return intendedNavigationId == navigationId
                && completion.TrySetResult(
                    policyCanceled
                        ? PublisherVisibleConnectNavigationOutcome.PolicyCanceled
                        : webViewOutcome);
        }
    }

    public Task<PublisherVisibleConnectNavigationOutcome> WaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        completion.Task.WaitAsync(timeout, cancellationToken);
}
