using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Tests.Genshin;

public sealed class GenshinInspectionAdapterTests
{
    [Fact]
    public void Complete_fake_game_with_empty_product_fields_is_ready_and_uses_config_game_version()
    {
        using var fixture = FakeInstall.CreateGame();
        var adapter = fixture.CreateAdapter(gameProductVersion: "2017.4.30.0");

        var result = adapter.InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.Ready, result.Status);
        Assert.Equal(GenshinInspectionReason.None, result.Reason);
        Assert.Equal("6.8.0", result.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_game_path_is_not_found(string? path)
    {
        var result = new GenshinInspectionAdapter(new StubMetadataReader()).InspectGame(path);

        Assert.Equal(GenshinInspectionStatus.NotFound, result.Status);
        Assert.Equal(GenshinInspectionReason.PathNotProvided, result.Reason);
    }

    [Fact]
    public void Nonexistent_game_folder_is_not_found_without_creating_it()
    {
        using var fixture = FakeInstall.CreateEmpty();
        var missing = Path.Combine(fixture.Root, "missing");

        var result = fixture.CreateAdapter().InspectGame(missing);

        Assert.Equal(GenshinInspectionStatus.NotFound, result.Status);
        Assert.Equal(GenshinInspectionReason.DirectoryNotFound, result.Reason);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Missing_previously_saved_game_folder_needs_review()
    {
        using var fixture = FakeInstall.CreateEmpty();
        var missing = Path.Combine(fixture.Root, "saved-but-moved");

        var result = fixture.CreateAdapter().InspectGame(missing, GenshinPathOrigin.PreviouslySaved);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.SavedDirectoryMissing, result.Reason);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Candidate_on_non_fixed_drive_needs_review_before_file_system_inspection()
    {
        using var fixture = FakeInstall.CreateGame();
        var metadataReader = new StubMetadataReader();
        var adapter = new GenshinInspectionAdapter(
            metadataReader,
            new StubDriveTypeReader(DriveType.Network));

        var result = adapter.InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.DriveIsNotLocalFixed, result.Reason);
        Assert.Empty(metadataReader.ReadPaths);
    }

    [Theory]
    [InlineData("relative\\Genshin")]
    [InlineData("C:drive-relative")]
    [InlineData(@"\\server\share\Genshin")]
    [InlineData(@"\\?\C:\Genshin")]
    public void Unsafe_game_path_needs_review(string path)
    {
        var result = new GenshinInspectionAdapter(new StubMetadataReader()).InspectGame(path);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.PathIsNotLocalAndCanonical, result.Reason);
    }

