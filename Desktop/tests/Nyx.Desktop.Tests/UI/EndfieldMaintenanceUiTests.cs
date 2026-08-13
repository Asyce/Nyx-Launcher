namespace Nyx.Desktop.Tests.UI;

public sealed class EndfieldMaintenanceUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_owns_one_saved_root_service_and_startup_check_is_an_independent_lane()
    {
        var app = ReadAppFile("App.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("EndfieldMaintenance = new EndfieldOfficialMaintenanceService(EndfieldRootStore)", app, StringComparison.Ordinal);
        Assert.Contains("var endfieldCheck = endfieldMaintenanceScanFinished", page, StringComparison.Ordinal);
        Assert.Contains("RefreshEndfieldMaintenanceAsync(lease)", page, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenOrObserveCurrentAsync", Slice(
            page,
            "private async Task RefreshEndfieldMaintenanceAsync",
            "private void App_WindowReactivated"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_explicit_Endfield_click_opens_and_repeated_click_is_suppressed()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var click = Slice(
            page,
            "private async void OpenUpdaterButton_Click",
            "private async Task OpenWuWaMaintenanceAsync");

        Assert.Contains("selected.Id == \"ae\"", click, StringComparison.Ordinal);
        Assert.Contains("await OpenEndfieldMaintenanceAsync(lease)", click, StringComparison.Ordinal);
        Assert.Contains("if (endfieldMaintenanceActionInFlight", click, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceActionInFlight = true", click, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenance.OpenOrObserveCurrentAsync", click, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceActionInFlight = false", click, StringComparison.Ordinal);
        Assert.Contains("BoundedMaintenanceObservation.ObserveAsync", click, StringComparison.Ordinal);
        Assert.Contains("EndfieldLaunchObservationCount", click, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_folder_picker_validates_before_saving_and_maintenance_remains_separate()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var folder = Slice(
            page,
            "private async Task ChooseGameFolderAsync",
            "private async void OpenUpdaterButton_Click");
        var maintenance = Slice(
            page,
            "private async Task OpenEndfieldMaintenanceAsync",
            "private async Task OpenWuWaMaintenanceAsync");

        Assert.Contains("IsValidManualInstallRoot", folder, StringComparison.Ordinal);
        Assert.Contains("ManualInstallRoots", folder, StringComparison.Ordinal);
        Assert.Contains("sessionRefresh.RefreshNowAsync", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenUpdaterButton.IsEnabled = false", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLaunchControls", folder, StringComparison.Ordinal);
        Assert.Contains("endfieldFolderActionInFlight", maintenance, StringComparison.Ordinal);
        Assert.Contains("EndfieldUiActionKind.OpenMaintenance", maintenance, StringComparison.Ordinal);
        Assert.Contains("OpenUpdaterButton.IsEnabled = false", maintenance, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenance.OpenOrObserveCurrentAsync", maintenance, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_race_and_page_unload_cannot_publish_stale_ready_state()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var activation = Slice(
            page,
            "private void App_WindowReactivated",
            "private async void LaunchButton_Click");
        var open = Slice(
            page,
            "private async Task OpenEndfieldMaintenanceAsync",
            "private async Task OpenWuWaMaintenanceAsync");

        Assert.Contains("if (!endfieldMaintenanceActionInFlight)", activation, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceGeneration.Next()", open, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceGeneration.TryApply(generation", open, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceGeneration.IsCurrent(generation)", open, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceGeneration.Next();", Slice(
            page,
            "private void MainPage_Unloaded",
            "private HoyoMaintenanceUiSnapshot DiscoverHoyoMaintenance"), StringComparison.Ordinal);
        Assert.Contains("lease.CancellationToken", open, StringComparison.Ordinal);
        Assert.Contains("GameLauncherItem { Id: \"ae\" }", open, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_launch_HoYo_WuWa_and_Endfield_admissions_stay_separate()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var direct = Slice(
            page,
            "private async void LaunchButton_Click",
            "private async Task ChooseGameFolderAsync");
        var endfield = Slice(
            page,
            "private async Task OpenEndfieldMaintenanceAsync",
            "private async Task OpenWuWaMaintenanceAsync");

        Assert.Contains("sessions.RequestLaunchAsync(gameId", direct, StringComparison.Ordinal);
        Assert.DoesNotContain("Maintenance", direct, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenance.OpenOrObserveCurrentAsync", endfield, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions.RequestLaunchAsync", endfield, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyoPlayExecutor", endfield, StringComparison.Ordinal);
        Assert.DoesNotContain("wuwaMaintenance", endfield, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_wording_assigns_all_maintenance_work_to_Gryphlink()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(page, "private void RenderEndfield", "private void RenderWuWa");

        Assert.Contains("Official Launcher", render, StringComparison.Ordinal);
        Assert.Contains("updates, pre-downloads, verification and repairs", render, StringComparison.Ordinal);
        Assert.Contains("EndfieldOfficialMaintenanceStatus.NotFound", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Nyx updates", render, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("headless", render, StringComparison.OrdinalIgnoreCase);
    }

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
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
