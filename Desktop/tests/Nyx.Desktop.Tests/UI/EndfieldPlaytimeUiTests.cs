using System.Text.RegularExpressions;

namespace Nyx.Desktop.Tests.UI;

public sealed class EndfieldPlaytimeUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_composes_playtime_from_root_state_and_disposes_it_before_session_refresh()
    {
        var app = ReadAppFile("App.xaml.cs");
        var composition = Slice(app, "_sessionRefresh = new GameSessionRefreshPump", "_launcherBanners = new");

        ContainsNormalized(composition, "_sessionRefresh = new GameSessionRefreshPump(_sessions);");
        ContainsNormalized(composition, "_endfieldPlaytime = new EndfieldPlaytimeService(");
        ContainsNormalized(composition, "LauncherState.Snapshot.EndfieldPlaytime");
        ContainsNormalized(composition, "playtime => LauncherState.TryUpdate(state => state with { EndfieldPlaytime = playtime })");
        ContainsNormalized(composition, "_sessionRefresh);");
        Assert.True(
            composition.IndexOf("_sessionRefresh = new GameSessionRefreshPump", StringComparison.Ordinal)
            < composition.IndexOf("_endfieldPlaytime = new EndfieldPlaytimeService", StringComparison.Ordinal));

        var closing = Slice(app, "private void AppWindow_Closing", "private async Task ShutDownAccountsAndCloseAsync");
        AssertBefore(closing, "_endfieldPlaytime?.Dispose();", "_sessionRefresh?.Stop();");

        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "internal void StartStableUpdate");
        AssertBefore(shutdown, "_endfieldPlaytime?.Dispose();", "await DisposeRefreshAsync(_sessionRefresh);");

        var closed = Slice(app, "private void Window_Closed", "private async Task RefreshAfterActivationAsync");
        AssertBefore(closed, "_endfieldPlaytime?.Dispose();", "_sessionRefresh?.Stop();");
    }

    [Fact]
    public void Playtime_button_and_heading_are_visible_only_for_the_real_Endfield_entry()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var button = Slice(xaml, "x:Name=\"PlaytimeStatsButton\"", "</Button>");
        ContainsNormalized(button, "x:Name=\"PlaytimeStatsButton\"");
        ContainsNormalized(button, "AutomationProperties.Name=\"Open Endfield playtime statistics\"");
        ContainsNormalized(button, "Click=\"PlaytimeStatsButton_Click\"");

        var page = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(page, "private void RenderExportTools", "private static string FormatExportStatus");
        ContainsNormalized(render, "var showPlaytime = !selected.IsCustom && selected.Id == \"ae\";");
        ContainsNormalized(render, "StableExportHeading.Text = showPlaytime ? \"EXPORT & STATS\" : \"EXPORT\";");
        ContainsNormalized(render, "PlaytimeStatsButton.Visibility = showPlaytime ? Visibility.Visible : Visibility.Collapsed;");
        ContainsNormalized(render, "PlaytimeStatsButton.IsEnabled = showPlaytime && !endfieldPlaytimeActionInFlight;");

        var click = Slice(page, "private async void PlaytimeStatsButton_Click", "private async Task ScanEndfieldPlaytimeAsync");
        ContainsNormalized(click, "GameSelector?.SelectedItem is not GameLauncherItem { Id: \"ae\", IsCustom: false }");
    }

    [Fact]
    public void Playtime_scan_uses_native_picker_dialog_progress_and_explicit_status_priority()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var click = Slice(page, "private async void PlaytimeStatsButton_Click", "private async Task ScanEndfieldPlaytimeAsync");
        ContainsNormalized(click, "if (endfieldPlaytime.Current.ScanStatus is EndfieldPlaytimeScanStatus.NotScanned)");
        ContainsNormalized(click, "new FolderPicker");
        ContainsNormalized(click, "WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle)");
        ContainsNormalized(click, "picker.PickSingleFolderAsync()");

        var scan = Slice(page, "private async Task ScanEndfieldPlaytimeAsync", "private ContentDialog CreateEndfieldPlaytimeDialog");
        ContainsNormalized(scan, "new ProgressRing");
        ContainsNormalized(scan, "IsActive = true");
        ContainsNormalized(scan, "AutomationProperties.SetLiveSetting");
        ContainsNormalized(scan, "AutomationLiveSetting.Polite");
        ContainsNormalized(scan, "new ContentDialog");
        ContainsNormalized(scan, "await endfieldPlaytime.ScanAsync(selectedRoot, cancellationToken)");

        var status = Slice(page, "private static string EndfieldPlaytimeStatusText", "private static void AddPlaytimeHeading");
        foreach (var state in new[] { "Normal", "Empty", "Capped", "Corrupt" })
            ContainsNormalized(status, $"EndfieldPlaytimeScanStatus.{state}");
        ContainsNormalized(status, "Choose Scan again to read local Endfield history.");
        ContainsNormalized(status, "Endfield is running. The unfinished session is saved but excluded from every total below.");
        ContainsNormalized(status, "Endfield is running. Nyx saw its verified start but could not save it yet, so this session is excluded from every total below.");
        ContainsNormalized(status, "Endfield is running, but Nyx did not see a verified start. This session is excluded from every total below.");
        ContainsNormalized(status, "A previous unfinished session is waiting for matching official history and remains excluded.");
        ContainsNormalized(status, "Scanning local Endfield logs within Nyx's safety limits.");
        Assert.True(
            status.IndexOf("snapshot.IsRunning", StringComparison.Ordinal)
            < status.IndexOf("snapshot.IsScanning", StringComparison.Ordinal));
        Assert.True(
            status.IndexOf("snapshot.IsScanning", StringComparison.Ordinal)
            < status.IndexOf("snapshot.ScanStatus", StringComparison.Ordinal));
    }

    [Fact]
    public void Playtime_dialog_has_all_stats_and_never_renders_private_log_details()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var dialog = Slice(page, "private ContentDialog CreateEndfieldPlaytimeDialog", "private static string EndfieldPlaytimeStatusText");

        foreach (var label in new[]
        {
            "GAMEPLAY",
            "Verified total",
            "Sessions",
            "Active days",
            "Average session",
            "Average active day",
            "Shortest / longest",
            "Longest streak",
            "Session lengths",
            "Night play (22:00–06:00)",
            "Launch hours",
            "Time by weekday",
            "Time by month",
            "OFFICIAL LAUNCHER ACTIVITY",
            "Open time",
            "Visits",
            "Visits that launched the game",
            "Launcher-only visits",
            "Skipped safely",
        })
            ContainsNormalized(dialog, label);

        ContainsNormalized(dialog, "PrimaryButtonText = \"Scan again\"");
        ContainsNormalized(dialog, "SecondaryButtonText = \"Choose folder\"");
        ContainsNormalized(dialog, "CloseButtonText = \"Close\"");
        ContainsNormalized(dialog, "AutomationProperties.SetLiveSetting");
        ContainsNormalized(dialog, "AutomationLiveSetting.Polite");

        var status = Slice(page, "private static string EndfieldPlaytimeStatusText", "private static void AddPlaytimeHeading");
        foreach (var display in new[] { dialog, status })
        {
            Assert.DoesNotMatch(
                new Regex(@"(?:FileName|Exception|Exception\.Message|selectedRoot|snapshot\.(?:Path|File|Log))", RegexOptions.CultureInvariant),
                display);
        }

        var scan = Slice(page, "private async Task ScanEndfieldPlaytimeAsync", "private ContentDialog CreateEndfieldPlaytimeDialog");
        foreach (var playtimeDialog in new[] { scan, dialog })
        {
            Assert.Contains("Application.Current.Resources", playtimeDialog, StringComparison.Ordinal);
            Assert.DoesNotContain("(FontFamily)Resources[\"NyxBodyFont\"]", playtimeDialog, StringComparison.Ordinal);
            Assert.DoesNotContain("(Brush)Resources[\"MistBrush\"]", playtimeDialog, StringComparison.Ordinal);
        }
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
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
