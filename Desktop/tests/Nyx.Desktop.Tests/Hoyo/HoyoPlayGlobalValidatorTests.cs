using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Hoyo;

namespace Nyx.Desktop.Tests.Hoyo;

public sealed class HoyoPlayGlobalValidatorTests
{
    [Fact]
    public void Exact_signed_root_and_matching_version_launcher_produce_sealed_installation()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionStatus.Ready, result.Status);
        Assert.NotNull(result.Installation);
        Assert.Equal(fixture.Root, result.Installation.CanonicalRoot);
        Assert.Equal(Path.Combine(fixture.Root, "launcher.exe"), result.Installation.LauncherPath);
        Assert.Equal("1.8.0.0", result.Installation.Version);
    }

    [Fact]
    public void Missing_root_launcher_is_rejected()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        File.Delete(Path.Combine(fixture.Root, "launcher.exe"));

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.LaunchTargetMissing, result.Reason);
    }

    [Theory]
    [InlineData(false, "COGNOSPHERE PTE. LTD.", "HoYoPlay", "HoYoPlay", HoyoInspectionReason.SignatureInvalid)]
    [InlineData(true, "Other", "HoYoPlay", "HoYoPlay", HoyoInspectionReason.PublisherMismatch)]
    [InlineData(true, "COGNOSPHERE PTE. LTD.", "HoYoPlay Beta", "HoYoPlay", HoyoInspectionReason.ProductIdentityMismatch)]
    [InlineData(true, "COGNOSPHERE PTE. LTD.", "HoYoPlay", "Launcher", HoyoInspectionReason.ProductIdentityMismatch)]
    public void Root_launcher_identity_is_strict(
        bool signed,
        string publisher,
        string product,
        string description,
        HoyoInspectionReason expected)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        fixture.RootMetadata = new(signed, publisher, product, description, "1.8.0.0");

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.8.beta")]
    [InlineData(" 1.8.0.0")]
    [InlineData("1.8.0.0.1")]
    public void Root_product_version_is_strict(string version)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        fixture.RootMetadata = fixture.RootMetadata with { ProductVersion = version };

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.ExecutableVersionInvalid, result.Reason);
    }

    [Fact]
    public void Missing_exact_version_folder_launcher_is_rejected()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        File.Delete(Path.Combine(fixture.Root, "1.8.0.0", "launcher.exe"));

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.VersionFolderLauncherMissing, result.Reason);
    }

    [Fact]
    public void Nested_launcher_version_must_equal_root_version_exactly()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        fixture.NestedMetadata = fixture.NestedMetadata with { ProductVersion = "1.8.0.1" };

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.VersionFolderMismatch, result.Reason);
    }

    [Fact]
    public void Root_metadata_target_drift_is_rejected()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        fixture.RootMetadataSecond = fixture.RootMetadata with { ProductVersion = "1.8.0.1" };

        var result = fixture.CreateValidator().Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.Installation);
    }

    [Fact]
    public void Non_fixed_drive_is_rejected_before_executable_reads()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var reader = fixture.CreateMetadataReader();
        var validator = new HoyoPlayGlobalValidator(reader, new FakeDriveTypeReader(DriveType.Removable));

        var result = validator.Validate(fixture.Root);

        Assert.Equal(HoyoInspectionReason.DriveIsNotLocalFixed, result.Reason);
        Assert.Empty(reader.Paths);
    }

    [Fact]
    public void Repeated_validation_does_not_change_fixture()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var before = fixture.Snapshot();

        Assert.Equal(HoyoInspectionStatus.Ready, fixture.CreateValidator().Validate(fixture.Root).Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    internal sealed class FakeHoyoPlay : IDisposable
    {
        private FakeHoyoPlay(string root, string version)
        {
            Root = root;
            Version = version;
            RootMetadata = GoodMetadata(version);
            NestedMetadata = GoodMetadata(version);
        }

        public string Root { get; }

        public string Version { get; }

        public ExecutableMetadata RootMetadata { get; set; }

        public ExecutableMetadata NestedMetadata { get; set; }

        public ExecutableMetadata? RootMetadataSecond { get; set; }

        public ExecutableMetadata? NestedMetadataSecond { get; set; }

        public static FakeHoyoPlay Create(string version)
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nyx-hoyoplay-{Guid.NewGuid():N}"))
                .FullName;
            File.WriteAllBytes(Path.Combine(root, "launcher.exe"), [1, 2, 3]);
            var versionRoot = Directory.CreateDirectory(Path.Combine(root, version)).FullName;
            File.WriteAllBytes(Path.Combine(versionRoot, "launcher.exe"), [4, 5, 6]);
            return new(root, version);
        }

        public HoyoPlayGlobalValidator CreateValidator() =>
            new(CreateMetadataReader(), new FakeDriveTypeReader());

        public PathMetadataReader CreateMetadataReader()
        {
            var rootPath = Path.Combine(Root, "launcher.exe");
            var nestedPath = Path.Combine(Root, Version, "launcher.exe");
            return new(
                new Dictionary<string, IReadOnlyList<ExecutableMetadata>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootPath] = [RootMetadata, RootMetadataSecond ?? RootMetadata],
                    [nestedPath] = [NestedMetadata, NestedMetadataSecond ?? NestedMetadata],
                });
        }

        public string Snapshot() => string.Join(
            "|",
            Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetRelativePath(Root, path)}:{(File.Exists(path) ? Convert.ToHexString(File.ReadAllBytes(path)) : "dir")}"));

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static ExecutableMetadata GoodMetadata(string version) =>
            new(true, "COGNOSPHERE PTE. LTD.", "HoYoPlay", "HoYoPlay", version);
    }

    internal sealed class PathMetadataReader(
        IReadOnlyDictionary<string, IReadOnlyList<ExecutableMetadata>> values) : IExecutableMetadataReader
    {
        private readonly Dictionary<string, int> indexes = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Paths { get; } = [];

        public ExecutableMetadata Read(string executablePath)
        {
            Paths.Add(executablePath);
            var sequence = values[executablePath];
            indexes.TryGetValue(executablePath, out var index);
            indexes[executablePath] = index + 1;
            return sequence[Math.Min(index, sequence.Count - 1)];
        }
    }

    internal sealed class FakeDriveTypeReader(DriveType driveType = DriveType.Fixed) : IDriveTypeReader
    {
        public DriveType GetDriveType(string driveRoot) => driveType;
    }
}
