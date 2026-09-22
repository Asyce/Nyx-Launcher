using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabZzzBundleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly PublisherRoleBinding Binding = new("123456789", "prod_gf_eu");

    internal static HoyoLabZzzBuildSnapshot Builds() => new(Fixture("builds"));
    internal static HoyoLabZzzEndgameSnapshot Endgame() => new(Fixture("endgame"));

    private static JsonElement Fixture(string capability)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", $"hoyolab-zzz-{capability}-v1.fixture.json")));
        return document.RootElement.GetProperty("snapshot").Clone();
    }

    internal static HoyoLabGameBundle Bundle(DateTimeOffset observedAt) => new(2, "zzz",
        [new(new(Binding, "Synthetic Proxy", "Europe"),
            new(null, null, observedAt, null, null, observedAt, null, null), null, null,
            ZzzBuilds: Builds(), ZzzEndgame: Endgame())],
        Binding, new(false, false, true, false, false, true, false, false), [], []);

    [Fact]
    public void Both_payloads_round_trip_without_losing_source_precision_or_null_equipment()
    {
        var bundle = Bundle(Now);
        Assert.True(HoyoLabGameBundleRules.IsValid(bundle, Now));
        Assert.True(HoyoLabGameBundleStore.TryParseBundle(HoyoLabGameBundleStore.SerializeBundle(bundle), Now, out var parsed));
        Assert.Equal("zzz", parsed!.GameId);
        var role = Assert.Single(parsed.Roles);
        Assert.True(HoyoLabZzzBuildRules.ValuesEqual(Builds(), role.ZzzBuilds));
        Assert.True(HoyoLabZzzEndgameRules.ValuesEqual(Endgame(), role.ZzzEndgame));
        Assert.Null(role.HsrBuilds);
        Assert.Null(role.GenshinBuilds);
        Assert.Equal(nameof(HoyoLabGameBundleRole), role.ToString());
    }

    [Fact]
    public void Wrong_game_disabled_consent_unsupported_capabilities_and_missing_observations_fail_closed()
    {
        var bundle = Bundle(Now);
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { GameId = "hsr" }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Consents = bundle.Consents with { Builds = false } }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Consents = bundle.Consents with { Endgame = false } }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Consents = bundle.Consents with { Events = true } }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with
        {
            Roles = [bundle.Roles[0] with
        {
            Observations = bundle.Roles[0].Observations with { Builds = null },
        }]
        }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { CapabilityTombstones = [new(Binding, "endgame", Now)] }, Now));
        foreach (var capability in new[] { "achievements", "events", "exploration", "inventory", "currency" })
            Assert.False(HoyoLabGameBundleRules.SupportsLocalCapability("zzz", capability));
    }

    [Fact]
    public void Merge_keeps_newer_complete_records_rejects_equal_time_changes_and_obeys_each_deletion_barrier()
    {
        var older = Bundle(Now.AddHours(-1));
        var newer = Bundle(Now);
        var merge = HoyoLabGameBundleMerge.Merge(older, newer, Now);
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, merge.Outcome);
        Assert.Equal(Now, merge.Bundle!.Roles[0].Observations.Builds);
        Assert.Equal(Now, merge.Bundle.Roles[0].Observations.Endgame);
        var changed = JsonNode.Parse(Builds().Data.GetRawText())!;
        changed["avatars"]![0]!["name"] = "Different synthetic Agent";
        using var document = JsonDocument.Parse(changed.ToJsonString());
        var conflicting = newer with { Roles = [newer.Roles[0] with { ZzzBuilds = new(document.RootElement) }] };
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Conflict, HoyoLabGameBundleMerge.Merge(newer, conflicting, Now).Outcome);
        foreach (var capability in new[] { "builds", "endgame" })
        {
            var role = capability == "builds"
                ? newer.Roles[0] with { ZzzBuilds = null, Observations = newer.Roles[0].Observations with { Builds = null } }
                : newer.Roles[0] with { ZzzEndgame = null, Observations = newer.Roles[0].Observations with { Endgame = null } };
            var removed = newer with { Roles = [role], CapabilityTombstones = [new(Binding, capability, Now.AddSeconds(1))] };
            var result = HoyoLabGameBundleMerge.Merge(newer, removed, Now.AddSeconds(1));
            Assert.NotEqual(HoyoLabGameBundleMergeOutcome.Conflict, result.Outcome);
            if (capability == "builds")
            {
                Assert.Null(result.Bundle!.Roles[0].ZzzBuilds);
                Assert.NotNull(result.Bundle.Roles[0].ZzzEndgame);
            }
            else
            {
                Assert.Null(result.Bundle!.Roles[0].ZzzEndgame);
                Assert.NotNull(result.Bundle.Roles[0].ZzzBuilds);
            }
        }
    }

    [Fact]
    public void Encrypted_zzz_bundle_is_bound_to_its_game_and_survives_round_trip()
    {
        Assert.True(HoyoLabSyncCrypto.TryDerive("NYX-HOYO-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH", out var derived));
        using var secrets = derived!;
        Assert.True(HoyoLabSyncCrypto.TryEncryptBundle(secrets, Bundle(Now), Now, out var envelope));
        Assert.True(HoyoLabSyncCrypto.TryDecryptBundle(secrets, envelope, Now, out var parsed, gameId: "zzz"));
        Assert.True(HoyoLabZzzBuildRules.ValuesEqual(Builds(), parsed!.Roles[0].ZzzBuilds));
        Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(secrets, envelope, Now, out _, gameId: "hsr"));
        Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(secrets, envelope, Now, out _, gameId: "gi"));
    }
}
