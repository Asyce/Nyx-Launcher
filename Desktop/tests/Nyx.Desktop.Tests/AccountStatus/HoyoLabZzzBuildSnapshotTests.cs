using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabZzzBuildSnapshotTests
{
    private static readonly PublisherRoleBinding Binding = new("123456789", "prod_gf_eu");

    internal static HoyoLabZzzBuildSnapshot Snapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", "hoyolab-zzz-builds-v1.fixture.json")));
        return new(document.RootElement.GetProperty("snapshot").Clone());
    }

    [Fact]
    public void Complete_build_preserves_source_precision_empty_gear_and_awakening_state()
    {
        var snapshot = Snapshot();
        Assert.True(HoyoLabZzzBuildRules.IsValid(snapshot));
        var avatars = snapshot.Data.GetProperty("avatars");
        Assert.Equal("12.30%", avatars[0].GetProperty("stats")[0].GetProperty("final").GetString());
        Assert.Equal("", avatars[0].GetProperty("stats")[0].GetProperty("base").GetString());
        Assert.Equal(86.17, avatars[0].GetProperty("plan").GetProperty("score").GetDouble());
        Assert.Equal(JsonValueKind.Null, avatars[1].GetProperty("weapon").ValueKind);
        Assert.Empty(avatars[1].GetProperty("equipment").EnumerateArray());
        Assert.False(avatars[1].GetProperty("awakening").GetProperty("available").GetBoolean());
        Assert.Equal("Synthetic rank effect ", avatars[0].GetProperty("ranks")[0].GetProperty("description").GetString());
        Assert.Equal(nameof(HoyoLabZzzBuildSnapshot), snapshot.ToString());
        Assert.True(HoyoLabZzzBuildRules.ValuesEqual(snapshot, HoyoLabZzzBuildRules.Normalize(snapshot)));
    }

    [Fact]
    public void Invalid_or_unknown_fields_duplicate_slots_and_changed_stat_types_are_rejected()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["avatars"]!.AsArray().Add(root["avatars"]![0]!.DeepClone()),
            root => root["avatars"]![0]!.AsObject().Remove("weapon"),
            root => root["avatars"]![0]!["future"] = "unsupported",
            root => root["avatars"]![0]!["equipment"]!.AsArray().Add(root["avatars"]![0]!["equipment"]![0]!.DeepClone()),
            root => root["avatars"]![0]!["stats"]![0]!["final"] = 12.3,
            root => root["avatars"]![0]!["stats"]![0]!["final"] = "",
            root => root["avatars"]![0]!["awakening"]!["level"] = 7,
            root => root["avatars"]![0]!["ranks"]![0]!["description"] = "bad\u202Etext",
        ];
        foreach (var mutate in mutations)
        {
            var root = JsonNode.Parse(Snapshot().Data.GetRawText())!;
            mutate(root);
            using var document = JsonDocument.Parse(root.ToJsonString());
            Assert.False(HoyoLabZzzBuildRules.IsValid(new(document.RootElement)));
        }
    }

    [Fact]
    public void Exact_role_count_and_envelope_are_required_before_accepting_capture()
    {
        var root = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = Binding.RoleId,
            ["server"] = Binding.Server,
            ["count"] = 2,
            ["builds"] = JsonNode.Parse(Snapshot().Data.GetRawText()),
        };
        Assert.Equal(HoyoLabZzzBuildReadStatus.Completed, HoyoLabZzzBuildCapture.ParseResult(root.ToJsonString(), Binding).Status);
        foreach (var (key, value) in new (string, JsonNode?)[]
        {
            ("roleId", JsonValue.Create("999999999")), ("server", JsonValue.Create("prod_gf_us")),
            ("count", JsonValue.Create(1)), ("extra", JsonValue.Create("unknown")), ("builds", null),
        })
        {
            var invalid = root.DeepClone().AsObject();
            invalid[key] = value;
            Assert.Equal(HoyoLabZzzBuildReadStatus.NeedsReview, HoyoLabZzzBuildCapture.ParseResult(invalid.ToJsonString(), Binding).Status);
        }
    }

    [Fact]
    public void Explicit_empty_roster_and_integer_valued_number_notation_match_the_browser()
    {
        using var empty = JsonDocument.Parse("{\"avatars\":[]}");
        Assert.True(HoyoLabZzzBuildRules.IsValid(new(empty.RootElement)));
        var json = Snapshot().Data.GetRawText().Replace("\"id\": 1001", "\"id\": 1001e0", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.True(HoyoLabZzzBuildRules.IsValid(new(document.RootElement)));
    }
}
