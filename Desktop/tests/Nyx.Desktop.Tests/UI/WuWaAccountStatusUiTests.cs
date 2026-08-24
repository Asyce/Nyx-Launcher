using Nyx.Desktop.Core.AccountStatus;
using Nyx_Desktop_App.ViewModels;

namespace Nyx.Desktop.Tests.UI;

public sealed class WuWaAccountStatusUiTests
{
    [Fact]
    public void Status_strip_is_native_opt_in_wuwa_only_and_immediately_precedes_launch()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));

        var strip = xaml.IndexOf("x:Name=\"WuWaAccountStatusStrip\"", StringComparison.Ordinal);
        var launch = xaml.IndexOf("x:Name=\"LaunchButton\"", StringComparison.Ordinal);
        Assert.True(strip >= 0 && launch > strip);
        Assert.Contains("Visibility=\"Collapsed\"", xaml[strip..launch], StringComparison.Ordinal);
        Assert.Contains("WuWaAccountStatusToggle_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("WuWaAccountStatusRefreshButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("selected.Id == \"wuwa\"", code, StringComparison.Ordinal);
        Assert.Contains("WuWaAccountStatusStrip.Visibility", code, StringComparison.Ordinal);
        Assert.Contains("WP {snapshot.Energy}/{snapshot.MaxEnergy}", code, StringComparison.Ordinal);
        Assert.Contains(
            "DAILY {snapshot.Liveness}/{snapshot.LivenessMaxCount}",
            code,
            StringComparison.Ordinal);
        Assert.Contains("DailyCheckInButton.Visibility = Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("STALE {age}", code, StringComparison.Ordinal);
        Assert.Contains("RefreshWuWaAccountStatusAsync(lease)", code, StringComparison.Ordinal);
        Assert.Contains("wuwaAccountStatus.IsRefreshCoolingDown", code, StringComparison.Ordinal);
        Assert.Contains("var result = wuwaAccountStatus.Current", code, StringComparison.Ordinal);
        Assert.Contains("Width=\"64\"", xaml[strip..launch], StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(", code, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"AccountConnectionWarningText\"",
            xaml[strip..launch],
            StringComparison.Ordinal);
        var warning = xaml.IndexOf("x:Name=\"AccountConnectionWarningText\"", strip, StringComparison.Ordinal);
        Assert.True(warning >= 0);
        Assert.Contains(
            "Visibility=\"Collapsed\"",
            xaml[warning..xaml.IndexOf("/>", warning, StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Consent_is_persisted_as_a_non_secret_feature_flag()
    {
        var root = FindRepositoryRoot();
        var flags = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.Core", "Features", "LauncherFeatureFlags.cs"));
        var migration = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.Core", "State", "LauncherStateMigrations.cs"));

        Assert.Contains("public bool WuWaAccountStatus", flags, StringComparison.Ordinal);
        Assert.Contains("WuWaAccountStatus = dto.WuWaAccountStatus ?? false", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("oauthCode", flags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oauthCode", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opt_out_disables_and_clears_the_session_before_persistence_can_fail()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));
        var methodStart = code.IndexOf("private async void WuWaAccountStatusToggle_Click", StringComparison.Ordinal);
        var methodEnd = code.IndexOf("private async void WuWaAccountStatusRefreshButton_Click", methodStart, StringComparison.Ordinal);
        var method = code[methodStart..methodEnd];

        var disable = method.IndexOf("wuwaAccountStatus.DisableSession()", StringComparison.Ordinal);
        var persist = method.IndexOf("launcherState.TryUpdate", StringComparison.Ordinal);
        Assert.True(disable >= 0 && persist > disable);
        Assert.Contains("wuwaAccountStatusSessionDisabled = true", method, StringComparison.Ordinal);
        Assert.Contains("wuwaAccountStatusSaveFailed = !enable", method, StringComparison.Ordinal);
        Assert.Contains("&& !wuwaAccountStatusSessionDisabled", code, StringComparison.Ordinal);
        Assert.Contains("Checking official account status", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_wuwa_refresh_never_overwrites_another_selected_games_controls()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));
        var refreshStart = code.IndexOf("private async Task RefreshWuWaAccountStatusAsync", StringComparison.Ordinal);
        var refreshEnd = code.IndexOf("private async Task ChooseGameFolderAsync", refreshStart, StringComparison.Ordinal);
        var refresh = code[refreshStart..refreshEnd];

        Assert.Contains("UpdateWuWaAccountStatusIfSelected", refresh, StringComparison.Ordinal);
        Assert.Contains("GameLauncherItem { Id: \"wuwa\" }", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRun(lease, RenderWuWaAccountStatus)", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Wuwa_identity_reuses_the_shared_account_line_without_inventing_a_name()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));
        var start = code.IndexOf("private void RenderWuWaAccountIdentity", StringComparison.Ordinal);
        var end = code.IndexOf("private void RenderPublisherAccountStatus", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var render = code[start..end];

        Assert.Contains("wuwaAccountStatus.Current?.Identity", render, StringComparison.Ordinal);
        Assert.Contains("identity?.DisplayText", render, StringComparison.Ordinal);
        Assert.Contains("AccountAndToolsIdentityText.Text = identityText", render, StringComparison.Ordinal);
        Assert.Contains("accountSectionExpanded", render, StringComparison.Ordinal);
        Assert.Contains("UID and region", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Nickname", render, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayName", render, StringComparison.Ordinal);
        Assert.Contains("RenderWuWaAccountIdentity();", code, StringComparison.Ordinal);
        Assert.Contains("RenderWuWaAccountStatus();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_close_awaits_account_status_disposal_without_blocking_the_UI_thread()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "App.xaml.cs"));
        var closeStart = app.IndexOf("private void AppWindow_Closing", StringComparison.Ordinal);
        var closeEnd = app.IndexOf("private async Task RefreshAfterActivationAsync", closeStart, StringComparison.Ordinal);
        var close = app[closeStart..closeEnd];
        var disposeStart = app.IndexOf("private static async Task DisposeWuWaAccountStatusAsync", StringComparison.Ordinal);
        var disposeEnd = app.IndexOf("private static async Task DisposeExportsAsync", disposeStart, StringComparison.Ordinal);
        var dispose = app[disposeStart..disposeEnd];

        Assert.Contains("args.Cancel = true", close, StringComparison.Ordinal);
        Assert.Contains("DisposeWuWaAccountStatusAsync(_wuwaAccountStatus)", close, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(wuwaAccountShutdown, publisherAccountShutdown)", close, StringComparison.Ordinal);
        Assert.Contains("_accountShutdownComplete = true", close, StringComparison.Ordinal);
        Assert.Contains("await accountStatus.DisposeAsync()", dispose, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_countdown_uses_only_elapsed_local_time_and_clamps_at_zero()
    {
        var observed = DateTimeOffset.Parse("2026-07-21T22:00:00+02:00");
        var snapshot = new PublisherResourceSnapshot(
            "gi", "Original Resin", 120, 200, observed, RecoverySeconds: 600);

        Assert.Equal(600, PublisherAccountDisplayProjection.RemainingRecoverySeconds(snapshot, observed));
        Assert.Equal(359, PublisherAccountDisplayProjection.RemainingRecoverySeconds(snapshot, observed.AddSeconds(241)));
        Assert.Equal(0, PublisherAccountDisplayProjection.RemainingRecoverySeconds(snapshot, observed.AddMinutes(11)));
        Assert.DoesNotContain("FULL", PublisherAccountDisplayProjection.FormatResource(snapshot, observed.AddMinutes(11)), StringComparison.Ordinal);
    }

    [Fact]
    public void Wuwa_launch_resource_projection_preserves_waveplates_crystals_and_daily_activity()
    {
        var metrics = LauncherResourceMetricsProjection.FromWuWa(
            new WuWaAccountStatusSnapshot(
                Energy: 187,
                MaxEnergy: 300,
                StoreEnergy: 42,
                StoreEnergyRecoverTime: 0,
                EnergyRecoverTime: 7_260,
                Liveness: 100,
                LivenessMaxCount: 100));

        Assert.Equal("187/300", metrics.Primary);
        Assert.Equal("42", metrics.Reserve);
        Assert.Equal("2H 1M", metrics.Recovery);
        Assert.Equal("100/100", metrics.Daily);
        Assert.Contains("Waveplate Crystal 42", metrics.AutomationText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(WuWaAccountStatusRules.MaximumRecoverySeconds, "336H 0M")]
    [InlineData(WuWaAccountStatusRules.MaximumRecoverySeconds + 1, null)]
    [InlineData(long.MaxValue, null)]
    [InlineData(long.MinValue, null)]
    public void Wuwa_projection_never_throws_or_invents_an_unbounded_recovery(
        long recoverySeconds,
        string? expectedRecovery)
    {
        LauncherResourceMetrics? metrics = null;
        var exception = Record.Exception(() => metrics = LauncherResourceMetricsProjection.FromWuWa(
            new WuWaAccountStatusSnapshot(
                Energy: 187,
                MaxEnergy: 300,
                StoreEnergy: 42,
                StoreEnergyRecoverTime: 0,
                EnergyRecoverTime: recoverySeconds,
                Liveness: 100,
                LivenessMaxCount: 100)));

        Assert.Null(exception);
        Assert.NotNull(metrics);
        Assert.Equal("187/300", metrics.Primary);
        Assert.Equal("42", metrics.Reserve);
        Assert.Equal("100/100", metrics.Daily);
        Assert.Equal(expectedRecovery, metrics.Recovery);
        Assert.DoesNotContain("recovery", metrics.AutomationText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stale_and_midnight_expiry_are_visible_without_account_service_calls()
    {
        var observed = DateTimeOffset.Parse("2026-07-21T23:59:30+02:00");
        var stale = new PublisherResourceSnapshot(
            "hsr", "Trailblaze Power", 100, 300, observed, IsStale: true, RecoverySeconds: 3600);
        var checkIn = new DailyCheckInResult("hsr", DailyCheckInState.Claimed, "done", observed);

        Assert.Contains("STALE", PublisherAccountDisplayProjection.FormatResource(stale, observed.AddMinutes(2)), StringComparison.Ordinal);
        Assert.True(PublisherAccountPresentation.IsCurrentDayCheckIn(checkIn, observed.AddSeconds(20)));
        Assert.False(PublisherAccountPresentation.IsCurrentDayCheckIn(checkIn, observed.AddMinutes(2)));

        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));
        var start = code.IndexOf("private void RenderLocalAccountTimeTick", StringComparison.Ordinal);
        var end = code.IndexOf("private void BannerCategoryButton_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var tick = code[start..end];
        Assert.Contains("RenderPublisherAccountStatus(selected.Id)", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectAsync", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckInAllAsync", tick, StringComparison.Ordinal);
        Assert.Contains("DAY EXPIRED", code, StringComparison.Ordinal);
        Assert.Contains("Unofficial local connection · may stop working.", File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Network_availability_probe_avoids_the_unpackaged_winrt_crash_path()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));

        Assert.Contains("NetworkInterface.GetIsNetworkAvailable()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkInformation.GetInternetConnectionProfile()", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop", "src"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
