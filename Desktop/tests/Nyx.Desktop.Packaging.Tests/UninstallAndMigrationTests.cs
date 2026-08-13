using System.Diagnostics;
using System.Text.Json;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class UninstallAndMigrationTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("canonical-data")]
    [InlineData("legacy-data")]
    public void Root_junction_substituted_after_audit_is_refused_for_every_uninstall_root(string rootName)
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteCompleteLayout(layout);
        var target = SelectRoot(layout, rootName);
        var moved = target + "-moved";
        var outside = Path.Combine(fixture.Root, $"outside-{rootName}");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "outside");

        _ = SafePaths.AuditTreeWithoutLinks(target);
        Directory.Move(target, moved);
        CreateJunction(target, outside);
        try
        {
            var exception = Assert.Throws<UpdateContractException>(
                () => SafePaths.DeleteTreeWithoutFollowingLinks(target));

            Assert.Equal("UnsafePath", exception.Code);
            Assert.Equal("outside", File.ReadAllText(sentinel));
            Assert.True(File.Exists(Path.Combine(moved, RootPayloadName(rootName))));
        }
        finally
        {
            DeleteJunctionIfPresent(target);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("canonical-data")]
    [InlineData("legacy-data")]
    public void Nested_junction_substitution_during_delete_fails_closed_for_every_uninstall_root(string rootName)
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteCompleteLayout(layout);
        var target = SelectRoot(layout, rootName);
        var nested = Path.Combine(target, "race-child");
        var moved = target + "-captured-child";
        var outside = Path.Combine(fixture.Root, $"outside-nested-{rootName}");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "owned.txt"), "owned");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "outside");
        var injected = false;

        try
        {
            var exception = Assert.Throws<UpdateContractException>(() =>
                SafePaths.DeleteTreeWithoutFollowingLinks(target, (checkpoint, path) =>
                {
                    if (injected || checkpoint != SafeDeleteCheckpoint.BeforeChildOpen
                        || !string.Equals(path, nested, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Directory.Move(nested, moved);
                    CreateJunction(nested, outside);
                    injected = true;
                }));

            Assert.True(injected);
            Assert.Equal("UnsafePath", exception.Code);
            Assert.Equal("outside", File.ReadAllText(sentinel));
            Assert.Equal("owned", File.ReadAllText(Path.Combine(moved, "owned.txt")));
        }
        finally
        {
            DeleteJunctionIfPresent(nested);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("canonical-data")]
    [InlineData("legacy-data")]
    public void Root_rename_attempt_is_blocked_or_fails_closed_after_delete_binds_every_uninstall_root(string rootName)
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteCompleteLayout(layout);
        var target = SelectRoot(layout, rootName);
        var moved = target + "-unexpected-move";
        var outside = Path.Combine(fixture.Root, $"outside-bound-{rootName}");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "outside");
        var renameBlocked = false;

        var deletionError = Record.Exception(() => SafePaths.DeleteTreeWithoutFollowingLinks(target, (checkpoint, path) =>
        {
            if (checkpoint != SafeDeleteCheckpoint.RootOpened
                || !string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(target, moved);
                CreateJunction(target, outside);
                throw new InvalidOperationException("The bound root was replaceable.");
            }
            catch (IOException)
            {
                renameBlocked = true;
            }
        }));

        Assert.True(renameBlocked);
        if (deletionError is not null)
        {
            var contractError = Assert.IsType<UpdateContractException>(deletionError);
            Assert.Equal("UnsafePath", contractError.Code);
        }
        else
        {
            Assert.False(Directory.Exists(target));
        }

        Assert.False(Directory.Exists(moved));
        Assert.Equal("outside", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Default_uninstall_removes_program_and_shortcut_but_preserves_user_data()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteLayout(layout);
        Directory.CreateDirectory(layout.LegacyUserDataRoot);
        File.WriteAllText(Path.Combine(layout.LegacyUserDataRoot, "legacy-state.json"), "keep-legacy");

        NyxUninstaller.Uninstall(layout, removeUserData: false);

        Assert.False(Directory.Exists(layout.InstallRoot));
        Assert.False(File.Exists(layout.StartMenuShortcut));
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(layout.UserDataRoot, "launcher-state.json")));
        Assert.Equal("keep-legacy", File.ReadAllText(Path.Combine(layout.LegacyUserDataRoot, "legacy-state.json")));
    }

    [Fact]
    public void Explicit_data_removal_deletes_only_the_fixed_canonical_and_legacy_roots()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteLayout(layout);
        Directory.CreateDirectory(layout.LegacyUserDataRoot);
        File.WriteAllText(Path.Combine(layout.LegacyUserDataRoot, "legacy-state.json"), "legacy");
        var sibling = Path.Combine(fixture.Root, "user-data-sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");

        NyxUninstaller.Uninstall(layout, removeUserData: true);

        Assert.False(Directory.Exists(layout.UserDataRoot));
        Assert.False(Directory.Exists(layout.LegacyUserDataRoot));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
    }

    [Fact]
    public void Explicit_data_removal_handles_conflicting_canonical_and_legacy_roots()
    {
        using var fixture = new PackageFixture();
        var local = Path.Combine(fixture.Root, "local");
        var roaming = Path.Combine(fixture.Root, "roaming");
        var layout = UpdateLayout.ForUserRoots(local, roaming);
        WriteLayout(layout);
        var legacy = NyxUserDataPaths.LegacyRoot(local);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "keep.txt"), "keep");

        NyxUninstaller.Uninstall(layout, removeUserData: true);

        Assert.False(Directory.Exists(layout.UserDataRoot));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Reparse_in_user_data_fails_closed_before_program_or_shortcut_is_removed()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteLayout(layout);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(layout.UserDataRoot, "linked"), outside);

        Assert.Throws<UpdateContractException>(() => NyxUninstaller.Uninstall(layout, removeUserData: true));

        Assert.True(Directory.Exists(layout.InstallRoot));
        Assert.True(File.Exists(layout.StartMenuShortcut));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Reparse_in_legacy_data_fails_closed_before_canonical_program_or_shortcut_is_removed()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteLayout(layout);
        var outside = Path.Combine(fixture.Root, "outside-legacy");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateDirectory(layout.LegacyUserDataRoot);
        Directory.CreateSymbolicLink(Path.Combine(layout.LegacyUserDataRoot, "linked"), outside);

        Assert.Throws<UpdateContractException>(() => NyxUninstaller.Uninstall(layout, removeUserData: true));

        Assert.True(Directory.Exists(layout.InstallRoot));
        Assert.True(File.Exists(layout.StartMenuShortcut));
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(layout.UserDataRoot, "launcher-state.json")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Legacy_file_collision_fails_closed_before_any_uninstall_change()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteLayout(layout);
        File.WriteAllText(layout.LegacyUserDataRoot, "collision");

        var exception = Assert.Throws<UpdateContractException>(
            () => NyxUninstaller.Uninstall(layout, removeUserData: true));

        Assert.Equal("UnsafePath", exception.Code);
        Assert.True(Directory.Exists(layout.InstallRoot));
        Assert.True(File.Exists(layout.StartMenuShortcut));
        Assert.True(Directory.Exists(layout.UserDataRoot));
        Assert.Equal("collision", File.ReadAllText(layout.LegacyUserDataRoot));
    }

    [Fact]
    public void Packaging_defaults_match_runtime_defaults_and_migration_discards_legacy_export_paths()
    {
        var defaultsPath = Path.Combine(FindDesktopRoot(), "packaging", "first-run-defaults.json");
        using var defaults = JsonDocument.Parse(File.ReadAllText(defaultsPath));
        var runtimeDefaults = LauncherState.Defaults();
        Assert.Equal(runtimeDefaults.Preferences.StayVisibleAfterLaunch,
            defaults.RootElement.GetProperty("stayVisibleAfterLaunch").GetBoolean());
        Assert.True(defaults.RootElement.GetProperty("retainUserDataOnUninstall").GetBoolean());
        Assert.False(defaults.RootElement.GetProperty("exportPullsArmed").GetBoolean());
        Assert.False(defaults.RootElement.GetProperty("exportAchievementsArmed").GetBoolean());

        var migrated = LauncherStateMigrations.Read("""
        {"version":0,"selectedGameId":"custom-a","railOrder":["custom-a","gi"],
         "customGames":[{"id":"custom-a","name":"Mine","executablePath":"C:\\Games\\mine.exe","iconPath":"C:\\Games\\mine.png","creationOrder":7}],
         "appearance":{"custom-a":{"backgroundPath":"C:\\Art\\mine.png","artPinned":true}},
         "export":{"outputPaths":{"gi":"C:\\Exports\\gi.json"}}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, migrated.Status);
        Assert.Equal("custom-a", migrated.State!.SelectedGameId);
        Assert.Single(migrated.State.CustomGames);
        Assert.Equal("C:\\Art\\mine.png", migrated.State.Appearance["custom-a"].BackgroundPath);
        Assert.Empty(migrated.State.Export.OutputPaths);
    }

    [Fact]
    public void Runtime_and_updater_share_the_one_canonical_user_data_root()
    {
        using var fixture = new PackageFixture();
        var local = Path.Combine(fixture.Root, "local");
        var roaming = Path.Combine(fixture.Root, "roaming");

        var layout = UpdateLayout.ForUserRoots(local, roaming);

        Assert.Equal(NyxUserDataPaths.CanonicalRoot(local), layout.UserDataRoot);
        Assert.EndsWith(Path.Combine("Pengo", "Nyx"), layout.UserDataRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteLayout(UpdateLayout layout)
    {
        Directory.CreateDirectory(layout.InstallRoot);
        Directory.CreateDirectory(layout.UserDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.StartMenuShortcut)!);
        File.WriteAllText(Path.Combine(layout.InstallRoot, "app.exe"), "program");
        File.WriteAllText(Path.Combine(layout.UserDataRoot, "launcher-state.json"), "keep-me");
        File.WriteAllText(layout.StartMenuShortcut, "shortcut");
    }

    private static void WriteCompleteLayout(UpdateLayout layout)
    {
        WriteLayout(layout);
        Directory.CreateDirectory(layout.LegacyUserDataRoot);
        File.WriteAllText(Path.Combine(layout.LegacyUserDataRoot, "legacy-state.json"), "legacy");
    }

    private static string SelectRoot(UpdateLayout layout, string rootName) => rootName switch
    {
        "install" => layout.InstallRoot,
        "canonical-data" => layout.UserDataRoot,
        "legacy-data" => layout.LegacyUserDataRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(rootName)),
    };

    private static string RootPayloadName(string rootName) => rootName switch
    {
        "install" => "app.exe",
        "canonical-data" => "launcher-state.json",
        "legacy-data" => "legacy-state.json",
        _ => throw new ArgumentOutOfRangeException(nameof(rootName)),
    };

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
        var startInfo = new ProcessStartInfo(commandInterpreter)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start mklink.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink failed ({process.ExitCode}): {output} {error}");
    }

    private static void DeleteJunctionIfPresent(string path)
    {
        if (Directory.Exists(path)
            && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static string FindDesktopRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Nyx.Desktop.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
