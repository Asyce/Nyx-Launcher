namespace Nyx.Desktop.Tests.UI;

public sealed class Genshin120FpsUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void App_wires_fixed_packaged_helper_saved_preference_and_bounded_wait()
    {
        var app = Read("Desktop", "src", "Nyx.Desktop.App", "App.xaml.cs");

        Assert.Contains("Genshin120HelperPackageIdentity.FileName", app, StringComparison.Ordinal);
        Assert.Contains("Genshin120HelperPackageIdentity.Sha256", app, StringComparison.Ordinal);
        Assert.Contains("new Genshin120FpsProcessStarter", app, StringComparison.Ordinal);
        Assert.Contains("() => Genshin120FpsOnLaunch", app, StringComparison.Ordinal);
        Assert.DoesNotContain("launchDispatchTimeout:", app, StringComparison.Ordinal);
        var adapter = Read("Desktop", "src", "Nyx.Desktop.Infrastructure", "Sessions", "GenshinGameSessionAdapter.cs");
        Assert.Contains("LaunchDispatchTimeout => Timeout.InfiniteTimeSpan", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_120_fps_outcomes_have_selection_aware_safe_messages()
    {
        var page = Read("Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs");

        Assert.Contains("current.Id == gameId", page, StringComparison.Ordinal);
        Assert.Contains("Genshin started with 120 FPS.", page, StringComparison.Ordinal);
        Assert.Contains("Genshin started, but 120 FPS could not be enabled.", page, StringComparison.Ordinal);
        Assert.Contains("Genshin launch was handed off, but the final 120 FPS result was not received.", page, StringComparison.Ordinal);
        Assert.Contains("The verified 120 FPS helper is unavailable. Genshin Impact was not started.", page, StringComparison.Ordinal);
        Assert.Contains("The 120 FPS helper could not safely start Genshin Impact.", page, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([WorkspaceRoot, .. path]));

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The Nyx workspace root was not found.");
    }
}
