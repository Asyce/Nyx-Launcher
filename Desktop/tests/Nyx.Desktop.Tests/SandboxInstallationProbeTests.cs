using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Installations;
using Nyx.Desktop.Infrastructure.Installations;

namespace Nyx.Desktop.Tests;

public sealed class SandboxInstallationProbeTests
{
    [Fact]
    public void Existing_fake_install_is_found()
    {
        using var sandbox = new TemporarySandbox();
        var installPath = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Games", "Genshin")).FullName;
        var probe = new SandboxInstallationProbe(sandbox.Path);

        var result = probe.Probe("gi", installPath);

        Assert.Equal(InstallationStatus.Found, result.Status);
        Assert.Equal("gi", result.Game.Id);
        Assert.Equal(Path.GetFullPath(installPath), result.CheckedPath);
    }

    [Fact]
    public void Missing_fake_install_is_reported_without_creating_it()
    {
        using var sandbox = new TemporarySandbox();
        var installPath = Path.Combine(sandbox.Path, "Games", "NotInstalled");
        var probe = new SandboxInstallationProbe(sandbox.Path);
        var entriesBefore = Directory.GetFileSystemEntries(
            sandbox.Path,
            "*",
            SearchOption.AllDirectories);

        var result = probe.Probe("hsr", installPath);

        Assert.Equal(InstallationStatus.Missing, result.Status);
        Assert.False(Directory.Exists(installPath));
        Assert.Equal(
            entriesBefore,
            Directory.GetFileSystemEntries(sandbox.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("relative\\sandbox")]
    [InlineData("C:drive-relative")]
    [InlineData(@"\\server\share\sandbox")]
    [InlineData(@"\\?\C:\sandbox")]
    [InlineData(@"\\.\C:\sandbox")]
    public void Sandbox_root_must_be_a_fully_qualified_local_drive_path(string sandboxRoot)
    {
        Assert.Throws<ArgumentException>(() => new SandboxInstallationProbe(sandboxRoot));
    }

    [Theory]
    [InlineData("relative\\game")]
    [InlineData("C:drive-relative")]
    [InlineData(@"\\server\share\game")]
    [InlineData(@"\\?\C:\game")]
    [InlineData(@"\\.\C:\game")]
    public void Candidate_must_be_a_fully_qualified_local_drive_path(string candidatePath)
    {
        using var sandbox = new TemporarySandbox();
        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<ArgumentException>(() => probe.Probe("ae", candidatePath));
    }

    [Fact]
    public void Absolute_components_are_inspected_from_the_drive_root_not_the_current_directory()
    {
        using var sandbox = new TemporarySandbox();
        using var otherDirectory = new TemporarySandbox();
        var installPath = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Games", "Endfield")).FullName;
        var previousCurrentDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = otherDirectory.Path;
            var probe = new SandboxInstallationProbe(sandbox.Path);

            var result = probe.Probe("ae", installPath);

            Assert.Equal(Path.GetFullPath(installPath), result.CheckedPath);
            Assert.Equal(InstallationStatus.Found, result.Status);
            Assert.False(result.CheckedPath.StartsWith(otherDirectory.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;
        }
    }

    [Fact]
    public void Traversal_outside_the_sandbox_is_rejected()
    {
        using var sandbox = new TemporarySandbox();
        var outsidePath = Path.Combine(sandbox.Path, "..", "outside");
        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Probe("zzz", outsidePath));
    }

    [Fact]
    public void Sibling_with_matching_name_prefix_is_rejected()
    {
        using var sandbox = new TemporarySandbox();
        var siblingPath = sandbox.Path + "-outside";
        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Probe("wuwa", siblingPath));
    }

    [Fact]
    public void Unsupported_game_is_rejected_before_the_file_system_is_checked()
    {
        using var sandbox = new TemporarySandbox();
        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<UnsupportedGameException>(() => probe.Probe("wuthering-waves", sandbox.Path));
    }

    [Fact]
    public void Reparse_point_as_sandbox_root_is_rejected()
    {
        using var host = new TemporarySandbox();
        var targetPath = Directory.CreateDirectory(Path.Combine(host.Path, "target")).FullName;
        var linkPath = Path.Combine(host.Path, "sandbox-link");
        if (!TryCreateDirectoryLink(linkPath, targetPath))
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => new SandboxInstallationProbe(linkPath));
    }

    [Fact]
    public void Existing_reparse_component_in_candidate_is_rejected_before_it_is_followed()
    {
        using var sandbox = new TemporarySandbox();
        var targetPath = Directory.CreateDirectory(Path.Combine(sandbox.Path, "target")).FullName;
        Directory.CreateDirectory(Path.Combine(targetPath, "game"));
        var linkPath = Path.Combine(sandbox.Path, "linked-folder");
        if (!TryCreateDirectoryLink(linkPath, targetPath))
        {
            return;
        }

        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<ArgumentException>(() => probe.Probe("gi", Path.Combine(linkPath, "game")));
    }

    [Fact]
    public void Dangling_reparse_candidate_is_rejected()
    {
        using var sandbox = new TemporarySandbox();
        var missingTarget = Path.Combine(sandbox.Path, "missing-target");
        var linkPath = Path.Combine(sandbox.Path, "dangling-link");
        if (!TryCreateDirectoryLink(linkPath, missingTarget))
        {
            return;
        }

        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.Throws<ArgumentException>(() => probe.Probe("zzz", linkPath));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class TemporarySandbox : IDisposable
    {
        public TemporarySandbox()
        {
            Path = Directory.CreateDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NyxDesktopTests", Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
