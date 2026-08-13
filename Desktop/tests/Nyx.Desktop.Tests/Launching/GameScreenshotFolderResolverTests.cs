using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Tests.Launching;

public sealed class GameScreenshotFolderResolverTests
{
    [Theory]
    [InlineData("gi", @"C:\Games\Genshin", @"C:\Games\Genshin\ScreenShot")]
    [InlineData("hsr", @"C:\Games\Star Rail", @"C:\Games\Star Rail\StarRail_Data\ScreenShots")]
    [InlineData("zzz", @"C:\Games\ZZZ", @"C:\Games\ZZZ\ScreenShot")]
    [InlineData("wuwa", @"C:\Games\WuWa", @"C:\Games\WuWa\Wuthering Waves Game\Client\Saved\ScreenShot")]
    public void Exact_supported_mapping_returns_only_an_existing_contained_folder(
        string gameId,
        string root,
        string expected)
    {
        var fileSystem = new FakeFileSystem { Existing = expected };
        var resolver = new GameScreenshotFolderResolver(id => id == gameId ? root : null, fileSystem);

        var result = resolver.Resolve(gameId);

        Assert.Equal(GameScreenshotFolderStatus.Ready, result.Status);
        Assert.Equal(expected, result.FolderPath);
    }

    [Theory]
    [InlineData("custom-user-game")]
    [InlineData("GI")]
    public void Custom_and_unknown_ids_are_unsupported_without_resolving_a_root(string gameId)
    {
        var calls = 0;
        var resolver = new GameScreenshotFolderResolver(_ => { calls++; return @"C:\Never"; }, new FakeFileSystem());

        var result = resolver.Resolve(gameId);

        Assert.Equal(GameScreenshotFolderStatus.Unsupported, result.Status);
        Assert.Null(result.FolderPath);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Endfield_uses_the_pictures_known_folder_without_resolving_an_install_root()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            Assert.Equal(GameScreenshotFolderStatus.Unavailable, new GameScreenshotFolderResolver(_ => @"C:\Never", new FakeFileSystem()).Resolve("ae").Status);
            return;
        }

        var expected = Path.Combine(pictures, "Endfield");
        var calls = 0;
        var fileSystem = new FakeFileSystem { Existing = expected };
        var resolver = new GameScreenshotFolderResolver(
            _ => { calls++; return @"C:\Never"; },
            fileSystem);

        var result = resolver.Resolve("ae");

        Assert.Equal(GameScreenshotFolderStatus.Ready, result.Status);
        Assert.Equal(expected, result.FolderPath);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Endfield_missing_or_reparse_pictures_folder_is_unavailable()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            Assert.Equal(GameScreenshotFolderStatus.Unavailable, new GameScreenshotFolderResolver(_ => @"C:\Never", new FakeFileSystem()).Resolve("ae").Status);
            return;
        }

        var expected = Path.Combine(pictures, "Endfield");
        var fileSystem = new FakeFileSystem();
        var resolver = new GameScreenshotFolderResolver(_ => @"C:\Never", fileSystem);

        Assert.Equal(GameScreenshotFolderStatus.Unavailable, resolver.Resolve("ae").Status);

        fileSystem.Existing = expected;
        fileSystem.Reparse = expected;
        Assert.Equal(GameScreenshotFolderStatus.Unavailable, resolver.Resolve("ae").Status);
    }

    [Fact]
    public void Every_request_revalidates_and_a_replaced_root_is_not_reused()
    {
        var calls = 0;
        var roots = new[] { @"C:\Games\First", @"C:\Games\Second" };
        var fileSystem = new FakeFileSystem();
        var resolver = new GameScreenshotFolderResolver(
            _ => roots[Math.Min(calls++, roots.Length - 1)],
            fileSystem);

        fileSystem.Existing = @"C:\Games\First\ScreenShot";
        Assert.Equal(@"C:\Games\First\ScreenShot", resolver.Resolve("gi").FolderPath);
        fileSystem.Existing = @"C:\Games\Second\ScreenShot";
        Assert.Equal(@"C:\Games\Second\ScreenShot", resolver.Resolve("gi").FolderPath);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Games\Title\..\Other")]
    [InlineData(@"\\server\share\Game")]
    [InlineData(@"C:\Games\Title\")]
    public void Missing_noncanonical_remote_or_changed_roots_are_unavailable_without_path_leakage(string? root)
    {
        var resolver = new GameScreenshotFolderResolver(_ => root, new FakeFileSystem());

        var result = resolver.Resolve("gi");

        Assert.Equal(GameScreenshotFolderStatus.Unavailable, result.Status);
        Assert.Null(result.FolderPath);
    }

    [Fact]
    public void Missing_or_reparse_folder_is_unavailable()
    {
        var fileSystem = new FakeFileSystem();
        var resolver = new GameScreenshotFolderResolver(_ => @"C:\Games\Genshin", fileSystem);
        Assert.Equal(GameScreenshotFolderStatus.Unavailable, resolver.Resolve("gi").Status);

        fileSystem.Existing = @"C:\Games\Genshin\ScreenShot";
        fileSystem.Reparse = fileSystem.Existing;
        var result = resolver.Resolve("gi");
        Assert.Equal(GameScreenshotFolderStatus.Unavailable, result.Status);
        Assert.Null(result.FolderPath);
    }

    [Fact]
    public void Resolver_has_no_creation_shell_launch_drive_scan_or_logging_path()
    {
        var root = FindWorkspaceRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "src",
            "Nyx.Desktop.Infrastructure",
            "Launching",
            "GameScreenshotFolderResolver.cs"));

        Assert.DoesNotContain("Directory.Create", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.Launch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DriveInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Logger", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SpecialFolder.MyPictures", source, StringComparison.Ordinal);
        Assert.Contains("[\"ae\"] = \"Endfield\"", source, StringComparison.Ordinal);
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.Infrastructure")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }

    private sealed class FakeFileSystem : IScreenshotFolderFileSystem
    {
        public string? Existing { get; set; }
        public string? Reparse { get; set; }

        public bool DirectoryExists(string path) =>
            string.Equals(path, Existing, StringComparison.OrdinalIgnoreCase);

        public bool ContainsReparsePoint(string path) =>
            string.Equals(path, Reparse, StringComparison.OrdinalIgnoreCase);
    }
}
