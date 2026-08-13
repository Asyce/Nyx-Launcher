using Nyx.Desktop.Core.PublisherGames;
using Xunit.Sdk;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class EndfieldIdentityAdapterTests
{
    [Fact]
    public void Valid_identity_keeps_game_version_unavailable_and_exposes_only_maintenance()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(PublisherGameInspectionReason.VersionUnavailable, result.Reason);
        Assert.Equal(PublisherGameVersionState.Unavailable, result.VersionState);
        Assert.Null(result.Version);
        Assert.NotNull(result.MaintenanceTarget);
        Assert.False(result.AllowsDirectGameLaunch);
    }

    [Theory]
    [InlineData("Launcher.exe", PublisherGameInspectionReason.RootLauncherMissing)]
    [InlineData(@"1.5.0\Launcher.exe", PublisherGameInspectionReason.VersionedLauncherMissing)]
    [InlineData(@"1.5.0\Games.exe", PublisherGameInspectionReason.GamesExecutableMissing)]
    [InlineData(@"games\EndField Game", PublisherGameInspectionReason.GameDirectoryMissing)]
    [InlineData(@"games\EndField Game\Endfield.exe", PublisherGameInspectionReason.BootstrapMissing)]
    [InlineData(@"games\EndField Game\PlatformProcess.exe", PublisherGameInspectionReason.RuntimeMissing)]
    public void Missing_exact_evidence_fails_closed(string relativePath, PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        fixture.Delete(relativePath);

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Theory]
    [InlineData(false, FakePublisherInstall.GryphPublisher, PublisherGameInspectionReason.SignatureInvalid)]
    [InlineData(true, "Other Publisher", PublisherGameInspectionReason.PublisherMismatch)]
    public void Signature_and_publisher_are_mandatory(
        bool signatureValid,
        string publisher,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var game = fixture.PathOf(@"games\EndField Game\Endfield.exe");
        fixture.Metadata.Set(game, fixture.Metadata.Get(game) with
        {
            HasValidAuthenticodeSignature = signatureValid,
            Publisher = publisher,
        });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Same_publisher_games_executable_cannot_replace_blank_pe_game_target()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var game = fixture.PathOf(@"games\EndField Game\Endfield.exe");
        fixture.Metadata.Set(game, fixture.Metadata.Get(fixture.PathOf(@"1.5.0\Games.exe")));

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ProductIdentityMismatch, result.Reason);
    }

    [Theory]
    [InlineData("Other Product", "GRYPHLINK", "Games.exe", "Gryph Frontier Pte. Ltd.", PublisherGameInspectionReason.ProductIdentityMismatch)]
    [InlineData("GRYPHLINK", "GRYPHLINK", "Other.exe", "Gryph Frontier Pte. Ltd.", PublisherGameInspectionReason.ExecutableIdentityMismatch)]
    [InlineData("GRYPHLINK", "GRYPHLINK", "Games.exe", "Other Company", PublisherGameInspectionReason.ExecutableIdentityMismatch)]
    public void Games_companion_requires_exact_public_pe_identity(
        string product,
        string description,
        string originalFilename,
        string company,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var games = fixture.PathOf(@"1.5.0\Games.exe");
        fixture.Metadata.Set(games, fixture.Metadata.Get(games) with
        {
            ProductName = product,
            FileDescription = description,
            OriginalFilename = originalFilename,
            CompanyName = company,
        });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Versioned_launcher_must_match_root_launcher_version()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var versioned = fixture.PathOf(@"1.5.0\Launcher.exe");
        fixture.Metadata.Set(
            versioned,
            fixture.Metadata.Get(versioned) with { ProductVersion = "1.5.0.1508" });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.LauncherMismatch, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Root_and_versioned_launchers_must_be_byte_identical()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        File.WriteAllBytes(fixture.PathOf(@"1.5.0\Launcher.exe"), [3, 2, 1]);

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.LauncherMismatch, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Theory]
    [InlineData("Launcher.exe")]
    [InlineData(@"1.5.0\Launcher.exe")]
    public void Games_executable_identity_cannot_substitute_for_either_launcher(string launcherPath)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var launcher = fixture.PathOf(launcherPath);
        fixture.Metadata.Set(
            launcher,
            fixture.Metadata.Get(fixture.PathOf(@"1.5.0\Games.exe")));

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ExecutableIdentityMismatch, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Strict_four_part_product_version_selects_only_its_three_part_folder_prefix()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.NotNull(result.MaintenanceTarget);
        Assert.Contains(
            fixture.Metadata.ReadPaths,
            path => string.Equals(
                path,
                fixture.PathOf(@"1.5.0\Launcher.exe"),
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            fixture.Metadata.ReadPaths,
            path => path.Contains("1.5.0.1507", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("1.5.0.01507")]
    [InlineData("1.5.0.1507.1")]
    [InlineData("1.5.0.-1")]
    public void Launcher_product_version_must_remain_strictly_four_part(string version)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var launcher = fixture.PathOf("Launcher.exe");
        fixture.Metadata.Set(launcher, fixture.Metadata.Get(launcher) with { ProductVersion = version });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.LauncherVersionInvalid, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Obsolete_four_part_version_folder_cannot_replace_the_exact_three_part_folder()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        Directory.Move(fixture.PathOf("1.5.0"), fixture.PathOf("1.5.0.1507"));

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.VersionedLauncherMissing, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Stale_and_prefix_collision_folders_are_never_selected_over_exact_prefix()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        foreach (var folder in new[] { "1.4.9", "1.5.0.1507", "1.5.00", "1.5.0-old" })
        {
            var directory = Directory.CreateDirectory(fixture.PathOf(folder)).FullName;
            File.WriteAllBytes(Path.Combine(directory, "Launcher.exe"), [91, 92]);
            File.WriteAllBytes(Path.Combine(directory, "Games.exe"), [93, 94]);
        }

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.NotNull(result.MaintenanceTarget);
        Assert.DoesNotContain(
            fixture.Metadata.ReadPaths,
            path => path.Contains("1.4.9", StringComparison.OrdinalIgnoreCase)
                || path.Contains("1.5.0.1507", StringComparison.OrdinalIgnoreCase)
                || path.Contains("1.5.00", StringComparison.OrdinalIgnoreCase)
                || path.Contains("1.5.0-old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Games_companion_product_version_must_exactly_match_full_launcher_version()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var games = fixture.PathOf(@"1.5.0\Games.exe");
        fixture.Metadata.Set(
            games,
            fixture.Metadata.Get(games) with { ProductVersion = "1.5.0.1508" });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.LauncherMismatch, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Theory]
    [InlineData(DriveType.Network, "NTFS", PublisherGameInspectionReason.DriveIsNotLocalFixed)]
    [InlineData(DriveType.Fixed, "ReFS", PublisherGameInspectionReason.FileSystemIsNotNtfs)]
    public void Unsafe_volume_is_rejected_before_metadata(
        DriveType driveType,
        string fileSystem,
        PublisherGameInspectionReason expected)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter(driveType, fileSystem).Inspect(fixture.Root);

        Assert.Equal(expected, result.Reason);
        Assert.Empty(fixture.Metadata.ReadPaths);
    }

    [Theory]
    [InlineData("relative\\endfield")]
    [InlineData(@"\\server\share\endfield")]
    [InlineData(@"\\?\C:\endfield")]
    public void Unsafe_path_is_rejected(string path)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter().Inspect(path);

        Assert.Equal(PublisherGameInspectionReason.PathIsNotLocalAndCanonical, result.Reason);
    }

    [Fact]
    public void Game_target_metadata_drift_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var platform = fixture.PathOf(@"games\EndField Game\PlatformProcess.exe");
        var good = fixture.Metadata.Get(platform);
        fixture.Metadata.Set(platform, good, good with { CompanyName = "Changed" });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Launcher_metadata_drift_never_survives_as_maintenance_proof()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var launcher = fixture.PathOf("Launcher.exe");
        var good = fixture.Metadata.Get(launcher);
        fixture.Metadata.Set(launcher, good, good, good with { Publisher = "Changed Publisher" });

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Root_volume_drift_never_survives_as_maintenance_proof()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapterWithFileSystems("NTFS", "NTFS", "ReFS")
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Protected_launcher_handle_blocks_same_length_same_time_swap_and_replay()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var launcher = fixture.PathOf("Launcher.exe");
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
                File.WriteAllBytes(launcher, [3, 2, 1]);
                File.SetLastWriteTimeUtc(launcher, timestamp);
            }
            catch (IOException)
            {
                swapWasBlocked = true;
            }
        };

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.True(swapWasBlocked);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Stale_locator_is_ignored_when_one_fully_validated_candidate_remains()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter().InspectCandidates(
            [Path.Combine(fixture.Root, "missing-old-root"), fixture.Root]);

        Assert.Equal(fixture.Root, result.CanonicalRoot);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Launcher_only_candidate_loses_to_one_fully_validated_install()
    {
        using var partial = FakePublisherInstall.CreateEndfield();
        using var complete = FakePublisherInstall.CreateEndfield();
        complete.Metadata.Import(partial.Metadata);
        partial.Delete(@"games\EndField Game");

        var result = complete.CreateEndfieldAdapter().InspectCandidates([partial.Root, complete.Root]);

        Assert.Equal(complete.Root, result.CanonicalRoot);
        Assert.NotNull(result.MaintenanceTarget);
    }

    [Fact]
    public void Two_launcher_only_candidates_are_not_misreported_as_two_valid_installs()
    {
        using var first = FakePublisherInstall.CreateEndfield();
        using var second = FakePublisherInstall.CreateEndfield();
        first.Metadata.Import(second.Metadata);
        first.Delete(@"games\EndField Game");
        second.Delete(@"games\EndField Game");

        var result = first.CreateEndfieldAdapter().InspectCandidates([first.Root, second.Root]);

        Assert.Equal(PublisherGameInspectionReason.GameDirectoryMissing, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Stale_locator_alone_never_becomes_a_target()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var result = fixture.CreateEndfieldAdapter().InspectCandidates(
            [Path.Combine(fixture.Root, "missing-old-root")]);

        Assert.Equal(PublisherGameInspectionStatus.NotFound, result.Status);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Two_valid_candidates_are_ambiguous()
    {
        using var first = FakePublisherInstall.CreateEndfield();
        using var second = FakePublisherInstall.CreateEndfield();
        first.Metadata.Import(second.Metadata);

        var result = first.CreateEndfieldAdapter().InspectCandidates([first.Root, second.Root]);

        Assert.Equal(PublisherGameInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Unrelated_ace_files_are_never_read_as_targets()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var ace = fixture.PathOf(@"games\EndField Game\AntiCheatExpert\SGuard64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ace)!);
        File.WriteAllBytes(ace, [22, 23]);

        fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.DoesNotContain(
            fixture.Metadata.ReadPaths,
            path => path.Contains("AntiCheatExpert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reparse_component_in_game_tree_is_deterministically_rejected()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var gameRoot = fixture.PathOf(@"games\EndField Game");

        var result = fixture.CreateEndfieldAdapterWithReparsePoints(gameRoot)
            .Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
        Assert.False(result.HasFullInstallMaintenanceProof);
        Assert.Throws<ArgumentNullException>(
            () => OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget!));
    }

    [Fact]
    public void Companion_becoming_reparse_at_entry_open_is_rejected()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var companion = fixture.PathOf(@"1.5.0\Games.exe");
        var entryOpener = new FakePublisherInstall.ReparseRejectingEntryOpener(companion);

        var result = fixture.CreateEndfieldAdapterWithEntryOpener(entryOpener)
            .Inspect(fixture.Root);

        Assert.True(entryOpener.RejectionReached);
        Assert.Equal(
            [
                fixture.PathOf("Launcher.exe"),
                fixture.PathOf(@"1.5.0\Launcher.exe"),
                companion,
            ],
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
        using var fixture = FakePublisherInstall.CreateEndfield();
        var link = fixture.PathOf(@"games\EndField Game");
        var target = fixture.PathOf(@"games\moved-endfield-game");
        Directory.Move(link, target);
        if (!TryCreateDirectoryLink(link, target))
        {
            Directory.Move(target, link);
            throw SkipException.ForSkip(
                "Windows did not grant symlink creation; deterministic seam coverage still runs.");
        }

        var result = fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(PublisherGameInspectionReason.ReparsePointFound, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Repeated_inspection_is_read_only()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var before = fixture.Snapshot();

        fixture.CreateEndfieldAdapter().Inspect(fixture.Root);

        Assert.Equal(before, fixture.Snapshot());
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }
}
