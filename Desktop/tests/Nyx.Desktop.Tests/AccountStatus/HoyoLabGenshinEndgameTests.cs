using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinEndgameTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly PublisherRoleBinding Binding = new("123456789", "os_euro");

    internal static HoyoLabGenshinEndgameSnapshot Snapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", "hoyolab-genshin-abyss-v1.fixture.json")));
        return new(document.RootElement.GetProperty("snapshot").Clone());
    }

    internal static HoyoLabGameBundle Bundle(DateTimeOffset observedAt, PublisherRoleBinding? binding = null)
    {
        var selected = binding ?? Binding;
        return new(2, "gi", [new(new(selected, "Synthetic Traveler", "Europe"),
            new(null, null, null, null, null, observedAt, null, null), null, null,
            GenshinEndgame: Snapshot())], selected, new(false, false, false, false, false, true, false, false), [], []);
    }

    [Fact]
    public void Both_periods_preserve_skipped_floors_teams_and_all_rankings_in_the_wire_round_trip()
    {
        var snapshot = Snapshot();
        Assert.True(HoyoLabGenshinEndgameRules.IsValid(snapshot));
        var bundle = Bundle(Now);
        Assert.True(HoyoLabGameBundleRules.IsValid(bundle, Now));
        var bytes = HoyoLabGameBundleStore.SerializeBundle(bundle);
        Assert.True(HoyoLabGameBundleStore.TryParseBundle(bytes, Now, out var parsed));
        Assert.True(HoyoLabGenshinEndgameRules.ValuesEqual(snapshot, Assert.Single(parsed!.Roles).GenshinEndgame));
        Assert.Equal("10-3", snapshot.Data.GetProperty("abyss")[0].GetProperty("skippedFloor").GetString());
        Assert.Single(snapshot.Data.GetProperty("abyss")[0].GetProperty("floors").EnumerateArray());
        Assert.Equal(nameof(HoyoLabGenshinEndgameSnapshot), snapshot.ToString());
    }

    [Fact]
    public void Integer_valued_json_numbers_match_the_browser_contract()
    {
        var exponentNumbers = System.Text.RegularExpressions.Regex.Replace(
            Snapshot().Data.GetRawText(), @"(:\s*)(\d+)(?=[,\s}\]])", "$1${2}e0");
        using var document = JsonDocument.Parse(exponentNumbers);
        Assert.True(HoyoLabGenshinEndgameRules.IsValid(new(document.RootElement)));
    }

    [Fact]
    public void Capture_requires_exact_saved_role_period_count_and_no_extra_fields()
    {
        var root = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = Binding.RoleId,
            ["server"] = Binding.Server,
            ["count"] = 2,
            ["endgame"] = JsonNode.Parse(Snapshot().Data.GetRawText()),
        };
        Assert.Equal(HoyoLabGenshinEndgameReadStatus.Completed,
            HoyoLabGenshinEndgameCapture.ParseResult(root.ToJsonString(), Binding).Status);
        foreach (var (name, value) in new (string, JsonNode?)[]
        {
            ("roleId", JsonValue.Create("987654321")), ("server", JsonValue.Create("os_usa")),
            ("count", JsonValue.Create(1)), ("extra", JsonValue.Create("synthetic")), ("endgame", null),
        })
        {
            var invalid = root.DeepClone().AsObject();
            invalid[name] = value;
            Assert.Equal(HoyoLabGenshinEndgameReadStatus.NeedsReview,
                HoyoLabGenshinEndgameCapture.ParseResult(invalid.ToJsonString(), Binding).Status);
        }
    }

    [Fact]
    public void Missing_duplicate_invalid_date_and_inconsistent_star_rows_reject_the_whole_snapshot()
    {
        Action<JsonNode>[] changes =
        [
            root => root["abyss"]!.AsArray().RemoveAt(1),
            root => root["abyss"]![1]!["id"] = root["abyss"]![0]!["id"]!.DeepClone(),
            root => root["abyss"]![0]!["rankings"]!.AsObject().Remove("damage"),
            root => root["abyss"]![0]!["floors"]![0]!["stars"] = 4,
            root => root["abyss"]![0]!["floors"]!.AsArray().Add(root["abyss"]![0]!["floors"]![0]!.DeepClone()),
            root => root["abyss"]![0]!["floors"]![0]!["chambers"]![0]!["battles"]![0]!["settledTime"]!["day"] = 30,
            root => root["abyss"]![0]!["floors"]![0]!["chambers"]![0]!["battles"]![0]!["avatars"] = new JsonArray(),
            root => root["abyss"]![0]!["floors"]![0]!["effects"] = new JsonArray("bad\u202Etext"),
            root => root["abyss"]![0]!["start"] = "1709164800.0",
            root => root["abyss"]![0]!["next"] = "unknown continuation",
        ];
        foreach (var change in changes)
        {
            var root = JsonNode.Parse(Snapshot().Data.GetRawText())!;
            change(root);
            using var document = JsonDocument.Parse(root.ToJsonString());
            Assert.False(HoyoLabGenshinEndgameRules.IsValid(new(document.RootElement)));
        }
    }

    [Fact]
    public void Disabled_consent_wrong_game_missing_observation_and_tombstoned_payload_are_not_admitted()
    {
        var bundle = Bundle(Now);
        var role = bundle.Roles[0];
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Consents = bundle.Consents with { Endgame = false } }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { GameId = "hsr" }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Roles = [role with { Observations = role.Observations with { Endgame = null } }] }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { CapabilityTombstones = [new(Binding, "endgame", Now)] }, Now));
    }

    [Fact]
    public void Merge_preserves_newer_records_conflicts_at_equal_time_and_obeys_deletion_barriers()
    {
        var older = Bundle(Now.AddHours(-1));
        var newer = Bundle(Now);
        var merged = HoyoLabGameBundleMerge.Merge(older, newer, Now);
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Merged, merged.Outcome);
        Assert.Equal(Now, merged.Bundle!.Roles[0].Observations.Endgame);
        var changed = JsonNode.Parse(Snapshot().Data.GetRawText())!;
        changed["abyss"]![0]!["battles"] = 2;
        using var document = JsonDocument.Parse(changed.ToJsonString());
        var different = newer with { Roles = [newer.Roles[0] with { GenshinEndgame = new(document.RootElement) }] };
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Conflict, HoyoLabGameBundleMerge.Merge(newer, different, Now).Outcome);
        var removed = newer with
        {
            Roles = [newer.Roles[0] with { Observations = newer.Roles[0].Observations with { Endgame = null }, GenshinEndgame = null }],
            CapabilityTombstones = [new(Binding, "endgame", Now.AddSeconds(1))],
        };
        var deleted = HoyoLabGameBundleMerge.Merge(newer, removed, Now.AddSeconds(1));
        Assert.NotEqual(HoyoLabGameBundleMergeOutcome.Conflict, deleted.Outcome);
        Assert.Null(deleted.Bundle!.Roles[0].GenshinEndgame);
    }
}
