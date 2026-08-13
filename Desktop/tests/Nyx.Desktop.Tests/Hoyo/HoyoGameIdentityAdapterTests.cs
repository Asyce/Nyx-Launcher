using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Hoyo;

namespace Nyx.Desktop.Tests.Hoyo;

public sealed class HoyoGameIdentityAdapterTests
{
    [Theory]
    [InlineData("hsr", "Star Rail", "7.0.0")]
    [InlineData("zzz", null, "3.0.0")]
    [InlineData("zzz", "Zenless Zone Zero", "3.0.0")]
    public void Complete_fake_game_is_ready(string gameId, string? productName, string expectedVersion)
    {
        using var fixture = FakeHoyoGame.Create(gameId);

        var result = fixture.CreateAdapter(productName: productName).Inspect(gameId, fixture.Root);

        Assert.Equal(HoyoInspectionStatus.Ready, result.Status);
        Assert.Equal(HoyoInspectionReason.None, result.Reason);
        Assert.Equal(expectedVersion, result.Version);
        Assert.Equal(Path.TrimEndingDirectorySeparator(fixture.Root), result.CanonicalRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\game")]
    [InlineData(@"\\server\share\game")]
    [InlineData(@"\\?\C:\game")]
    public void Missing_or_unsafe_path_never_inspects_metadata(string? path)
    {
        var metadata = new FakeMetadataReader(DefaultMetadata("Star Rail"));
        var result = new HoyoGameIdentityAdapter(metadata, new FakeDriveTypeReader()).Inspect("hsr", path);

        Assert.NotEqual(HoyoInspectionStatus.Ready, result.Status);
        Assert.Empty(metadata.Paths);
    }

    [Fact]
    public void Non_fixed_drive_is_rejected_before_metadata()
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        var metadata = new FakeMetadataReader(DefaultMetadata("Star Rail"));
        var adapter = new HoyoGameIdentityAdapter(metadata, new FakeDriveTypeReader(DriveType.Network));

        var result = adapter.Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionReason.DriveIsNotLocalFixed, result.Reason);
        Assert.Empty(metadata.Paths);
    }

