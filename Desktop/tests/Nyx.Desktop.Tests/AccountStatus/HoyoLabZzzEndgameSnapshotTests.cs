using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabZzzEndgameSnapshotTests
{
    private static readonly PublisherRoleBinding Binding = new("123456789", "prod_gf_eu");

    internal static HoyoLabZzzEndgameSnapshot Snapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Contracts", "hoyolab-zzz-endgame-v1.fixture.json")));
        return new(document.RootElement.GetProperty("snapshot").Clone());
    }

    [Fact]
    public void Two_periods_preserve_percentage_scale_source_seconds_and_absent_fourth_frontier_scores()
    {
        var snapshot = Snapshot();
        Assert.True(HoyoLabZzzEndgameRules.IsValid(snapshot));
        var periods = snapshot.Data.GetProperty("shiyu");
        Assert.Equal(2, periods.GetArrayLength());
        Assert.Equal(1234, periods[0].GetProperty("summary").GetProperty("rankPercentHundredths").GetInt32());
        Assert.False(periods[0].GetProperty("fourth").GetProperty("teams")[0].TryGetProperty("score", out _));
        Assert.Equal(7, periods[0].GetProperty("fifth").GetProperty("teams")[0].GetProperty("time").GetProperty("second").GetInt32());
        Assert.Equal(nameof(HoyoLabZzzEndgameSnapshot), snapshot.ToString());
        Assert.True(HoyoLabZzzEndgameRules.ValuesEqual(snapshot, HoyoLabZzzEndgameRules.Normalize(snapshot)));
    }

    [Fact]
    public void Partial_unknown_and_inconsistent_periods_are_rejected_without_manufacturing_missing_fields()
    {
        Action<JsonNode>[] mutations =
        [
            root => root["shiyu"]!.AsArray().RemoveAt(1),
            root => root["shiyu"]![1]!["zoneId"] = 99,
            root => root["shiyu"]![1]!["period"] = 1,
            root => root["shiyu"]![0]!["fourth"] = null,
            root => root["shiyu"]![0]!["third"] = new JsonObject(),
            root => root["shiyu"]![0]!["fifth"]!["teams"]!.AsArray().RemoveAt(2),
            root => root["shiyu"]![0]!["fifth"]!["teams"]![0]!["id"] = 1,
            root => root["shiyu"]![0]!["fourth"]!["teams"]![0]!["score"] = 0,
            root => root["shiyu"]![0]!["fourth"]!["teams"]![0]!["buddy"] = null,
            root => root["shiyu"]![0]!["summary"]!["score"] = 60013,
            root => root["shiyu"]![0]!["summary"]!["maxScore"] = 150001,
            root => root["shiyu"]![0]!["summary"]!["rankPercentHundredths"] = 12.34,
            root => root["shiyu"]![0]!["summary"]!["rankPercentHundredths"] = 10001,
            root => root["shiyu"]![0]!["start"]!.AsObject().Remove("second"),
            root => { root["shiyu"]![0]!["start"]!["month"] = 2; root["shiyu"]![0]!["start"]!["day"] = 30; },
            root => root["shiyu"]![0]!["startEpoch"] = 1893456000,
            root => root["shiyu"]![0]!["endEpoch"] = root["shiyu"]![0]!["startEpoch"]!.DeepClone(),
            root => root["shiyu"]![0]!["fourth"]!["buff"]!["text"] = "bad\u202Etext",
        ];
        foreach (var mutate in mutations)
        {
            var root = JsonNode.Parse(Snapshot().Data.GetRawText())!;
            mutate(root);
            using var document = JsonDocument.Parse(root.ToJsonString());
            Assert.False(HoyoLabZzzEndgameRules.IsValid(new(document.RootElement)));
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
            ["endgame"] = JsonNode.Parse(Snapshot().Data.GetRawText()),
        };
        Assert.Equal(HoyoLabZzzEndgameReadStatus.Completed, HoyoLabZzzEndgameCapture.ParseResult(root.ToJsonString(), Binding).Status);
        foreach (var (key, value) in new (string, JsonNode?)[]
        {
            ("roleId", JsonValue.Create("999999999")), ("server", JsonValue.Create("prod_gf_us")),
            ("count", JsonValue.Create(1)), ("extra", JsonValue.Create("unknown")), ("endgame", null),
        })
        {
            var invalid = root.DeepClone().AsObject();
            invalid[key] = value;
            Assert.Equal(HoyoLabZzzEndgameReadStatus.NeedsReview, HoyoLabZzzEndgameCapture.ParseResult(invalid.ToJsonString(), Binding).Status);
        }
        Assert.Equal(HoyoLabZzzEndgameReadStatus.NeedsReview,
            HoyoLabZzzEndgameCapture.ParseResult(root.ToJsonString(), new("123456789", "os_euro")).Status);
    }

    [Fact]
    public void Failure_envelopes_are_small_redacted_and_never_include_partial_snapshots()
    {
        foreach (var (state, status) in new[]
        {
            ("login-required", HoyoLabZzzEndgameReadStatus.LoginRequired), ("canceled", HoyoLabZzzEndgameReadStatus.Canceled),
            ("timed-out", HoyoLabZzzEndgameReadStatus.TimedOut), ("too-large", HoyoLabZzzEndgameReadStatus.TooLarge),
            ("unknown", HoyoLabZzzEndgameReadStatus.NeedsReview),
        })
        {
            var output = HoyoLabZzzEndgameCapture.ParseResult(JsonSerializer.Serialize(new { status = state }), Binding);
            Assert.Equal(status, output.Status);
            Assert.Null(output.Snapshot);
            Assert.Equal(nameof(HoyoLabZzzEndgameReadResult), output.ToString());
            Assert.Equal(HoyoLabZzzEndgameReadStatus.NeedsReview,
                HoyoLabZzzEndgameCapture.ParseResult(JsonSerializer.Serialize(new { status = state, endgame = new { } }), Binding).Status);
        }
        Assert.Equal(HoyoLabZzzEndgameReadStatus.NeedsReview, HoyoLabZzzEndgameCapture.ParseResult("{\"status\":\"done\",\"status\":\"canceled\"}", Binding).Status);
        Assert.Throws<ArgumentException>(() => HoyoLabZzzEndgameCapture.CreateScript("untrusted-key", Binding));
        Assert.Throws<ArgumentException>(() => HoyoLabZzzEndgameCapture.CreateScript("__pengoNyxZzzEndgame_test", new("123456789", "os_euro")));
    }
}
