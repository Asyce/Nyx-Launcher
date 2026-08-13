using System.Text.RegularExpressions;
using Nyx.Desktop.Core.AccountStatus;
using Nyx_Desktop_App.ViewModels;

namespace Nyx.Desktop.Tests.UI;

public sealed class HoyoLiveSessionUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_registers_production_adapters_for_all_five_rows()
    {
        var app = ReadAppFile("App.xaml.cs");

        var hsr = Slice(app, "[\"hsr\"] = new(", "[\"zzz\"] = new(");
        Assert.Contains("hoyoDiscovery", hsr, StringComparison.Ordinal);
        Assert.Contains("hoyoLaunchService", hsr, StringComparison.Ordinal);
        Assert.Contains("() => GetManualInstallRoot(\"hsr\")", hsr, StringComparison.Ordinal);
        Assert.Contains("[\"zzz\"] = new(", app, StringComparison.Ordinal);
        Assert.Contains("() => GetManualInstallRoot(\"zzz\")", app, StringComparison.Ordinal);
        Assert.Contains("() => GetHoyoRenderingMode(\"zzz\")", app, StringComparison.Ordinal);
        Assert.Contains("\"hsr\" or \"zzz\" => HoyoSessions[game.Id]", app, StringComparison.Ordinal);
        Assert.Contains("[\"wuwa\"] = new(", app, StringComparison.Ordinal);
        Assert.Contains("[\"ae\"] = new(", app, StringComparison.Ordinal);
        Assert.Contains("\"wuwa\" or \"ae\" => PublisherGameSessions[game.Id]", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new FailClosedGameSessionAdapter", app, StringComparison.Ordinal);
    }

    [Fact]
    public void One_launch_button_targets_the_selected_game_and_tracks_inflight_per_game()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("var gameId = selected.Id;", page, StringComparison.Ordinal);
        Assert.Contains("sessions.RequestLaunchAsync(gameId", page, StringComparison.Ordinal);
        Assert.Contains("HashSet<string> gameActionsInFlight", page, StringComparison.Ordinal);
        Assert.Contains("gameActionsInFlight.Add(gameId)", page, StringComparison.Ordinal);
        Assert.Contains("gameActionsInFlight.Remove(gameId)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions.RequestLaunchAsync(\"gi\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Delayed_launch_admission_stays_bound_to_the_captured_game()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(page, "private async void LaunchButton_Click", "private async void WuWaAccountStatusToggle_Click");
        var admission = Slice(
            launch,
            "async cancellationToken =>",
            "lease.CancellationToken);");

        Assert.Contains("var gameId = selected.Id;", launch, StringComparison.Ordinal);
        Assert.Contains("var admissionSnapshot = sessions.GetSnapshot(gameId);", admission, StringComparison.Ordinal);
        Assert.Contains("admissionSnapshot.Status is LocalGameStatus.Running", admission, StringComparison.Ordinal);
        Assert.Contains("sessions.RequestLaunchAsync(gameId, cancellationToken)", admission, StringComparison.Ordinal);
        Assert.DoesNotContain("gameSnapshot?.Status", launch, StringComparison.Ordinal);
        Assert.True(
            admission.IndexOf("sessions.GetSnapshot(gameId)", StringComparison.Ordinal)
            < admission.IndexOf("sessions.RequestLaunchAsync(gameId", StringComparison.Ordinal));
    }

    [Fact]
    public void Delayed_launch_result_and_failure_cannot_overwrite_another_selected_game()
    {
        var page = ReadAppFile("MainPage.xaml.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var launch = Slice(page, "private async void LaunchButton_Click", "private async void WuWaAccountStatusToggle_Click");
        const string selectionGuard =
            @"GameSelector\?\.SelectedItem is GameLauncherItem current\s*&& current\.Id == gameId\s*\)\s*\{";
        var resultUpdate = Slice(
            launch,
            "var result = await sessions.RequestLaunchAsync",
            "return result.Outcome");
        Assert.Matches(
            selectionGuard + @"[\s\S]*gameSnapshot = result\.Snapshot",
            resultUpdate);
        var failureUpdate = Slice(launch, "catch (Exception)", "finally");
        Assert.Matches(
            selectionGuard + @"[\s\S]*gameSnapshot = sessions\.GetSnapshot\(gameId\)",
            failureUpdate);
    }

    [Fact]
    public void Same_game_running_export_shortcut_still_admits_without_a_second_launch()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(page, "private async void LaunchButton_Click", "private async void WuWaAccountStatusToggle_Click");
        var admission = Slice(launch, "async cancellationToken =>", "if (gameId == \"hsr\")");

        Assert.Contains("var admissionSnapshot = sessions.GetSnapshot(gameId);", admission, StringComparison.Ordinal);
        Assert.Contains("admissionSnapshot.Status is LocalGameStatus.Running", admission, StringComparison.Ordinal);
        Assert.Contains("&& arm.CanStartWhileGameRunning", admission, StringComparison.Ordinal);
        Assert.Contains("return true;", admission, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_launch_prepares_120_fps_before_request_and_blocks_on_failure()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var launch = Slice(page, "var exportResult = await exports.RunForLaunchAsync(", "catch (OperationCanceledException)");
        var prepare = launch.IndexOf("app.PrepareHsr120FpsForLaunch()", StringComparison.Ordinal);
        var request = launch.IndexOf("sessions.RequestLaunchAsync(gameId", StringComparison.Ordinal);

        Assert.True(prepare >= 0 && request > prepare);
        Assert.Contains("if (!preparation.AllowsLaunch)", launch, StringComparison.Ordinal);
        Assert.Contains("hsr120FpsPreparationFailed = true", launch, StringComparison.Ordinal);
        Assert.Contains("return false", launch, StringComparison.Ordinal);
        Assert.Contains(
            "120 FPS safety check failed. Star Rail was not started.",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Screenshot_folder_click_resolves_freshly_and_opens_only_ready_paths()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var click = Slice(
            page,
            "private async void OpenScreenshotFolderButton_Click",
            "private void Fps120Toggle_Click");
        var resolve = click.IndexOf("await Task.Run(", StringComparison.Ordinal);
        var ready = click.IndexOf("result.Status is not GameScreenshotFolderStatus.Ready", StringComparison.Ordinal);
        var open = click.IndexOf("Windows.System.Launcher.LaunchFolderPathAsync", StringComparison.Ordinal);
        var finalizer = click.IndexOf("finally", StringComparison.Ordinal);
        var restored = click.IndexOf("screenshotFolderActionInFlight = false", StringComparison.Ordinal);

        Assert.Contains("var gameId = selected.Id", click, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(", click, StringComparison.Ordinal);
        Assert.Contains("() => app.ResolveScreenshotFolder(gameId)", click, StringComparison.Ordinal);
        Assert.Contains("current.Id != gameId", click, StringComparison.Ordinal);
        Assert.Contains("GameScreenshotFolderStatus.Ready", click, StringComparison.Ordinal);
        Assert.Contains("Windows.System.Launcher.LaunchFolderPathAsync(result.FolderPath)", click, StringComparison.Ordinal);
        Assert.True(resolve >= 0 && ready > resolve && open > ready && finalizer > open && restored > finalizer);
        Assert.DoesNotContain("Directory.CreateDirectory", click, StringComparison.Ordinal);
        Assert.DoesNotContain("result.FolderPath}", click, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_120_fps_toggle_reverts_when_the_preference_cannot_be_saved()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var toggle = Slice(
            page,
            "private void Fps120Toggle_Click",
            "private void SetOfficialLauncherStatus");

        Assert.Contains("app.TrySet120FpsOnLaunch(selected.Id, enabled)", toggle, StringComparison.Ordinal);
        Assert.Contains("Fps120Toggle.IsChecked = app.Is120FpsOnLaunch(selected.Id)", toggle, StringComparison.Ordinal);
        Assert.Contains("Nyx could not save the 120 FPS setting.", toggle, StringComparison.Ordinal);
    }

    [Fact]
    public void Async_launcher_status_is_bound_to_the_game_that_started_the_action()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var status = Slice(
            page,
            "private void SetOfficialLauncherStatus",
            "private async void OpenUpdaterButton_Click");
        var launchFinally = Slice(
            page,
            "if (hsr120FpsPreparationFailed)",
            "private async void WuWaAccountStatusToggle_Click");
        var screenshot = Slice(
            page,
            "private async void OpenScreenshotFolderButton_Click",
            "private void Fps120Toggle_Click");

        Assert.Contains("selected.IsCustom", status, StringComparison.Ordinal);
        Assert.Contains("selected.Id != gameId", status, StringComparison.Ordinal);
        Assert.Contains("SetOfficialLauncherStatus(", launchFinally, StringComparison.Ordinal);
        Assert.Contains("gameId,", launchFinally, StringComparison.Ordinal);
        Assert.Contains("SetOfficialLauncherStatus(gameId, result.Status switch", screenshot, StringComparison.Ordinal);
        Assert.Contains("await Windows.System.Launcher.LaunchFolderPathAsync", screenshot, StringComparison.Ordinal);
        Assert.Contains("SetOfficialLauncherStatus(gameId, \"Windows could not open that screenshot folder.\")", screenshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_and_refresh_render_each_hoyo_game_independently()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("gameSnapshot = sessions.TryGetSnapshot(selected.Id", page, StringComparison.Ordinal);
        Assert.Contains("selected.Id is \"hsr\" or \"zzz\"", page, StringComparison.Ordinal);
        Assert.Contains("RenderHoyo(selected)", page, StringComparison.Ordinal);
        Assert.Contains("e.Snapshots.TryGetValue(selected.Id", page, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalProjector.Project", page, StringComparison.Ordinal);
        Assert.Contains("PrimaryGameStatusProjector.Project", page, StringComparison.Ordinal);
        Assert.Contains("OpenUpdaterButton.Visibility = Visibility.Visible", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_window_activation_refresh_does_not_reset_close_confirmation()
    {
        var app = ReadAppFile("App.xaml.cs");
        var start = app.IndexOf("private async Task RefreshAfterActivationAsync()", StringComparison.Ordinal);
        var end = app.IndexOf("private static async Task DisposeRefreshAsync", start, StringComparison.Ordinal);
        var activation = app[start..end];

        Assert.Contains("await SessionRefresh.RefreshNowAsync()", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetAfterResumeAndRefreshAsync", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_account_strip_expires_daily_labels_and_displays_hsr_reserve_and_recovery()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var projection = ReadAppFile(Path.Combine("ViewModels", "LauncherLayoutState.cs"));

        Assert.Contains("PublisherAccountPresentation.IsCurrentDayCheckIn(checkIn, now)", page, StringComparison.Ordinal);
        Assert.Contains("RenderLocalAccountTimeTick();", page, StringComparison.Ordinal);
        Assert.Contains("resource.Reserve is { } reserve", projection, StringComparison.Ordinal);
        Assert.Contains("RemainingRecoverySeconds(resource, now)", projection, StringComparison.Ordinal);
        Assert.Contains("RESERVE {reserve}", projection, StringComparison.Ordinal);
        Assert.Contains("FULL {label}", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void Endfield_uses_separate_connect_lifecycle_and_keeps_numeric_data_in_protocol_terminal()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var service = ReadAppFile("PublisherAccountService.cs");

        Assert.Contains("ConnectPublisherAccountAsync(selected.Id)", page, StringComparison.Ordinal);
        Assert.Contains("selected.Id == \"ae\"", page, StringComparison.Ordinal);
        Assert.Contains("OpenOfficialResourcePageAsync(\"ae\")", page, StringComparison.Ordinal);
        Assert.Contains("OFFICIAL PROTOCOL TERMINAL", page, StringComparison.Ordinal);
        Assert.Contains("AcquireProfileOwnership(\"SKPORT\")", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RunProviderCheckInsAsync(\"SKPORT\", [\"ae\"]", service, StringComparison.Ordinal);
        Assert.Contains("oldSkportSession.CancelAsync()", service, StringComparison.Ordinal);
        Assert.Contains("skportGate.Dispose()", service, StringComparison.Ordinal);
        Assert.Contains("skportProfileOwner.Release()", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_stays_selected_while_periodic_energy_refresh_considers_supported_games_sequentially()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var service = ReadAppFile("PublisherAccountService.cs");
        var click = Slice(
            page,
            "private async void DailyCheckInButton_Click",
            "private async Task SetPublisherConsentAsync");
        var postDailyRefresh = Slice(
            page,
            "private async Task RefreshPublisherResourceAfterCheckInAsync",
            "private async Task DisconnectPublisherAccountAsync");
        var periodicRefresh = Slice(
            page,
            "private async void PublisherResourceRefreshTimer_Tick",
            "private void RenderLocalAccountTimeTick");
        var checkInBoundary = Slice(
            service,
            "public Task<DailyCheckInResult> CheckInAsync",
            "private async Task<DailyCheckInResult> CheckInCoreAsync");
        var checkInOperation = Slice(
            service,
            "private async Task<DailyCheckInResult> CheckInCoreAsync",
            "private async Task RunProviderCheckInsAsync");

        Assert.Contains("PublisherAccountCatalog.Get(selected.Id).SupportsDailyCheckIn", click, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.CheckInAsync(", click, StringComparison.Ordinal);
        Assert.Contains("selected.Id,", click, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckInAllAsync", click, StringComparison.Ordinal);
        Assert.Contains(
            "DailyCheckInState.Claimed or DailyCheckInState.AlreadyClaimed",
            click,
            StringComparison.Ordinal);
        Assert.Contains("selectedGameId", postDailyRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach", postDailyRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("selected: false", postDailyRefresh, StringComparison.Ordinal);
        Assert.Contains("foreach (var gameId in new[] { \"gi\", \"hsr\", \"zzz\" })", periodicRefresh, StringComparison.Ordinal);
        Assert.Contains("await RefreshPublisherResourceAutomaticallyAsync(", periodicRefresh, StringComparison.Ordinal);
        Assert.Contains("selected: string.Equals(selectedId, gameId", periodicRefresh, StringComparison.Ordinal);
        Assert.Contains("IsWuWaAccountStatusEnabled()", periodicRefresh, StringComparison.Ordinal);
        Assert.Contains("await RefreshWuWaAccountStatusAsync(lease)", periodicRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll", periodicRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ae\"", periodicRefresh, StringComparison.Ordinal);
        Assert.Contains("Interval = TimeSpan.FromMinutes(5)", page, StringComparison.Ordinal);
        Assert.Contains("!entry.SupportsDailyCheckIn", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("DailyCheckInState.Unavailable", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("checkInSingleFlights.TryGetValue(gameId", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("[entry.GameId]", checkInOperation, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"gi\", \"hsr\", \"zzz\"]", checkInOperation, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"ae\"]", checkInOperation, StringComparison.Ordinal);
        Assert.Contains(
            "var dailySupported = PublisherAccountCatalog.Get(selected.Id).SupportsDailyCheckIn;",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_energy_refresh_requires_connected_supported_accounts_and_only_renders_the_selected_game()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var refresh = Slice(
            page,
            "private async Task RefreshPublisherResourceAutomaticallyAsync",
            "private async Task RefreshPublisherResourcesOnStartupAsync");

        Assert.Contains("entry?.SupportsNumericResource != true", refresh, StringComparison.Ordinal);
        Assert.Contains("connection != PublisherConnectionState.Connected", refresh, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceRefreshPolicy.IsDue(", refresh, StringComparison.Ordinal);
        Assert.Contains("await publisherAccounts.RefreshResourceAsync(", refresh, StringComparison.Ordinal);
        Assert.Contains("string.Equals(current.Id, gameId, StringComparison.Ordinal)", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_account_actions_are_double_gated_by_default_off_per_publisher_consent()
    {
        var app = ReadAppFile("App.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");
        var service = ReadAppFile("PublisherAccountService.cs");
        var flags = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.Core",
            "Features",
            "LauncherFeatureFlags.cs"));

        Assert.Contains("public bool HoyoLabAccountAccess { get; init; }", flags, StringComparison.Ordinal);
        Assert.Contains("public bool SkportAccountAccess { get; init; }", flags, StringComparison.Ordinal);
        Assert.Contains("public bool HoyoLabAccountCleanupPending { get; init; }", flags, StringComparison.Ordinal);
        Assert.Contains("public bool SkportAccountCleanupPending { get; init; }", flags, StringComparison.Ordinal);
        Assert.Contains("accountFlags.HoyoLabAccountAccess", app, StringComparison.Ordinal);
        Assert.Contains("accountFlags.SkportAccountAccess", app, StringComparison.Ordinal);
        Assert.Contains("accountFlags.HoyoLabAccountCleanupPending", app, StringComparison.Ordinal);
        Assert.Contains("accountFlags.SkportAccountCleanupPending", app, StringComparison.Ordinal);
        Assert.Contains("LauncherState.Changed += LauncherState_Changed", app, StringComparison.Ordinal);
        Assert.Contains("_publisherAccounts?.ApplyConsentSnapshot", app, StringComparison.Ordinal);
        Assert.Contains("RecoverPendingPublisherRevocationsAsync", app, StringComparison.Ordinal);
        Assert.Contains("HasPublisherConsent(gameId)", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.RevokeConsentAsync", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.PrepareConsentEnableAsync", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.CompleteConsentRevocation", page, StringComparison.Ordinal);
        Assert.Contains(
            "launcherState.TryUpdatePublisherCleanupPending(",
            page,
            StringComparison.Ordinal);
        Assert.Contains("cleanupPending: !enabled", page, StringComparison.Ordinal);
        Assert.Contains("accountAccess: enabled", page, StringComparison.Ordinal);
        Assert.Contains("OFF · CLEANUP PENDING", page, StringComparison.Ordinal);

        var officialPage = Slice(
            service,
            "public async Task<bool> OpenOfficialResourcePageAsync",
            "public PublisherAccountSummary");
        Assert.Contains("consent.IsEnabled(entry.Provider)", officialPage, StringComparison.Ordinal);
        Assert.True(
            officialPage.IndexOf("consent.IsEnabled(entry.Provider)", StringComparison.Ordinal)
            < officialPage.IndexOf("Launcher.LaunchUriAsync", StringComparison.Ordinal));
        Assert.Contains(
            "if (!consent.IsEnabled(entry.Provider))",
            Slice(service, "public async Task<PublisherConnectionState> ConnectAsync", "public Task<PublisherResourceSnapshot?>"),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!consent.IsEnabled(entry.Provider))",
            Slice(service, "public Task<PublisherResourceSnapshot?> RefreshResourceAsync", "private async Task<PublisherResourceSnapshot?>"),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!consent.IsEnabled(entry.Provider))",
            Slice(service, "public async Task<PublisherConnectionState> DisconnectAsync", "public async Task<PublisherConnectionState> RevokeConsentAsync"),
            StringComparison.Ordinal);
        Assert.Contains("if (!consent.IsEnabled(entry.Provider))", service, StringComparison.Ordinal);
        var revoke = Slice(
            service,
            "public async Task<PublisherConnectionState> RevokeConsentAsync",
            "private async Task<PublisherConnectionState> DisconnectCoreAsync");
        Assert.True(
            revoke.IndexOf(
                "SetConsentSynchronized(entry.Provider, enabled: false)",
                StringComparison.Ordinal)
            < revoke.IndexOf(
                "revocations.MarkOptOutPending(entry.Provider)",
                StringComparison.Ordinal));
        Assert.True(
            revoke.IndexOf(
                "revocations.MarkOptOutPending(entry.Provider)",
                StringComparison.Ordinal)
            < revoke.IndexOf("DisconnectCoreAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Multiple_hoyo_roles_use_a_transient_unselected_identity_picker_and_protected_store()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var service = ReadAppFile("PublisherAccountService.cs");
        var store = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.Infrastructure",
            "AccountStatus",
            "PublisherRoleBindingStore.cs"));

        var picker = Slice(
            page,
            "private async Task<PublisherRoleBinding?> ChoosePublisherRoleAsync",
            "private async Task ConnectPublisherAccountAsync");
        Assert.Contains("IsPrimaryButtonEnabled = false", picker, StringComparison.Ordinal);
        Assert.Contains(
            "DisplayMemberPath = nameof(PublisherRoleChoice.DisplayText)",
            picker,
            StringComparison.Ordinal);
        Assert.Contains("Title = \"Choose Region\"", picker, StringComparison.Ordinal);
        Assert.Contains("Content = list", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("nickname and full UID", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("only in this chooser", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("masked UID", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", picker, StringComparison.Ordinal);
        Assert.Contains("list.ItemsSource = null", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedIndex", picker, StringComparison.Ordinal);
        Assert.Contains("roleBindings.Save", service, StringComparison.Ordinal);
        Assert.Contains("roleBindings.DeleteProvider", service, StringComparison.Ordinal);
        Assert.Contains(
            "TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation)",
            service,
            StringComparison.Ordinal);
        Assert.Contains("CryptProtectData", store, StringComparison.Ordinal);
        Assert.Contains("CryptUnprotectData", store, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", store, StringComparison.Ordinal);
        Assert.Contains("PublisherRoleRecordRules.IsValid", store, StringComparison.Ordinal);
        Assert.Contains("record.Nickname ?? string.Empty", store, StringComparison.Ordinal);
        Assert.Contains("SerializeV2(gameId, record)", store, StringComparison.Ordinal);
        Assert.Contains(
            "StrictUtf8.GetBytes($\"1\\n{gameId}\\n{binding.RoleId}\\n{binding.Server}\")",
            store,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Private_role_identity_chooser_never_exports_identity()
    {
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.DoesNotContain("publisherRoleChooserOpen", page, StringComparison.Ordinal);
        var picker = Slice(
            page,
            "private async Task<PublisherRoleBinding?> ChoosePublisherRoleAsync",
            "private async Task ConnectPublisherAccountAsync");
        Assert.DoesNotContain("Clipboard", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_publisher_resource_projection_keeps_gi_hsr_and_zzz_numbers_intact()
    {
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
        var gi = PublisherAccountDisplayProjection.FormatCompactResource(
            new("gi", "Original Resin", 200, 200, now),
            now);
        var hsr = PublisherAccountDisplayProjection.FormatCompactResource(
            new("hsr", "Trailblaze Power", 300, 300, now, Reserve: 2400),
            now);
        var zzz = PublisherAccountDisplayProjection.FormatCompactResource(
            new("zzz", "Battery Charge", 240, 240, now),
            now);

        Assert.Equal("200/200", gi.Value);
        Assert.Contains("ORIGINAL RESIN", gi.Label, StringComparison.Ordinal);
        Assert.Contains("300/300", hsr.Value, StringComparison.Ordinal);
        Assert.Contains("2400", hsr.Value, StringComparison.Ordinal);
        Assert.Contains("RESERVE", hsr.Label, StringComparison.Ordinal);
        Assert.Equal("240/240", zzz.Value);
        Assert.Contains("BATTERY CHARGE", zzz.Label, StringComparison.Ordinal);
        Assert.Contains("200/200", gi.AutomationText, StringComparison.Ordinal);
        Assert.Contains("300/300", hsr.AutomationText, StringComparison.Ordinal);
        Assert.Contains("2400", hsr.AutomationText, StringComparison.Ordinal);
        Assert.Contains("240/240", zzz.AutomationText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_publisher_resource_projection_keeps_recovery_numbers_and_stale_semantics()
    {
        var observed = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
        var fresh = PublisherAccountDisplayProjection.FormatCompactResource(
            new(
                "gi",
                "Original Resin",
                120,
                200,
                observed,
                RecoverySeconds: 3660),
            observed);
        var stale = PublisherAccountDisplayProjection.FormatCompactResource(
            new(
                "hsr",
                "Trailblaze Power",
                100,
                300,
                observed,
                IsStale: true,
                RecoverySeconds: 3600,
                Reserve: 840),
            observed);

        Assert.Contains("FULL", fresh.Label, StringComparison.Ordinal);
        Assert.Contains("1H", fresh.Value, StringComparison.Ordinal);
        Assert.Contains("1M", fresh.Value, StringComparison.Ordinal);
        Assert.Contains("STALE", stale.Label, StringComparison.Ordinal);
        Assert.Contains("100/300", stale.Value, StringComparison.Ordinal);
        Assert.Contains("840", stale.Value, StringComparison.Ordinal);
        Assert.Contains("1H", stale.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_resource_projection_keeps_each_icon_value_separate()
    {
        var observed = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var hsr = LauncherResourceMetricsProjection.FromPublisher(
            new(
                "hsr",
                "Trailblaze Power",
                119,
                300,
                observed,
                RecoverySeconds: 64_680,
                Reserve: 86),
            observed);

        Assert.Equal("119/300", hsr.Primary);
        Assert.Equal("86", hsr.Reserve);
        Assert.Equal("17H 58M", hsr.Recovery);
        Assert.Null(hsr.Daily);
        Assert.Contains("TRAILBLAZE POWER", hsr.AutomationText, StringComparison.Ordinal);

        var page = ReadAppFile("MainPage.xaml.cs");
        Assert.Contains("[\"hsr\"] = \"ms-appx:///Assets/Content/ResourceIcons/trailblaze-power.webp\"", page, StringComparison.Ordinal);
        Assert.Contains("[\"hsr\"] = \"ms-appx:///Assets/Content/ResourceIcons/reserved-trailblaze-power.webp\"", page, StringComparison.Ordinal);
        Assert.Contains("private readonly Dictionary<string, BitmapImage> imageSourceCache", page, StringComparison.Ordinal);
        Assert.Contains("var primaryIcon = ResolveImageSource(primaryIconPath)", page, StringComparison.Ordinal);
        Assert.Contains("var reserveIcon = ResolveImageSource(reserveIconPath)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Redesigned_controls_use_icons_and_only_offer_real_export_sources()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var page = ReadAppFile("MainPage.xaml.cs");
        var launchStart = xaml.IndexOf("x:Name=\"LaunchResourceMetricsPanel\"", StringComparison.Ordinal);
        var launchSurfaceStart = xaml.LastIndexOf("<Border", launchStart, StringComparison.Ordinal);
        var launchSurface = xaml[launchSurfaceStart..launchStart];
        var launch = Slice(xaml, "x:Name=\"LaunchResourceMetricsPanel\"", "x:Name=\"LaunchButton\"");
        var achievementToggle = Slice(xaml, "x:Name=\"StableAchievementExportToggle\"", "/>");
        var pullToggle = Slice(xaml, "x:Name=\"StablePullExportToggle\"", "/>");
        var gameAchievementSource = Slice(xaml, "x:Name=\"GameAchievementSourceRadio\"", "/>");
        var hoyoAchievementSource = Slice(xaml, "x:Name=\"HoyoLabAchievementSourceRadio\"", "/>");
        var sourceOptions = Slice(xaml, "x:Name=\"AchievementSourceOptionsPanel\"", ">");

        Assert.Contains("x:Name=\"LaunchPrimaryResourceIcon\"", launch, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchReserveResourceIcon\"", launch, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchRecoveryResourceItem\"", launch, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,2\"", launchSurface, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=\"50\"", launch + launchSurface, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(LaunchPrimaryResourceIcon.Source, primaryIcon)", page, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(LaunchReserveResourceIcon.Source, reserveIcon)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchResourceLabelText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchResourceValueText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PullSourceOptionsPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GamePullSourceRadio", xaml, StringComparison.Ordinal);
        foreach (var control in new[] { achievementToggle, pullToggle, gameAchievementSource, hoyoAchievementSource })
        {
            Assert.Contains("Padding=\"4,0\"", control, StringComparison.Ordinal);
            Assert.Contains("VerticalContentAlignment=\"Center\"", control, StringComparison.Ordinal);
        }
        Assert.Contains("MinHeight=\"28\"", gameAchievementSource, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"28\"", hoyoAchievementSource, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("Text=\"Export from:\"", Slice(xaml, "x:Name=\"AchievementSourcePrefix\"", "/>"), StringComparison.Ordinal);
        Assert.DoesNotContain("Choose what Nyx should export", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Use the official launcher for updates and pre-loads", xaml, StringComparison.Ordinal);
        Assert.Contains("HoyoLabAchievementSourceRadio.Visibility = selected.Id == \"hsr\"", page, StringComparison.Ordinal);
        Assert.Contains("AchievementSourceOptionsPanel.Visibility = selected.Id == \"hsr\"", page, StringComparison.Ordinal);
        Assert.Contains("var sourceLocked = armed.PullsArmed || armed.AchievementsArmed", page, StringComparison.Ordinal);
        Assert.Contains("GameAchievementSourceRadio.IsEnabled = achievementsSupported", page, StringComparison.Ordinal);
        Assert.Contains("&& !sourceLocked", Slice(page, "GameAchievementSourceRadio.IsEnabled", "AchievementExportToggle.Height"), StringComparison.Ordinal);
        Assert.Contains("HoyoLabAchievementSourceRadio.Visibility = selected.Id == \"hsr\" && !armed.PullsArmed", page, StringComparison.Ordinal);
        Assert.Contains("if (existing.PullsArmed || existing.AchievementsArmed)", page, StringComparison.Ordinal);
        Assert.Contains("var pullsOffered = selected.Id is \"gi\" or \"hsr\" or \"zzz\" or \"wuwa\"", page, StringComparison.Ordinal);
        Assert.Contains("var achievementsOffered = selected.Id is \"gi\" or \"hsr\" or \"zzz\"", page, StringComparison.Ordinal);
        var achievementCard = Slice(xaml, "x:Name=\"AchievementExportCard\"", "x:Name=\"StableAchievementExportToggle\"");
        var pullCard = Slice(xaml, "x:Name=\"PullExportCard\"", "x:Name=\"StablePullExportToggle\"");
        Assert.Contains("Grid.Column=\"1\"", achievementCard, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=\"1\"", pullCard, StringComparison.Ordinal);
        Assert.Contains("dailyLabel ?? resourceLabel", page, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"{resourceLabel} · {dailyLabel}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_resource_icons_match_the_reviewed_content_addresses()
    {
        var iconRoot = Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "Assets",
            "Content",
            "ResourceIcons");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["original-resin.webp"] = "fd63ab52b8646134853ae04dba16ba018bc80dc33b4fba2ecb80eb0f317472b0",
            ["trailblaze-power.webp"] = "56b4f641fc107cf0fbcb280c5b5766f8fba2f1b3e181c0bb519e1c9409318a9f",
            ["reserved-trailblaze-power.webp"] = "793337ed5dc72b3b61c3084f937046369300c93f7463a7c604f58954cdd9bf88",
            ["battery-charge.webp"] = "102fa085204b461e3d9d7b40e5ec5623820a6dc97120541115621a3195f5afee",
            ["backup-energy.webp"] = "b6c88c97b37e0497beab94da8c8799d58523e703d983dd7a62c526c688c9e900",
            ["waveplate.webp"] = "3434adc405327e4465ac5d59fa38c6bb3bbc6d35d61d4767563bcb955de36791",
            ["waveplate-crystal.webp"] = "e40291f7b29d83df1c8b5e2500cc45df434718cb2dd8de24af64d9a89a3edc41",
        };

        foreach (var (file, hash) in expected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(iconRoot, file));
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    [Fact]
    public void Fixed_width_account_strip_trims_label_before_the_numeric_value()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var page = ReadAppFile("MainPage.xaml.cs");
        var strip = Slice(
            xaml,
            "x:Name=\"WuWaAccountStatusStrip\"",
            "x:Name=\"PengoToolsLabel\"");
        var render = Slice(
            page,
            "private void RenderPublisherAccountStatus",
            "public static string FormatPublisherResource");

        Assert.Contains("x:Name=\"PublisherResourceMetricGrid\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WuWaAccountResourceValueText\"", strip, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", strip, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", strip, StringComparison.Ordinal);
        var value = Slice(
            strip,
            "x:Name=\"WuWaAccountResourceValueText\"",
            "/>");
        Assert.Contains("TextWrapping=\"NoWrap\"", value, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"None\"", value, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherAccountDisplayProjection.FormatCompactResource(resource, now)",
            render,
            StringComparison.Ordinal);
        Assert.Contains("WuWaAccountResourceValueText.Text = compact.Value", render, StringComparison.Ordinal);
        Assert.Contains("WuWaAccountMetricsText.Text = compact.Label", render, StringComparison.Ordinal);
        Assert.Contains("WuWaAccountResourceValueText.Text = string.Empty", render, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(", render, StringComparison.Ordinal);
        Assert.Contains("compact.AutomationText", render, StringComparison.Ordinal);
        Assert.Contains("resourceGuidance is not null", render, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceState.Fresh when resource is not null",
            render,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceState.Stale when resource is not null",
            render,
            StringComparison.Ordinal);
        var stripOpeningTag = strip[..strip.IndexOf('>')];
        Assert.Contains("Height=\"58\"", stripOpeningTag, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"", stripOpeningTag, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_account_rows_preserve_resource_value_and_show_each_recovery_action_once()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var page = ReadAppFile("MainPage.xaml.cs");
        var strip = Slice(
            xaml,
            "x:Name=\"WuWaAccountStatusStrip\"",
            "x:Name=\"PengoToolsLabel\"");
        var header = Slice(
            strip,
            "x:Name=\"PublisherAccountHeaderGrid\"",
            "x:Name=\"AccountConnectionWarningText\"");
        var actions = Slice(
            xaml,
            "x:Name=\"PublisherAccountActionGrid\"",
            "x:Name=\"PengoToolsLabel\"");
        var render = Slice(
            page,
            "private void RenderPublisherAccountStatus",
            "public static string FormatPublisherResource");

        Assert.Contains("x:Name=\"RenderingModePanel\"", header, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WuWaAccountStatusToggle\"", header, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PublisherResourceMetricGrid\"", header, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PublisherResourceMetricGrid\"", actions, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WuWaAccountResourceValueText\"", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WuWaAccountStatusToggle\"", actions, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PublisherAccountConnectButton\"", actions, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WuWaAccountStatusRefreshButton\"", actions, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DailyCheckInButton\"", actions, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", actions, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", actions, StringComparison.Ordinal);
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(render, "SIGN IN AGAIN")
                .Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains(
            "PublisherResourceState.LoginRequired => $\"{entry.ResourceName.ToUpperInvariant()}  —\"",
            render,
            StringComparison.Ordinal);
        Assert.Contains("&& connection == PublisherConnectionState.Connected", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_achievement_session_uses_the_current_official_session_initializer()
    {
        var source = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            source,
            "private static string BuildHsrAchievementExportScript",
            "private async Task NavigateAsync");

        Assert.Contains("typeof window.Vue === 'function'", script, StringComparison.Ordinal);
        Assert.Contains("webpackRequire.n(vueModule)", script, StringComparison.Ordinal);
        Assert.Contains("Vue.prototype.$session", script, StringComparison.Ordinal);
        Assert.Contains("typeof publisherSession.init === 'function'", script, StringComparison.Ordinal);
        Assert.Contains("typeof publisherSession.recheck === 'function'", script, StringComparison.Ordinal);
        Assert.Contains("typeof publisherSession.initGameRole === 'function'", script, StringComparison.Ordinal);
        Assert.Contains("typeof roleUtil.setInitOptions === 'function'", script, StringComparison.Ordinal);
        Assert.Contains("roleUtil.setInitOptions({ tokenType: 'ltoken' });", script, StringComparison.Ordinal);
        Assert.Contains("await publisherSession.recheck();", script, StringComparison.Ordinal);
        Assert.Contains("await publisherSession.init();", script, StringComparison.Ordinal);
        Assert.Contains("const publisherState = publisherSession.state", script, StringComparison.Ordinal);
        Assert.Contains("await publisherSession.initGameRole();", script, StringComparison.Ordinal);
        Assert.Contains(
            "const FALLBACK_LIST = 'https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list';",
            script,
            StringComparison.Ordinal);
        Assert.Contains("if (retcode === -100)", script, StringComparison.Ordinal);
        Assert.Contains("result = await request(fallbackUrl, 2097152, 'list');", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("roleUtil.setInitOptions({ tokenType: 'ltoken' });", StringComparison.Ordinal)
            < script.IndexOf("await publisherSession.recheck();", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("await publisherSession.recheck();", StringComparison.Ordinal)
            < script.IndexOf("await publisherSession.init();", StringComparison.Ordinal));
        var primaryRoleInit = Slice(
            script,
            "await publisherSession.initGameRole();",
            "publisherRole = publisherSession.state");
        Assert.DoesNotContain("throw new Error('session-role');", primaryRoleInit, StringComparison.Ordinal);
        Assert.Contains("publisherSession.state.role", script, StringComparison.Ordinal);
        Assert.Contains("Vue.prototype.$accountRoleUtil", script, StringComparison.Ordinal);
        Assert.Contains("await roleUtil.initGameRole({", script, StringComparison.Ordinal);
        Assert.Contains("chooseRoleExplicitly: list =>", script, StringComparison.Ordinal);
        Assert.Contains("const provenRole = Object.freeze({", script, StringComparison.Ordinal);
        Assert.Contains("publisherRole = publisherRole || explicitlySelectedRole || provenRole", script, StringComparison.Ordinal);
        Assert.Contains("matches.length !== 1", script, StringComparison.Ordinal);
        Assert.Contains("explicitlySelectedRole = matches[0]", script, StringComparison.Ordinal);
        Assert.Contains("publisherRole = publisherRole || explicitlySelectedRole", script, StringComparison.Ordinal);
        Assert.Contains("publisherRole = selectedRole", script, StringComparison.Ordinal);
        Assert.Contains("cookie.Name, \"account_id_v2\"", source, StringComparison.Ordinal);
        Assert.Contains("cookie.Name, \"ltuid_v2\"", source, StringComparison.Ordinal);
        Assert.Contains(".Distinct(StringComparer.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("accountIds.Length != 1", source, StringComparison.Ordinal);
        Assert.Contains("accountIds[0].Length is < 1 or > 32", source, StringComparison.Ordinal);
        Assert.Contains("!accountIds[0].All(char.IsAsciiDigit)", source, StringComparison.Ordinal);
        Assert.Contains("hoyolab-api-account-mismatch", source, StringComparison.Ordinal);
        Assert.Contains(
            "throw new ExportProviderException(\"hoyolab-api-account-mismatch\");",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$\"hoyolab-api-account-mismatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("publisherState.account", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLISHER_ACCOUNT_ID", script, StringComparison.Ordinal);
        Assert.DoesNotContain("getRoleInfoByAccount", script, StringComparison.Ordinal);
        Assert.DoesNotContain("getInfoByAccount", script, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT_LOGIN_ACCOUNT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bindRoleDirect", script, StringComparison.Ordinal);
        Assert.DoesNotContain("method: 'POST'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelectorAll", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_achievement_login_recovery_opens_the_exact_tool_once_then_retries_hidden()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var recovery = Slice(
            service,
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsWithVisibleRecoveryAsync",
            "public PublisherAccountSummary Current");
        var hiddenRead = Slice(
            recovery,
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsOnceAsync",
            "private static bool RequiresVisibleHsrAchievementLogin");

        var firstHiddenAttempt = recovery.IndexOf(
            "return await ReadHsrAchievementsOnceAsync(",
            StringComparison.Ordinal);
        var reviewedFailure = recovery.IndexOf(
            "RequiresVisibleHsrAchievementLogin(exception.Code)",
            StringComparison.Ordinal);
        var visiblePage = recovery.IndexOf("visible: true", StringComparison.Ordinal);
        var completion = recovery.IndexOf(
            "completion = await window.WaitForConnectCompletionAsync",
            StringComparison.Ordinal);
        var boundedRetry = recovery.LastIndexOf(
            "return await ReadHsrAchievementsOnceAsync(",
            StringComparison.Ordinal);
        Assert.True(
            firstHiddenAttempt >= 0
            && firstHiddenAttempt < reviewedFailure
            && reviewedFailure < visiblePage
            && visiblePage < completion
            && completion < boundedRetry);
        Assert.Contains(
            "PublisherAccountCatalog.GetAchievementPageUri(gameId)",
            recovery,
            StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Connect", recovery, StringComparison.Ordinal);
        Assert.Contains("ProfileMutationsFor(provider)", recovery, StringComparison.Ordinal);
        Assert.Contains("purpose: PublisherSessionPurpose.Achievements", hiddenRead, StringComparison.Ordinal);
        Assert.Contains("hoyolab-login-required", recovery, StringComparison.Ordinal);
        Assert.Contains("hoyolab-api-cookie-missing", recovery, StringComparison.Ordinal);
        Assert.Contains("hoyolab-login-retcode--100", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyolab-list-retcode", recovery, StringComparison.Ordinal);
        Assert.Contains("completion != PublisherVisibleConnectCompletion.Done", recovery, StringComparison.Ordinal);
        Assert.Contains("hoyolab-achievement-login-canceled", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", recovery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CookieManager", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void Redesigned_hoyo_account_panel_keeps_identity_separate_from_connection_state()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var page = ReadAppFile("MainPage.xaml.cs");
        var accountButton = Slice(xaml, "x:Name=\"ChangePublisherAccountButton\"", "/>" );

        Assert.Contains("x:Name=\"AccountAndToolsIdentityText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"HoYoLAB character identity\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Connect or review the selected publisher account\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Accounts\"", accountButton, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Change region or account\"", accountButton, StringComparison.Ordinal);
        Assert.DoesNotContain("Accounts &amp; region", xaml, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.GetHoyoLabIdentity(selected.Id)", page, StringComparison.Ordinal);
        Assert.Contains("identity.CharacterSummary", page, StringComparison.Ordinal);
        Assert.Contains("AccountAndToolsIdentityText.Visibility = accountSectionExpanded && identity is { IsBound: true }", page, StringComparison.Ordinal);
        Assert.Contains("? Visibility.Visible\n            : Visibility.Collapsed", page.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\u00B7 Choose Region", page, StringComparison.Ordinal);
        Assert.DoesNotContain("localLabel is not null", page, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountAndToolsIdentityText.Text = connection", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Hoyo_account_manager_uses_single_selection_inline_labels_and_exact_slot_actions()
    {
        var page = ReadAppFile("MainPage.xaml.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private async Task ShowHoyoLabAccountManagerAsync(string gameId)", page, StringComparison.Ordinal);
        Assert.Contains("SelectionMode = ListViewSelectionMode.Single", page, StringComparison.Ordinal);
        Assert.Contains("MaxLength = HoyoLabAccountSlotRules.MaximumLabelScalars", page, StringComparison.Ordinal);
        Assert.Contains("CreateHoyoLabManagerButton(\"Use\"", page, StringComparison.Ordinal);
        Assert.Contains("CreateHoyoLabManagerButton(\"Add\"", page, StringComparison.Ordinal);
        Assert.Contains("CreateHoyoLabManagerButton(\"Rename\"", page, StringComparison.Ordinal);
        Assert.Contains("CreateHoyoLabManagerButton(\"Forget\"", page, StringComparison.Ordinal);
        Assert.Contains("CreateHoyoLabManagerButton(\n            \"Choose Region\"", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.AddHoyoLabAccountAsync(", page, StringComparison.Ordinal);
        Assert.Contains("label,\n                        gameId,", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.RenameHoyoLabAccountAsync(", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.UseHoyoLabAccountAsync(", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.ForgetHoyoLabAccountAsync(", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.ChangeRoleAsync(", page, StringComparison.Ordinal);
        Assert.Contains("slot.Id,\n                        label,", page, StringComparison.Ordinal);
        Assert.Contains("slot.Id,\n                        gameId,", page, StringComparison.Ordinal);
        Assert.Contains("slot.Id,\n                        cancellationToken", page, StringComparison.Ordinal);
        Assert.Contains("ChoosePublisherRoleAsync,", page, StringComparison.Ordinal);
        Assert.Contains("void QueueRegionChoice(string slotId)", page, StringComparison.Ordinal);
        Assert.Contains("QueueRegionChoice(activeSlotId)", page, StringComparison.Ordinal);
        Assert.Contains("QueueRegionChoice(slot.Id)", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.GetHoyoLabIdentity(gameId)?.IsBound != true", page, StringComparison.Ordinal);
        Assert.Contains("Title = \"Choose Region\"", page, StringComparison.Ordinal);
        Assert.Contains("Content = list", page, StringComparison.Ordinal);
        Assert.Contains("if (choices.Count == 0)", page, StringComparison.Ordinal);
        Assert.Contains("PublisherConnectionState.LoginRequired => \"Sign in required\"", page, StringComparison.Ordinal);
        Assert.Contains("statuses.Add(\"Active\")", page, StringComparison.Ordinal);
        Assert.Contains("statuses.Add(\"Legacy\")", page, StringComparison.Ordinal);
        Assert.Contains("statuses.Add(\"Removal pending\")", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Email", page, StringComparison.Ordinal);
        Assert.DoesNotContain("email", page, StringComparison.Ordinal);
        Assert.DoesNotContain("slot.Id} \\u00B7", page, StringComparison.Ordinal);
        var manager = Slice(page, "private async Task ShowHoyoLabAccountManagerAsync", "private void AutomaticDailyCheckInToggle_Click");
        Assert.DoesNotContain("Distinct", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("HashSet", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", manager, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("merge", manager, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Choose a saved HoYoLAB sign-in", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("No account is selected automatically", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("No saved accounts", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("Select an account first", manager, StringComparison.Ordinal);
        Assert.Contains("await dialog.ShowAsync()", manager, StringComparison.Ordinal);
        Assert.Contains("var regionSlotId = pendingRegionSlotId", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskCompletionSource", manager, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(slots", manager, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(labelBox", manager, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(button, accessibleName)", manager, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetLiveSetting(managerStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Hoyo_account_manager_is_available_without_connection_and_missing_active_slot_opens_from_connection()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var redesigned = Slice(page, "private async void AccountConnectionButton_Click", "private async void ChangePublisherAccountButton_Click");
        var managerHandler = Slice(page, "private async void ChangePublisherAccountButton_Click", "private async Task ShowHoyoLabAccountManagerAsync");

        Assert.Contains("hoyoAccounts.Available", redesigned, StringComparison.Ordinal);
        Assert.Contains("hoyoAccounts.ActiveSlotId is null", redesigned, StringComparison.Ordinal);
        Assert.Contains("await ShowHoyoLabAccountManagerAsync(selected.Id)", redesigned, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.HoyoLabAccounts.Available", managerHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("connection == PublisherConnectionState.Connected", managerHandler, StringComparison.Ordinal);
        Assert.Contains("selectedSlotId = null", page, StringComparison.Ordinal);
        Assert.Contains("clearSelection: true", page, StringComparison.Ordinal);
        Assert.Contains("slots.SelectedIndex = -1", page, StringComparison.Ordinal);
        Assert.Contains("selected!.Slot.Id", page, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.HoyoLabAccounts.ActiveSlotId", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Select the active account first.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Hoyo_account_switch_refreshes_the_bound_slot_and_resets_every_hoyo_game_cadence()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var manager = Slice(
            page,
            "private async Task ShowHoyoLabAccountManagerAsync",
            "private void AutomaticDailyCheckInToggle_Click");

        Assert.Contains("async Task RefreshBoundSlotAfterChangeAsync(", manager, StringComparison.Ordinal);
        Assert.Contains("publisherResourceAutomaticAttempts.Remove(\"gi\")", manager, StringComparison.Ordinal);
        Assert.Contains("publisherResourceAutomaticAttempts.Remove(\"hsr\")", manager, StringComparison.Ordinal);
        Assert.Contains("publisherResourceAutomaticAttempts.Remove(\"zzz\")", manager, StringComparison.Ordinal);
        Assert.Contains("rolePicker: null", manager, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(manager, "await RefreshBoundSlotAfterChangeAsync\\(connectionState, cancellationToken\\)").Count);
        Assert.Contains("publisherResourceAutomaticAttempts[gameId] = AccountDisplayClock()", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_hoyo_state_is_status_only_and_the_resource_button_owns_manual_refresh()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var connectionClick = Slice(
            page,
            "private async void AccountConnectionButton_Click",
            "private async void ChangePublisherAccountButton_Click");
        var accountRender = Slice(
            page,
            "private void SyncRedesignedControls",
            "private void RenderHoyoLabAccountIdentity");

        Assert.DoesNotContain("RefreshPublisherResourceAsync", connectionClick, StringComparison.Ordinal);
        Assert.Contains(
            "connection is not (PublisherConnectionState.Connecting or PublisherConnectionState.Connected)",
            accountRender,
            StringComparison.Ordinal);
        Assert.Contains("? $\"{entry.Provider} connected\"", accountRender, StringComparison.Ordinal);
        Assert.Contains("AccountConnectionButton.Content = enabled ? \"Stop\" : \"Start\"", accountRender, StringComparison.Ordinal);
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return text[start..end];
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(Path.Combine(
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
