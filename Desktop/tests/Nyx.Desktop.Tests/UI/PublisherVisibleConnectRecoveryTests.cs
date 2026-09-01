using Nyx_Desktop_App;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.UI;

public sealed class PublisherVisibleConnectRecoveryTests
{
    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    [InlineData("zzz")]
    public void Ordinary_HoYoLAB_add_and_connect_start_at_the_reviewed_login(string gameId)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        var initialUri = PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry);

        Assert.Equal(
            new Uri("https://account.hoyolab.com/login-platform/index.html?app_id=c9oqaq3s3gu8"),
            initialUri);
        Assert.True(PublisherVisibleConnectNavigationPolicy.IsAllowedInitial(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            gameId,
            initialUri));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            gameId,
            initialUri,
            "GET",
            PublisherWebResourceContext.Document));
    }

    [Fact]
    public void Endfield_connect_starts_at_the_normal_sign_in_page()
    {
        var entry = PublisherAccountCatalog.Get("ae");

        Assert.Same(entry.CheckInUri, PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry));
    }

    [Fact]
    public void Endfield_connect_hides_only_the_SKPORT_download_banner()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var initialization = Slice(
            source,
            "var core = await InitializeBrowserProfileAsync",
            "core.NavigationStarting += Core_NavigationStarting");

        Assert.Contains("visible", initialization, StringComparison.Ordinal);
        Assert.Contains("purpose == PublisherSessionPurpose.Connect", initialization, StringComparison.Ordinal);
        Assert.Contains("provider == \"SKPORT\"", initialization, StringComparison.Ordinal);
        Assert.Contains("gameId == \"ae\"", initialization, StringComparison.Ordinal);
        Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync", initialization, StringComparison.Ordinal);
        Assert.Contains("img.mobile-logo", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("input", initialization, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("form", initialization, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "about:blank", true, true)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "about:blank", false, false)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://accounts.google.com/", true, false)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "hsr", "about:blank", true, false)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "ae", "about:blank", true, false)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "about:blank", true, false)]
    public void Only_the_user_clicked_Endfield_social_login_popup_is_released_to_WebView2(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        string target,
        bool isUserInitiated,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublisherVisibleConnectNavigationPolicy.IsAllowedPopup(
                provider,
                purpose,
                gameId,
                target,
                isUserInitiated));
    }

    [Fact]
    public void Endfield_social_login_popup_stays_in_the_owned_profile_and_under_host_guards()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var popup = Slice(
            source,
            "private async void Core_NewWindowRequested",
            "private bool IsAllowedConnectTopLevel");

        Assert.DoesNotContain("args.Handled = false", popup, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", popup, StringComparison.Ordinal);
        Assert.Contains("using var deferral = args.GetDeferral()", popup, StringComparison.Ordinal);
        Assert.Contains("OpenSocialLoginWindowAsync(sender.Environment, args)", popup, StringComparison.Ordinal);
        Assert.Contains("popupBrowser.EnsureCoreWebView2Async(environment)", popup, StringComparison.Ordinal);
        Assert.Contains("args.NewWindow = core", popup, StringComparison.Ordinal);
        Assert.Contains("core.NavigationStarting += Core_SocialLoginNavigationStarting", popup, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2WebResourceContext.Document", popup, StringComparison.Ordinal);
        Assert.Contains("core.WebResourceRequested += Core_SocialLoginWebResourceRequested", popup, StringComparison.Ordinal);
        Assert.Contains("TryBlockWebResourceRequest(sender, args)", popup, StringComparison.Ordinal);
        Assert.Contains("core.NewWindowRequested += Core_SocialLoginNewWindowRequested", popup, StringComparison.Ordinal);
        Assert.Contains("core.DownloadStarting += Core_DownloadStarting", popup, StringComparison.Ordinal);
        Assert.Contains("core.PermissionRequested += Core_PermissionRequested", popup, StringComparison.Ordinal);
        Assert.Contains("accounts.google.com", popup, StringComparison.Ordinal);
        Assert.Contains("/third_party/v1/google_callback", popup, StringComparison.Ordinal);
        Assert.Contains("/endfield/sign-in", popup, StringComparison.Ordinal);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", popup, StringComparison.Ordinal);
        Assert.True(
            popup.IndexOf("core.NavigationStarting +=", StringComparison.Ordinal)
            < popup.IndexOf("args.NewWindow = core", StringComparison.Ordinal));
        Assert.True(
            popup.IndexOf("args.NewWindow = core", StringComparison.Ordinal)
            < popup.IndexOf("core.WebResourceRequested +=", StringComparison.Ordinal));
    }

    [Fact]
    public void Visible_connect_monitor_establishes_authentication_baseline_before_auto_completion()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var monitor = Slice(
            source,
            "private async Task MonitorVisibleConnectAsync",
            "public Task<PublisherVisibleConnectCompletion> WaitForConnectCompletionAsync");

        var baseline = monitor.IndexOf("var baselineEstablished = false", StringComparison.Ordinal);
        var transition = monitor.IndexOf(
            "baselineEstablished && !wasAuthenticated && authenticated",
            StringComparison.Ordinal);
        var update = monitor.IndexOf("wasAuthenticated = authenticated", StringComparison.Ordinal);
        Assert.True(baseline >= 0 && baseline < transition && transition < update);
        Assert.Contains("baselineEstablished = true", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewedEndfieldIdentity", monitor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://www.hoyolab.com/home")]
    [InlineData("https://user:password@www.hoyolab.com/home")]
    [InlineData("https://www.hoyolab.com:444/home")]
    [InlineData("https://www.hoyolab.com.attacker.example/home")]
    [InlineData("https://hoyoverse.com.attacker.example/home")]
    [InlineData("https://example.com/home")]
    public void Connect_policy_still_rejects_unsafe_or_external_destinations(string target)
    {
        Assert.False(PublisherVisibleConnectNavigationPolicy.IsAllowed(
            "HoYoLAB",
            "hsr",
            new Uri(target)));
    }

    [Fact]
    public void Initial_login_requires_a_current_HoYo_game()
    {
        Assert.False(PublisherVisibleConnectNavigationPolicy.IsAllowedInitial(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "unknown",
            new Uri("https://account.hoyolab.com/login-platform/index.html?app_id=c9oqaq3s3gu8")));
    }

    [Fact]
    public void Ordinary_connect_changes_leave_achievement_and_hidden_pages_exact()
    {
        var source = ReadAppFile("PublisherAccountService.cs");
        var add = Slice(
            source,
            "public async Task<PublisherConnectionState> AddHoyoLabAccountAsync",
            "public async Task<bool> RenameHoyoLabAccountAsync");
        var achievementLogin = Slice(
            source,
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsWithVisibleRecoveryAsync",
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsOnceAsync");
        var achievementHidden = Slice(
            source,
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsOnceAsync",
            "private static bool RequiresVisibleHsrAchievementLogin");
        var connect = Slice(
            source,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        var endfieldReview = Slice(
            source,
            "public async Task<PublisherEndfieldAccountReviewResult> ReviewEndfieldAccountAsync",
            "private async Task<bool> ClearAllHoyoSavedPasswordsAsync");
        var resource = Slice(
            source,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var daily = Slice(
            source,
            "private async Task RunProviderCheckInsAsync",
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync");
        var dailyRole = Slice(
            source,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var probe = Slice(
            source,
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync",
            "private PublisherSessionWindow CreateWindow");

        Assert.Contains("PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry)", add, StringComparison.Ordinal);
        Assert.Contains("PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry)", connect, StringComparison.Ordinal);
        Assert.Contains("PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry)", endfieldReview, StringComparison.Ordinal);
        Assert.Equal(
            3,
            source.Split(
                "PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry)",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("PublisherAccountCatalog.GetAchievementPageUri(gameId)", achievementLogin, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Connect", achievementLogin, StringComparison.Ordinal);
        Assert.Contains("PublisherAccountCatalog.GetAchievementPageUri(gameId)", achievementHidden, StringComparison.Ordinal);
        Assert.Contains("visible: false", achievementHidden, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Achievements", achievementHidden, StringComparison.Ordinal);
        Assert.Contains("entry.ResourceUri!", resource, StringComparison.Ordinal);
        Assert.Contains("visible: false", resource, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Resource", resource, StringComparison.Ordinal);
        Assert.Contains("entry.CheckInUri!", daily, StringComparison.Ordinal);
        Assert.Contains("visible: false", daily, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.CheckIn", daily, StringComparison.Ordinal);
        Assert.Contains("entry.ResourceUri", dailyRole, StringComparison.Ordinal);
        Assert.Contains("visible: false", dailyRole, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Resource", dailyRole, StringComparison.Ordinal);
        Assert.Contains("entry.CheckInUri ?? entry.ResourceUri!", probe, StringComparison.Ordinal);
        Assert.Contains("visible: false", probe, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.ConnectionProbe", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_captures_safe_completion_status_and_retry_creates_a_fresh_correlation()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var navigation = Slice(
            source,
            "private async Task<PublisherVisibleConnectNavigationOutcome> NavigateWithOutcomeAsync",
            "private void Core_NavigationStarting");
        var retry = Slice(
            source,
            "private async void RetryButton_Click",
            "public async ValueTask DisposeAsync");

        Assert.Contains("new PublisherNavigationCompletionCorrelation(uri)", navigation, StringComparison.Ordinal);
        Assert.Contains("args.WebErrorStatus", navigation, StringComparison.Ordinal);
        Assert.Contains("args.Cancel", navigation, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2WebErrorStatus.OperationCanceled", navigation, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2WebErrorStatus.Timeout", navigation, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2WebErrorStatus.UnexpectedError", navigation, StringComparison.Ordinal);
        Assert.Contains("PublisherVisibleConnectNavigationOutcome.NetworkFailure", navigation, StringComparison.Ordinal);
        Assert.Contains("AttemptVisibleConnectPageAsync(", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("WebErrorStatus.ToString", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Uri)", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_uses_one_navigation_only_attempt_and_never_automates_or_clears_the_page()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var attempt = Slice(
            source,
            "private async Task AttemptVisibleConnectPageAsync",
            "public Task<PublisherVisibleConnectCompletion> WaitForConnectCompletionAsync");

        Assert.Contains("PublisherVisibleConnectFlow.AttemptPageAsync(", attempt, StringComparison.Ordinal);
        Assert.Contains("NavigateWithOutcomeAsync(uri, operationCancellation)", attempt, StringComparison.Ordinal);
        Assert.Contains("? presentation.Guidance ?? string.Empty", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("The official page and WebView2 handle sign-in directly.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryOpenHoyoLabLoginDialogAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherLoginTriggerOutcome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSessionProofAsync", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("querySelector", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("getElementById", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("document.", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("input[type", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain(".value", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain(".click(", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearBrowsingData", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory", attempt, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieManager", attempt, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_evergreen_runtime_offers_only_an_explicit_official_install_link()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var initialization = Slice(
            source,
            "public async Task InitializeAsync",
            "public async Task ClearSavedPasswordsAsync");
        var presentation = Slice(
            source,
            "private void ShowWebView2RuntimeRequired",
            "private async Task<PublisherVisibleConnectNavigationOutcome> NavigateWithOutcomeAsync");
        var retry = Slice(
            source,
            "private async void RetryButton_Click",
            "public async ValueTask DisposeAsync");

        Assert.Contains("visible", initialization, StringComparison.Ordinal);
        Assert.Contains("purpose == PublisherSessionPurpose.Connect", initialization, StringComparison.Ordinal);
        Assert.Contains("!IsWebView2RuntimeAvailable()", initialization, StringComparison.Ordinal);
        Assert.Contains("ShowWebView2RuntimeRequired();", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchUriAsync", initialization, StringComparison.Ordinal);
        Assert.Contains("catch (FileNotFoundException)", presentation, StringComparison.Ordinal);
        Assert.Contains("RetryButton.Content = \"INSTALL\";", presentation, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(RetryButton, \"Install Microsoft WebView2\")", presentation, StringComparison.Ordinal);
        Assert.Contains("if (webView2RuntimeUnavailable)", retry, StringComparison.Ordinal);
        Assert.Contains("Windows.System.Launcher.LaunchUriAsync(WebView2DownloadUri)", retry, StringComparison.Ordinal);
        Assert.Contains(
            "https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", retry, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            fileName));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker}.");
        Assert.True(end > start, $"Could not find {endMarker} after {startMarker}.");
        return source[start..end];
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