    [Fact]
    public void Path_with_parent_traversal_needs_review_even_when_it_resolves_inside_a_real_folder()
    {
        using var fixture = FakeInstall.CreateGame();
        var nonCanonical = Path.Combine(fixture.Root, "child", "..") + Path.DirectorySeparatorChar;

        var result = fixture.CreateAdapter().InspectGame(nonCanonical);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.PathIsNotLocalAndCanonical, result.Reason);
    }

    [Fact]
    public void Reparse_component_needs_review()
    {
        using var fixture = FakeInstall.CreateEmpty();
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "target")).FullName;
        var link = Path.Combine(fixture.Root, "linked-game");
        Assert.True(
            TryCreateDirectoryLink(link, target),
            "This safety test requires directory-link support; link creation failed instead of being silently ignored.");

        var result = fixture.CreateAdapter().InspectGame(link);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.ReparsePointFound, result.Reason);
    }

    [Theory]
    [InlineData("GenshinImpact.exe", false)]
    [InlineData("config.ini", false)]
    [InlineData("GenshinImpact_Data", true)]
    public void Linked_game_evidence_child_needs_review(string evidenceName, bool isDirectory)
    {
        using var fixture = FakeInstall.CreateGame();
        var evidencePath = Path.Combine(fixture.Root, evidenceName);
        var targetPath = Path.Combine(fixture.Root, $"target-{evidenceName}");

        bool linkCreated;
        if (isDirectory)
        {
            Directory.Move(evidencePath, targetPath);
            linkCreated = TryCreateDirectoryLink(evidencePath, targetPath);
        }
        else
        {
            File.Move(evidencePath, targetPath);
            linkCreated = TryCreateFileLink(evidencePath, targetPath);
        }

        Assert.True(
            linkCreated,
            "This safety test requires link support; link creation failed instead of being silently ignored.");

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.ReparsePointFound, result.Reason);
    }

    [Fact]
    public void Lookalike_game_folder_without_data_directory_needs_review()
    {
        using var fixture = FakeInstall.CreateGame();
        Directory.Delete(Path.Combine(fixture.Root, "GenshinImpact_Data"));

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.DataDirectoryMissing, result.Reason);
    }

    [Fact]
    public void Game_without_known_package_manifest_needs_review()
    {
        using var fixture = FakeInstall.CreateGame();
        File.Delete(Path.Combine(fixture.Root, "pkg_version"));

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionReason.PackageManifestMissing, result.Reason);
    }

    [Fact]
    public void Game_without_launch_target_needs_review()
    {
        using var fixture = FakeInstall.CreateGame();
        File.Delete(Path.Combine(fixture.Root, "GenshinImpact.exe"));

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionReason.LaunchTargetMissing, result.Reason);
    }

    [Fact]
    public void Invalid_game_signature_needs_review()
    {
        using var fixture = FakeInstall.CreateGame();

        var result = fixture.CreateAdapter(signatureValid: false).InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionReason.SignatureInvalid, result.Reason);
    }

    [Fact]
    public void Unexpected_game_publisher_needs_review()
    {
        using var fixture = FakeInstall.CreateGame();

        var result = fixture.CreateAdapter(publisher: "Lookalike Studio").InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionReason.PublisherMismatch, result.Reason);
    }

    [Theory]
    [InlineData("channel=2\nsub_channel=0\ncps=mihoyo\ngame_version=6.8.0", GenshinInspectionReason.ConfigIdentityMismatch)]
    [InlineData("channel=1\nsub_channel=0\ncps=mihoyo\ngame_version=not-a-version", GenshinInspectionReason.GameVersionInvalid)]
    [InlineData("channel=1\nchannel=1\nsub_channel=0\ncps=mihoyo\ngame_version=6.8.0", GenshinInspectionReason.ConfigMalformed)]
    [InlineData("channel=1\nsub_channel=0\ncps=mihoyo", GenshinInspectionReason.ConfigMalformed)]
    public void Bad_or_ambiguous_config_needs_review(string config, GenshinInspectionReason expectedReason)
    {
        using var fixture = FakeInstall.CreateGame(config);

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Non_allowlisted_config_keys_are_ignored()
    {
        const string config = """
            [General]
            channel=1
            sub_channel=0
            cps=mihoyo
            game_version=6.8.0
            account_token=must-not-be-used
            telemetry_path=must-not-be-used
            """;
        using var fixture = FakeInstall.CreateGame(config);

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.Ready, result.Status);
    }

    [Fact]
    public void Oversized_config_needs_review()
    {
        using var fixture = FakeInstall.CreateGame(new string('x', 16 * 1024 + 1));

        var result = fixture.CreateAdapter().InspectGame(fixture.Root);

        Assert.Equal(GenshinInspectionReason.ConfigTooLarge, result.Reason);
    }

    [Fact]
    public void Complete_fake_updater_is_ready()
    {
        using var fixture = FakeInstall.CreateUpdater("1.8.0.0");

        var result = fixture.CreateAdapter(updaterProductVersion: "1.8.0.0").InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.Ready, result.Status);
        Assert.Equal("1.8.0.0", result.Version);
    }

    [Fact]
    public void Missing_updater_is_not_found_independently_of_the_game()
    {
        using var fixture = FakeInstall.CreateGame();
        var missing = Path.Combine(fixture.Root, "HoYoPlay-not-installed");

        var result = fixture.CreateAdapter().InspectUpdater(missing);

        Assert.Equal(GenshinInspectionStatus.NotFound, result.Status);
        Assert.Equal(GenshinInspectionReason.DirectoryNotFound, result.Reason);
    }

    [Fact]
    public void Updater_without_root_launcher_needs_review()
    {
        using var fixture = FakeInstall.CreateUpdater("1.8.0.0");
        File.Delete(Path.Combine(fixture.Root, "launcher.exe"));

        var result = fixture.CreateAdapter(updaterProductVersion: "1.8.0.0").InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionReason.LaunchTargetMissing, result.Reason);
    }

    [Fact]
    public void Updater_with_wrong_product_identity_needs_review()
    {
        using var fixture = FakeInstall.CreateUpdater("1.8.0.0");

        var result = fixture.CreateAdapter(productName: "Other Launcher", description: "Other Launcher")
            .InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionReason.ProductIdentityMismatch, result.Reason);
    }

    [Fact]
    public void Updater_without_matching_version_folder_launcher_needs_review()
    {
        using var fixture = FakeInstall.CreateUpdater("1.7.0.0");

        var result = fixture.CreateAdapter(updaterProductVersion: "1.8.0.0").InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionReason.VersionFolderLauncherMissing, result.Reason);
    }

    [Fact]
    public void Updater_with_mismatched_nested_launcher_version_needs_review()
    {
        using var fixture = FakeInstall.CreateUpdater("1.8.0.0");
        var rootLauncher = Path.Combine(fixture.Root, "launcher.exe");
        var nestedLauncher = Path.Combine(fixture.Root, "1.8.0.0", "launcher.exe");
        var reader = new StubMetadataReader(path =>
            GoodMetadata(productVersion: path == rootLauncher ? "1.8.0.0" : "1.7.9.0"));

        var result = new GenshinInspectionAdapter(reader).InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionReason.VersionFolderMismatch, result.Reason);
        Assert.Equal(2, reader.ReadPaths.Count);
        Assert.Contains(rootLauncher, reader.ReadPaths);
        Assert.Contains(nestedLauncher, reader.ReadPaths);
    }

    [Fact]
    public void Linked_nested_updater_launcher_needs_review()
    {
        using var fixture = FakeInstall.CreateUpdater("1.8.0.0");
        var nestedLauncher = Path.Combine(fixture.Root, "1.8.0.0", "launcher.exe");
        var targetLauncher = Path.Combine(fixture.Root, "nested-launcher-target.exe");
        File.Move(nestedLauncher, targetLauncher);
        Assert.True(
            TryCreateFileLink(nestedLauncher, targetLauncher),
            "This safety test requires file-link support; link creation failed instead of being silently ignored.");

        var result = fixture.CreateAdapter(updaterProductVersion: "1.8.0.0").InspectUpdater(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(GenshinInspectionReason.ReparsePointFound, result.Reason);
    }

    [Fact]
    public void Repeated_scans_do_not_mutate_the_fake_install()
    {
        using var fixture = FakeInstall.CreateGame();
        var adapter = fixture.CreateAdapter();
        var before = Snapshot(fixture.Root);

        var first = adapter.InspectGame(fixture.Root);
        var second = adapter.InspectGame(fixture.Root);
        var after = Snapshot(fixture.Root);

        Assert.Equal(GenshinInspectionStatus.Ready, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(before, after);
    }

    private static ExecutableMetadata GoodMetadata(
        bool signatureValid = true,
        string publisher = "COGNOSPHERE PTE. LTD.",
        string productName = "HoYoPlay",
        string description = "HoYoPlay",
        string productVersion = "1.8.0.0")
    {
        return new(signatureValid, publisher, productName, description, productVersion);
    }

    private static string[] Snapshot(string root)
    {
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(root, path)}|{info.Attributes}|{(info.Exists ? info.Length : -1)}|{info.LastWriteTimeUtc.Ticks}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    private sealed class StubMetadataReader : IExecutableMetadataReader
    {
        private readonly Func<string, ExecutableMetadata> read;

        public StubMetadataReader(Func<string, ExecutableMetadata>? read = null)
        {
            this.read = read ?? (_ => GoodMetadata());
        }

        public List<string> ReadPaths { get; } = [];

        public ExecutableMetadata Read(string executablePath)
        {
            ReadPaths.Add(executablePath);
            return read(executablePath);
        }
    }

    private sealed class StubDriveTypeReader(DriveType driveType) : IDriveTypeReader
    {
        public DriveType GetDriveType(string driveRoot) => driveType;
    }

    private sealed class FakeInstall : IDisposable
    {
        private FakeInstall()
        {
            Root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "NyxGenshinTests", Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Root { get; }

        public static FakeInstall CreateEmpty() => new();

        public static FakeInstall CreateGame(string? config = null)
        {
            var fixture = new FakeInstall();
            File.WriteAllBytes(Path.Combine(fixture.Root, "GenshinImpact.exe"), [0x4D, 0x5A]);
            Directory.CreateDirectory(Path.Combine(fixture.Root, "GenshinImpact_Data"));
            File.WriteAllText(Path.Combine(fixture.Root, "pkg_version"), "{}\n");
            File.WriteAllText(
                Path.Combine(fixture.Root, "config.ini"),
                config ?? "channel=1\nsub_channel=0\ncps=mihoyo\ngame_version=6.8.0\n");
            return fixture;
        }

        public static FakeInstall CreateUpdater(string versionFolder)
        {
            var fixture = new FakeInstall();
            File.WriteAllBytes(Path.Combine(fixture.Root, "launcher.exe"), [0x4D, 0x5A]);
            var versionDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, versionFolder)).FullName;
            File.WriteAllBytes(Path.Combine(versionDirectory, "launcher.exe"), [0x4D, 0x5A]);
            return fixture;
        }

        public GenshinInspectionAdapter CreateAdapter(
            bool signatureValid = true,
            string publisher = "COGNOSPHERE PTE. LTD.",
            string productName = "HoYoPlay",
            string description = "HoYoPlay",
            string gameProductVersion = "2017.4.30.0",
            string updaterProductVersion = "1.8.0.0",
            DriveType driveType = DriveType.Fixed)
        {
            return new(
                new StubMetadataReader(path =>
                    Path.GetFileName(path).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase)
                        ? GoodMetadata(
                            signatureValid,
                            publisher,
                            productName: string.Empty,
                            description: string.Empty,
                            productVersion: gameProductVersion)
                        : GoodMetadata(
                            signatureValid,
                            publisher,
                            productName,
                            description,
                            updaterProductVersion)),
                new StubDriveTypeReader(driveType));
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
