using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class UpdatePackageStagerTests
{
    [Fact]
    public void Verified_package_stages_complete_tree()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();

        var staged = UpdatePackageStager.Stage(manifest, fixture.PackagePath, fixture.Staging);

        Assert.Equal("new-app", File.ReadAllText(Path.Combine(staged, "Nyx.Desktop.App.exe")));
        UpdatePackageStager.VerifyTree(manifest, staged);
    }

    [Fact]
    public void Download_tampering_is_rejected_before_extraction()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        using (var stream = new FileStream(fixture.PackagePath, FileMode.Append, FileAccess.Write))
        {
            stream.WriteByte(0x42);
        }

        var exception = Assert.Throws<UpdateContractException>(
            () => UpdatePackageStager.Stage(manifest, fixture.PackagePath, fixture.Staging));

        Assert.Equal("PackageHashMismatch", exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Staging));
    }

    [Fact]
    public void Correct_outer_hash_cannot_hide_wrong_inner_file_hash()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage(
            contents: new Dictionary<string, string>
            {
                ["Assets/data.txt"] = "evil",
                ["Nyx.Desktop.App.exe"] = "new-app",
            },
            manifestContents: new Dictionary<string, string>
            {
                ["Assets/data.txt"] = "good",
                ["Nyx.Desktop.App.exe"] = "new-app",
            });

        var exception = Assert.Throws<UpdateContractException>(
            () => UpdatePackageStager.Stage(manifest, fixture.PackagePath, fixture.Staging));

        Assert.Equal("FileHashMismatch", exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Staging));
    }

    [Fact]
    public void Archive_traversal_entry_never_writes_outside_staging()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage(new Dictionary<string, string>
        {
            ["../escape.txt"] = "escape",
            ["Nyx.Desktop.App.exe"] = "new-app",
        });
        manifest = manifest with
        {
            Files = [new("Nyx.Desktop.App.exe", 7, PackageFixture.Sha256("new-app"))],
        };

        var exception = Assert.Throws<UpdateContractException>(
            () => UpdatePackageStager.Stage(manifest, fixture.PackagePath, fixture.Staging));

        Assert.Equal("ArchiveEntrySetInvalid", exception.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "install", "escape.txt")));
    }

    [Fact]
    public void Reparse_backed_staging_root_is_rejected_before_any_payload_write()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var outside = Path.Combine(fixture.Root, "outside");
        var linkedStaging = Path.Combine(fixture.Root, "linked-staging");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedStaging, outside);

        var exception = Assert.Throws<UpdateContractException>(
            () => UpdatePackageStager.Stage(manifest, fixture.PackagePath, linkedStaging));

        Assert.Equal("UnsafePath", exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
}
