namespace Nyx.Desktop.Tests.UI;

public sealed class HoyoMaintenanceUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_owns_and_disposes_one_advisory_publisher_status_source()
    {
        var app = ReadAppFile("App.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("new HoyoPublisherStatusSource", app, StringComparison.Ordinal);
        Assert.Contains("GenshinSession.Version", app, StringComparison.Ordinal);
        Assert.Contains("HoyoSessions[\"hsr\"].Version", app, StringComparison.Ordinal);
        Assert.Contains("HoyoSessions[\"zzz\"].Version", app, StringComparison.Ordinal);
        Assert.Contains("DisposePublisherStatusAsync", app, StringComparison.Ordinal);
        Assert.Contains("await sessionRefresh.RefreshNowAsync", page, StringComparison.Ordinal);
        Assert.Contains("publisherStatus.Start();", page, StringComparison.Ordinal);
        Assert.Contains("publisherStatus.Updated += PublisherStatus_Updated", page, StringComparison.Ordinal);
        Assert.Contains("publisherStatus.Updated -= PublisherStatus_Updated", page, StringComparison.Ordinal);
    }

    [Fact]
    public void UI_projects_independent_update_and_predownload_labels()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        Assert.Contains("GameRailSignalProjector.Project", page, StringComparison.Ordinal);
        Assert.Contains("PrimaryGameStatusProjector.Project", page, StringComparison.Ordinal);
        var projector = ReadAppFile(Path.Combine("ViewModels", "PrimaryGameStatusProjector.cs"));
        Assert.Contains("Update available · use Official Launcher", projector, StringComparison.Ordinal);
        Assert.Contains("Pre-download available · use Official Launcher", projector, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLaunchControls(false, \"UPDATE", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_and_zzz_open_only_the_visible_sealed_hoyoplay_handoff()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var executor = ReadInfrastructureFile("Hoyo", "HoyoPlayHandoffExecutor.cs");

        Assert.Contains("selected.Id is not (\"gi\" or \"hsr\" or \"zzz\")", page, StringComparison.Ordinal);
        Assert.Contains("hoyoPlayExecutor.OpenOrObserveCurrentAsync", page, StringComparison.Ordinal);
        Assert.Contains("OpenUpdaterButton.Visibility = Visibility.Visible", page, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("launchService.OpenUpdater", page, StringComparison.Ordinal);
        Assert.Contains("foreach (var argument in request.Arguments)", executor, StringComparison.Ordinal);
        Assert.Contains("OfficialLauncherFamilyAdmission", executor, StringComparison.Ordinal);
        var observeMethod = executor.IndexOf(
            "OpenOrObserveCurrentAsync",
            StringComparison.Ordinal);
        var synchronousAdmission = executor.IndexOf(
            "var admission = familyAdmission.TryEnter();",
            observeMethod,
            StringComparison.Ordinal);
        var backgroundWork = executor.IndexOf(
            "return await Task.Run(",
            synchronousAdmission,
            StringComparison.Ordinal);
        Assert.True(observeMethod >= 0);
        Assert.True(synchronousAdmission > observeMethod);
        Assert.True(backgroundWork > synchronousAdmission);
        Assert.Contains(".EnterAsync(cancellationToken)", executor, StringComparison.Ordinal);
        Assert.Contains("CheckStrict(\"launcher\"", executor, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HoyoPlayOpenStatus.Running or HoyoPlayOpenStatus.Opened or HoyoPlayOpenStatus.Busy",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WindowStyle", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNoWindow", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("Verb = \"runas\"", executor, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName) => File.ReadAllText(Path.Combine(
        WorkspaceRoot,
        "Desktop",
        "src",
        "Nyx.Desktop.App",
        fileName));

    private static string ReadInfrastructureFile(params string[] segments) => File.ReadAllText(Path.Combine(
        [WorkspaceRoot, "Desktop", "src", "Nyx.Desktop.Infrastructure", .. segments]));

    private static string ReadCoreFile(params string[] segments) => File.ReadAllText(Path.Combine(
        [WorkspaceRoot, "Desktop", "src", "Nyx.Desktop.Core", .. segments]));

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
