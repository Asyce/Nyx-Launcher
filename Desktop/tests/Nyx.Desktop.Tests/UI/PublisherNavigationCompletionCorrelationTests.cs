using Nyx_Desktop_App;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.UI;

public sealed class PublisherNavigationCompletionCorrelationTests
{
    private static readonly Uri IntendedUri =
        new("https://act.hoyolab.com/app/zzz-game-record/index.html#/zzz");

    [Fact]
    public async Task Unrelated_completion_cannot_finish_the_intended_navigation()
    {
        var correlation = new PublisherNavigationCompletionCorrelation(IntendedUri);

        Assert.False(correlation.TryObserveCompleted(
            40,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));
        Assert.True(correlation.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            41,
            isRedirected: false));
        Assert.False(correlation.TryObserveCompleted(
            40,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));
        Assert.True(correlation.TryObserveCompleted(
            41,
            PublisherVisibleConnectNavigationOutcome.Succeeded));

        Assert.Equal(PublisherVisibleConnectNavigationOutcome.Succeeded, await correlation.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Matching_failed_navigation_is_not_hidden()
    {
        var correlation = new PublisherNavigationCompletionCorrelation(IntendedUri);

        Assert.True(correlation.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            51,
            isRedirected: false));
        Assert.True(correlation.TryObserveCompleted(
            51,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));

        Assert.Equal(PublisherVisibleConnectNavigationOutcome.NetworkFailure, await correlation.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Redirects_keep_the_initial_navigation_id()
    {
        var correlation = new PublisherNavigationCompletionCorrelation(IntendedUri);

        Assert.True(correlation.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            61,
            isRedirected: false));
        Assert.True(correlation.TryObserveStarting(
            "https://act.hoyolab.com/app/zzz-game-record/index.html#/zzz/overview",
            61,
            isRedirected: true));
        Assert.False(correlation.TryObserveStarting(
            "https://act.hoyolab.com/unrelated",
            62,
            isRedirected: true));
        Assert.False(correlation.TryObserveCompleted(
            62,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));
        Assert.True(correlation.TryObserveCompleted(
            61,
            PublisherVisibleConnectNavigationOutcome.Succeeded));

        Assert.Equal(PublisherVisibleConnectNavigationOutcome.Succeeded, await correlation.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Wait_preserves_timeout_and_cancellation()
    {
        var timedOut = new PublisherNavigationCompletionCorrelation(IntendedUri);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            timedOut.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        var canceled = new PublisherNavigationCompletionCorrelation(IntendedUri);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            canceled.WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public async Task Unsafe_or_mismatched_navigation_cannot_take_over_the_wait()
    {
        var correlation = new PublisherNavigationCompletionCorrelation(IntendedUri);

        Assert.False(correlation.TryObserveStarting(
            "https://act.hoyolab.com.attacker.example/app/zzz-game-record/index.html#/zzz",
            71,
            isRedirected: false));
        Assert.False(correlation.TryObserveCompleted(
            71,
            PublisherVisibleConnectNavigationOutcome.Succeeded));

        Assert.True(correlation.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            72,
            isRedirected: false));
        Assert.True(correlation.TryObserveStarting(
            "https://example.com/blocked-redirect",
            72,
            isRedirected: true,
            wasPolicyCanceled: true));
        Assert.True(correlation.TryObserveCompleted(
            72,
            PublisherVisibleConnectNavigationOutcome.WebViewCanceled));

        Assert.Equal(PublisherVisibleConnectNavigationOutcome.PolicyCanceled, await correlation.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(PublisherVisibleConnectNavigationOutcome.WebViewCanceled)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.NetworkFailure)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.BrowserFailure)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.TimedOut)]
    public async Task Safe_webview_failure_classification_is_preserved(
        PublisherVisibleConnectNavigationOutcome outcome)
    {
        var correlation = new PublisherNavigationCompletionCorrelation(IntendedUri);

        Assert.True(correlation.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            81,
            isRedirected: false));
        Assert.True(correlation.TryObserveCompleted(81, outcome));

        Assert.Equal(outcome, await correlation.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
        Assert.DoesNotContain(IntendedUri.AbsoluteUri, outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_retry_correlation_cannot_be_completed_by_the_prior_attempt()
    {
        var priorAttempt = new PublisherNavigationCompletionCorrelation(IntendedUri);
        var retryAttempt = new PublisherNavigationCompletionCorrelation(IntendedUri);
        Assert.True(priorAttempt.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            91,
            isRedirected: false));
        Assert.True(priorAttempt.TryObserveCompleted(
            91,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));

        Assert.False(retryAttempt.TryObserveCompleted(
            91,
            PublisherVisibleConnectNavigationOutcome.NetworkFailure));
        Assert.True(retryAttempt.TryObserveStarting(
            IntendedUri.AbsoluteUri,
            92,
            isRedirected: false));
        Assert.True(retryAttempt.TryObserveCompleted(
            92,
            PublisherVisibleConnectNavigationOutcome.Succeeded));

        Assert.Equal(PublisherVisibleConnectNavigationOutcome.Succeeded, await retryAttempt.WaitAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }
}
