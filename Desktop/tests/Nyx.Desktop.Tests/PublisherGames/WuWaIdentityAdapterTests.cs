using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.PublisherGames;
using Xunit.Sdk;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class WuWaIdentityAdapterTests
{
    [Fact]
    public void Observed_350_config_and_351_resource_is_version_conflict_not_claim()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(PublisherGameInspectionReason.VersionConflict, result.Reason);
        Assert.Equal(PublisherGameVersionState.Conflict, result.VersionState);
        Assert.Null(result.Version);
        Assert.NotNull(result.MaintenanceTarget);
        Assert.False(result.AllowsDirectGameLaunch);
    }

    [Fact]
    public void Matching_strict_public_versions_can_be_ready_but_direct_launch_stays_disabled()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionStatus.Ready, result.Status);
        Assert.Equal("3.5.0", result.Version);
        Assert.False(result.AllowsDirectGameLaunch);
    }

    [Fact]
    public void Current_official_resource_shape_uses_runtime_size_and_checksum_when_from_folder_is_null()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(
            configVersion: "3.5.3",
            resourceVersion: "3.5.3");
        var runtimePath = fixture.PathOf(
            @"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe");
        var runtimeBytes = File.ReadAllBytes(runtimePath);
        var runtimeMd5 = Convert.ToHexString(MD5.HashData(runtimeBytes)).ToLowerInvariant();
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"),
            $$"""
            {"resource":[{
              "dest":"{{WuWaPublicEvidenceParser.ExpectedRuntimeDestination}}",
              "size":{{runtimeBytes.Length}},
              "md5":"{{runtimeMd5}}",
              "fromFolder":null,
              "chunkInfos":[{"start":0,"end":{{runtimeBytes.Length - 1}},"md5":"{{runtimeMd5}}"}]
            }]}
            """);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionStatus.Ready, result.Status);
        Assert.Equal("3.5.3", result.Version);
    }

    [Theory]
    [InlineData(4, "d4cdb8e9b7fb58a7baaba746deee3d03")]
    [InlineData(3, "00000000000000000000000000000000")]
    [InlineData(3, "D4CDB8E9B7FB58A7BAABA746DEEE3D03")]
    public void Current_resource_shape_rejects_runtime_size_or_checksum_mismatch(
        int size,
        string md5)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"),
            $$"""
            {"resource":[{
              "dest":"{{WuWaPublicEvidenceParser.ExpectedRuntimeDestination}}",
              "size":{{size}},
              "md5":"{{md5}}",
              "fromFolder":null
            }]}
            """);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ResourceEvidenceMalformed, result.Reason);
        Assert.False(result.HasFullInstallMaintenanceProof);
    }

    [Theory]
    [InlineData("launcher.exe", PublisherGameInspectionReason.RootLauncherMissing)]
    [InlineData(@"2.6.3.0\launcher.exe", PublisherGameInspectionReason.VersionedLauncherMissing)]
    [InlineData(@"Wuthering Waves Game\Wuthering Waves.exe", PublisherGameInspectionReason.BootstrapMissing)]
    [InlineData(@"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe", PublisherGameInspectionReason.RuntimeMissing)]
    [InlineData(@"Wuthering Waves Game\launcherDownloadConfig.json", PublisherGameInspectionReason.ConfigMissing)]
    [InlineData(@"Wuthering Waves Game\launcherDownload\launcherDownloadConfig.json", PublisherGameInspectionReason.ConfigMissing)]
    [InlineData(@"Wuthering Waves Game\LocalGameResources.json", PublisherGameInspectionReason.ResourceEvidenceMissing)]
    public void Missing_exact_evidence_fails_closed(string relativePath, PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        fixture.Delete(relativePath);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Root_and_versioned_kuro_launchers_must_be_byte_identical()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllBytes(fixture.PathOf(@"2.6.3.0\launcher.exe"), [9, 9, 9]);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.LauncherMismatch, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Theory]
    [InlineData("{\"version\":\"3.5.0\",\"isPreDownload\":false,\"appId\":50004}")]
    [InlineData("{\"version\":\"3.5.0\",\"isPreDownload\":true,\"appId\":\"50004\"}")]
    [InlineData("{\"version\":\"3.5.0\",\"isPreDownload\":false,\"appId\":\"50005\"}")]
    [InlineData("{\"Version\":\"3.5.0\",\"isPreDownload\":false,\"appId\":\"50004\"}")]
    [InlineData("{\"version\":\"3.5.0\",\"version\":\"3.5.0\",\"isPreDownload\":false,\"appId\":\"50004\"}")]
    [InlineData("{\"version\":\"03.5.0\",\"isPreDownload\":false,\"appId\":\"50004\"}")]
    [InlineData("{\"version\":\"3.5.0\",\"isPreDownload\":false,\"appId\":\"50004\",\"x\":{\"y\":{\"z\":1}}}")]
    public void Malformed_or_wrong_download_config_is_rejected(string json)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllText(fixture.PathOf(@"Wuthering Waves Game\launcherDownloadConfig.json"), json);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ConfigMalformed, result.Reason);
    }

    [Fact]
    public void Root_and_nested_download_configs_must_match()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\launcherDownload\launcherDownloadConfig.json"),
            "{\"version\":\"3.5.2\",\"isPreDownload\":false,\"appId\":\"50004\"}");

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ConfigIdentityMismatch, result.Reason);
    }

    [Fact]
    public void Oversized_download_config_is_rejected_before_parsing()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\launcherDownloadConfig.json"),
            new string('x', WuWaPublicEvidenceParser.MaximumConfigBytes + 1));

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ConfigTooLarge, result.Reason);
    }

    [Fact]
    public void Unreadable_download_config_fails_closed()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        using var locked = new FileStream(
            fixture.PathOf(@"Wuthering Waves Game\launcherDownloadConfig.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.InspectionFailed, result.Reason);
    }

    [Theory]
    [InlineData("{\"resource\":[]}", PublisherGameInspectionReason.ResourceEvidenceMissing)]
    [InlineData("{\"Resource\":[]}", PublisherGameInspectionReason.ResourceEvidenceMalformed)]
    [InlineData("{\"resource\":[{\"dest\":123,\"fromFolder\":null}]}", PublisherGameInspectionReason.ResourceEvidenceMalformed)]
    [InlineData("{\"resource\":[{\"dest\":\"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\"fromFolder\":null}]}", PublisherGameInspectionReason.ResourceEvidenceMalformed)]
    [InlineData("{\"resource\":[{\"dest\":\"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\"fromFolder\":\"a/3.5.1/b/4.0.0/c\"}]}", PublisherGameInspectionReason.ResourceEvidenceMalformed)]
    [InlineData("{\"resource\":[{\"dest\":\"Client/Binaries/Win64/Client-Win64-Shipping.exe\",\"fromFolder\":\"a/3.5.1/b\",\"x\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":1}}}}}}}}]}", PublisherGameInspectionReason.ResourceEvidenceMalformed)]
    public void Resource_manifest_is_bounded_exact_and_unambiguous(
        string json,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllText(fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"), json);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Oversized_resource_manifest_is_rejected_before_parsing()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"),
            new string('x', WuWaPublicEvidenceParser.MaximumResourceBytes + 1));

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ResourceEvidenceTooLarge, result.Reason);
    }

    [Fact]
    public void Duplicate_runtime_resource_match_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        const string runtimeMd5 = "d4cdb8e9b7fb58a7baaba746deee3d03";
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"),
            $$"""
            {"resource":[
              {"dest":"{{WuWaPublicEvidenceParser.ExpectedRuntimeDestination}}","size":3,"md5":"{{runtimeMd5}}","fromFolder":"a/3.5.1/b"},
              {"dest":"{{WuWaPublicEvidenceParser.ExpectedRuntimeDestination}}","size":3,"md5":"{{runtimeMd5}}","fromFolder":"c/3.5.1/d"}
            ]}
            """);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ResourceEvidenceMalformed, result.Reason);
    }

    [Fact]
    public void Resource_entry_count_is_bounded()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var entries = string.Join(
            ',',
            Enumerable.Repeat("{\"dest\":\"x\",\"fromFolder\":null}", 10_001));
        File.WriteAllText(
            fixture.PathOf(@"Wuthering Waves Game\LocalGameResources.json"),
            $"{{\"resource\":[{entries}]}}");

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ResourceEvidenceMalformed, result.Reason);
    }

    [Fact]
    public void Reparse_component_in_game_tree_is_deterministically_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var gameRoot = fixture.PathOf("Wuthering Waves Game");

        var result = fixture.CreateWuWaAdapterWithReparsePoints(gameRoot)
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
        Assert.False(result.HasFullInstallMaintenanceProof);
        Assert.Throws<ArgumentNullException>(
            () => OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget!));
    }

    [Fact]
    public void Root_launcher_becoming_reparse_at_entry_open_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var launcher = fixture.PathOf("launcher.exe");
        var entryOpener = new FakePublisherInstall.ReparseRejectingEntryOpener(launcher);

        var result = fixture.CreateWuWaAdapterWithEntryOpener(entryOpener)
            .Inspect(fixture.Root);

        Assert.True(entryOpener.RejectionReached);
        Assert.Equal([launcher], entryOpener.OpenedPaths);
        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
        Assert.False(result.HasFullInstallMaintenanceProof);
        Assert.Throws<ArgumentNullException>(
            () => OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget!));
    }

    [Fact]
    public void Versioned_launcher_becoming_reparse_at_entry_open_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var launcher = fixture.PathOf(@"2.6.3.0\launcher.exe");
        var entryOpener = new FakePublisherInstall.ReparseRejectingEntryOpener(launcher);

        var result = fixture.CreateWuWaAdapterWithEntryOpener(entryOpener)
            .Inspect(fixture.Root);

        Assert.True(entryOpener.RejectionReached);
        Assert.Equal(
            [fixture.PathOf("launcher.exe"), launcher],
            entryOpener.OpenedPaths);
        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
        Assert.False(result.HasFullInstallMaintenanceProof);
        Assert.Throws<ArgumentNullException>(
            () => OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget!));
    }

    [Fact]
    public void Optional_live_reparse_point_in_game_tree_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var gameRoot = fixture.PathOf("Wuthering Waves Game");
        var original = Path.Combine(gameRoot, "launcherDownloadConfig.json");
        var target = Path.Combine(fixture.Root, "external-config.json");
        File.Move(original, target);
        if (!TryCreateFileLink(original, target))
        {
            File.Move(target, original);
            throw SkipException.ForSkip(
                "Windows did not grant symlink creation; deterministic seam coverage still runs.");
        }

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Optional_live_native_entry_open_rejects_file_symlink()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var launcher = fixture.PathOf("launcher.exe");
        var target = fixture.PathOf("launcher-real.exe");
        File.Move(launcher, target);
        if (!TryCreateFileLink(launcher, target))
        {
            File.Move(target, launcher);
            throw SkipException.ForSkip(
                "Windows did not grant symlink creation; deterministic entry-opener coverage still runs.");
        }

        var entryOpener = new WindowsPublisherExecutableEntryOpener();

        Assert.Throws<PublisherReparsePointException>(() =>
        {
            using var handle = entryOpener.Open(launcher);
        });
    }

    [Fact]
    public void Same_publisher_launcher_substituted_for_blank_pe_game_target_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var bootstrap = fixture.PathOf(@"Wuthering Waves Game\Wuthering Waves.exe");
        var launcherMetadata = fixture.Metadata.Get(fixture.PathOf("launcher.exe"));
        fixture.Metadata.Set(bootstrap, launcherMetadata);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ProductIdentityMismatch, result.Reason);
    }

    [Fact]
    public void Swapped_blank_pe_game_files_never_enable_direct_launch()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var bootstrap = fixture.PathOf(@"Wuthering Waves Game\Wuthering Waves.exe");
        var runtime = fixture.PathOf(
            @"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe");
        var bootstrapBytes = File.ReadAllBytes(bootstrap);
        File.WriteAllBytes(bootstrap, File.ReadAllBytes(runtime));
        File.WriteAllBytes(runtime, bootstrapBytes);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.False(result.AllowsDirectGameLaunch);
        Assert.Equal(PublisherGameInspectionReason.ResourceEvidenceMalformed, result.Reason);
    }

    [Theory]
    [InlineData(false, FakePublisherInstall.KuroPublisher, PublisherGameInspectionReason.SignatureInvalid)]
    [InlineData(true, "Other Publisher", PublisherGameInspectionReason.PublisherMismatch)]
    public void Signer_and_signature_are_mandatory(
        bool signatureValid,
        string publisher,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var runtime = fixture.PathOf(@"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe");
        var metadata = fixture.Metadata.Get(runtime) with
        {
            HasValidAuthenticodeSignature = signatureValid,
            Publisher = publisher,
        };
        fixture.Metadata.Set(runtime, metadata);

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Certificate_rotation_is_allowed_when_signature_and_expected_publisher_remain_valid()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.NotEqual(PublisherGameInspectionReason.SignatureInvalid, result.Reason);
        Assert.NotEqual(PublisherGameInspectionReason.PublisherMismatch, result.Reason);
    }

    [Theory]
    [InlineData(DriveType.Network, "NTFS", PublisherGameInspectionReason.DriveIsNotLocalFixed)]
    [InlineData(DriveType.Fixed, "ReFS", PublisherGameInspectionReason.FileSystemIsNotNtfs)]
    public void Unsafe_volume_is_rejected_before_metadata(
        DriveType driveType,
        string fileSystem,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var adapter = fixture.CreateWuWaAdapter(driveType, fileSystem);

        var result = adapter.Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
        Assert.Empty(fixture.Metadata.ReadPaths);
    }

    [Theory]
    [InlineData("relative\\wuwa")]
    [InlineData(@"\\server\share\wuwa")]
    [InlineData(@"\\?\C:\wuwa")]
    public void Unsafe_path_is_rejected(string path)
    {
        using var fixture = FakePublisherInstall.CreateWuWa();

        var result = fixture.CreateWuWaAdapter().Inspect(path);

        Assert.Equal(PublisherGameInspectionReason.PathIsNotLocalAndCanonical, result.Reason);
    }

    [Fact]
    public void Absolute_path_with_traversal_is_rejected_even_when_it_resolves_to_the_install()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var traversing = Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root));

        var result = fixture.CreateWuWaAdapter().Inspect(traversing);

        Assert.Equal(PublisherGameInspectionReason.PathIsNotLocalAndCanonical, result.Reason);
    }

    [Fact]
    public void Metadata_target_drift_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var runtime = fixture.PathOf(@"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe");
        var good = fixture.Metadata.Get(runtime);
        fixture.Metadata.Set(runtime, good, good with { Publisher = "Changed Publisher" });

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Launcher_metadata_drift_never_survives_as_maintenance_proof()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var launcher = fixture.PathOf("launcher.exe");
        var good = fixture.Metadata.Get(launcher);
        fixture.Metadata.Set(launcher, good, good, good with { Publisher = "Changed Publisher" });

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Root_volume_drift_never_survives_as_maintenance_proof()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");

        var result = fixture.CreateWuWaAdapterWithFileSystems("NTFS", "NTFS", "ReFS")
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Evidence_path_drift_never_survives_as_maintenance_proof()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var runtime = fixture.PathOf(
            @"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe");
        var nestedConfig = fixture.PathOf(
            @"Wuthering Waves Game\launcherDownload\launcherDownloadConfig.json");
        fixture.Metadata.OnRead = (path, index) =>
        {
            if (index == 1 && string.Equals(path, runtime, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(nestedConfig);
            }
        };

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Protected_launcher_handle_blocks_same_length_same_time_swap_and_replay()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var launcher = fixture.PathOf("launcher.exe");
        var timestamp = File.GetLastWriteTimeUtc(launcher);
        var swapWasBlocked = false;
        fixture.Metadata.OnRead = (path, index) =>
        {
            if (index != 1 || !string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.WriteAllBytes(launcher, [10, 7, 4, 1]);
                File.SetLastWriteTimeUtc(launcher, timestamp);
            }
            catch (IOException)
            {
                swapWasBlocked = true;
            }
        };

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.True(swapWasBlocked);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Protected_root_directory_blocks_ancestor_rename_and_replacement_during_metadata()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var launcher = fixture.PathOf("launcher.exe");
        var movedRoot = $"{fixture.Root}-moved";
        var replacementRoot = $"{fixture.Root}-replacement";
        Directory.CreateDirectory(replacementRoot);
        var renameWasBlocked = false;
        var replacementWasBlocked = false;
        fixture.Metadata.OnRead = (path, index) =>
        {
            if (index != 0 || !string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(fixture.Root, movedRoot);
            }
            catch (IOException)
            {
                renameWasBlocked = true;
            }

            try
            {
                Directory.Move(replacementRoot, fixture.Root);
            }
            catch (IOException)
            {
                replacementWasBlocked = true;
            }
        };

        try
        {
            var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

            Assert.True(renameWasBlocked);
            Assert.True(replacementWasBlocked);
            Assert.NotNull(result.MaintenanceTarget);
        }
        finally
        {
            if (Directory.Exists(movedRoot) && !Directory.Exists(fixture.Root))
            {
                Directory.Move(movedRoot, fixture.Root);
            }

            if (Directory.Exists(replacementRoot))
            {
                Directory.Delete(replacementRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Path_handle_file_identity_mismatch_fails_before_metadata_can_be_trusted()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var original = new PublisherNtfsFileIdentity(7, 11, 1);
        var replacement = new PublisherNtfsFileIdentity(7, 12, 1);
        var identityReader = new FakePublisherInstall.SequenceFileIdentityReader(
            original,
            replacement);

        var result = fixture.CreateWuWaAdapterWithIdentityReader(identityReader)
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.InspectionFailed, result.Reason);
        Assert.Empty(fixture.Metadata.ReadPaths);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Post_metadata_path_handle_mismatch_discards_observed_metadata_and_token()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var original = new PublisherNtfsFileIdentity(7, 21, 1);
        var replacement = new PublisherNtfsFileIdentity(7, 22, 1);
        var identityReader = new FakePublisherInstall.SequenceFileIdentityReader(
            original,
            original,
            replacement);

        var result = fixture.CreateWuWaAdapterWithIdentityReader(identityReader)
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.InspectionFailed, result.Reason);
        Assert.Single(fixture.Metadata.ReadPaths);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Hard_linked_launcher_evidence_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var rootLauncher = fixture.PathOf("launcher.exe");
        var versionedLauncher = fixture.PathOf(@"2.6.3.0\launcher.exe");
        File.Delete(versionedLauncher);
        Assert.True(CreateHardLinkW(versionedLauncher, rootLauncher, IntPtr.Zero));

        var result = fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.InspectionFailed, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Stale_locator_is_ignored_when_one_fully_validated_candidate_remains()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var stale = Path.Combine(fixture.Root, "missing-old-root");

        var result = fixture.CreateWuWaAdapter().InspectCandidates([stale, fixture.Root]);

        Assert.Equal(fixture.Root, result.CanonicalRoot);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Launcher_only_candidate_loses_to_one_fully_validated_install()
    {
        using var partial = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var complete = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        complete.Metadata.Import(partial.Metadata);
        partial.Delete("Wuthering Waves Game");

        var result = complete.CreateWuWaAdapter().InspectCandidates([partial.Root, complete.Root]);

        Assert.Equal(complete.Root, result.CanonicalRoot);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Two_launcher_only_candidates_are_not_misreported_as_two_valid_installs()
    {
        using var first = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var second = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        first.Metadata.Import(second.Metadata);
        first.Delete("Wuthering Waves Game");
        second.Delete("Wuthering Waves Game");

        var result = first.CreateWuWaAdapter().InspectCandidates([first.Root, second.Root]);

        Assert.Equal(PublisherGameInspectionReason.GameDirectoryMissing, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Two_valid_candidates_are_ambiguous()
    {
        using var first = FakePublisherInstall.CreateWuWa();
        using var second = FakePublisherInstall.CreateWuWa();
        first.Metadata.Import(second.Metadata);

        var result = first.CreateWuWaAdapter().InspectCandidates([first.Root, second.Root]);

        Assert.Equal(PublisherGameInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Candidate_count_is_bounded_before_inspection()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var candidates = Enumerable.Range(0, 17)
            .Select(index => (string?)Path.Combine(fixture.Root, $"candidate-{index}"))
            .ToArray();

        var result = fixture.CreateWuWaAdapter().InspectCandidates(candidates);

        Assert.Equal(PublisherGameInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Empty(fixture.Metadata.ReadPaths);
    }

    [Fact]
    public void Repeated_inspection_is_read_only()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var before = fixture.Snapshot();

        fixture.CreateWuWaAdapter().Inspect(fixture.Root);

        Assert.Equal(before, fixture.Snapshot());
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