    [Theory]
    [InlineData("StarRail.exe", HoyoInspectionReason.LaunchTargetMissing)]
    [InlineData("StarRail_Data", HoyoInspectionReason.DataDirectoryMissing)]
    [InlineData("pkg_version", HoyoInspectionReason.PackageManifestMissing)]
    [InlineData("config.ini", HoyoInspectionReason.ConfigMissing)]
    public void Missing_hsr_evidence_is_rejected(string name, HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        fixture.Delete(name);

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Alternate_manifest_conflicts_with_exact_pkg_version_contract()
    {
        using var fixture = FakeHoyoGame.Create("zzz");
        File.WriteAllText(Path.Combine(fixture.Root, "pkg_version.json"), "lookalike");

        var result = fixture.CreateAdapter().Inspect("zzz", fixture.Root);

        Assert.Equal(HoyoInspectionReason.PackageManifestConflict, result.Reason);
    }

    [Fact]
    public void Swapped_hsr_files_do_not_make_a_zzz_installation()
    {
        using var fixture = FakeHoyoGame.Create("hsr");

        var result = fixture.CreateAdapter().Inspect("zzz", fixture.Root);

        Assert.Equal(HoyoInspectionReason.LaunchTargetMissing, result.Reason);
    }

    [Theory]
    [InlineData("channel=2\nsub_channel=1\ncps=hoyoverse_PC\ngame_version=7.0.0", HoyoInspectionReason.ConfigIdentityMismatch)]
    [InlineData("channel=1\nsub_channel=0\ncps=hoyoverse_PC\ngame_version=7.0.0", HoyoInspectionReason.ConfigIdentityMismatch)]
    [InlineData("channel=1\nsub_channel=1\ncps=mihoyo\ngame_version=7.0.0", HoyoInspectionReason.ConfigIdentityMismatch)]
    [InlineData("channel=1\nsub_channel=1\ncps=hoyoverse_PC\ngame_version=seven", HoyoInspectionReason.GameVersionInvalid)]
    [InlineData("channel=1\nchannel=1\nsub_channel=1\ncps=hoyoverse_PC\ngame_version=7.0.0", HoyoInspectionReason.ConfigMalformed)]
    [InlineData("channel=1\nsub_channel=1\ncps=hoyoverse_PC", HoyoInspectionReason.ConfigMalformed)]
    public void Hsr_config_must_match_exact_identity(string config, HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoGame.Create("hsr", config);

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Oversized_config_is_rejected_without_unbounded_read()
    {
        using var fixture = FakeHoyoGame.Create("hsr", new string('x', 16 * 1024 + 1));

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionReason.ConfigTooLarge, result.Reason);
    }

    [Theory]
    [InlineData(null, HoyoInspectionReason.VersionInfoMissing)]
    [InlineData("OSPRODWin3.0.1", HoyoInspectionReason.VersionInfoMismatch)]
    [InlineData("OSPRODWin3.0.0\n", HoyoInspectionReason.VersionInfoMalformed)]
    [InlineData("OSPRODWin3.x.0", HoyoInspectionReason.VersionInfoMalformed)]
    [InlineData("{\"version\":\"3.0.0\"}", HoyoInspectionReason.VersionInfoMalformed)]
    public void Zzz_version_info_is_exact_bounded_plain_text(string? versionInfo, HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoGame.Create("zzz");
        var path = Path.Combine(fixture.Root, "version_info");
        if (versionInfo is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, versionInfo);
        }

        var result = fixture.CreateAdapter().Inspect("zzz", fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Zzz_version_info_rejects_a_byte_order_mark_as_extra_content()
    {
        using var fixture = FakeHoyoGame.Create("zzz");
        File.WriteAllText(
            Path.Combine(fixture.Root, "version_info"),
            "OSPRODWin3.0.0",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = fixture.CreateAdapter().Inspect("zzz", fixture.Root);

        Assert.Equal(HoyoInspectionReason.VersionInfoMalformed, result.Reason);
    }

    [Fact]
    public void Oversized_zzz_version_info_is_rejected()
    {
        using var fixture = FakeHoyoGame.Create("zzz");
        File.WriteAllText(Path.Combine(fixture.Root, "version_info"), new string('1', 129));

        var result = fixture.CreateAdapter().Inspect("zzz", fixture.Root);

        Assert.Equal(HoyoInspectionReason.VersionInfoTooLarge, result.Reason);
    }

    [Theory]
    [InlineData("hsr", "", HoyoInspectionReason.ProductIdentityMismatch)]
    [InlineData("hsr", "Zenless Zone Zero", HoyoInspectionReason.ProductIdentityMismatch)]
    [InlineData("zzz", "Star Rail", HoyoInspectionReason.ProductIdentityMismatch)]
    public void Nonmatching_nonblank_product_identity_is_rejected(
        string gameId,
        string productName,
        HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoGame.Create(gameId);

        var result = fixture.CreateAdapter(productName: productName).Inspect(gameId, fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Theory]
    [InlineData(false, "COGNOSPHERE PTE. LTD.", HoyoInspectionReason.SignatureInvalid)]
    [InlineData(true, "Lookalike Publisher", HoyoInspectionReason.PublisherMismatch)]
    public void Signature_and_publisher_are_mandatory(
        bool signatureValid,
        string publisher,
        HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoGame.Create("hsr");

        var result = fixture.CreateAdapter(signatureValid, publisher).Inspect("hsr", fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Metadata_drift_during_inspection_is_rejected()
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        var good = DefaultMetadata("Star Rail");
        var changed = good with { ProductVersion = "changed" };
        var reader = new FakeMetadataReader(good, changed);

        var result = new HoyoGameIdentityAdapter(reader, new FakeDriveTypeReader()).Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionReason.TargetChangedDuringInspection, result.Reason);
    }

    [Fact]
    public void Config_drift_during_inspection_is_rejected_without_retaining_the_payload()
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        var reader = new FakeMetadataReader(DefaultMetadata("Star Rail"));
        reader.OnRead = (call, _) =>
        {
            if (call == 0)
            {
                File.AppendAllText(Path.Combine(fixture.Root, "config.ini"), "\nignored_key=changed");
            }
        };

        var result = new HoyoGameIdentityAdapter(reader, new FakeDriveTypeReader())
            .Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.DoesNotContain("ignored_key=changed", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_config_value_is_never_exposed_by_result_or_error_state()
    {
        const string forbiddenMarker = "fixture-secret-value-7f193";
        const string config = """
            channel=1
            sub_channel=1
            cps=hoyoverse_PC
            game_version=7.0.0
            ignored_private_key=fixture-secret-value-7f193
            """;
        using var fixture = FakeHoyoGame.Create("hsr", config);

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionStatus.Ready, result.Status);
        Assert.DoesNotContain(forbiddenMarker, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            forbiddenMarker,
            string.Join("|", typeof(HoyoGameInspectionResult).GetProperties().Select(property => property.GetValue(result))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_binary_config_value_is_discarded_without_decoding()
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        var knownText = System.Text.Encoding.ASCII.GetBytes(
            "channel=1\nsub_channel=1\ncps=hoyoverse_PC\ngame_version=7.0.0\nignored_binary=");
        File.WriteAllBytes(
            Path.Combine(fixture.Root, "config.ini"),
            [.. knownText, 0xFF, 0xFE, 0xFD]);

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionStatus.Ready, result.Status);
        Assert.Equal(HoyoInspectionReason.None, result.Reason);
    }

    [Fact]
    public void Repeated_inspection_of_unchanged_fixture_is_stable_and_read_only()
    {
        using var fixture = FakeHoyoGame.Create("zzz");
        var before = fixture.Snapshot();
        var adapter = fixture.CreateAdapter(productName: null);

        var first = adapter.Inspect("zzz", fixture.Root);
        var second = adapter.Inspect("zzz", fixture.Root);

        Assert.Equal(first, second);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Linked_required_evidence_is_rejected()
    {
        using var fixture = FakeHoyoGame.Create("hsr");
        var evidence = Path.Combine(fixture.Root, "config.ini");
        var target = Path.Combine(fixture.Root, "config-target.ini");
        File.Move(evidence, target);
        File.CreateSymbolicLink(evidence, target);

        var result = fixture.CreateAdapter().Inspect("hsr", fixture.Root);

        Assert.Equal(HoyoInspectionReason.ReparsePointFound, result.Reason);
    }

    private static ExecutableMetadata DefaultMetadata(string? productName) =>
        new(true, "COGNOSPHERE PTE. LTD.", productName, null, "1.0.0.0");

    private sealed class FakeHoyoGame : IDisposable
    {
        private FakeHoyoGame(string root, string gameId)
        {
            Root = root;
            GameId = gameId;
        }

        public string Root { get; }

        private string GameId { get; }

        public static FakeHoyoGame Create(string gameId, string? config = null)
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nyx-hoyo-{Guid.NewGuid():N}"))
                .FullName;
            var fixture = new FakeHoyoGame(root, gameId);
            var isHsr = gameId == "hsr";
            File.WriteAllBytes(Path.Combine(root, isHsr ? "StarRail.exe" : "ZenlessZoneZero.exe"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(root, isHsr ? "StarRail_Data" : "ZenlessZoneZero_Data"));
            File.WriteAllText(Path.Combine(root, "pkg_version"), "fixture-only");
            File.WriteAllText(
                Path.Combine(root, "config.ini"),
                config ?? (isHsr
                    ? "channel=1\nsub_channel=1\ncps=hoyoverse_PC\ngame_version=7.0.0"
                    : "channel=1\nsub_channel=0\ncps=mihoyo\ngame_version=3.0.0"));
            if (!isHsr)
            {
                File.WriteAllText(Path.Combine(root, "version_info"), "OSPRODWin3.0.0");
            }

            return fixture;
        }

        public HoyoGameIdentityAdapter CreateAdapter(
            bool signatureValid = true,
            string publisher = "COGNOSPHERE PTE. LTD.",
            string? productName = "__default__")
        {
            var resolvedProduct = productName == "__default__"
                ? GameId == "hsr" ? "Star Rail" : null
                : productName;
            var metadata = new ExecutableMetadata(signatureValid, publisher, resolvedProduct, null, "1.0.0.0");
            return new(new FakeMetadataReader(metadata), new FakeDriveTypeReader());
        }

        public void Delete(string name)
        {
            var path = Path.Combine(Root, name);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }

        public string Snapshot() => string.Join(
            "|",
            Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetRelativePath(Root, path)}:{(File.Exists(path) ? Convert.ToHexString(File.ReadAllBytes(path)) : "dir")}"));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeMetadataReader(params ExecutableMetadata[] sequence) : IExecutableMetadataReader
    {
        private readonly ExecutableMetadata[] sequence = sequence;
        private int index;

        public List<string> Paths { get; } = [];

        public Action<int, string>? OnRead { get; set; }

        public ExecutableMetadata Read(string executablePath)
        {
            Paths.Add(executablePath);
            OnRead?.Invoke(index, executablePath);
            var selected = sequence[Math.Min(index, sequence.Length - 1)];
            index++;
            return selected;
        }
    }

    private sealed class FakeDriveTypeReader(DriveType driveType = DriveType.Fixed) : IDriveTypeReader
    {
        public DriveType GetDriveType(string driveRoot) => driveType;
    }
}
