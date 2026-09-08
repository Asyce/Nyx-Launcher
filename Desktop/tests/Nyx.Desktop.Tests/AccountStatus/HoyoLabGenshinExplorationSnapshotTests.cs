using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinExplorationSnapshotTests
{
    private static readonly string[] CountNames =
    [
        "anemoculi", "geoculi", "electroculi", "dendroculi", "hydroculi", "pyroculi", "lunoculi", "cryoculi",
        "commonChests", "exquisiteChests", "preciousChests", "luxuriousChests", "remarkableChests",
        "waypoints", "domains",
    ];

    // Synthetic world exploration only; this is not an account export.
    internal const string DataJson = """
        {
          "counts": {
            "anemoculi":1234,"geoculi":2,"electroculi":3,"dendroculi":4,
            "hydroculi":5,"pyroculi":6,"lunoculi":7,"cryoculi":8,
            "commonChests":9,"exquisiteChests":10,"preciousChests":11,
            "luxuriousChests":12,"remarkableChests":13,"waypoints":14,"domains":15
          },
          "worlds":[
            {
              "id":1,"parentId":0,"name":"Mondstadt \uD83D\uDE00","kind":"Nation","worldType":1,"level":10,
              "percentage":1234,"statueLevel":10,"indexActive":true,"detailActive":true,
              "offerings":[{"name":"Locked Offering","level":0,"state":"Locked"},{"name":"Unknown Offering","level":1,"state":"Unknow"}],
              "areas":[{"name":"Stormterror's Lair \uFFFD","percentage":2345}],
              "bosses":[{"name":"Synthetic Boss","kills":0}],
              "tribes":[{"id":11,"name":"Synthetic Tribe","level":3}]
            },
            {
              "id":2,"parentId":1,"name":"Liyue","kind":"Nation","worldType":1,"level":20,
              "percentage":9876,"statueLevel":8,"indexActive":false,"detailActive":true,
              "offerings":[],"areas":[],"bosses":[],"tribes":[]
            },
            {
              "id":3,"parentId":1,"name":"Synthetic Subregion","kind":"Subregion","worldType":2,"level":1,
              "percentage":1001,"statueLevel":0,"indexActive":true,"detailActive":false,
              "offerings":[],"areas":[],"bosses":[],"tribes":null
            }
          ],
          "displayGroups":[
            {"id":1,"areas":[{"ids":[1,2],"percentage":3456}]},
            {"id":2,"areas":[{"ids":[3],"percentage":4567}]}
          ]
        }
        """;

    [Fact]
    public void Complete_projection_preserves_all_fifteen_counts_and_nested_states()
    {
        var snapshot = Snapshot();

        Assert.True(HoyoLabGenshinExplorationRules.IsValid(snapshot));
        var data = snapshot.Data;
        var counts = data.GetProperty("counts");
        Assert.Equal(CountNames, counts.EnumerateObject().Select(property => property.Name));
        Assert.Equal(15, counts.EnumerateObject().Count());
        Assert.Equal(1234, counts.GetProperty("anemoculi").GetInt32());
        Assert.Equal(15, counts.GetProperty("domains").GetInt32());

        var worlds = data.GetProperty("worlds");
        Assert.Equal(3, worlds.GetArrayLength());
        Assert.Equal("Mondstadt 😀", worlds[0].GetProperty("name").GetString());
        Assert.Equal(1234, worlds[0].GetProperty("percentage").GetInt32());
        Assert.Equal(2345, worlds[0].GetProperty("areas")[0].GetProperty("percentage").GetInt32());
        Assert.Equal("Locked", worlds[0].GetProperty("offerings")[0].GetProperty("state").GetString());
        Assert.Equal("Unknow", worlds[0].GetProperty("offerings")[1].GetProperty("state").GetString());
        Assert.Equal(0, worlds[0].GetProperty("bosses")[0].GetProperty("kills").GetInt32());
        Assert.Equal(1, worlds[0].GetProperty("tribes").GetArrayLength());
        Assert.Equal(0, worlds[1].GetProperty("tribes").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, worlds[2].GetProperty("tribes").ValueKind);

        var groups = data.GetProperty("displayGroups");
        Assert.Equal(3456, groups[0].GetProperty("areas")[0].GetProperty("percentage").GetInt32());
        Assert.Equal(new[] { 1, 2 }, groups[0].GetProperty("areas")[0].GetProperty("ids")
            .EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(nameof(HoyoLabGenshinExplorationSnapshot), snapshot.ToString());
        Assert.False(data.TryGetProperty("achievements", out _));
        Assert.False(data.TryGetProperty("inventory", out _));
    }

    [Theory]
    [InlineData("duplicate-world")]
    [InlineData("duplicate-group")]
    [InlineData("duplicate-tribe")]
    [InlineData("missing-group")]
    [InlineData("foreign-group")]
    [InlineData("foreign-group-area")]
    [InlineData("missing-parent")]
    [InlineData("foreign-parent")]
    [InlineData("parent-cycle")]
    [InlineData("unknown-field")]
    [InlineData("wrong-tribes-type")]
    public void Duplicate_foreign_cyclic_and_mixed_projection_data_is_rejected(string mutation)
    {
        var root = Root();
        var worlds = root["worlds"]!.AsArray();
        var groups = root["displayGroups"]!.AsArray();
        switch (mutation)
        {
            case "duplicate-world":
                worlds.Add(worlds[0]!.DeepClone());
                break;
            case "duplicate-group":
                groups.Add(groups[0]!.DeepClone());
                break;
            case "duplicate-tribe":
                worlds[0]!["tribes"]!.AsArray().Add(worlds[0]!["tribes"]![0]!.DeepClone());
                break;
            case "missing-group":
                groups[0]!.AsObject().Remove("id");
                break;
            case "foreign-group":
                groups[0]!["id"] = 999;
                break;
            case "foreign-group-area":
                groups[0]!["areas"]![0]!["ids"]![0] = 999;
                break;
            case "missing-parent":
                worlds[1]!.AsObject().Remove("parentId");
                break;
            case "foreign-parent":
                worlds[1]!["parentId"] = 999;
                break;
            case "parent-cycle":
                worlds[0]!["parentId"] = 2;
                worlds[1]!["parentId"] = 1;
                break;
            case "unknown-field":
                root["achievements"] = new JsonArray();
                break;
            case "wrong-tribes-type":
                worlds[1]!["tribes"] = new JsonObject();
                break;
        }

        Assert.False(HoyoLabGenshinExplorationRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Empty_tribe_arrays_and_null_tribes_are_both_valid_but_null_data_is_not()
    {
        var root = Root();
        root["worlds"]![2]!["tribes"] = new JsonArray();
        Assert.True(HoyoLabGenshinExplorationRules.IsValid(Snapshot(root.ToJsonString())));
        Assert.False(HoyoLabGenshinExplorationRules.IsValid(null));
        Assert.False(HoyoLabGenshinExplorationRules.IsValid(Snapshot("null")));
    }

    [Theory]
    [InlineData("worlds")]
    [InlineData("groups")]
    [InlineData("offerings")]
    [InlineData("areas")]
    [InlineData("bosses")]
    [InlineData("tribes")]
    public void Collection_limits_are_enforced(string mutation)
    {
        var root = Root();
        var worlds = root["worlds"]!.AsArray();
        var first = worlds[0]!.AsObject();
        switch (mutation)
        {
            case "worlds":
                for (var index = 1; index <= HoyoLabGenshinExplorationRules.MaximumWorlds; index++)
                {
                    var copy = first.DeepClone();
                    copy["id"] = 1000 + index;
                    copy["parentId"] = 0;
                    worlds.Add(copy);
                }
                break;
            case "groups":
                for (var index = 1; index <= HoyoLabGenshinExplorationRules.MaximumWorlds; index++)
                    root["displayGroups"]!.AsArray().Add(new JsonObject
                    {
                        ["id"] = 1000 + index,
                        ["areas"] = new JsonArray(),
                    });
                break;
            case "offerings":
                for (var index = 1; index <= 128; index++)
                    first["offerings"]!.AsArray().Add(new JsonObject
                    {
                        ["name"] = "Offering " + index,
                        ["level"] = 0,
                        ["state"] = "Locked",
                    });
                break;
            case "areas":
                for (var index = 1; index <= 512; index++)
                    first["areas"]!.AsArray().Add(new JsonObject
                    {
                        ["name"] = "Area " + index,
                        ["percentage"] = 0,
                    });
                break;
            case "bosses":
                for (var index = 1; index <= 256; index++)
                    first["bosses"]!.AsArray().Add(new JsonObject
                    {
                        ["name"] = "Boss " + index,
                        ["kills"] = 0,
                    });
                break;
            case "tribes":
                for (var index = 1; index <= 64; index++)
                    first["tribes"]!.AsArray().Add(new JsonObject
                    {
                        ["id"] = 1000 + index,
                        ["name"] = "Tribe " + index,
                        ["level"] = 0,
                    });
                break;
        }

        Assert.False(HoyoLabGenshinExplorationRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Raw_unpaired_text_is_rejected_without_throwing_and_valid_unicode_is_preserved()
    {
        var invalid = DataJson.Replace(
            "\"name\":\"Mondstadt \\uD83D\\uDE00\"",
            "\"name\":\"Mondstadt \\uD800\"",
            StringComparison.Ordinal);
        Assert.Contains("\\uD800", invalid, StringComparison.OrdinalIgnoreCase);
        var exception = Record.Exception(() => HoyoLabGenshinExplorationRules.IsValid(Snapshot(invalid)));
        Assert.Null(exception);
        Assert.False(HoyoLabGenshinExplorationRules.IsValid(Snapshot(invalid)));
        Assert.Equal("Mondstadt 😀", Snapshot().Data.GetProperty("worlds")[0].GetProperty("name").GetString());
        Assert.Equal("Stormterror's Lair �", Snapshot().Data.GetProperty("worlds")[0]
            .GetProperty("areas")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Normalize_owns_a_deep_clone_and_values_equal_uses_json_numeric_semantics()
    {
        HoyoLabGenshinExplorationSnapshot owned;
        HoyoLabGenshinExplorationSnapshot borrowed;
        using (var document = JsonDocument.Parse(DataJson))
        {
            borrowed = new(document.RootElement);
            owned = HoyoLabGenshinExplorationRules.Normalize(borrowed);
        }

        Assert.False(HoyoLabGenshinExplorationRules.IsValid(borrowed));
        Assert.True(HoyoLabGenshinExplorationRules.IsValid(owned));
        Assert.True(HoyoLabGenshinExplorationRules.ValuesEqual(owned, Snapshot()));
        Assert.True(HoyoLabGenshinExplorationRules.ValuesEqual(
            Snapshot(),
            Snapshot(DataJson.Replace("\"percentage\":1234", "\"percentage\":1234.0", StringComparison.Ordinal))));
        Assert.False(HoyoLabGenshinExplorationRules.ValuesEqual(
            owned,
            Snapshot(DataJson.Replace("\"percentage\":1234", "\"percentage\":1235", StringComparison.Ordinal))));
    }

    private static HoyoLabGenshinExplorationSnapshot Snapshot(string json = DataJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private static JsonObject Root(string json = DataJson) => JsonNode.Parse(json)!.AsObject();
}
