using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherVisibleConnectFlowTests
{
    [Fact]
    public void Achievement_response_requires_the_current_concrete_request_token()
    {
        const string uri = "https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?page_num=1";

        Assert.True(PublisherVisibleConnectFlow.IsCurrentHsrAchievementRequest(
            "new-request",
            uri,
            "new-request",
            uri));
        Assert.False(PublisherVisibleConnectFlow.IsCurrentHsrAchievementRequest(
            "new-request",
            uri,
            "old-same-uri-request",
            uri));
        Assert.False(PublisherVisibleConnectFlow.IsCurrentHsrAchievementRequest(
            "second-overlapping-request",
            uri,
            "first-overlapping-request",
            uri));
        Assert.False(PublisherVisibleConnectFlow.IsCurrentHsrAchievementRequest(
            "new-request",
            uri,
            "new-request",
            uri + "&page_num=2"));
    }

    [Theory]
    [InlineData(false, false, false, true, true, false)]
    [InlineData(false, true, false, true, false, true)]
    [InlineData(false, true, true, true, true, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, true, false, false, true, false)]
    public void Auto_completion_uses_auth_transition_except_for_ready_hsr_achievements(
        bool isHsrAchievementConnect,
        bool baselineEstablished,
        bool wasAuthenticated,
        bool authenticated,
        bool achievementPageReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublisherVisibleConnectFlow.ShouldAutoComplete(
                isHsrAchievementConnect,
                baselineEstablished,
                wasAuthenticated,
                authenticated,
                achievementPageReady));
    }

    [Fact]
    public async Task Close_cancels_without_probe_and_returns_a_retryable_terminal_state()
    {
        var probes = 0;

        var state = await PublisherVisibleConnectFlow.CompleteAsync(
            PublisherVisibleConnectCompletion.Canceled,
            _ =>
            {
                probes++;
                return Task.FromResult(PublisherSessionProof.Authenticated);
            });

        Assert.Equal(0, probes);
        Assert.Equal(PublisherConnectionState.NeedsReview, state);
        Assert.NotEqual(PublisherConnectionState.Connecting, state);
    }

    [Fact]
    public async Task Done_runs_the_hidden_probe_and_projects_its_result()
    {
        var probes = 0;

        var state = await PublisherVisibleConnectFlow.CompleteAsync(
            PublisherVisibleConnectCompletion.Done,
            _ =>
            {
                probes++;
                return Task.FromResult(PublisherSessionProof.Authenticated);
            });

        Assert.Equal(1, probes);
        Assert.Equal(PublisherConnectionState.Connected, state);
    }

    [Fact]
    public async Task Navigation_exception_shows_a_visible_retry_path()
    {
        var presentation = await PublisherVisibleConnectFlow.AttemptPageAsync(
            _ => throw new InvalidOperationException("simulated-navigation-failure"));

        Assert.False(presentation.Ready);
        Assert.True(presentation.ShowRetry);
        Assert.Contains("did not load", presentation.Guidance, StringComparison.Ordinal);
        Assert.Contains("Retry", presentation.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_navigation_is_ready_for_manual_sign_in_without_retry()
    {
        var navigationCalls = 0;
        var presentation = await PublisherVisibleConnectFlow.AttemptPageAsync(
            _ =>
            {
                navigationCalls++;
                return Task.FromResult(PublisherVisibleConnectNavigationOutcome.Succeeded);
            });

        Assert.Equal(1, navigationCalls);
        Assert.True(presentation.Ready);
        Assert.False(presentation.ShowRetry);
        Assert.Equal(
            "Sign in on the official page. Nyx will finish automatically; choose Done if needed.",
            presentation.Guidance);
    }

    [Theory]
    [InlineData(PublisherVisibleConnectNavigationOutcome.NetworkFailure)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.BrowserFailure)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.WebViewCanceled)]
    [InlineData(PublisherVisibleConnectNavigationOutcome.TimedOut)]
    public async Task Direct_navigation_failure_shows_retry(
        PublisherVisibleConnectNavigationOutcome outcome)
    {
        var presentation = await PublisherVisibleConnectFlow.AttemptPageAsync(
            _ => Task.FromResult(outcome));

        Assert.False(presentation.Ready);
        Assert.True(presentation.ShowRetry);
        Assert.Contains("did not load", presentation.Guidance, StringComparison.Ordinal);
        Assert.Contains("Retry", presentation.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_canceled_navigation_stops_without_retry()
    {
        var presentation = await PublisherVisibleConnectFlow.AttemptPageAsync(
            _ => Task.FromResult(PublisherVisibleConnectNavigationOutcome.PolicyCanceled));

        Assert.False(presentation.Ready);
        Assert.False(presentation.ShowRetry);
        Assert.Contains("needs an update", presentation.Guidance, StringComparison.Ordinal);
        Assert.Contains("Close", presentation.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_stops_navigation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublisherVisibleConnectFlow.AttemptPageAsync(
                token => Task.FromCanceled<PublisherVisibleConnectNavigationOutcome>(token),
                cancellation.Token));
    }
}
