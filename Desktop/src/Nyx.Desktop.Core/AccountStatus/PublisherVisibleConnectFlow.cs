namespace Nyx.Desktop.Core.AccountStatus;

public enum PublisherVisibleConnectCompletion
{
    Canceled,
    Done,
}

public enum PublisherVisibleConnectNavigationOutcome
{
    Succeeded,
    PolicyCanceled,
    WebViewCanceled,
    NetworkFailure,
    BrowserFailure,
    TimedOut,
}

public sealed record PublisherVisibleConnectPresentation(
    bool Ready,
    bool ShowRetry,
    string? Guidance)
{
    public static PublisherVisibleConnectPresentation ReadyToSignIn { get; } =
        new(true, false, "Sign in on the official page. Nyx will finish automatically; choose Done if needed.");

    public static PublisherVisibleConnectPresentation NavigationFailed { get; } =
        new(
            false,
            true,
            "The official sign-in page did not load. Check your connection, then choose Retry or close this window.");

    public static PublisherVisibleConnectPresentation NavigationBlocked { get; } =
        new(
            false,
            false,
            "Nyx blocked a page that left the reviewed official sign-in addresses. Nyx needs an update before this page can be opened safely. Close this window.");

}

public static class PublisherVisibleConnectFlow
{
    public static bool IsCurrentHsrAchievementRequest(
        string? currentToken,
        string? currentUri,
        string? responseToken,
        string? responseUri) =>
        !string.IsNullOrEmpty(currentToken)
        && !string.IsNullOrEmpty(currentUri)
        && string.Equals(currentToken, responseToken, StringComparison.Ordinal)
        && string.Equals(currentUri, responseUri, StringComparison.Ordinal);

    public static bool ShouldAutoComplete(
        bool isHsrAchievementConnect,
        bool baselineEstablished,
        bool wasAuthenticated,
        bool authenticated,
        bool achievementPageReady) =>
        isHsrAchievementConnect
            ? authenticated && achievementPageReady
            : baselineEstablished && !wasAuthenticated && authenticated;

    public static async Task<PublisherConnectionState> CompleteAsync(
        PublisherVisibleConnectCompletion completion,
        Func<CancellationToken, Task<PublisherSessionProof>> probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (completion is PublisherVisibleConnectCompletion.Canceled)
            return PublisherConnectionState.NeedsReview;
        if (completion is not PublisherVisibleConnectCompletion.Done)
            throw new ArgumentOutOfRangeException(nameof(completion));

        var proof = await probe(cancellationToken);
        return PublisherAccountStatePolicy.ForSessionProof(proof);
    }

    public static async Task<PublisherVisibleConnectPresentation> AttemptPageAsync(
        Func<CancellationToken, Task<PublisherVisibleConnectNavigationOutcome>> navigate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        try
        {
            var navigationOutcome = await navigate(cancellationToken);
            if (navigationOutcome is not PublisherVisibleConnectNavigationOutcome.Succeeded)
            {
                return navigationOutcome is PublisherVisibleConnectNavigationOutcome.PolicyCanceled
                    ? PublisherVisibleConnectPresentation.NavigationBlocked
                    : PublisherVisibleConnectPresentation.NavigationFailed;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PublisherVisibleConnectPresentation.NavigationFailed;
        }

        return PublisherVisibleConnectPresentation.ReadyToSignIn;
    }

}
