using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabHsrEventsSnapshotTests
{
    // Synthetic calendar values only; this is not an account export.
    internal const string DataJson = """
        {
          "activities":[
            {
              "id":1,"version":"3.7","name":"Synthetic Double Reward","kind":"Activity","status":"0.0%",
              "total":100,"progress":0,
              "time":{"startTimestamp":"1709164800","endTimestamp":"1709251200",
                "startTime":"2024-02-29 00:00:00","endTime":"2024-02-29 23:59:59","now":"1709164800"},
              "rewards":[{"id":1,"name":"Synthetic Reward","quantity":0,"rarity":"5","kind":"Material"}],
              "specialReward":{"id":0,"name":"","quantity":0,"rarity":"5","kind":null},
              "finished":false,"showText":"0.0%","timeKind":"Calendar","description":"",
              "dropType":1,"dropTypes":[1.0,2],"refreshType":0,"count":0,"multiplier":0,"afterVersion":false
            },
            {
              "id":2,"version":"3.7","name":"Synthetic Scheduled Activity","kind":"Activity","status":"Ready",
              "total":0,"progress":0,
              "time":{"startTimestamp":"0","endTimestamp":"0","startTime":"","endTime":"","now":"0"},
              "rewards":[],"specialReward":null,"finished":true,"showText":"","timeKind":"None",
              "description":"0.0%","dropType":0,"dropTypes":[],"refreshType":0,"count":0,"multiplier":0,"afterVersion":true
            }
          ],
          "challenges":[
            {
              "id":1,"name":"Synthetic Challenge","kind":"Challenge","status":"Locked",
              "total":10,"progress":0,"extraProgress":0,
              "time":{"startTimestamp":"1720000000","endTimestamp":"1720086400",
                "startTime":"2024-07-03 12:30:00","endTime":"2024-07-04 12:30:00","now":"1720000000"},
              "rewards":[{"id":2,"name":"Synthetic Challenge Reward","quantity":0,"rarity":"4","kind":null}],
              "specialReward":null,"showText":"0.0%","rankKind":"","startVersion":""
            }
          ],
          "now":"1709164800","version":"3.7"
        }
        """;

    [Fact]
    public void Complete_projection_preserves_dates_display_strings_placeholders_and_numeric_shape()
    {
        var snapshot = Snapshot();

        Assert.True(HoyoLabHsrEventsRules.IsValid(snapshot));
        var data = snapshot.Data;
        Assert.Equal(2, data.GetProperty("activities").GetArrayLength());
        Assert.Equal(1, data.GetProperty("challenges").GetArrayLength());
        Assert.Equal("1709164800", data.GetProperty("now").GetString());
        Assert.Equal("3.7", data.GetProperty("version").GetString());

        var activity = data.GetProperty("activities")[0];
        Assert.Equal("0.0%", activity.GetProperty("status").GetString());
        Assert.Equal("0.0%", activity.GetProperty("showText").GetString());
        Assert.Equal("2024-02-29 00:00:00", activity.GetProperty("time").GetProperty("startTime").GetString());
        Assert.Equal("1709164800", activity.GetProperty("time").GetProperty("startTimestamp").GetString());
        Assert.Equal("1.0", activity.GetProperty("dropTypes")[0].GetRawText());
        Assert.Equal(0, activity.GetProperty("rewards")[0].GetProperty("quantity").GetInt32());
        var placeholder = activity.GetProperty("specialReward");
        Assert.Equal(0, placeholder.GetProperty("id").GetInt32());
        Assert.Equal(string.Empty, placeholder.GetProperty("name").GetString());
        Assert.Equal(0, placeholder.GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, placeholder.GetProperty("kind").ValueKind);

        var unscheduled = data.GetProperty("activities")[1].GetProperty("time");
        Assert.Equal("0", unscheduled.GetProperty("startTimestamp").GetString());
        Assert.Equal(string.Empty, unscheduled.GetProperty("startTime").GetString());
        Assert.Equal(string.Empty, unscheduled.GetProperty("endTime").GetString());
        Assert.Equal("0", unscheduled.GetProperty("now").GetString());
        var challenge = data.GetProperty("challenges")[0];
        Assert.Equal("Locked", challenge.GetProperty("status").GetString());
        Assert.Equal(string.Empty, challenge.GetProperty("rankKind").GetString());
        Assert.Equal(string.Empty, challenge.GetProperty("startVersion").GetString());
        Assert.Equal(nameof(HoyoLabHsrEventsSnapshot), snapshot.ToString());
    }

    [Fact]
    public void Same_numeric_id_in_distinct_collections_is_valid_but_duplicate_composite_ids_are_not()
    {
        Assert.True(HoyoLabHsrEventsRules.IsValid(Snapshot()));

        var duplicateActivity = Root();
        duplicateActivity["activities"]!.AsArray().Add(duplicateActivity["activities"]![0]!.DeepClone());
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(duplicateActivity.ToJsonString())));

        var duplicateChallenge = Root();
        duplicateChallenge["challenges"]!.AsArray().Add(duplicateChallenge["challenges"]![0]!.DeepClone());
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(duplicateChallenge.ToJsonString())));
    }

    [Fact]
    public void Empty_collections_and_null_special_rewards_are_valid_but_null_collections_are_not()
    {
        var empty = Root();
        empty["activities"] = new JsonArray();
        empty["challenges"] = new JsonArray();
        Assert.True(HoyoLabHsrEventsRules.IsValid(Snapshot(empty.ToJsonString())));

        var nullSpecial = Root();
        nullSpecial["activities"]![0]!["specialReward"] = null;
        Assert.True(HoyoLabHsrEventsRules.IsValid(Snapshot(nullSpecial.ToJsonString())));

        var nullCollection = Root();
        nullCollection["activities"] = null;
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(nullCollection.ToJsonString())));
        Assert.False(HoyoLabHsrEventsRules.IsValid(null));
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot("null")));
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-activity")]
    [InlineData("id-zero")]
    [InlineData("wrong-version")]
    [InlineData("wrong-status")]
    [InlineData("timestamp-number")]
    [InlineData("timestamp-leading-zero")]
    [InlineData("timestamp-overflow")]
    [InlineData("server-time-invalid")]
    [InlineData("server-time-wrong-type")]
    [InlineData("total-overflow")]
    [InlineData("drop-type-fraction")]
    [InlineData("drop-types-wrong-type")]
    [InlineData("special-placeholder-name")]
    [InlineData("special-placeholder-quantity")]
    [InlineData("reward-empty-name")]
    [InlineData("reward-kind-empty")]
    public void Invalid_fields_types_dates_ranges_and_source_fields_are_rejected(string mutation)
    {
        var root = Root();
        var activity = root["activities"]![0]!.AsObject();
        switch (mutation)
        {
            case "unknown-root":
                root["avatar_card_pool_list"] = new JsonArray();
                break;
            case "unknown-activity":
                activity["sourceText"] = "Synthetic source";
                break;
            case "id-zero":
                activity["id"] = 0;
                break;
            case "wrong-version":
                activity["version"] = 1;
                break;
            case "wrong-status":
                activity["status"] = "";
                break;
            case "timestamp-number":
                activity["time"]!["startTimestamp"] = 1709164800;
                break;
            case "timestamp-leading-zero":
                activity["time"]!["startTimestamp"] = "01";
                break;
            case "timestamp-overflow":
                activity["time"]!["startTimestamp"] = "253402300800";
                break;
            case "server-time-invalid":
                activity["time"]!["startTime"] = "2023-02-29 00:00:00";
                break;
            case "server-time-wrong-type":
                activity["time"]!["startTime"] = 1;
                break;
            case "total-overflow":
                activity["total"] = 2147483648;
                break;
            case "drop-type-fraction":
                activity["dropType"] = 1.5;
                break;
            case "drop-types-wrong-type":
                activity["dropTypes"] = new JsonArray("1");
                break;
            case "special-placeholder-name":
                activity["specialReward"]!["name"] = "not empty";
                break;
            case "special-placeholder-quantity":
                activity["specialReward"]!["quantity"] = 1;
                break;
            case "reward-empty-name":
                activity["rewards"]![0]!["name"] = "";
                break;
            case "reward-kind-empty":
                activity["rewards"]![0]!["kind"] = "";
                break;
        }

        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Unknown_list_fields_and_missing_required_projection_fields_are_rejected()
    {
        var unknownList = Root();
        unknownList["untrusted_list"] = new JsonArray();
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(unknownList.ToJsonString())));

        var missing = Root();
        missing["activities"]![0]!.AsObject().Remove("dropTypes");
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(missing.ToJsonString())));
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected_without_normalizing_them_away()
    {
        var duplicateRoot = DataJson.Replace(
            "\"activities\":[",
            "\"activities\":[],\"activities\":[",
            StringComparison.Ordinal);
        var duplicateActivity = DataJson.Replace(
            "\"id\":1,\"version\":",
            "\"id\":1,\"id\":1,\"version\":",
            StringComparison.Ordinal);
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(duplicateRoot)));
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(duplicateActivity)));
    }

    [Fact]
    public void Raw_unpaired_text_is_rejected_without_throwing_and_valid_unicode_is_preserved()
    {
        var invalid = DataJson.Replace(
            "\"name\":\"Synthetic Double Reward\"",
            "\"name\":\"Synthetic \\uD800\"",
            StringComparison.Ordinal);
        Assert.Contains("\\uD800", invalid, StringComparison.OrdinalIgnoreCase);
        var exception = Record.Exception(() => HoyoLabHsrEventsRules.IsValid(Snapshot(invalid)));
        Assert.Null(exception);
        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(invalid)));

        var supplementary = DataJson.Replace(
            "Synthetic Double Reward",
            "Synthetic 😀 Double Reward",
            StringComparison.Ordinal);
        var replacement = DataJson.Replace(
            "Synthetic Double Reward",
            "Synthetic � Double Reward",
            StringComparison.Ordinal);
        Assert.True(HoyoLabHsrEventsRules.IsValid(Snapshot(supplementary)));
        Assert.True(HoyoLabHsrEventsRules.IsValid(Snapshot(replacement)));
    }

    [Theory]
    [InlineData("activities")]
    [InlineData("challenges")]
    [InlineData("rewards")]
    [InlineData("dropTypes")]
    public void Collection_limits_are_enforced(string collection)
    {
        var root = Root();
        switch (collection)
        {
            case "activities":
                for (var index = 1; index <= HoyoLabHsrEventsRules.MaximumEvents; index++)
                {
                    var copy = root["activities"]![0]!.DeepClone();
                    copy!["id"] = 1000 + index;
                    root["activities"]!.AsArray().Add(copy);
                }
                break;
            case "challenges":
                for (var index = 1; index <= HoyoLabHsrEventsRules.MaximumEvents; index++)
                {
                    var copy = root["challenges"]![0]!.DeepClone();
                    copy!["id"] = 1000 + index;
                    root["challenges"]!.AsArray().Add(copy);
                }
                break;
            case "rewards":
                for (var index = 1; index <= 1024; index++)
                {
                    var copy = root["activities"]![0]!["rewards"]![0]!.DeepClone();
                    copy!["id"] = index + 1;
                    root["activities"]![0]!["rewards"]!.AsArray().Add(copy);
                }
                break;
            case "dropTypes":
                for (var index = 1; index <= 64; index++)
                    root["activities"]![0]!["dropTypes"]!.AsArray().Add(index);
                break;
        }

        Assert.False(HoyoLabHsrEventsRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Normalize_owns_a_deep_clone_and_values_equal_uses_json_numeric_semantics()
    {
        HoyoLabHsrEventsSnapshot owned;
        HoyoLabHsrEventsSnapshot borrowed;
        using (var document = JsonDocument.Parse(DataJson))
        {
            borrowed = new(document.RootElement);
            owned = HoyoLabHsrEventsRules.Normalize(borrowed);
        }

        Assert.False(HoyoLabHsrEventsRules.IsValid(borrowed));
        Assert.True(HoyoLabHsrEventsRules.IsValid(owned));
        Assert.True(HoyoLabHsrEventsRules.ValuesEqual(owned, Snapshot()));
        Assert.True(HoyoLabHsrEventsRules.ValuesEqual(
            Snapshot(),
            Snapshot(DataJson.Replace("\"dropTypes\":[1.0,2]", "\"dropTypes\":[1,2]", StringComparison.Ordinal))));
        Assert.False(HoyoLabHsrEventsRules.ValuesEqual(
            owned,
            Snapshot(DataJson.Replace("\"version\":\"3.7\"", "\"version\":\"3.8\"", StringComparison.Ordinal))));
        Assert.True(HoyoLabHsrEventsRules.ValuesEqual(null, null));
        Assert.False(HoyoLabHsrEventsRules.ValuesEqual(null, Snapshot()));
    }

    private static HoyoLabHsrEventsSnapshot Snapshot(string json = DataJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private static JsonObject Root(string json = DataJson) => JsonNode.Parse(json)!.AsObject();
}
