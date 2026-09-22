using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabHsrEndgameTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly PublisherRoleBinding Binding = new("123456789", "prod_official_eur");

    internal static HoyoLabHsrEndgameSnapshot Snapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", "hoyolab-hsr-challenges-v1.fixture.json")));
        return new(document.RootElement.GetProperty("snapshot").Clone());
    }

    internal static HoyoLabGameBundle Bundle(DateTimeOffset observedAt, PublisherRoleBinding? binding = null)
    {
        var selected = binding ?? Binding;
        return new(2, "hsr", [new(new(selected, "Synthetic Trailblazer", "Europe"),
            new(null, null, null, null, null, observedAt, null, null), null, null,
            HsrEndgame: Snapshot())], selected, new(false, false, false, false, false, true, false, false), [], []);
    }

    [Fact]
    public void Six_periods_preserve_third_teams_quick_clear_markers_and_score_precision()
    {
        var snapshot = Snapshot();
        Assert.True(HoyoLabHsrEndgameRules.IsValid(snapshot));
        var bundle = Bundle(Now);
        Assert.True(HoyoLabGameBundleRules.IsValid(bundle, Now));
        Assert.True(HoyoLabGameBundleStore.TryParseBundle(HoyoLabGameBundleStore.SerializeBundle(bundle), Now, out var parsed));
        Assert.True(HoyoLabHsrEndgameRules.ValuesEqual(snapshot, Assert.Single(parsed!.Roles).HsrEndgame));
        var periods = snapshot.Data.GetProperty("challenges");
        Assert.Equal(6, periods.GetArrayLength());
        var floors = periods[4].GetProperty("floors");
        Assert.Equal(3, floors[0].GetProperty("nodes").GetArrayLength());
        Assert.Equal("4003", floors[0].GetProperty("nodes")[2].GetProperty("score").GetString());
        Assert.True(floors[1].GetProperty("fast").GetBoolean());
        Assert.Empty(floors[1].GetProperty("nodes")[0].GetProperty("avatars").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, floors[1].GetProperty("nodes")[0].GetProperty("time").ValueKind);
        Assert.Equal(nameof(HoyoLabHsrEndgameSnapshot), snapshot.ToString());
        Assert.True(HoyoLabHsrEndgameRules.ValuesEqual(snapshot, HoyoLabHsrEndgameRules.Normalize(snapshot)));
    }

    [Fact]
    public void Changed_periods_duplicate_identities_invalid_calendars_and_partial_copies_fail_closed()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["challenges"]!.AsArray().RemoveAt(5),
            root => root["challenges"]![0]!["period"] = 2,
            root => root["challenges"]![1]!["groups"]![0]!["name"] = "Changed schedule",
            root => root["challenges"]![0]!["id"] = 48,
            root => root["challenges"]![0]!["stars"] = 7,
            root => root["challenges"]![0]!["extraStars"] = 0,
            root => root["challenges"]![0]!["floors"]!.AsArray().Add(root["challenges"]![0]!["floors"]![0]!.DeepClone()),
            root => root["challenges"]![0]!["floors"]![0]!["nodes"]!.AsArray().RemoveAt(2),
            root => root["challenges"]![0]!["floors"]![0]!["nodes"]![0]!["time"]!["day"] = 30,
            root => root["challenges"]![2]!["floors"]![0]!["nodes"]![0]!["score"] = "04",
            root => root["challenges"]![2]!["floors"]![0]!["nodes"]![0]!["time"] = null,
            root => root["challenges"]![0]!["next"] = "unknown continuation",
        ];
        foreach (var mutate in mutations)
        {
            var value = JsonNode.Parse(Snapshot().Data.GetRawText())!;
            mutate(value);
            using var document = JsonDocument.Parse(value.ToJsonString());
            Assert.False(HoyoLabHsrEndgameRules.IsValid(new(document.RootElement)));
        }
    }

    [Fact]
    public void Canonical_large_scores_and_integer_valued_json_numbers_remain_lossless()
    {
        var value = JsonNode.Parse(Snapshot().Data.GetRawText())!;
        const string score = "9007199254740993123456789";
        value["challenges"]![2]!["floors"]![0]!["nodes"]![0]!["score"] = score;
        var json = System.Text.RegularExpressions.Regex.Replace(value.ToJsonString(), @"(:\s*)(\d+)(?=[,\s}\]])", "$1${2}e0");
        using var document = JsonDocument.Parse(json);
        Assert.True(HoyoLabHsrEndgameRules.IsValid(new(document.RootElement)));
        Assert.Equal(score, document.RootElement.GetProperty("challenges")[2].GetProperty("floors")[0].GetProperty("nodes")[0].GetProperty("score").GetString());
    }

    [Fact]
    public void Capture_result_requires_exact_saved_role_and_all_six_periods()
    {
        var root = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = Binding.RoleId,
            ["server"] = Binding.Server,
            ["count"] = 6,
            ["endgame"] = JsonNode.Parse(Snapshot().Data.GetRawText()),
        };
        Assert.Equal(HoyoLabHsrEndgameReadStatus.Completed, HoyoLabHsrEndgameCapture.ParseResult(root.ToJsonString(), Binding).Status);
        foreach (var (name, value) in new (string, JsonNode?)[]
        {
            ("roleId", JsonValue.Create("987654321")), ("server", JsonValue.Create("prod_official_usa")),
            ("count", JsonValue.Create(5)), ("extra", JsonValue.Create("synthetic")), ("endgame", null),
        })
        {
            var invalid = root.DeepClone().AsObject();
            invalid[name] = value;
            Assert.Equal(HoyoLabHsrEndgameReadStatus.NeedsReview, HoyoLabHsrEndgameCapture.ParseResult(invalid.ToJsonString(), Binding).Status);
        }
    }
    [Fact]
    public void Disabled_consent_wrong_game_missing_observation_and_tombstoned_payload_are_not_admitted()
    {
        var bundle = Bundle(Now);
        var role = bundle.Roles[0];
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { Consents = bundle.Consents with { Endgame = false } }, Now));
        Assert.False(HoyoLabGameBundleRules.IsValid(bundle with { GameId = "gi" }, Now));
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
        changed["challenges"]![0]!["battles"] = 2;
        using var document = JsonDocument.Parse(changed.ToJsonString());
        var different = newer with { Roles = [newer.Roles[0] with { HsrEndgame = new(document.RootElement) }] };
        Assert.Equal(HoyoLabGameBundleMergeOutcome.Conflict, HoyoLabGameBundleMerge.Merge(newer, different, Now).Outcome);
        var removed = newer with
        {
            Roles = [newer.Roles[0] with { Observations = newer.Roles[0].Observations with { Endgame = null }, HsrEndgame = null }],
            CapabilityTombstones = [new(Binding, "endgame", Now.AddSeconds(1))],
        };
        var deleted = HoyoLabGameBundleMerge.Merge(newer, removed, Now.AddSeconds(1));
        Assert.NotEqual(HoyoLabGameBundleMergeOutcome.Conflict, deleted.Outcome);
        Assert.Null(deleted.Bundle!.Roles[0].HsrEndgame);
    }
}
