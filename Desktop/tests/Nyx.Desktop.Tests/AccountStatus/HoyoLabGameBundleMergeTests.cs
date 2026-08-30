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
    public void Same_binding_metadata_disagreement_is_a_conflict()
    {
        var localRole = Role(1, nickname: "Local");
        var remoteRole = Role(1, nickname: "Remote");

        AssertConflict(MergeValid(
            Bundle([localRole], localRole.Role.Binding),
            Bundle([remoteRole], remoteRole.Role.Binding)));
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
        IReadOnlyList<HoyoLabRoleTombstone>? roleTombstones = null) => new(
            HoyoLabGameBundleRules.SchemaVersion,
            HoyoLabGameBundleRules.GameId,
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

    private static PublisherRoleBinding Binding(
        int index,
        string server = "prod_official_eur") => new(index.ToString("D20"), server);

    private static HoyoLabCapabilityConsentSet Consents(
        bool resources = false,
        bool achievements = false) => new(
            resources,
            Inventory: false,
            Builds: false,
            achievements,
            Exploration: false,
            Endgame: false,
            Events: false,
            Currency: false);
}
