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
        ContainsNormalized(render, "PlaytimeStatsButton.IsEnabled = showPlaytime;");

        var click = Slice(page, "private async void PlaytimeStatsButton_Click", "private ContentDialog CreateEndfieldPlaytimeDialog");
        ContainsNormalized(click, "GameSelector?.SelectedItem is not GameLauncherItem { Id: \"ae\", IsCustom: false }");
    }

    [Fact]
    public void Playtime_button_opens_a_close_only_dialog_from_the_current_snapshot()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var click = Slice(page, "private async void PlaytimeStatsButton_Click", "private ContentDialog CreateEndfieldPlaytimeDialog");
        ContainsNormalized(click, "GameSelector?.SelectedItem is not GameLauncherItem { Id: \"ae\", IsCustom: false }");
        ContainsNormalized(click, "var dialog = CreateEndfieldPlaytimeDialog(endfieldPlaytime.Current);");
        ContainsNormalized(click, "await dialog.ShowAsync().AsTask(lease.CancellationToken);");
        Assert.DoesNotContain("ScanAsync", click, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", click, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressRing", click, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialogResult", click, StringComparison.Ordinal);
        Assert.DoesNotContain("endfieldPlaytimeActionInFlight", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanEndfieldPlaytimeAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("EndfieldPlaytimeScanStatus", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Playtime_dialog_discloses_tracked_gameplay_and_never_renders_private_details()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var dialog = Slice(page, "private ContentDialog CreateEndfieldPlaytimeDialog", "private static void AddPlaytimeHeading");

        ContainsNormalized(dialog, "LOCAL PLAYTIME TRACKED BY NYX");
        ContainsNormalized(
            dialog,
            "Nyx counts only complete sessions whose exact Endfield process start and end it observed during the same Nyx run. Earlier playtime is unavailable; incomplete sessions are excluded.");
        ContainsNormalized(dialog, "Tracked total");
        ContainsNormalized(dialog, "AddPlaytimeStat(panel, \"Status\", FormatEndfieldPlaytimeStatus(snapshot));");
        ContainsNormalized(dialog, "snapshot.SaveFailed");
        ContainsNormalized(dialog, "snapshot.IsRunning");
        ContainsNormalized(dialog, "snapshot.HasPendingSession");
        ContainsNormalized(dialog, "Tracking this Endfield session now.");
        ContainsNormalized(dialog, "Its start is saved.");
        ContainsNormalized(
            dialog,
            "This running Endfield session is not being counted because Nyx did not observe its start after Endfield was confirmed closed.");
        ContainsNormalized(
            dialog,
            "Nyx could not save the latest playtime update and will keep trying while it is open.");
        ContainsNormalized(dialog, "snapshot.IncompleteSessions");
        ContainsNormalized(dialog, "Incomplete tracked sessions");
        ContainsNormalized(
            dialog,
            "No complete sessions have been tracked yet. Keep Nyx open before starting Endfield and until the game closes.");
        ContainsNormalized(dialog, "if (gameplay.Sessions == 0)");

        foreach (var label in new[]
        {
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
        })
            ContainsNormalized(dialog, label);

        ContainsNormalized(dialog, "CloseButtonText = \"Close\"");
        Assert.Contains("Application.Current.Resources", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("(FontFamily)Resources[\"NyxBodyFont\"]", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("(Brush)Resources[\"MistBrush\"]", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Folder", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProgressRing", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Warnings", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Open time", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Statistics.Launcher", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryButtonText", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryButtonText", dialog, StringComparison.Ordinal);

        Assert.DoesNotMatch(
            new Regex(@"(?:FileName|Exception|Exception\.Message|selectedRoot|snapshot\.(?:Path|File|Log))", RegexOptions.CultureInvariant),
            dialog);
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
