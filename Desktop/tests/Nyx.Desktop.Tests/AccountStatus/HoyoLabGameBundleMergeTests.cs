using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGameBundleMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oldest = Now.AddHours(-3);
    private static readonly DateTimeOffset Older = Now.AddHours(-2);
    private static readonly DateTimeOffset Newer = Now.AddHours(-1);

    [Fact]
    public void Local_newer_observation_wins_idempotently()
    {
        var localRole = Role(1, resourcesAt: Newer, current: 200);
        var remoteRole = Role(1, resourcesAt: Older, current: 100);

        var result = MergeValid(
            Bundle([localRole], localRole.Role.Binding, Consents(resources: true)),
            Bundle([remoteRole], remoteRole.Role.Binding, Consents(resources: true)));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, result.Outcome);
        var merged = Assert.IsType<HoyoLabGameBundle>(result.Bundle);
        Assert.Equal(Newer, merged.Roles[0].Observations.Resources);
        Assert.Equal(200, merged.Roles[0].Resource!.Current);
    }

    [Fact]
    public void Remote_newer_supported_observations_are_merged()
    {
        var localRole = Role(
            1,
            resourcesAt: Older,
            current: 100,
            achievementsAt: Older,
            achievements: [1]);
        var remoteRole = Role(
            1,
            resourcesAt: Newer,
            current: 200,
            achievementsAt: Newer,
            achievements: [1, 7]);
        var consents = Consents(resources: true, achievements: true);

        var result = MergeValid(
            Bundle([localRole], localRole.Role.Binding, consents),
            Bundle([remoteRole], remoteRole.Role.Binding, consents));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal(Newer, mergedRole.Observations.Resources);
        Assert.Equal(200, mergedRole.Resource!.Current);
        Assert.Equal(Newer, mergedRole.Observations.Achievements);
        Assert.Equal([1, 7], mergedRole.CompletedHsrAchievementIds);
    }

    [Fact]
    public void Genshin_builds_merge_latest_values_semantically_and_block_role_tombstones()
    {
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = HoyoLabGenshinBuildSnapshotTests.Snapshot();
        var semanticallyEqual = HoyoLabGenshinBuildSnapshotTests.Snapshot(
            HoyoLabGenshinBuildSnapshotTests.CharactersJson.Replace(
                "\n", " ", StringComparison.Ordinal));
        var localRole = GenshinRole(binding, Older, snapshot);
        var remoteRole = GenshinRole(binding, Newer, semanticallyEqual);
        var consents = Consents(builds: true);

        var result = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle([remoteRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal(Newer, mergedRole.Observations.Builds);
        Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(semanticallyEqual, mergedRole.GenshinBuilds));

        AssertConflict(MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [GenshinRole(
                    binding,
                    Older,
                    HoyoLabGenshinBuildSnapshotTests.Snapshot(
                        HoyoLabGenshinBuildSnapshotTests.CharactersJson.Replace(
                            "\"level\":95",
                            "\"level\":96",
                            StringComparison.Ordinal)))],
                binding,
                consents,
                gameId: HoyoLabGameBundleRules.GenshinGameId)));

        var olderTombstone = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [],
                null,
                consents,
                roleTombstones: [new(binding, Oldest)],
                gameId: HoyoLabGameBundleRules.GenshinGameId));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, olderTombstone.Outcome);
        Assert.Contains(olderTombstone.Bundle!.Roles, role => role.Role.Binding == binding);
    }

    [Fact]
    public void Genshin_exploration_merge_is_semantic_and_newer_than_role_or_capability_deletes()
    {
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = Exploration();
        var semanticallyEqual = Exploration(
            HoyoLabGenshinExplorationSnapshotTests.DataJson.Replace(
                "\"percentage\":1234", "\"percentage\":1234.0", StringComparison.Ordinal));
        var localRole = GenshinExplorationRole(binding, Older, snapshot);
        var remoteRole = GenshinExplorationRole(binding, Newer, semanticallyEqual);
        var consents = Consents(exploration: true);

        var result = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle([remoteRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal(Newer, mergedRole.Observations.Exploration);
        Assert.True(HoyoLabGenshinExplorationRules.ValuesEqual(semanticallyEqual, mergedRole.GenshinExploration));

        AssertConflict(MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [GenshinExplorationRole(
                    binding,
                    Older,
                    Exploration(HoyoLabGenshinExplorationSnapshotTests.DataJson.Replace(
                        "\"percentage\":1234", "\"percentage\":1235", StringComparison.Ordinal)))],
                binding,
                consents,
                gameId: HoyoLabGameBundleRules.GenshinGameId)));

        var cleared = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [GenshinExplorationRole(binding)],
                binding,
                consents,
                capabilityTombstones:
                [
                    new(binding, HoyoLabGameBundleRules.Exploration, Newer),
                ],
                gameId: HoyoLabGameBundleRules.GenshinGameId));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, cleared.Outcome);
        var clearedBundle = Assert.IsType<HoyoLabGameBundle>(cleared.Bundle);
        var clearedRole = Assert.Single(clearedBundle.Roles);
        Assert.Null(clearedRole.Observations.Exploration);
        Assert.Null(clearedRole.GenshinExploration);
        Assert.Equal(Newer, Assert.Single(clearedBundle.CapabilityTombstones).DeletedAt);

        var olderRoleDelete = MergeValid(
            Bundle([remoteRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [],
                null,
                consents,
                roleTombstones: [new(binding, Older)],
                gameId: HoyoLabGameBundleRules.GenshinGameId));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, olderRoleDelete.Outcome);
        Assert.Contains(olderRoleDelete.Bundle!.Roles, role => role.Role.Binding == binding);
        Assert.Empty(olderRoleDelete.Bundle.RoleTombstones);
    }

    [Fact]
    public void Genshin_events_merge_is_semantic_and_newer_than_role_or_capability_deletes()
    {
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = Events();
        var semanticallyEqual = Events(
            HoyoLabGenshinEventsSnapshotTests.DataJson.Replace(
                "\"percentage\":1234.5", "\"percentage\":1234.50", StringComparison.Ordinal));
        var localRole = GenshinEventsRole(binding, Older, snapshot);
        var remoteRole = GenshinEventsRole(binding, Newer, semanticallyEqual);
        var consents = Consents(events: true);

        var result = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle([remoteRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal(Newer, mergedRole.Observations.Events);
        Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(semanticallyEqual, mergedRole.GenshinEvents));

        AssertConflict(MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [GenshinEventsRole(
                    binding,
                    Older,
                    Events(HoyoLabGenshinEventsSnapshotTests.DataJson.Replace(
                        "\"percentage\":1234.5", "\"percentage\":1235.5", StringComparison.Ordinal)))],
                binding,
                consents,
                gameId: HoyoLabGameBundleRules.GenshinGameId)));

        var cleared = MergeValid(
            Bundle([localRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [GenshinEventsRole(binding)],
                binding,
                consents,
                capabilityTombstones:
                [
                    new(binding, HoyoLabGameBundleRules.Events, Newer),
                ],
                gameId: HoyoLabGameBundleRules.GenshinGameId));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, cleared.Outcome);
        var clearedBundle = Assert.IsType<HoyoLabGameBundle>(cleared.Bundle);
        var clearedRole = Assert.Single(clearedBundle.Roles);
        Assert.Null(clearedRole.Observations.Events);
        Assert.Null(clearedRole.GenshinEvents);
        Assert.Equal(Newer, Assert.Single(clearedBundle.CapabilityTombstones).DeletedAt);

        var olderRoleDelete = MergeValid(
            Bundle([remoteRole], binding, consents, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Bundle(
                [],
                null,
                consents,
                roleTombstones: [new(binding, Older)],
                gameId: HoyoLabGameBundleRules.GenshinGameId));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, olderRoleDelete.Outcome);
        Assert.Contains(olderRoleDelete.Bundle!.Roles, role => role.Role.Binding == binding);
        Assert.Empty(olderRoleDelete.Bundle.RoleTombstones);
    }

    [Fact]
    public void Hsr_builds_merge_latest_values_semantically_reject_equal_conflicts_and_apply_newer_tombstones()
    {
        var binding = Binding(123456789);
        var snapshot = HsrSnapshot();
        var semanticallyEqual = HsrSnapshot(
            HsrBuildCharactersJson.Replace(
                "\"level\":80",
                "\"level\":8.0e1",
                StringComparison.Ordinal));
        var localRole = HsrRole(binding, Older, snapshot);
        var remoteRole = HsrRole(binding, Newer, semanticallyEqual);
        var consents = Consents(builds: true);

        var result = MergeValid(
            Bundle([localRole], binding, consents),
            Bundle([remoteRole], binding, consents));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal(Newer, mergedRole.Observations.Builds);
        Assert.True(HoyoLabHsrBuildRules.ValuesEqual(semanticallyEqual, mergedRole.HsrBuilds));

        AssertConflict(MergeValid(
            Bundle([localRole], binding, consents),
            Bundle(
                [HsrRole(
                    binding,
                    Older,
                    HsrSnapshot(81))],
                binding,
                consents)));

        var cleared = MergeValid(
            Bundle([localRole], binding, consents),
            Bundle(
                [HsrRole(binding)],
                binding,
                consents,
                capabilityTombstones:
                [
                    new(binding, HoyoLabGameBundleRules.Builds, Newer),
                ]));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, cleared.Outcome);
        var clearedBundle = Assert.IsType<HoyoLabGameBundle>(cleared.Bundle);
        var clearedRole = Assert.Single(clearedBundle.Roles);
        Assert.Null(clearedRole.Observations.Builds);
        Assert.Null(clearedRole.HsrBuilds);
        Assert.Equal(Newer, Assert.Single(clearedBundle.CapabilityTombstones).DeletedAt);
    }

    [Fact]
    public void Equal_observations_are_idempotent_only_when_values_match()
    {
        var localRole = Role(
            1,
            resourcesAt: Newer,
            current: 200,
            achievementsAt: Newer,
            achievements: [1, 7]);
        var identicalRole = Role(
            1,
            resourcesAt: Newer,
            current: 200,
            achievementsAt: Newer,
            achievements: [1, 7]);
        var consents = Consents(resources: true, achievements: true);
        var local = Bundle([localRole], localRole.Role.Binding, consents);

        var identical = MergeValid(
            local,
            Bundle([identicalRole], identicalRole.Role.Binding, consents));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, identical.Outcome);
        Assert.NotNull(identical.Bundle);

        AssertConflict(MergeValid(
            local,
            Bundle(
                [Role(1, resourcesAt: Newer, current: 201, achievementsAt: Newer, achievements: [1, 7])],
                localRole.Role.Binding,
                consents)));
        AssertConflict(MergeValid(
            local,
            Bundle(
                [Role(1, resourcesAt: Newer, current: 200, achievementsAt: Newer, achievements: [1, 8])],
                localRole.Role.Binding,
                consents)));
    }

    [Fact]
    public void Capability_tombstone_wins_only_when_strictly_newer()
    {
        var localRole = Role(1, resourcesAt: Older, current: 200);
        var remoteRole = Role(1);
        var local = Bundle(
            [localRole],
            localRole.Role.Binding,
            Consents(resources: true));

        var deleted = MergeValid(
            local,
            Bundle(
                [remoteRole],
                remoteRole.Role.Binding,
                Consents(resources: true),
                capabilityTombstones:
                [
                    new(
                        remoteRole.Role.Binding,
                        HoyoLabGameBundleRules.Resources,
                        Newer),
                ]));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, deleted.Outcome);
        var deletedBundle = Assert.IsType<HoyoLabGameBundle>(deleted.Bundle);
        var deletedRole = Assert.Single(deletedBundle.Roles);
        Assert.Null(deletedRole.Observations.Resources);
        Assert.Null(deletedRole.Resource);
        Assert.Equal(Newer, Assert.Single(deletedBundle.CapabilityTombstones).DeletedAt);

        AssertConflict(MergeValid(
            local,
            Bundle(
                [remoteRole],
                remoteRole.Role.Binding,
                Consents(resources: true),
                capabilityTombstones:
                [
                    new(
                        remoteRole.Role.Binding,
                        HoyoLabGameBundleRules.Resources,
                        Older),
                ])));

        var observationWins = MergeValid(
            local,
            Bundle(
                [remoteRole],
                remoteRole.Role.Binding,
                Consents(resources: true),
                capabilityTombstones:
                [
                    new(
                        remoteRole.Role.Binding,
                        HoyoLabGameBundleRules.Resources,
                        Oldest),
                ]));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, observationWins.Outcome);
        Assert.Empty(observationWins.Bundle!.CapabilityTombstones);
        Assert.Equal(200, observationWins.Bundle.Roles[0].Resource!.Current);
    }

    [Fact]
    public void Role_tombstone_compares_with_the_newest_supported_role_observation()
    {
        var retained = Role(1);
        var target = Role(
            2,
            resourcesAt: Oldest,
            current: 200,
            achievementsAt: Newer,
            achievements: [1, 7]);
        var consents = Consents(resources: true, achievements: true);
        var local = Bundle([retained, target], retained.Role.Binding, consents);

        var olderTombstone = MergeValid(
            local,
            DeletedRoleBundle(retained, target.Role.Binding, Older, consents));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, olderTombstone.Outcome);
        Assert.Contains(olderTombstone.Bundle!.Roles, role => role.Role.Binding == target.Role.Binding);
        Assert.Empty(olderTombstone.Bundle.RoleTombstones);

        AssertConflict(MergeValid(
            local,
            DeletedRoleBundle(retained, target.Role.Binding, Newer, consents)));

        var deleted = MergeValid(
            local,
            DeletedRoleBundle(retained, target.Role.Binding, Now, consents));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, deleted.Outcome);
        var deletedBundle = Assert.IsType<HoyoLabGameBundle>(deleted.Bundle);
        Assert.DoesNotContain(deletedBundle.Roles, role => role.Role.Binding == target.Role.Binding);
        Assert.Equal(target.Role.Binding, Assert.Single(deletedBundle.RoleTombstones).Binding);
        Assert.Equal(retained.Role.Binding, deletedBundle.SelectedRole);
    }

    [Fact]
    public void Same_binding_metadata_disagreement_keeps_local_label_and_merges_newer_observations()
    {
        var localRole = Role(1, resourcesAt: Older, current: 100, nickname: "Local");
        var remoteRole = Role(1, resourcesAt: Newer, current: 200, nickname: "Remote");
        var consents = Consents(resources: true);

        var result = MergeValid(
            Bundle([localRole], localRole.Role.Binding, consents),
            Bundle([remoteRole], remoteRole.Role.Binding, consents));

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var mergedRole = Assert.Single(Assert.IsType<HoyoLabGameBundle>(result.Bundle).Roles);
        Assert.Equal("Local", mergedRole.Role.Nickname);
        Assert.Equal(Newer, mergedRole.Observations.Resources);
        Assert.Equal(200, mergedRole.Resource!.Current);
    }

    [Fact]
    public void Remote_selected_role_deletion_selects_survivor_and_clearing_all_roles_clears_selection()
    {
        var selected = Role(1, resourcesAt: Older, current: 100);
        var survivor = Role(2);
        var consents = Consents(resources: true);
        var local = Bundle([selected, survivor], selected.Role.Binding, consents);

        var withSurvivor = MergeValid(
            local,
            Bundle(
                [survivor],
                survivor.Role.Binding,
                consents,
                roleTombstones: [new(selected.Role.Binding, Newer)]));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, withSurvivor.Outcome);
        Assert.Equal(
            survivor.Role.Binding,
            Assert.Single(Assert.IsType<HoyoLabGameBundle>(withSurvivor.Bundle).Roles).Role.Binding);
        Assert.Equal(survivor.Role.Binding, withSurvivor.Bundle.SelectedRole);

        var withoutSurvivor = MergeValid(
            Bundle([selected], selected.Role.Binding, consents),
            Bundle(
                [],
                null,
                consents,
                roleTombstones: [new(selected.Role.Binding, Newer)]));
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, withoutSurvivor.Outcome);
        Assert.Empty(Assert.IsType<HoyoLabGameBundle>(withoutSurvivor.Bundle).Roles);
        Assert.Null(withoutSurvivor.Bundle.SelectedRole);
    }

    [Fact]
    public void Mixed_game_bundles_are_rejected_before_merge()
    {
        var hsrRole = Role(1);
        var genshinBinding = new PublisherRoleBinding("123456789", "os_euro");
        var genshinRole = new HoyoLabGameBundleRole(
            new(
                genshinBinding,
                "Genshin",
                PublisherRoleRecordRules.CanonicalRegionLabel(genshinBinding.Server)),
            new(null, null, null, null, null, null, null, null),
            null,
            null);

        AssertConflict(HoyoLabGameBundleMerge.Merge(
            Bundle([hsrRole], hsrRole.Role.Binding),
            Bundle([genshinRole], genshinBinding, gameId: HoyoLabGameBundleRules.GenshinGameId),
            Now));
    }

    [Fact]
    public void Local_selection_and_consents_are_preserved_with_deterministic_order()
    {
        var europe = Role(1);
        var america = Role(2, server: "prod_official_usa");
        var asia = Role(3, server: "prod_official_asia");
        var deletedFour = Binding(4);
        var deletedFive = Binding(5);
        var localConsents = Consents(resources: true);
        var local = Bundle(
            [america, europe],
            america.Role.Binding,
            localConsents,
            capabilityTombstones:
            [
                new(
                    america.Role.Binding,
                    HoyoLabGameBundleRules.Achievements,
                    Older),
            ],
            roleTombstones: [new(deletedFive, Older)]);
        var remote = Bundle(
            [europe, asia, america],
            europe.Role.Binding,
            Consents(achievements: true),
            capabilityTombstones:
            [
                new(
                    europe.Role.Binding,
                    HoyoLabGameBundleRules.Resources,
                    Older),
            ],
            roleTombstones: [new(deletedFour, Older)]);

        var result = MergeValid(local, remote);

        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, result.Outcome);
        var merged = Assert.IsType<HoyoLabGameBundle>(result.Bundle);
        Assert.Equal(america.Role.Binding, merged.SelectedRole);
        Assert.Equal(localConsents, merged.Consents);
        Assert.Equal(
            ["prod_official_asia", "prod_official_eur", "prod_official_usa"],
            merged.Roles.Select(role => role.Role.Binding.Server));
        Assert.Equal(
            [europe.Role.Binding, america.Role.Binding],
            merged.CapabilityTombstones.Select(tombstone => tombstone.Binding));
        Assert.Equal(
            [deletedFour, deletedFive],
            merged.RoleTombstones.Select(tombstone => tombstone.Binding));

        var repeated = MergeValid(merged, remote);
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Idempotent, repeated.Outcome);
        Assert.Equal(
            merged.Roles.Select(role => role.Role.Binding),
            repeated.Bundle!.Roles.Select(role => role.Role.Binding));
    }

    [Fact]
    public void Invalid_inputs_and_merged_bounds_fail_closed_without_a_bundle()
    {
        var validRole = Role(1);
        var valid = Bundle([validRole], validRole.Role.Binding);
        AssertConflict(HoyoLabGameBundleMerge.Merge(
            valid with { SchemaVersion = HoyoLabGameBundleRules.SchemaVersion + 1 },
            valid,
            Now));

        var localRoles = Enumerable.Range(1, HoyoLabGameBundleRules.MaximumRoles)
            .Select(static index => Role(index))
            .ToArray();
        var remoteRoles = Enumerable.Range(2, HoyoLabGameBundleRules.MaximumRoles)
            .Select(static index => Role(index))
            .ToArray();
        AssertConflict(MergeValid(
            Bundle(localRoles, localRoles[0].Role.Binding),
            Bundle(remoteRoles, remoteRoles[0].Role.Binding)));

        var localRoleTombstones = Enumerable.Range(1, HoyoLabGameBundleRules.MaximumRoleTombstones)
            .Select(index => new HoyoLabRoleTombstone(Binding(index), Older))
            .ToArray();
        var remoteRoleTombstones = Enumerable.Range(2, HoyoLabGameBundleRules.MaximumRoleTombstones)
            .Select(index => new HoyoLabRoleTombstone(Binding(index), Older))
            .ToArray();
        AssertConflict(MergeValid(
            Bundle([], null, roleTombstones: localRoleTombstones),
            Bundle([], null, roleTombstones: remoteRoleTombstones)));

        var backingRoleTombstones = Enumerable.Range(1, 9)
            .Select(index => new HoyoLabRoleTombstone(Binding(index), Older))
            .ToArray();
        var allCapabilityTombstones = backingRoleTombstones
            .SelectMany(role => HoyoLabGameBundleRules.Capabilities.Select(capability =>
                new HoyoLabCapabilityTombstone(role.Binding, capability, Older)))
            .OrderBy(tombstone => tombstone.DeletedAt)
            .ThenBy(tombstone => tombstone.Binding.Server, StringComparer.Ordinal)
            .ThenBy(tombstone => tombstone.Binding.RoleId, StringComparer.Ordinal)
            .ThenBy(tombstone => tombstone.Capability, StringComparer.Ordinal)
            .ToArray();
        AssertConflict(MergeValid(
            Bundle(
                [],
                null,
                capabilityTombstones: allCapabilityTombstones[..HoyoLabGameBundleRules.MaximumCapabilityTombstones],
                roleTombstones: backingRoleTombstones),
            Bundle(
                [],
                null,
                capabilityTombstones: allCapabilityTombstones[^HoyoLabGameBundleRules.MaximumCapabilityTombstones..],
                roleTombstones: backingRoleTombstones)));
    }

    private static HoyoLabGameBundleMergeResult MergeValid(
        HoyoLabGameBundle local,
        HoyoLabGameBundle remote)
    {
        Assert.True(HoyoLabGameBundleRules.IsValid(local, Now));
        Assert.True(HoyoLabGameBundleRules.IsValid(remote, Now));
        return HoyoLabGameBundleMerge.Merge(local, remote, Now);
    }

    private static void AssertConflict(HoyoLabGameBundleMergeResult result)
    {
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Conflict, result.Outcome);
        Assert.Null(result.Bundle);
    }

    private static HoyoLabGameBundle DeletedRoleBundle(
        HoyoLabGameBundleRole retained,
        PublisherRoleBinding deleted,
        DateTimeOffset deletedAt,
        HoyoLabCapabilityConsentSet consents) => Bundle(
            [retained],
            retained.Role.Binding,
            consents,
            roleTombstones: [new(deleted, deletedAt)]);

    private static HoyoLabGameBundle Bundle(
        IReadOnlyList<HoyoLabGameBundleRole> roles,
        PublisherRoleBinding? selected,
        HoyoLabCapabilityConsentSet? consents = null,
        IReadOnlyList<HoyoLabCapabilityTombstone>? capabilityTombstones = null,
        IReadOnlyList<HoyoLabRoleTombstone>? roleTombstones = null,
        string gameId = HoyoLabGameBundleRules.GameId) => new(
            HoyoLabGameBundleRules.SchemaVersion,
            gameId,
            roles,
            selected,
            consents ?? Consents(),
            capabilityTombstones ?? Array.Empty<HoyoLabCapabilityTombstone>(),
            roleTombstones ?? Array.Empty<HoyoLabRoleTombstone>());

    private static HoyoLabGameBundleRole Role(
        int index,
        DateTimeOffset? resourcesAt = null,
        int current = 100,
        DateTimeOffset? achievementsAt = null,
        IReadOnlyList<long>? achievements = null,
        string? nickname = null,
        string server = "prod_official_eur") => new(
            new(
                Binding(index, server),
                nickname,
                PublisherRoleRecordRules.CanonicalRegionLabel(server)),
            new(
                resourcesAt,
                null,
                null,
                achievementsAt,
                null,
                null,
                null,
                null),
            resourcesAt is null
                ? null
                : new(
                    HoyoLabGameBundleRules.GameId,
                    "Trailblaze Power",
                    current,
                    300,
                    resourcesAt.Value,
                    RecoverySeconds: 120,
                    Reserve: 20),
            achievementsAt is null ? null : achievements?.ToArray() ?? []);

    private static HoyoLabGameBundleRole GenshinRole(
        PublisherRoleBinding binding,
        DateTimeOffset buildsAt,
        HoyoLabGenshinBuildSnapshot builds) => new(
        new(
            binding,
            "Genshin",
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server)),
        new(null, null, buildsAt, null, null, null, null, null),
        null,
        null,
        builds);

    private static HoyoLabGameBundleRole GenshinExplorationRole(
        PublisherRoleBinding binding,
        DateTimeOffset? explorationAt = null,
        HoyoLabGenshinExplorationSnapshot? exploration = null) => new(
        new(
            binding,
            "Genshin",
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server)),
        new(null, null, null, null, explorationAt, null, null, null),
        null,
        null,
        null,
        null,
        exploration);

    private static HoyoLabGameBundleRole GenshinEventsRole(
        PublisherRoleBinding binding,
        DateTimeOffset? eventsAt = null,
        HoyoLabGenshinEventsSnapshot? events = null) => new(
        new(
            binding,
            "Genshin",
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server)),
        new(null, null, null, null, null, null, eventsAt, null),
        null,
        null,
        null,
        null,
        null,
        events);

    private static HoyoLabGameBundleRole HsrRole(
        PublisherRoleBinding binding,
        DateTimeOffset? buildsAt = null,
        HoyoLabHsrBuildSnapshot? builds = null) => new(
        new(
            binding,
            "Honkai: Star Rail",
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server)),
        new(null, null, buildsAt, null, null, null, null, null),
        null,
        null,
        null,
        builds);

    private static PublisherRoleBinding Binding(
        int index,
        string server = "prod_official_eur") => new(index.ToString("D20"), server);

    private static HoyoLabCapabilityConsentSet Consents(
        bool resources = false,
        bool achievements = false,
        bool builds = false,
        bool exploration = false,
        bool events = false) => new(
            resources,
            Inventory: false,
            Builds: builds,
            achievements,
            Exploration: exploration,
            Endgame: false,
            Events: events,
            Currency: false);

    private static HoyoLabGenshinExplorationSnapshot Exploration(
        string json = HoyoLabGenshinExplorationSnapshotTests.DataJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private static HoyoLabGenshinEventsSnapshot Events(
        string json = HoyoLabGenshinEventsSnapshotTests.DataJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private const string HsrBuildCharactersJson = """
        [{"id":1001,"name":"Synthetic Trailblazer","level":80,"rarity":5,"element":"Quantum","path":3,"rank":1,"enhancedId":1001001,"avatarType":"Girl","lightCone":null,"eidolons":[],"relics":[],"ornaments":[],"properties":[],"traces":[],"specialTraces":[],"memosprite":null}]
        """;

    private static HoyoLabHsrBuildSnapshot HsrSnapshot(int level = 80) => HsrSnapshot(
        HsrBuildCharactersJson.Replace(
            "\"level\":80",
            $"\"level\":{level}",
            StringComparison.Ordinal));

    private static HoyoLabHsrBuildSnapshot HsrSnapshot(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }
}
