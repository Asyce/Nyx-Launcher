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
    public void Pre_install_notice_is_focusable_transition_only_and_uses_static_highlight()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var xaml = ReadAppFile("MainPage.xaml");
        var renderStart = page.IndexOf("private void RenderPreInstallNotice", StringComparison.Ordinal);
        var renderEnd = page.IndexOf("private void RenderHoyoLabAccountIdentity", renderStart, StringComparison.Ordinal);
        var render = page[renderStart..renderEnd];

        Assert.Contains("Pre-install available — open Official Launcher", render, StringComparison.Ordinal);
        Assert.Contains("Update and pre-install available — open Official Launcher", render, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(preInstallNoticeKey, key", render, StringComparison.Ordinal);
        Assert.Contains("PreInstallNoticeBrush", render, StringComparison.Ordinal);
        Assert.Contains("PreInstallSurfaceBrush", render, StringComparison.Ordinal);
        Assert.Contains("new Thickness(available ? 2 : 1)", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", render, StringComparison.Ordinal);
        var noticeEnabled = render.IndexOf(
            "PreInstallNoticeButton.IsEnabled = available && StableOpenUpdaterButton.IsEnabled",
            StringComparison.Ordinal);
        var transitionKey = render.IndexOf("var key = message is null ? null", StringComparison.Ordinal);
        var transitionReturn = render.IndexOf("return;", transitionKey, StringComparison.Ordinal);
        Assert.True(noticeEnabled >= 0);
        Assert.True(transitionKey > noticeEnabled);
        Assert.True(transitionReturn > transitionKey);

        var hoyoAdmission = page.IndexOf("if (updaterActionInFlight", StringComparison.Ordinal);
        var hoyoNoticeDisable = page.IndexOf(
            "PreInstallNoticeButton.IsEnabled = false",
            hoyoAdmission,
            StringComparison.Ordinal);
        var hoyoActionStart = page.IndexOf("updaterActionInFlight = true", hoyoAdmission, StringComparison.Ordinal);
        Assert.True(hoyoNoticeDisable > hoyoAdmission);
        Assert.True(hoyoActionStart > hoyoNoticeDisable);
        Assert.Contains("updaterActionInFlight = false", page, StringComparison.Ordinal);
        Assert.Contains("RenderSelection();", page, StringComparison.Ordinal);

        var wuwaAdmission = page.IndexOf("if (wuwaActionInFlight", StringComparison.Ordinal);
        var wuwaNoticeDisable = page.IndexOf(
            "PreInstallNoticeButton.IsEnabled = false",
            wuwaAdmission,
            StringComparison.Ordinal);
        var wuwaActionStart = page.IndexOf("wuwaActionInFlight = true", wuwaAdmission, StringComparison.Ordinal);
        Assert.True(wuwaNoticeDisable > wuwaAdmission);
        Assert.True(wuwaActionStart > wuwaNoticeDisable);
        Assert.Contains("wuwaActionInFlight = false", page, StringComparison.Ordinal);

        var launchStart = xaml.IndexOf("x:Name=\"LaunchStack\"", StringComparison.Ordinal);
        var launchEnd = xaml.IndexOf("x:Name=\"StableOpenScreenshotFolderButton\"", launchStart, StringComparison.Ordinal);
        var launch = xaml[launchStart..launchEnd];
        Assert.Contains("x:Name=\"PreInstallNoticeButton\"", launch, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", launch, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenUpdaterButton_Click\"", launch, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", launch, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"Bold\"", launch, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource PreInstallNoticeBrush}\"", launch, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", launch, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"2\"", launch, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"3\"", launch, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"4\"", launch, StringComparison.Ordinal);
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
