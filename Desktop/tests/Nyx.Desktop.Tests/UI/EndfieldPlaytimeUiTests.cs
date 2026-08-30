using System.Text.RegularExpressions;

namespace Nyx.Desktop.Tests.UI;

public sealed class EndfieldPlaytimeUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_composes_shared_playtime_and_unregisters_power_callbacks_before_teardown()
    {
        var app = ReadLauncherFile("App.xaml.cs");
        var composition = Slice(app, "_sessionRefresh = new GameSessionRefreshPump", "_launcherBanners = new");

        ContainsNormalized(composition, "_sessionRefresh = new GameSessionRefreshPump(_sessions);");
        ContainsNormalized(composition, "_gamePlaytime = new GamePlaytimeService(");
        ContainsNormalized(composition, "LauncherState.Snapshot.PlaytimeSecondsByGame");
        ContainsNormalized(composition, "playtime => LauncherState.TryUpdate(state => state with { PlaytimeSecondsByGame = playtime })");
        ContainsNormalized(composition, "_sessionRefresh);");

        var closing = Slice(app, "private void AppWindow_Closing", "private async Task ShutDownAccountsAndCloseAsync");
        AssertBefore(closing, "UnregisterSuspendResumeNotifications();", "_sessionRefresh?.Stop();");

        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "internal void StartStableUpdate");
        AssertBefore(shutdown, "await UnregisterSuspendResumeNotifications();", "_gamePlaytime?.Dispose();");
        AssertBefore(shutdown, "_gamePlaytime?.Dispose();", "await DisposeRefreshAsync(_sessionRefresh);");

        var closed = Slice(app, "private void Window_Closed", "private async Task RefreshAfterActivationAsync");
        AssertBefore(closed, "UnregisterSuspendResumeNotifications().GetAwaiter().GetResult();", "_gamePlaytime?.Dispose();");
        AssertBefore(closed, "_gamePlaytime?.Dispose();", "_sessionRefresh?.Stop();");
    }

    [Fact]
    public void Playtime_text_is_outlined_above_account_resources_for_every_game()
    {
        var xaml = ReadLauncherFile("MainPage.xaml");
        var display = xaml.IndexOf("x:Name=\"LaunchPlayTimeDisplay\"", StringComparison.Ordinal);
        var resources = xaml.IndexOf("x:Name=\"LaunchResourceMetricsPanel\"", StringComparison.Ordinal);
        var launchButton = xaml.IndexOf("x:Name=\"LaunchButton\"", StringComparison.Ordinal);
        var utilityButtons = xaml.IndexOf("x:Name=\"LaunchUtilityButtons\"", StringComparison.Ordinal);

        Assert.True(display >= 0 && display < resources);
        Assert.True(resources < launchButton && launchButton < utilityButtons);

        var playtimeDisplay = Slice(xaml, "x:Name=\"LaunchPlayTimeDisplay\"", "x:Name=\"LaunchResourceMetricsPanel\"");
        ContainsNormalized(playtimeDisplay, "Grid.Row=\"2\"");
        var outline = Slice(playtimeDisplay, "x:Name=\"LaunchPlayTimeOutlineText\"", "/>");
        var text = Slice(xaml, "x:Name=\"LaunchPlayTimeText\"", "/>");
        ContainsNormalized(outline, "AutomationProperties.AccessibilityView=\"Raw\"");
        ContainsNormalized(outline, "Foreground=\"Black\"");
        ContainsNormalized(outline, "Text=\"Play Time: 0m\"");
        ContainsNormalized(text, "Text=\"Play Time: 0m\"");
        Assert.DoesNotContain("Visibility=", text, StringComparison.Ordinal);
        ContainsNormalized(text, "AutomationProperties.Name=\"Play Time: 0m.");
        ContainsNormalized(text, "ToolTipService.ToolTip=\"Counted only after Nyx launched this game on this PC while Nyx remained open; earlier, outside-Nyx, and other-device time is excluded.\"");

        var render = ReadLauncherFile("MainPage.xaml.cs");
        ContainsNormalized(render, "LaunchPlayTimeOutlineText.Text = LaunchPlayTimeText.Text = unavailable;");
        ContainsNormalized(render, "LaunchPlayTimeOutlineText.Text = LaunchPlayTimeText.Text = value;");
    }

    [Fact]
    public void Playtime_is_rendered_from_the_shared_service_on_selection_and_session_refresh()
    {
        var page = ReadLauncherFile("MainPage.xaml.cs");
        ContainsNormalized(page, "private readonly GamePlaytimeService gamePlaytime;");
        ContainsNormalized(page, "gamePlaytime = app.GamePlaytime;");

        var selection = Slice(page, "private void RenderSelection()", "private void RenderGamePlaytime");
        ContainsNormalized(selection, "RenderGamePlaytime(selected);");
        Assert.True(
            selection.IndexOf("RenderGamePlaytime(selected);", StringComparison.Ordinal)
            < selection.IndexOf("if (selected.IsCustom)", StringComparison.Ordinal));

        var refresh = Slice(page, "private void SessionRefresh_Refreshed", "private void LauncherBanners_Updated");
        ContainsNormalized(refresh, "RenderSelection();");
    }

    [Fact]
    public void Native_suspend_resume_registration_is_rooted_guarded_and_fail_closed()
    {
        var app = ReadLauncherFile("App.xaml.cs");
        var startup = Slice(app, "_launchStage = \"main-window-activation\"", "_launcherBanners.Start();");
        AssertBefore(startup, "_window.Activate();", "TryRegisterSuspendResumeNotifications()");
        AssertBefore(startup, "TryRegisterSuspendResumeNotifications()", "_sessionRefresh.Start();");
        ContainsNormalized(startup, "_gamePlaytime.DisableTracking();");

        ContainsNormalized(app, "PowerRegisterSuspendResumeNotification(");
        ContainsNormalized(app, "PowerUnregisterSuspendResumeNotification(");
        ContainsNormalized(app, "private DeviceNotifyCallbackRoutine? _powerCallback;");
        ContainsNormalized(app, "_powerCallback = SuspendResumeNotification;");
        ContainsNormalized(app, "GameSessionRefreshPump.ClassifyPowerBroadcast(eventType)");
        ContainsNormalized(app, "dispatcher?.TryEnqueue(");
        ContainsNormalized(app, "if (!_accountShutdownStarted");
        ContainsNormalized(app, "_powerCallbacksInFlight++;");
        ContainsNormalized(app, "await UnregisterSuspendResumeNotifications();");

        var activation = Slice(
            app,
            "private async Task RefreshAfterActivationAsync()",
            "private static async Task DisposeRefreshAsync");
        ContainsNormalized(activation, "await SessionRefresh.RefreshNowAsync();");
        Assert.DoesNotContain("RequestSystemResume", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetAfterResume", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_adapter_changes_use_one_reserved_state_then_adapter_commit()
    {
        var page = ReadLauncherFile("MainPage.xaml.cs");
        var transaction = Slice(
            page,
            "private async Task<LauncherStateUpdateFailure> CommitCustomSessionMutationAsync",
            "private static void ApplyNyxAccentResources");
        ContainsNormalized(transaction, "previous == current");
        ContainsNormalized(transaction, "previous == game");
        ContainsNormalized(transaction, "mutations[removedId] = null;");
        ContainsNormalized(transaction, "if (!targetById.ContainsKey(gameId))");
        ContainsNormalized(transaction, "gamePlaytime.ForgetRemovedGame(gameId);");
        AssertBefore(transaction, "TryAcquireExclusivePublicationAsync", "TryReserveCustomAdapterMutations");
        AssertBefore(transaction, "var failure = commitState();", "gamePlaytime.CloseRuntime(gameId);");
        AssertBefore(transaction, "var failure = commitState();", "gamePlaytime.ForgetRemovedGame(gameId);");
        AssertBefore(transaction, "gamePlaytime.CloseRuntime(gameId);", "reservation.Commit();");
        AssertBefore(transaction, "gamePlaytime.ForgetRemovedGame(gameId);", "reservation.Commit();");
        Assert.DoesNotContain("TryRemoveCustomAdapter", page, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRegisterCustomAdapter", page, StringComparison.Ordinal);
        Assert.True(
            page.Split("CommitCustomSessionMutationAsync(", StringSplitOptions.None).Length - 1 >= 6,
            "Edit, delete, add, reset, restore, and the shared helper must use the reservation path.");
    }

    [Fact]
    public void Playtime_uses_whole_minute_formats_and_an_honest_save_status_disclosure()
    {
        var page = ReadLauncherFile("MainPage.xaml.cs");
        var render = Slice(page, "private void RenderGamePlaytime", "private void ApplySavedPanelVisibility");

        ContainsNormalized(render, "var snapshot = gamePlaytime.Current(selected.Id);");
        ContainsNormalized(render, "Play Time: tracking unavailable");
        ContainsNormalized(render, "var totalMinutes = Math.Max(0L, snapshot.TotalSeconds / 60);");
        ContainsNormalized(render, "Play Time: {totalMinutes}m");
        ContainsNormalized(render, "Play Time: {totalMinutes / 60}h {totalMinutes % 60}m");
        Assert.DoesNotContain("snapshot.Total.TotalMinutes", render, StringComparison.Ordinal);
        ContainsNormalized(render, "if (snapshot.SaveFailed) value += \" · save pending\";");
        ContainsNormalized(render, "AutomationProperties.SetName(");
        ContainsNormalized(render, "ToolTipService.SetToolTip(");

        const string disclosure =
            "Counted only after Nyx launched this game on this PC while Nyx remained open; earlier, outside-Nyx, and other-device time is excluded.";
        ContainsNormalized(render, disclosure);
        ContainsNormalized(ReadLauncherFile("MainPage.xaml"), disclosure);
    }

    [Fact]
    public void Endfield_only_playtime_control_and_private_scan_ui_are_removed()
    {
        var page = ReadLauncherFile("MainPage.xaml.cs");
        var xaml = ReadLauncherFile("MainPage.xaml");

        foreach (var forbidden in new[]
        {
            "PlaytimeStatsButton",
            "PlaytimeStatsButton_Click",
            "CreateEndfieldPlaytimeDialog",
            "EndfieldPlaytimeService",
            "endfieldPlaytime",
            "ScanEndfieldPlaytimeAsync",
            "EndfieldPlaytimeScanStatus",
            "EXPORT & STATS",
        })
        {
            Assert.DoesNotContain(forbidden, page, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, xaml, StringComparison.Ordinal);
        }

        var render = Slice(page, "private void RenderGamePlaytime", "private void ApplySavedPanelVisibility");
        foreach (var forbidden in new[] { "Scan", "Folder", "Log", "FileName", "Exception", "Path" })
            Assert.DoesNotContain(forbidden, render, StringComparison.OrdinalIgnoreCase);
    }

    private static void ContainsNormalized(string source, string expected)
    {
        Assert.Contains(Normalize(expected), Normalize(source), StringComparison.Ordinal);
    }

    private static void AssertBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex);
    }

    private static string Normalize(string source) => Regex.Replace(source, @"\s+", " ").Trim();

    private static string Slice(string source, string startValue, string endValue)
    {
        var start = source.IndexOf(startValue, StringComparison.Ordinal);
        var end = source.IndexOf(endValue, start + startValue.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string ReadLauncherFile(string fileName) => File.ReadAllText(Path.Combine(
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
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
