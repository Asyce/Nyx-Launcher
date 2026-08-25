using System.Text.RegularExpressions;

namespace Nyx.Desktop.Tests.UI;

public sealed class PengoWebToolsUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Shell_exposes_persistent_pull_and_achievement_export_arming()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Single(Regex.Matches(xaml, "x:Name=\"PullExportToggle\"").Cast<Match>());
        Assert.Single(Regex.Matches(xaml, "x:Name=\"AchievementExportToggle\"").Cast<Match>());
        Assert.Single(Regex.Matches(xaml, "x:Name=\"AchievementSourceButton\"").Cast<Match>());
        Assert.Contains("<CheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Pull tracker\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Achievements\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HoYoLAB — export now, without opening the game", xaml, StringComparison.Ordinal);
        Assert.Contains("Game — capture after entering the world", xaml, StringComparison.Ordinal);
        Assert.Contains("ExportToggle_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("state.Export.Games.ToDictionary", code, StringComparison.Ordinal);
        Assert.Contains("launcherState.TryUpdate", code, StringComparison.Ordinal);
        Assert.Contains("PullsArmed = pullsArmed", code, StringComparison.Ordinal);
        Assert.Contains("AchievementsArmed = achievementsArmed", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PengoWebToolCatalog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENS IN YOUR BROWSER", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Elevated_capture_failure_is_honest_for_both_supported_games()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var failure = Slice(
            code,
            "private static string FormatAchievementFailure",
            "private void RefreshGameRailSignals");

        Assert.Contains(
            "Achievements: close Nyx and reopen it normally, not as administrator.",
            failure,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Genshin must run without administrator rights",
            failure,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_runs_exports_through_the_same_validated_game_admission()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(code, "private async void LaunchButton_Click", "private async Task ChooseGameFolderAsync");

        Assert.Contains("ExportArmSnapshot.From", launch, StringComparison.Ordinal);
        Assert.Contains("exports.RunForLaunchAsync", launch, StringComparison.Ordinal);
        Assert.Contains("sessions.RequestLaunchAsync(gameId, cancellationToken)", launch, StringComparison.Ordinal);
        Assert.Contains("GameLaunchRequestOutcome.Accepted", launch, StringComparison.Ordinal);
        Assert.Contains("GameLaunchRequestOutcome.AlreadyRunning", launch, StringComparison.Ordinal);
        Assert.Contains("AchievementExportSources.Game", launch, StringComparison.Ordinal);
        Assert.Contains("AchievementExportHandoffs.TrackAsync", launch, StringComparison.Ordinal);
        Assert.Contains("TrackExportJobAsync", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchUriAsync", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", launch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_offers_export_controls_while_feature_flags_gate_provider_availability()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderExportTools", "private static string FormatExportStatus");

        Assert.Contains("ExportProviderCatalog.GetEnabled", render, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportProviderCatalog.Get(selected.Id)", render, StringComparison.Ordinal);
        Assert.Contains("armed.AchievementSource", render, StringComparison.Ordinal);
        Assert.Contains("achievementSource);", render, StringComparison.Ordinal);
        Assert.Contains("NyxToolsPanel.Visibility = Visibility.Collapsed", render, StringComparison.Ordinal);
        Assert.Contains("ApplySavedPanelVisibility(selected)", render, StringComparison.Ordinal);
        Assert.True(
            render.IndexOf("if (selected.IsCustom) return;", StringComparison.Ordinal)
            < render.IndexOf("GameCatalog.GetRequired(selected.Id)", StringComparison.Ordinal));
        Assert.Contains("var pullsOffered = definition.SupportsPulls", render, StringComparison.Ordinal);
        Assert.Contains("var achievementsOffered = definition.SupportsAchievements", render, StringComparison.Ordinal);
        Assert.Contains("PullExportToggle.Visibility = pullsOffered", render, StringComparison.Ordinal);
        Assert.Contains("AchievementExportPanel.Visibility = achievementsOffered", render, StringComparison.Ordinal);
        Assert.DoesNotContain("var pullsOffered = selected.Id", render, StringComparison.Ordinal);
        Assert.DoesNotContain("var achievementsOffered = selected.Id", render, StringComparison.Ordinal);
        Assert.Contains("No supported export tools for this game.", render, StringComparison.Ordinal);
        Assert.Contains("Export tools for this game are not ready yet.", render, StringComparison.Ordinal);
        Assert.Contains("PullExportToggle.IsEnabled = pullsAvailable", render, StringComparison.Ordinal);
        Assert.Contains("AchievementExportToggle.IsEnabled = achievementsAvailable", render, StringComparison.Ordinal);
        Assert.Contains("AchievementSourceButton.IsEnabled", render, StringComparison.Ordinal);
        Assert.Contains("StablePullExportToggle.IsEnabled", render, StringComparison.Ordinal);
        Assert.Contains("StableAchievementExportToggle.IsEnabled", render, StringComparison.Ordinal);
        Assert.DoesNotContain("future provider script", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_resource_refresh_prioritizes_the_selected_game_and_preloads_the_others()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var startup = Slice(
            code,
            "private async Task RefreshPublisherResourcesOnStartupAsync",
            "private async Task RefreshPublisherResourceAfterCheckInAsync");

        Assert.Contains("selectedId is \"gi\" or \"hsr\" or \"zzz\" ? selectedId : null", startup, StringComparison.Ordinal);
        Assert.Contains(".Distinct(StringComparer.Ordinal)", startup, StringComparison.Ordinal);
        var selectedFirst = startup.IndexOf("? selectedId : null,", StringComparison.Ordinal);
        Assert.True(selectedFirst >= 0);
        Assert.True(startup.IndexOf("\"gi\",", selectedFirst + 1, StringComparison.Ordinal) > selectedFirst);
        Assert.Contains("RefreshWuWaAccountStatusAsync", startup, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(startup, "RefreshPublisherResourceAutomaticallyAsync", RegexOptions.CultureInvariant)
                .Cast<Match>());
        Assert.Contains("foreach", startup, StringComparison.Ordinal);
        Assert.Contains("selected: gameId == selectedId", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("force:", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll", startup, StringComparison.Ordinal);
        Assert.Contains("if (gameId == skipGameId) continue;", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_controls_reject_preflight_and_active_job_races()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var exportToggle = Slice(code, "private void ExportToggle_Click", "private async void AchievementSource_Click");
        var source = Slice(code, "private async void AchievementSource_Click", "private async Task StartHoyoLabAchievementExportAsync");
        var hoyoLab = Slice(code, "private async Task StartHoyoLabAchievementExportAsync", "private string GetAchievementSource");
        var render = Slice(code, "private void RenderExportTools", "private static string FormatExportStatus");

        Assert.Contains("gameActionsInFlight.Contains(selected.Id)", exportToggle, StringComparison.Ordinal);
        Assert.Contains("hoyoLabExportReservation.IsHeld", exportToggle, StringComparison.Ordinal);
        Assert.Contains("gameActionsInFlight.Contains(\"hsr\")", source, StringComparison.Ordinal);
        Assert.Contains("hoyoLabExportReservation.IsHeld", source, StringComparison.Ordinal);
        Assert.Contains("HasUnfinishedExport(\"hsr\")", source, StringComparison.Ordinal);
        Assert.Contains("gameActionsInFlight.Contains(\"hsr\")", hoyoLab, StringComparison.Ordinal);
        Assert.Contains("hoyoLabExportReservation.IsHeld", hoyoLab, StringComparison.Ordinal);
        Assert.Contains("!gameActionsInFlight.Contains(selected.Id)", render, StringComparison.Ordinal);
        Assert.Contains("hasHoyoLabExportPreparation", render, StringComparison.Ordinal);
        Assert.Contains("AchievementSourceButton.IsEnabled", render, StringComparison.Ordinal);
        Assert.Contains("hoyoLabExportReservation.IsHeld", Slice(code, "private void SetLaunchControls", "private static string WithVersion"), StringComparison.Ordinal);
    }

    [Fact]
    public void Star_rail_source_choices_exist_only_while_achievements_are_selected()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var exportToggle = Slice(code, "private void ExportToggle_Click", "private async void AchievementSource_Click");
        var source = Slice(code, "private async void AchievementSource_Click", "private async Task StartHoyoLabAchievementExportAsync");
        var render = Slice(code, "private void RenderExportTools", "private static string FormatExportStatus");

        Assert.Contains("ReferenceEquals(sender, AchievementExportToggle) && AchievementExportToggle.IsChecked == true", exportToggle, StringComparison.Ordinal);
        Assert.Contains("? AchievementExportSources.Game", exportToggle, StringComparison.Ordinal);
        Assert.Contains("var showAchievementSource = selected.Id == \"hsr\"", render, StringComparison.Ordinal);
        Assert.Contains("&& armed.AchievementsArmed", render, StringComparison.Ordinal);
        Assert.Contains("AchievementSourceOptionsPanel.Visibility = showAchievementSource", render, StringComparison.Ordinal);
        Assert.Contains("HoyoLabAchievementSourceRadio.Visibility = showAchievementSource && !armed.PullsArmed", render, StringComparison.Ordinal);
        Assert.Contains("if (existing.PullsArmed)", source, StringComparison.Ordinal);
        Assert.Contains("AchievementsArmed = source == AchievementExportSources.HoyoLab", source, StringComparison.Ordinal);
        Assert.Contains("await StartHoyoLabAchievementExportAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HoyoLab_export_reservation_is_taken_before_first_await_and_cleared_in_finally()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(code, "private async void LaunchButton_Click", "private void ExportToggle_Click");
        var hoyoLab = Slice(code, "private async Task StartHoyoLabAchievementExportAsync", "private string GetAchievementSource");

        Assert.Contains("if (gameId == \"hsr\" && hoyoLabExportReservation.IsHeld)", launch, StringComparison.Ordinal);
        var reserved = hoyoLab.IndexOf("hoyoLabExportReservation.TryAcquire()", StringComparison.Ordinal);
        var rendered = hoyoLab.IndexOf("RenderSelection();", reserved, StringComparison.Ordinal);
        var firstAwait = hoyoLab.IndexOf("await exports.RunForLaunchAsync", StringComparison.Ordinal);
        Assert.True(reserved >= 0 && rendered > reserved && firstAwait > rendered);
        Assert.Contains("NyxToolsStatusText.Text = \"HoYoLAB is exporting achievements. Star Rail can stay closed.\"", hoyoLab, StringComparison.Ordinal);
        var finallyIndex = hoyoLab.IndexOf("finally", StringComparison.Ordinal);
        var released = hoyoLab.LastIndexOf("reservation.Dispose()", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0 && released > finallyIndex);
    }

    [Fact]
    public void Star_rail_can_export_hoyolab_achievements_without_launching_the_game()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var immediate = Slice(
            code,
            "private async Task StartHoyoLabAchievementExportAsync",
            "private string GetAchievementSource");

        Assert.Contains("AchievementSource_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("AchievementExportSources.HoyoLab", code, StringComparison.Ordinal);
        Assert.Contains("ExportProviderCatalog.GetEnabled", immediate, StringComparison.Ordinal);
        Assert.Contains("achievementSource);", immediate, StringComparison.Ordinal);
        Assert.Contains("!capability.Supports(ExportKind.Achievements)", immediate, StringComparison.Ordinal);
        Assert.Contains("new ExportArmSnapshot(\"hsr\", PullsArmed: false, AchievementsArmed: true)", immediate, StringComparison.Ordinal);
        Assert.Contains("static _ => ValueTask.FromResult(true)", immediate, StringComparison.Ordinal);
        Assert.Contains("TrackExportJobAsync(\"hsr\"", immediate, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions.RequestLaunchAsync", immediate, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowGameActionInProgress", immediate, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_controls_are_keyboard_sized_automatic_and_allow_safe_cancel()
    {
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var start = controls.IndexOf("x:Key=\"NyxExportToggleStyle\"", StringComparison.Ordinal);
        var end = controls.IndexOf("x:Key=\"NyxLaunchButtonStyle\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var style = controls[start..end];

        Assert.Contains("MinHeight\" Value=\"44", style, StringComparison.Ordinal);
        Assert.Contains("UseSystemFocusVisuals\" Value=\"True", style, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelExportButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenExportsButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmWorldButton", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmHistoryButton", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("exportSignals", code, StringComparison.Ordinal);
        Assert.Contains("Nyx continues automatically", code, StringComparison.Ordinal);
        Assert.Contains("job.Pulls.ErrorCode", code, StringComparison.Ordinal);
        Assert.Contains("job.Achievements.ErrorCode", code, StringComparison.Ordinal);
        Assert.Contains("exports.Cancel(jobId)", code, StringComparison.Ordinal);
        Assert.Contains("LaunchFolderPathAsync", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_close_drains_page_before_starting_export_and_account_cleanup()
    {
        var app = ReadAppFile("App.xaml.cs");
        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "private void Window_Closed");
        var page = shutdown.IndexOf("await DisposeMainPageAsync(mainWindow)", StringComparison.Ordinal);
        var taskStarts = new[]
        {
            shutdown.IndexOf("DisposeLauncherBannersAsync(_launcherBanners)", StringComparison.Ordinal),
            shutdown.IndexOf("DisposePublisherStatusAsync(_hoyoPublisherStatus)", StringComparison.Ordinal),
            shutdown.IndexOf("DisposeWuWaAccountStatusAsync(_wuwaAccountStatus)", StringComparison.Ordinal),
            shutdown.IndexOf("DisposePublisherAccountsAsync(_publisherAccounts)", StringComparison.Ordinal),
            shutdown.IndexOf("CloseExportsForLauncherAsync(_exports)", StringComparison.Ordinal),
        };
        var discovery = shutdown.IndexOf("await AwaitEndfieldSiblingDiscoveryAsync()", StringComparison.Ordinal);
        var refresh = shutdown.IndexOf("await DisposeRefreshAsync(_sessionRefresh)", StringComparison.Ordinal);
        var sessions = shutdown.IndexOf("await DisposeSessionsAsync(_sessions)", StringComparison.Ordinal);
        var background = shutdown.IndexOf("await Task.WhenAll(", StringComparison.Ordinal);
        var handoffs = shutdown.IndexOf("await DisposeAchievementHandoffsAsync", StringComparison.Ordinal);
        var exports = shutdown.IndexOf("await DisposeExportCoordinatorAsync", StringComparison.Ordinal);
        var pulls = shutdown.IndexOf("_pullExports?.Dispose()", StringComparison.Ordinal);
        var genshin = shutdown.IndexOf("await DisposeGenshin120FpsStarterAsync", StringComparison.Ordinal);
        var hoyo = shutdown.IndexOf("await DisposeHoyoPlayExecutorAsync", StringComparison.Ordinal);
        var unregister = shutdown.IndexOf("_currentInstance?.UnregisterKey()", StringComparison.Ordinal);
        var close = shutdown.IndexOf("_window?.Close()", StringComparison.Ordinal);

        Assert.True(page >= 0 && page < discovery);
        Assert.All(taskStarts, start => Assert.True(start > page && start < discovery));
        Assert.True(
            discovery >= 0
            && discovery < refresh
            && refresh < sessions
            && sessions < background
            && background < handoffs
            && handoffs < exports
            && exports < pulls
            && pulls < genshin
            && genshin < hoyo
            && hoyo < unregister
            && unregister < close);
        foreach (var call in new[]
        {
            "DisposeMainPageAsync",
            "DisposeLauncherBannersAsync",
            "DisposePublisherStatusAsync",
            "DisposeWuWaAccountStatusAsync",
            "DisposePublisherAccountsAsync",
            "CloseExportsForLauncherAsync",
            "DisposeRefreshAsync",
            "DisposeSessionsAsync",
            "DisposeAchievementHandoffsAsync",
            "DisposeExportCoordinatorAsync",
            "_pullExports?.Dispose()",
            "DisposeGenshin120FpsStarterAsync",
            "DisposeHoyoPlayExecutorAsync",
        })
        {
            Assert.Single(Regex.Matches(shutdown, Regex.Escape(call)).Cast<Match>());
        }
    }

    [Fact]
    public void Page_close_drains_export_registration_and_visual_preload_before_its_cache()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var window = ReadAppFile("MainWindow.xaml.cs");
        var shutdown = Slice(page, "internal Task ShutDownAsync()", "private HoyoMaintenanceUiSnapshot DiscoverHoyoMaintenance");
        var closeAdmission = shutdown.IndexOf("CloseExportRegistrationAdmission()", StringComparison.Ordinal);
        var terminate = shutdown.IndexOf("sessionUiLifetime.Terminate()", StringComparison.Ordinal);
        var drain = shutdown.IndexOf("await registrations", StringComparison.Ordinal);
        var preload = shutdown.IndexOf("await launcherVisualPreloadTask", StringComparison.Ordinal);
        var cache = shutdown.IndexOf("await launcherVisuals.DisposeAsync()", StringComparison.Ordinal);

        Assert.True(closeAdmission >= 0 && closeAdmission < terminate && terminate < drain && drain < preload && preload < cache);
        Assert.Contains("page.ShutDownAsync()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_registration_release_is_protected_before_any_render_can_throw()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(code, "private async void LaunchButton_Click", "private async Task ChooseGameFolderAsync");
        var immediate = Slice(code, "private async Task StartHoyoLabAchievementExportAsync", "private string GetAchievementSource");
        var shutdown = Slice(code, "private async Task ShutDownCoreAsync", "private HoyoMaintenanceUiSnapshot DiscoverHoyoMaintenance");

        Assert.Matches(
            @"if \(!TryEnterExportRegistration\(\)\)\s*\{\s*gameActionsInFlight\.Remove\(gameId\);\s*return;\s*\}\s*try\s*\{\s*RenderExportTools",
            launch);
        Assert.Matches(
            @"if \(!TryEnterExportRegistration\(\)\)\s*\{\s*reservation\.Dispose\(\);\s*return;\s*\}\s*try\s*\{\s*RenderSelection",
            immediate);
        foreach (var workflow in new[] { launch, immediate })
        {
            Assert.Single(Regex.Matches(workflow, "ReleaseExportRegistration\\(\\)").Cast<Match>());
            Assert.Matches(
                @"finally\s*\{\s*ReleaseExportRegistration\(\);\s*\}",
                workflow);
        }
        Assert.Contains("await registrations", shutdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepted_launch_stays_registered_until_export_close_can_start()
    {
        var app = ReadAppFile("App.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(page, "private async void LaunchButton_Click", "private async Task ChooseGameFolderAsync");
        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "private void Window_Closed");
        var admitted = launch.IndexOf("TryEnterExportRegistration()", StringComparison.Ordinal);
        var launchSettled = launch.IndexOf("await exports.RunForLaunchAsync", StringComparison.Ordinal);
        var handoffRegistered = launch.IndexOf("AchievementExportHandoffs.TrackAsync", StringComparison.Ordinal);
        var released = launch.LastIndexOf("ReleaseExportRegistration()", StringComparison.Ordinal);
        var pageDrained = shutdown.IndexOf("await DisposeMainPageAsync(mainWindow)", StringComparison.Ordinal);
        var exportCloseStarted = shutdown.IndexOf("CloseExportsForLauncherAsync(_exports)", StringComparison.Ordinal);

        Assert.True(admitted >= 0 && admitted < launchSettled && launchSettled < handoffRegistered && handoffRegistered < released);
        Assert.True(pageDrained >= 0 && pageDrained < exportCloseStarted);
    }

    [Fact]
    public void Activation_during_shutdown_cannot_show_or_refresh_the_page()
    {
        var app = ReadAppFile("App.xaml.cs");
        var closing = Slice(app, "private void AppWindow_Closing", "private async Task ShutDownAccountsAndCloseAsync");
        var instanceActivation = Slice(app, "private void CurrentInstance_Activated", "private void StartEndfieldSiblingDiscovery");
        var windowActivation = Slice(app, "private void Window_Activated", "private void LauncherState_Changed");
        var refresh = Slice(app, "private async Task RefreshAfterActivationAsync", "private static async Task DisposeRefreshAsync");
        var shutdownFlag = closing.IndexOf("_accountShutdownStarted = true", StringComparison.Ordinal);
        var instanceDetach = closing.IndexOf("_currentInstance.Activated -= CurrentInstance_Activated", StringComparison.Ordinal);
        var windowDetach = closing.IndexOf("_window.Activated -= Window_Activated", StringComparison.Ordinal);
        var hide = closing.IndexOf("sender.Hide()", StringComparison.Ordinal);

        Assert.True(shutdownFlag >= 0 && shutdownFlag < instanceDetach && shutdownFlag < windowDetach);
        Assert.True(instanceDetach < hide && windowDetach < hide);
        Assert.Equal(2, Regex.Matches(instanceActivation, "_accountShutdownStarted").Count);
        Assert.True(
            instanceActivation.LastIndexOf("_accountShutdownStarted", StringComparison.Ordinal)
            < instanceActivation.IndexOf("window.Activate()", StringComparison.Ordinal));
        Assert.True(
            windowActivation.IndexOf("!_accountShutdownStarted", StringComparison.Ordinal)
            < windowActivation.IndexOf("RefreshAfterActivationAsync()", StringComparison.Ordinal));
        Assert.True(
            refresh.IndexOf("if (_accountShutdownStarted) return", StringComparison.Ordinal)
            < refresh.IndexOf("WindowReactivated?.Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void Window_closed_only_detaches_handlers_and_runs_synchronous_abnormal_fallbacks()
    {
        var app = ReadAppFile("App.xaml.cs");
        var closed = Slice(app, "private void Window_Closed", "private async Task RefreshAfterActivationAsync");

        Assert.Contains("-= CurrentInstance_Activated", closed, StringComparison.Ordinal);
        Assert.Contains("-= LauncherState_Changed", closed, StringComparison.Ordinal);
        Assert.Contains("SessionUiLifetime.Terminate()", closed, StringComparison.Ordinal);
        Assert.Contains("CancelEndfieldSiblingDiscovery()", closed, StringComparison.Ordinal);
        Assert.Contains("_sessionRefresh?.Stop()", closed, StringComparison.Ordinal);
        Assert.Contains("_sessions?.Shutdown()", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("_ =", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeAsync", closed, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_tracking_captures_completion_before_publishing_or_handoff_registration()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(code, "private async void LaunchButton_Click", "private async Task ChooseGameFolderAsync");
        var immediate = Slice(code, "private async Task StartHoyoLabAchievementExportAsync", "private string GetAchievementSource");

        foreach (var workflow in new[] { launch, immediate })
        {
            var completion = workflow.IndexOf("WaitForCompletionAsync", StringComparison.Ordinal);
            var remember = workflow.IndexOf("ExportUiJobRetention.RememberLatest", StringComparison.Ordinal);
            var track = workflow.IndexOf("TrackExportJobAsync", remember, StringComparison.Ordinal);
            Assert.True(completion >= 0 && completion < remember && remember < track);
        }

        Assert.True(
            launch.IndexOf("TryEnterExportRegistration()", StringComparison.Ordinal)
            < launch.IndexOf("await exports.RunForLaunchAsync", StringComparison.Ordinal));
        Assert.True(
            launch.IndexOf("AchievementExportHandoffs.TrackAsync", StringComparison.Ordinal)
            < launch.IndexOf("ReleaseExportRegistration()", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_pull_router_uses_only_the_validated_wuwa_install_root()
    {
        var app = ReadAppFile("App.xaml.cs");
        var composition = Slice(
            app,
            "_pullExports = new RoutedPullExportProvider",
            "var achievementHelperPath");

        Assert.Contains("GetManualInstallRoot(\"wuwa\") ?? wuwaRootLocator.LocateRoot()", composition, StringComparison.Ordinal);
        Assert.Contains("PublisherGameLaunchService.CheckGame(\"wuwa\", root).Status", composition, StringComparison.Ordinal);
        Assert.Contains("PublisherGameLaunchStatus.Ready or PublisherGameLaunchStatus.Running", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("ae", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("Process", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void Post_close_owner_is_fixed_to_native_jobs_pengo_routes_and_export_folder()
    {
        var owner = ReadInfrastructureFile("Exports", "BoundedAchievementExportHandoffOwner.cs");
        var launcher = ReadAppFile("WindowsAchievementExportHandoffLauncher.cs");

        Assert.Contains("IsLauncherIndependentAchievementJob(jobId)", owner, StringComparison.Ordinal);
        Assert.Contains("WaitForCompletionAsync", owner, StringComparison.Ordinal);
        Assert.Contains("AchievementImportBridge", owner, StringComparison.Ordinal);
        Assert.Contains("exports.Cancel(jobId)", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", owner + launcher, StringComparison.Ordinal);
        Assert.Contains("uri.Host == \"pengo.gg\"", launcher, StringComparison.Ordinal);
        Assert.Contains("\"/genshin/achievements\" or \"/hsr/achievements\"", launcher, StringComparison.Ordinal);
        Assert.Contains("WindowsDocumentsDirectory.Get(), \"Pengo Exports\"", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_pull_exports_can_be_started_while_the_game_is_already_running()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var running = Slice(code, "private void SetRunningExportControls", "private void SetLaunchControls");
        var launch = Slice(code, "private async void LaunchButton_Click", "private async Task ChooseGameFolderAsync");
        var publisher = Slice(code, "private void RenderPublisherSession", "private void RenderEndfield");

        Assert.Contains("arm.CanStartWhileGameRunning", running, StringComparison.Ordinal);
        Assert.Contains("SetLaunchControls(true, \"EXPORT\"", running, StringComparison.Ordinal);
        Assert.Contains("!exports.GetSnapshot(jobId).IsFinished", running, StringComparison.Ordinal);
        Assert.Contains("var preflightSnapshot = sessions.GetSnapshot(gameId);", launch, StringComparison.Ordinal);
        Assert.Contains("preflightSnapshot.Status is LocalGameStatus.Running", launch, StringComparison.Ordinal);
        Assert.Contains("var admissionSnapshot = sessions.GetSnapshot(gameId);", launch, StringComparison.Ordinal);
        Assert.Contains("admissionSnapshot.Status is LocalGameStatus.Running", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("gameSnapshot?.Status", launch, StringComparison.Ordinal);
        Assert.Contains("!arm.CanStartWhileGameRunning", launch, StringComparison.Ordinal);
        Assert.Contains("&& arm.CanStartWhileGameRunning", launch, StringComparison.Ordinal);
        Assert.True(
            launch.IndexOf("&& arm.CanStartWhileGameRunning", StringComparison.Ordinal)
            < launch.IndexOf("sessions.RequestLaunchAsync", StringComparison.Ordinal));
        Assert.Contains("selected.Id == \"wuwa\"", publisher, StringComparison.Ordinal);
        Assert.Contains("SetRunningExportControls(selected.DisplayName, version: null)", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_achievement_export_opens_one_use_pengo_preview_with_file_fallback()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var tracker = Slice(
            code,
            "private async Task TrackExportJobAsync",
            "private void SessionRefresh_Refreshed");

        Assert.Contains("final.Achievements.State is ExportTaskState.Succeeded", tracker, StringComparison.Ordinal);
        Assert.Contains("IsHandoffCurrent: true", tracker, StringComparison.Ordinal);
        Assert.Contains("OutputPath: { Length: > 0 } outputPath", tracker, StringComparison.Ordinal);
        Assert.Contains("achievementImportBridge.StartAsync", tracker, StringComparison.Ordinal);
        Assert.Contains("Windows.System.Launcher.LaunchUriAsync(bridge.BrowserUri)", tracker, StringComparison.Ordinal);
        Assert.Contains("bridge.Completion.WaitAsync", tracker, StringComparison.Ordinal);
        Assert.Contains("AchievementHandoffUiState.Delivered", tracker, StringComparison.Ordinal);
        Assert.Contains("AchievementHandoffUiState.Fallback", tracker, StringComparison.Ordinal);
        Assert.Contains("Use Open Export Folder to view the file", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_pull_export_opens_only_the_documents_export_folder()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var tracker = Slice(
            code,
            "private async Task TrackExportJobAsync",
            "private void SessionRefresh_Refreshed");
        var directFolderAction = Slice(
            code,
            "private async void OpenExportsButton_Click",
            "private void CancelExportButton_Click");

        Assert.DoesNotContain("LaunchFileAsync", code, StringComparison.Ordinal);
        Assert.Contains("final.Pulls.State is ExportTaskState.Succeeded", tracker, StringComparison.Ordinal);
        Assert.Contains("await TryOpenExportsFolderAsync()", tracker, StringComparison.Ordinal);
        Assert.Contains("WindowsDocumentsDirectory.Get(), \"Pengo Exports\"", directFolderAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Photos", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OneDrive", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenExportsButton_Click", directFolderAction, StringComparison.Ordinal);
        Assert.Contains("LaunchFolderPathAsync", directFolderAction, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_layout_counts_only_controls_that_are_visible()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var layout = Slice(
            code,
            "private double ApplyToolButtonLayout",
            "private void SetRedemptionCodeRowHeight");

        Assert.Contains(".Where(button => button.Visibility is Visibility.Visible).ToArray()", layout, StringComparison.Ordinal);
        Assert.Contains("var visibleButtonCount = visibleButtons.Length", layout, StringComparison.Ordinal);
        Assert.Contains("var requiredWidth = visibleButtons.Sum", layout, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(button, AchievementExportPanel)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("var requiredWidth = 100 + 120 + 146", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Second_launcher_process_redirects_to_the_existing_window()
    {
        var app = ReadAppFile("App.xaml.cs");
        var launch = Slice(app, "protected override async void OnLaunched", "private void StartEndfieldSiblingDiscovery");

        Assert.Contains("AppInstance.FindOrRegisterForKey(MainInstanceKey)", launch, StringComparison.Ordinal);
        Assert.Contains("if (!mainInstance.IsCurrent)", launch, StringComparison.Ordinal);
        Assert.Contains("await mainInstance.RedirectActivationToAsync", launch, StringComparison.Ordinal);
        Assert.Contains("Exit();", launch, StringComparison.Ordinal);
        Assert.Contains("_currentInstance.Activated += CurrentInstance_Activated", launch, StringComparison.Ordinal);
        Assert.Contains("window.DispatcherQueue.TryEnqueue(() =>", launch, StringComparison.Ordinal);
        Assert.Contains("if (!_accountShutdownStarted) window.Activate()", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnqueue(window.Activate)", launch, StringComparison.Ordinal);
        Assert.True(
            launch.IndexOf("return;", launch.IndexOf("if (!mainInstance.IsCurrent)", StringComparison.Ordinal), StringComparison.Ordinal)
            < launch.IndexOf("var stateStore = new LauncherStateStore()", StringComparison.Ordinal));

        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "private void Window_Closed");
        var unregister = shutdown.IndexOf("_currentInstance?.UnregisterKey()", StringComparison.Ordinal);
        var close = shutdown.IndexOf("_window?.Close()", StringComparison.Ordinal);
        var unregisterFailure = shutdown.IndexOf("catch (Exception)", unregister, StringComparison.Ordinal);

        Assert.True(unregister >= 0 && unregisterFailure > unregister && unregisterFailure < close);
        Assert.True(shutdown.IndexOf("await Task.WhenAll", StringComparison.Ordinal) < unregister);
        Assert.True(shutdown.IndexOf("await DisposeExportCoordinatorAsync", StringComparison.Ordinal) < unregister);
        Assert.True(shutdown.IndexOf("await DisposeAchievementHandoffsAsync", StringComparison.Ordinal) < unregister);
    }

    [Fact]
    public void Unpackaged_launcher_sets_its_own_windows_identity_before_creating_a_window()
    {
        var app = ReadAppFile("App.xaml.cs");
        var launch = Slice(app, "protected override async void OnLaunched", "private void StartEndfieldSiblingDiscovery");

        Assert.Contains("private const string MainApplicationId = \"Pengo.Nyx.Desktop\"", app, StringComparison.Ordinal);
        Assert.Contains("SetCurrentProcessExplicitAppUserModelID(MainApplicationId)", launch, StringComparison.Ordinal);
        Assert.Contains("[DllImport(\"shell32.dll\", CharSet = CharSet.Unicode)]", app, StringComparison.Ordinal);
        Assert.True(
            launch.IndexOf("SetCurrentProcessExplicitAppUserModelID", StringComparison.Ordinal)
            < launch.IndexOf("AppInstance.FindOrRegisterForKey", StringComparison.Ordinal));
        Assert.True(
            launch.IndexOf("SetCurrentProcessExplicitAppUserModelID", StringComparison.Ordinal)
            < launch.IndexOf("_window = new MainWindow()", StringComparison.Ordinal));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker}.");
        Assert.True(end > start, $"Could not find {endMarker} after {startMarker}.");
        return source[start..end];
    }

    private static string ReadAppFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine(
            [WorkspaceRoot, "Desktop", "src", "Nyx.Desktop.App", .. relativeSegments]));

    private static string ReadInfrastructureFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine(
            [WorkspaceRoot, "Desktop", "src", "Nyx.Desktop.Infrastructure", .. relativeSegments]));

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
