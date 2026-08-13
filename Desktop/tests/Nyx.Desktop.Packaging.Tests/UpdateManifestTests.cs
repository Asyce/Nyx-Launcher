using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class UpdateManifestTests
{
    [Fact]
    public void Development_manifest_round_trips_with_exact_identity_and_hashes()
    {
        using var fixture = new PackageFixture();
        var expected = fixture.CreatePackage();

        var actual = UpdateManifestReader.Read(fixture.ManifestPath);

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.PackageSha256, actual.PackageSha256);
        Assert.Equal(2, actual.Files.Count);
    }

    [Theory]
    [InlineData("https://evil.example/desktop/updates/stable/Nyx-Desktop-2.0.0.0-win-x64.zip")]
    [InlineData("https://user@pengo.gg/desktop/updates/stable/Nyx-Desktop-2.0.0.0-win-x64.zip")]
    [InlineData("https://pengo.gg:444/desktop/updates/stable/Nyx-Desktop-2.0.0.0-win-x64.zip")]
    [InlineData("https://pengo.gg/desktop/updates/stable/Nyx-Desktop-2.0.0.0-win-x64.zip?swap=1")]
    [InlineData("http://pengo.gg/desktop/updates/stable/Nyx-Desktop-2.0.0.0-win-x64.zip")]
    public void Remote_channels_reject_host_port_identity_and_path_confusion(string url)
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage(channel: "stable", packageUrl: url);

        var exception = Assert.Throws<UpdateContractException>(() => UpdateManifestReader.Validate(manifest));

        Assert.Equal("PackageUrlInvalid", exception.Code);
    }

    [Fact]
    public void Stable_channel_requires_an_allowlisted_https_url()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage(channel: "stable");

        Assert.Equal(
            "PackageUrlMissing",
            Assert.Throws<UpdateContractException>(() => UpdateManifestReader.Validate(manifest)).Code);
    }

    [Theory]
    [InlineData("../Nyx.Desktop.App.exe")]
    [InlineData("C:/Nyx.Desktop.App.exe")]
    [InlineData("Assets\\tool.exe")]
    [InlineData("Assets/CON")]
    [InlineData("Assets/file. ")]
    public void File_manifest_rejects_escape_and_windows_alias_paths(string path)
    {
        var manifest = ValidManifestWithFile(path);

        Assert.Equal(
            "UnsafeRelativePath",
            Assert.Throws<UpdateContractException>(() => UpdateManifestReader.Validate(manifest)).Code);
    }

    [Fact]
    public void Case_colliding_files_are_rejected()
    {
        var manifest = ValidManifestWithFile("Nyx.Desktop.App.exe") with
        {
            Files =
            [
                new("NYX.DESKTOP.APP.EXE", 1, new string('a', 64)),
                new("Nyx.Desktop.App.exe", 1, new string('b', 64)),
            ],
        };

        Assert.Equal(
            "FileSetInvalid",
            Assert.Throws<UpdateContractException>(() => UpdateManifestReader.Validate(manifest)).Code);
    }

    private static UpdateReleaseManifest ValidManifestWithFile(string path) => new(
        1,
        "nyx-desktop",
        "development",
        "2.0.0.0",
        "win-x64",
        "Nyx-Desktop-2.0.0.0-win-x64.zip",
        1,
        new string('a', 64),
        "Nyx.Desktop.App.exe",
        null,
        [new(path, 1, new string('b', 64))]);
}
