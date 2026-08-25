namespace Nyx.Desktop.Tests.UI;

public sealed class WuWaMaintenanceUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Startup_runs_only_the_read_only_WuWa_check_and_stores_its_sealed_request()
    {
        var app = ReadAppFile("App.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("WuWaMaintenance = new WuWaMaintenanceService();", app, StringComparison.Ordinal);
        Assert.Contains("? wuwaMaintenance.Check()", page, StringComparison.Ordinal);
        Assert.Contains("ApplyWuWaMaintenanceResult(result)", page, StringComparison.Ordinal);
        Assert.Contains("RefreshHoyoMaintenanceAsync(lease, refreshSessions: true)", page, StringComparison.Ordinal);
        Assert.Contains("RefreshWuWaMaintenanceAsync(lease, useStoredRequest: false)", page, StringComparison.Ordinal);
        Assert.Contains("IndependentMaintenanceLaneRunner.RunAsync", page, StringComparison.Ordinal);
        var discoveryStart = page.IndexOf("private HoyoMaintenanceUiSnapshot DiscoverHoyoMaintenance()", StringComparison.Ordinal);
        var discoveryEnd = page.IndexOf("private async Task RefreshHoyoMaintenanceAsync", discoveryStart, StringComparison.Ordinal);
        var discovery = page[discoveryStart..discoveryEnd];
        Assert.DoesNotContain("OpenOrObserveCurrentAsync", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain(".Open(", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void HoYo_and_WuWa_checks_run_and_fail_independently()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var hoyoStart = page.IndexOf("private async Task RefreshHoyoMaintenanceAsync", StringComparison.Ordinal);
        var wuwaStart = page.IndexOf("private async Task RefreshWuWaMaintenanceAsync", StringComparison.Ordinal);
        var activationStart = page.IndexOf("private void App_WindowReactivated", StringComparison.Ordinal);
        var hoyo = page[hoyoStart..wuwaStart];
        var wuwa = page[wuwaStart..activationStart];

        Assert.Contains("Task.Run(DiscoverHoyoMaintenance", hoyo, StringComparison.Ordinal);
        Assert.Contains("updaterStatus = GenshinLaunchStatus.NeedsReview", hoyo, StringComparison.Ordinal);
        Assert.DoesNotContain("wuwaMaintenanceStatus", hoyo, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenance.Check()", wuwa, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenanceStatus = WuWaOfficialMaintenanceStatus.NeedsReview", wuwa, StringComparison.Ordinal);
        Assert.DoesNotContain("updaterStatus =", wuwa, StringComparison.Ordinal);
        Assert.Contains("wuwaRefreshGeneration.TryApply(generation", wuwa, StringComparison.Ordinal);
        Assert.Contains("generation != Volatile.Read(ref hoyoRefreshGeneration)", hoyo, StringComparison.Ordinal);
    }

    [Fact]
    public void WuWa_direct_session_and_official_maintenance_stay_separate()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var renderStart = page.IndexOf("private void RenderWuWa", StringComparison.Ordinal);
        var renderEnd = page.IndexOf("private void RenderLaunchFailure", renderStart, StringComparison.Ordinal);
        var render = page[renderStart..renderEnd];

        Assert.Contains("RenderPublisherSession(selected)", render, StringComparison.Ordinal);
        Assert.Contains("OpenUpdaterButton", render, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLaunchAsync", render, StringComparison.Ordinal);
        Assert.Contains("sessions.RequestLaunchAsync(gameId", page, StringComparison.Ordinal);
        var launchStart = page.IndexOf("private async void LaunchButton_Click", StringComparison.Ordinal);
        var launchEnd = page.IndexOf("private async void WuWaAccountStatusToggle_Click", launchStart, StringComparison.Ordinal);
        var launch = page[launchStart..launchEnd];
        Assert.DoesNotContain("gameId is not (\"gi\" or \"hsr\" or \"zzz\")", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_official_stays_visible_but_is_enabled_only_for_ready_or_exact_running_WuWa_state()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var renderStart = page.IndexOf("private void RenderWuWa", StringComparison.Ordinal);
        var renderEnd = page.IndexOf("private void RenderLaunchFailure", renderStart, StringComparison.Ordinal);
        var render = page[renderStart..renderEnd];

        Assert.DoesNotContain("OpenUpdaterButton.Visibility = Visibility.Collapsed", render, StringComparison.Ordinal);
        Assert.Equal(3, Count(render, "OpenUpdaterButton.Visibility = Visibility.Visible"));
        Assert.Contains("case WuWaOfficialMaintenanceStatus.Ready", render, StringComparison.Ordinal);
        Assert.Contains("case WuWaOfficialMaintenanceStatus.Running", render, StringComparison.Ordinal);
        Assert.Contains("case WuWaOfficialMaintenanceStatus.Opened", render, StringComparison.Ordinal);
        Assert.Contains("case WuWaOfficialMaintenanceStatus.Failed", render, StringComparison.Ordinal);
        Assert.Contains("case WuWaOfficialMaintenanceStatus.NotFound", render, StringComparison.Ordinal);
        Assert.Contains("Wuthering Waves maintenance needs review", render, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName", render, StringComparison.Ordinal);
    }

    [Fact]
    public void WuWa_pre_install_notice_requires_the_validated_request_and_never_animates()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var renderStart = page.IndexOf("private void RenderPreInstallNotice", StringComparison.Ordinal);
        var renderEnd = page.IndexOf("private void RenderHoyoLabAccountIdentity", renderStart, StringComparison.Ordinal);
        var render = page[renderStart..renderEnd];

        Assert.Contains("selected.Id == \"wuwa\"", render, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenanceRequest?.PreInstallAvailable == true", render, StringComparison.Ordinal);
        Assert.Contains("WuWaOfficialMaintenanceStatus.Ready", render, StringComparison.Ordinal);
        Assert.Contains("WuWaOfficialMaintenanceStatus.Running", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Endfield", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_explicit_click_dispatches_the_stored_request_and_suppresses_repeats()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var clickStart = page.IndexOf("private async void OpenUpdaterButton_Click", StringComparison.Ordinal);
        var clickEnd = page.IndexOf("private void SessionRefresh_Refreshed", clickStart, StringComparison.Ordinal);
        var click = page[clickStart..clickEnd];

        Assert.Contains("selected.Id == \"wuwa\"", click, StringComparison.Ordinal);
        Assert.Contains("await OpenWuWaMaintenanceAsync(lease)", click, StringComparison.Ordinal);
        Assert.Contains("if (wuwaActionInFlight", click, StringComparison.Ordinal);
        Assert.Contains("var request = wuwaMaintenanceRequest", click, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenance.OpenOrObserveCurrentAsync", click, StringComparison.Ordinal);
        Assert.Contains("lease.CancellationToken", click, StringComparison.Ordinal);
        Assert.Contains("wuwaActionInFlight = true", click, StringComparison.Ordinal);
        Assert.Contains("wuwaActionInFlight = false", click, StringComparison.Ordinal);
        Assert.Contains("Wuthering Waves launcher start requested", page, StringComparison.Ordinal);
        Assert.Contains("BoundedMaintenanceObservation.ObserveAsync", click, StringComparison.Ordinal);
        Assert.Contains("WuWaLaunchObservationCount", click, StringComparison.Ordinal);
        Assert.Contains("observation.Status is not WuWaOfficialMaintenanceStatus.Ready", click, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenance.Check(result.Request)", click, StringComparison.Ordinal);
        var wuwaOpenStart = click.IndexOf("private async Task OpenWuWaMaintenanceAsync", StringComparison.Ordinal);
        var wuwaOpen = click[wuwaOpenStart..];
        var failureStart = wuwaOpen.IndexOf("catch (Exception)", StringComparison.Ordinal);
        var finallyStart = wuwaOpen.IndexOf("finally", failureStart, StringComparison.Ordinal);
        var failure = wuwaOpen[failureStart..finallyStart];
        Assert.Contains("wuwaRefreshGeneration.TryApply(generation", failure, StringComparison.Ordinal);
        Assert.Contains("WuWaOfficialMaintenanceStatus.Failed", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_unload_cancels_WuWa_work_and_selection_switch_does_not_rebind_the_request()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("sessionUiLifetime.Deactivate(lease)", page, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)", page, StringComparison.Ordinal);
        Assert.Contains("GameSelector?.SelectedItem is not GameLauncherItem { Id: \"wuwa\" }", page, StringComparison.Ordinal);
        Assert.Contains("var request = wuwaMaintenanceRequest", page, StringComparison.Ordinal);
        Assert.Contains("sessionUiLifetime.TryRun(lease", page, StringComparison.Ordinal);
        Assert.Contains("app.WindowReactivated += App_WindowReactivated", page, StringComparison.Ordinal);
        Assert.Contains("app.WindowReactivated -= App_WindowReactivated", page, StringComparison.Ordinal);
        Assert.Contains("RefreshWuWaMaintenanceAsync(lease, useStoredRequest: true)", page, StringComparison.Ordinal);
        Assert.Contains("RefreshHoyoMaintenanceAsync(lease, refreshSessions: false)", page, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref hoyoRefreshGeneration)", page, StringComparison.Ordinal);
        Assert.Contains("wuwaRefreshGeneration.Next()", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_during_delayed_launcher_appearance_cannot_publish_premature_ready()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var activationStart = page.IndexOf("private void App_WindowReactivated", StringComparison.Ordinal);
        var activationEnd = page.IndexOf("private async void LaunchButton_Click", activationStart, StringComparison.Ordinal);
        var activation = page[activationStart..activationEnd];
        var openStart = page.IndexOf("private async Task OpenWuWaMaintenanceAsync", StringComparison.Ordinal);
        var openEnd = page.IndexOf("private void SessionRefresh_Refreshed", openStart, StringComparison.Ordinal);
        var open = page[openStart..openEnd];
        var renderStart = page.IndexOf("private void RenderWuWa", StringComparison.Ordinal);
        var renderEnd = page.IndexOf("private void RenderLaunchFailure", renderStart, StringComparison.Ordinal);
        var render = page[renderStart..renderEnd];

        var activationGuard = activation.IndexOf(
            "WuWaMaintenanceInteractionPolicy.AllowsActivationRefresh(wuwaActionInFlight)",
            StringComparison.Ordinal);
        var activationRefresh = activation.IndexOf("RefreshWuWaMaintenanceAsync(lease, useStoredRequest: true)", StringComparison.Ordinal);
        Assert.True(activationGuard >= 0);
        Assert.True(activationRefresh > activationGuard);

        var actionStarted = open.IndexOf("wuwaActionInFlight = true", StringComparison.Ordinal);
        var boundedObservation = open.IndexOf("BoundedMaintenanceObservation.ObserveAsync", StringComparison.Ordinal);
        var runningApplied = open.IndexOf("ApplyWuWaMaintenanceResult(observed)", StringComparison.Ordinal);
        var actionFinished = open.LastIndexOf("wuwaActionInFlight = false", StringComparison.Ordinal);
        Assert.True(actionStarted >= 0);
        Assert.True(boundedObservation > actionStarted);
        Assert.True(runningApplied > boundedObservation);
        Assert.True(actionFinished > runningApplied);

        var runningStart = render.IndexOf("case WuWaOfficialMaintenanceStatus.Running", StringComparison.Ordinal);
        var openedStart = render.IndexOf("case WuWaOfficialMaintenanceStatus.Opened", runningStart, StringComparison.Ordinal);
        var running = render[runningStart..openedStart];
        Assert.Contains("WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial", running, StringComparison.Ordinal);
        Assert.Contains("maintenanceReady: false", running, StringComparison.Ordinal);
    }

    [Fact]
    public void Endfield_direct_session_folder_choice_and_maintenance_are_separate()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("if (selected.Id == \"wuwa\")", page, StringComparison.Ordinal);
        Assert.Contains("RenderEndfield(selected)", page, StringComparison.Ordinal);
        Assert.Contains("supportsFolderPicker: !selected.IsCustom", page, StringComparison.Ordinal);
        Assert.Contains("PrimaryGameStatusAction.ChooseGameFolder", page, StringComparison.Ordinal);
        Assert.Contains("OpenUpdaterButton.Visibility = Visibility.Visible", page, StringComparison.Ordinal);
        Assert.Contains("OpenEndfieldMaintenanceAsync", page, StringComparison.Ordinal);
        var officialStart = page.IndexOf("private async void OpenUpdaterButton_Click", StringComparison.Ordinal);
        var officialEnd = page.IndexOf("private async Task OpenEndfieldMaintenanceAsync", StringComparison.Ordinal);
        var officialClick = page[officialStart..officialEnd];
        Assert.Contains("selected.Id == \"ae\"", officialClick, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyoPlayExecutor.OpenOrObserveCurrentAsync(\n                \"ae\"", officialClick, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_HoYo_and_game_launch_paths_remain_distinct()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("sessions.RequestLaunchAsync(gameId, cancellationToken)", page, StringComparison.Ordinal);
        Assert.Contains("hoyoPlayExecutor.OpenOrObserveCurrentAsync", page, StringComparison.Ordinal);
        var launchStart = page.IndexOf("private async void LaunchButton_Click", StringComparison.Ordinal);
        var launchEnd = page.IndexOf("private async void WuWaAccountStatusToggle_Click", launchStart, StringComparison.Ordinal);
        var launch = page[launchStart..launchEnd];
        Assert.DoesNotContain("gameId is not", launch, StringComparison.Ordinal);
        Assert.Contains("wuwaMaintenance.OpenOrObserveCurrentAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyoPlayExecutor.OpenOrObserveCurrentAsync(\n                \"wuwa\"", page, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string ReadAppFile(string fileName) => File.ReadAllText(Path.Combine(
        WorkspaceRoot,
        "Desktop",
        "src",
        "Nyx.Desktop.App",
        fileName));

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
