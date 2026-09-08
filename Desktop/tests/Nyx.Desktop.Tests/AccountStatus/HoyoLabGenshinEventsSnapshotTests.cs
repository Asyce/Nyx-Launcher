using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinEventsSnapshotTests
{
    // Synthetic event-calendar data only; this is not an account export.
    internal const string DataJson = """
        {
          "activities":[
            {
              "id":101,"kind":"ActTypeExplore","name":"Synthetic Expedition",
              "startTimestamp":"1709164800","endTimestamp":"1709251200",
              "startTime":{"year":2024,"month":2,"day":29,"hour":0,"minute":0,"second":0},
              "endTime":{"year":2024,"month":2,"day":29,"hour":23,"minute":59,"second":59},
              "countdown":0,"status":1,"finished":false,
              "rewards":[{"id":1,"name":"Synthetic Reward","quantity":0,"rarity":"5","overview":true}],
              "exploration":{"percentage":1234.5,"finished":false},
              "double":{"total":2,"remaining":1},
              "abyss":null,"theater":null,"onslaught":null
            },
            {
              "id":102,"kind":"ActTypeChallenge","name":"Synthetic Challenge",
              "startTimestamp":"0","endTimestamp":"0","startTime":null,"endTime":null,
              "countdown":0,"status":0,"finished":false,"rewards":[],
              "exploration":null,"double":null,"abyss":null,"theater":null,
              "onslaught":{"unlocked":true,"difficulty":0,"seconds":123,
                "sub":{"seconds":12.25,"x":0.125,"y":2147483647}}
            }
          ],
          "fixed":[
            {
              "id":0,"kind":"ActTypeTower","name":"Synthetic Tower",
              "startTimestamp":"1709251200","endTimestamp":"1709337600",
              "startTime":{"year":2024,"month":3,"day":1,"hour":0,"minute":0,"second":0},
              "endTime":{"year":2024,"month":3,"day":2,"hour":0,"minute":0,"second":0},
              "countdown":42,"status":2,"finished":true,"rewards":[],
              "exploration":null,"double":null,
              "abyss":{"unlocked":true,"maximumStars":36,"stars":0,"hasData":true},
              "theater":null,"onslaught":null
            },
            {
              "id":0,"kind":"ActTypeRoleCombat","name":"Synthetic Theater",
              "startTimestamp":"1720000000","endTimestamp":"1720086400",
              "startTime":{"year":2024,"month":7,"day":3,"hour":12,"minute":30,"second":0},
              "endTime":{"year":2024,"month":7,"day":4,"hour":12,"minute":30,"second":0},
              "countdown":7,"status":3,"finished":false,"rewards":[],
              "exploration":null,"double":null,"abyss":null,
              "theater":{"unlocked":false,"maximumRound":10,"hasData":false,"tarotFinished":0,"difficulty":3},
              "onslaught":null
            }
          ],
          "selected":[
            {"id":101,"kind":"ActTypeExplore"},
            {"id":0,"kind":"ActTypeTower"}
          ]
        }
        """;

    [Fact]
    public void Complete_projection_preserves_activity_identity_dates_and_all_nullable_summaries()
    {
        var snapshot = Snapshot();

        Assert.True(HoyoLabGenshinEventsRules.IsValid(snapshot));
        var data = snapshot.Data;
        Assert.Equal(2, data.GetProperty("activities").GetArrayLength());
        Assert.Equal(2, data.GetProperty("fixed").GetArrayLength());
        Assert.Equal(2, data.GetProperty("selected").GetArrayLength());

        var ordinary = data.GetProperty("activities")[0];
        Assert.Equal(101, ordinary.GetProperty("id").GetInt32());
        Assert.Equal("ActTypeExplore", ordinary.GetProperty("kind").GetString());
        Assert.Equal("1709164800", ordinary.GetProperty("startTimestamp").GetString());
        Assert.Equal(2024, ordinary.GetProperty("startTime").GetProperty("year").GetInt32());
        Assert.Equal(2, ordinary.GetProperty("startTime").GetProperty("month").GetInt32());
        Assert.Equal(29, ordinary.GetProperty("startTime").GetProperty("day").GetInt32());
        Assert.Equal(1234.5, ordinary.GetProperty("exploration").GetProperty("percentage").GetDouble());
        Assert.Equal(2, ordinary.GetProperty("double").GetProperty("total").GetInt32());
        Assert.Equal(0, ordinary.GetProperty("rewards")[0].GetProperty("quantity").GetInt32());

        var unscheduled = data.GetProperty("activities")[1];
        Assert.Equal("0", unscheduled.GetProperty("startTimestamp").GetString());
        Assert.Equal("0", unscheduled.GetProperty("endTimestamp").GetString());
        Assert.Equal(JsonValueKind.Null, unscheduled.GetProperty("startTime").ValueKind);
        Assert.Equal(JsonValueKind.Null, unscheduled.GetProperty("endTime").ValueKind);
        var sub = unscheduled.GetProperty("onslaught").GetProperty("sub");
        Assert.Equal(12.25, sub.GetProperty("seconds").GetDouble());
        Assert.Equal(0.125, sub.GetProperty("x").GetDouble());
        Assert.Equal(2147483647, sub.GetProperty("y").GetInt32());

        var tower = data.GetProperty("fixed")[0];
        var theater = data.GetProperty("fixed")[1];
        Assert.Equal(0, tower.GetProperty("id").GetInt32());
        Assert.Equal("ActTypeTower", tower.GetProperty("kind").GetString());
        Assert.Equal("ActTypeRoleCombat", theater.GetProperty("kind").GetString());
        Assert.Equal(0, tower.GetProperty("abyss").GetProperty("stars").GetInt32());
        Assert.Equal(0, theater.GetProperty("theater").GetProperty("tarotFinished").GetInt32());
        Assert.Equal(3, theater.GetProperty("theater").GetProperty("difficulty").GetInt32());
        Assert.Equal(101, data.GetProperty("selected")[0].GetProperty("id").GetInt32());
        Assert.Equal("ActTypeTower", data.GetProperty("selected")[1].GetProperty("kind").GetString());
        Assert.Equal(nameof(HoyoLabGenshinEventsSnapshot), snapshot.ToString());
    }

    [Theory]
    [InlineData("duplicate-activity")]
    [InlineData("duplicate-fixed")]
    [InlineData("duplicate-cross-array")]
    [InlineData("duplicate-selected")]
    [InlineData("foreign-selected")]
    public void Composite_identity_and_selected_bindings_are_strict(string mutation)
    {
        var root = Root();
        var activities = root["activities"]!.AsArray();
        var fixedActivities = root["fixed"]!.AsArray();
        var selected = root["selected"]!.AsArray();

        switch (mutation)
        {
            case "duplicate-activity":
                activities.Add(activities[0]!.DeepClone());
                break;
            case "duplicate-fixed":
                fixedActivities[1]!["kind"] = "ActTypeTower";
                break;
            case "duplicate-cross-array":
                fixedActivities[0]!["id"] = 101;
                fixedActivities[0]!["kind"] = "ActTypeExplore";
                break;
            case "duplicate-selected":
                selected.Add(selected[0]!.DeepClone());
                break;
            case "foreign-selected":
                selected[0]!["id"] = 999;
                break;
        }

        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Empty_collections_and_null_details_are_valid_but_missing_details_are_not()
    {
        var empty = Root();
        empty["activities"] = new JsonArray();
        empty["fixed"] = new JsonArray();
        empty["selected"] = new JsonArray();
        Assert.True(HoyoLabGenshinEventsRules.IsValid(Snapshot(empty.ToJsonString())));

        var missing = Root();
        missing["activities"]![0]!.AsObject().Remove("exploration");
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(missing.ToJsonString())));
        Assert.False(HoyoLabGenshinEventsRules.IsValid(null));
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot("null")));
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-activity")]
    [InlineData("unknown-summary")]
    [InlineData("id-string")]
    [InlineData("name-control")]
    [InlineData("kind-empty")]
    [InlineData("timestamp-number")]
    [InlineData("timestamp-leading-zero")]
    [InlineData("timestamp-negative")]
    [InlineData("timestamp-overflow")]
    [InlineData("calendar-invalid")]
    [InlineData("calendar-extra")]
    [InlineData("countdown-fraction")]
    [InlineData("exploration-string")]
    [InlineData("onslaught-sub-null")]
    [InlineData("onslaught-sub-extra")]
    [InlineData("reward-negative-id")]
    public void Invalid_types_ranges_dates_and_source_fields_are_rejected(string mutation)
    {
        var root = Root();
        var first = root["activities"]![0]!.AsObject();
        switch (mutation)
        {
            case "unknown-root":
                root["sourceText"] = "Synthetic source";
                break;
            case "unknown-activity":
                first["description"] = "Synthetic description";
                break;
            case "unknown-summary":
                first["exploration"]!["source"] = "Synthetic source";
                break;
            case "id-string":
                first["id"] = "101";
                break;
            case "name-control":
                first["name"] = "Synthetic\u0001Expedition";
                break;
            case "kind-empty":
                first["kind"] = "";
                break;
            case "timestamp-number":
                first["startTimestamp"] = 1709164800;
                break;
            case "timestamp-leading-zero":
                first["startTimestamp"] = "01";
                break;
            case "timestamp-negative":
                first["startTimestamp"] = "-1";
                break;
            case "timestamp-overflow":
                first["startTimestamp"] = "253402300800";
                break;
            case "calendar-invalid":
                first["startTime"]!["year"] = 2023;
                break;
            case "calendar-extra":
                first["startTime"]!["zone"] = "UTC";
                break;
            case "countdown-fraction":
                first["countdown"] = 1.5;
                break;
            case "exploration-string":
                first["exploration"]!["percentage"] = "1234.5";
                break;
            case "onslaught-sub-null":
                root["activities"]![1]!["onslaught"]!["sub"] = null;
                break;
            case "onslaught-sub-extra":
                root["activities"]![1]!["onslaught"]!["sub"]!["source"] = "Synthetic";
                break;
            case "reward-negative-id":
                first["rewards"]![0]!["id"] = 0;
                break;
        }

        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Theory]
    [InlineData("activities")]
    [InlineData("fixed")]
    [InlineData("selected")]
    [InlineData("rewards")]
    public void Collection_limits_are_enforced(string collection)
    {
        var root = Root();
        switch (collection)
        {
            case "activities":
                for (var index = 1; index <= HoyoLabGenshinEventsRules.MaximumEvents; index++)
                {
                    var copy = root["activities"]![0]!.DeepClone();
                    copy!["id"] = 1000 + index;
                    root["activities"]!.AsArray().Add(copy);
                }
                break;
            case "fixed":
                for (var index = 1; index <= HoyoLabGenshinEventsRules.MaximumEvents; index++)
                {
                    var copy = root["fixed"]![0]!.DeepClone();
                    copy!["kind"] = "ActTypeTower" + index;
                    root["fixed"]!.AsArray().Add(copy);
                }
                break;
            case "selected":
                for (var index = 1; index <= HoyoLabGenshinEventsRules.MaximumEvents - 1; index++)
                    root["selected"]!.AsArray().Add(root["selected"]![0]!.DeepClone());
                break;
            case "rewards":
                for (var index = 1; index <= 1024; index++)
                {
                    var copy = root["activities"]![0]!["rewards"]![0]!.DeepClone();
                    copy!["id"] = index + 1;
                    root["activities"]![0]!["rewards"]!.AsArray().Add(copy);
                }
                break;
        }

        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected_without_normalizing_them_away()
    {
        var duplicateRoot = DataJson.Replace(
            "\"activities\":[",
            "\"activities\":[],\"activities\":[",
            StringComparison.Ordinal);
        var duplicateActivity = DataJson.Replace(
            "\"id\":101,\"kind\":",
            "\"id\":101,\"id\":101,\"kind\":",
            StringComparison.Ordinal);
        var duplicateSelected = DataJson.Replace(
            "\"selected\":[",
            "\"selected\":[],\"selected\":[",
            StringComparison.Ordinal);

        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(duplicateRoot)));
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(duplicateActivity)));
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(duplicateSelected)));
    }

    [Fact]
    public void Numeric_overflow_and_negative_fractional_summary_values_are_rejected()
    {
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(DataJson.Replace(
            "\"percentage\":1234.5", "\"percentage\":1e9999", StringComparison.Ordinal))));
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(DataJson.Replace(
            "\"percentage\":1234.5", "\"percentage\":-0.1", StringComparison.Ordinal))));
    }

    [Fact]
    public void Raw_unpaired_text_is_rejected_without_throwing_and_valid_unicode_is_preserved()
    {
        var invalid = DataJson.Replace(
            "\"name\":\"Synthetic Expedition\"",
            "\"name\":\"Synthetic \\uD800\"",
            StringComparison.Ordinal);
        Assert.Contains("\\uD800", invalid, StringComparison.OrdinalIgnoreCase);
        var exception = Record.Exception(() => HoyoLabGenshinEventsRules.IsValid(Snapshot(invalid)));
        Assert.Null(exception);
        Assert.False(HoyoLabGenshinEventsRules.IsValid(Snapshot(invalid)));

        var supplementary = DataJson.Replace(
            "Synthetic Expedition",
            "Synthetic 😀 Expedition",
            StringComparison.Ordinal);
        var replacement = DataJson.Replace(
            "Synthetic Expedition",
            "Synthetic � Expedition",
            StringComparison.Ordinal);
        Assert.True(HoyoLabGenshinEventsRules.IsValid(Snapshot(supplementary)));
        Assert.True(HoyoLabGenshinEventsRules.IsValid(Snapshot(replacement)));
    }

    [Fact]
    public void Normalize_owns_a_deep_clone_and_values_equal_uses_json_numeric_semantics()
    {
        HoyoLabGenshinEventsSnapshot owned;
        HoyoLabGenshinEventsSnapshot borrowed;
        using (var document = JsonDocument.Parse(DataJson))
        {
            borrowed = new(document.RootElement);
            owned = HoyoLabGenshinEventsRules.Normalize(borrowed);
        }

        Assert.False(HoyoLabGenshinEventsRules.IsValid(borrowed));
        Assert.True(HoyoLabGenshinEventsRules.IsValid(owned));
        Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(owned, Snapshot()));
        Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(
            Snapshot(),
            Snapshot(DataJson.Replace("\"percentage\":1234.5", "\"percentage\":1234.50", StringComparison.Ordinal))));
        Assert.False(HoyoLabGenshinEventsRules.ValuesEqual(
            owned,
            Snapshot(DataJson.Replace("\"percentage\":1234.5", "\"percentage\":1235.5", StringComparison.Ordinal))));
        Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(null, null));
        Assert.False(HoyoLabGenshinEventsRules.ValuesEqual(null, Snapshot()));
    }

    private static HoyoLabGenshinEventsSnapshot Snapshot(string json = DataJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private static JsonObject Root(string json = DataJson) => JsonNode.Parse(json)!.AsObject();
}
