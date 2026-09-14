using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinBuildSnapshotTests
{
    // Synthetic contract values, not an account capture.
    internal const string CharactersJson = """
        [{"id":101,"level":95,"promotion":6,"friendship":null,"element":"Ice",
          "weapon":{"id":202,"level":90,"promotion":6,"refinement":1,
            "main":{"id":4,"base":123,"added":null,"final":123,"percent":false},"sub":null},
          "skills":[{"id":501,"type":1,"level":13,"unlocked":true},
                    {"id":502,"type":2,"level":1,"unlocked":false}],
          "constellations":[{"id":601,"position":1,"active":true}],
          "artifacts":[{"id":303,"setId":404,"slot":1,"rarity":5,"level":20,
            "main":{"id":2000,"value":4000,"percent":false,"rolls":null},
            "sub":[{"id":20,"value":8.6,"percent":true,"rolls":2},
                   {"id":22,"value":18.7,"percent":true,"rolls":3},
                   {"id":6,"value":10.5,"percent":true,"rolls":1},
                   {"id":23,"value":5.2,"percent":true,"rolls":0}]}],
          "properties":[{"group":0,"id":20,"base":5,"added":null,"final":13.6,"percent":true},
                        {"group":1,"id":20,"base":5,"added":8.6,"final":13.6,"percent":true}]}]
        """;

    internal static HoyoLabGenshinBuildSnapshot Snapshot(string json = CharactersJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    [Fact]
    public void Official_projected_values_are_preserved_without_game_rule_recalculation()
    {
        var snapshot = Snapshot();
        Assert.True(HoyoLabGenshinBuildRules.IsValid(snapshot));
        var character = snapshot.Characters[0];
        Assert.Equal(95, character.GetProperty("level").GetInt32());
        Assert.Equal(13, character.GetProperty("skills")[0].GetProperty("level").GetInt32());
        Assert.False(character.GetProperty("skills")[1].GetProperty("unlocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, character.GetProperty("friendship").ValueKind);
        Assert.Equal(4, character.GetProperty("artifacts")[0].GetProperty("sub").GetArrayLength());
        Assert.Equal(18.7, character.GetProperty("artifacts")[0].GetProperty("sub")[1].GetProperty("value").GetDouble());
        Assert.Equal(nameof(HoyoLabGenshinBuildSnapshot), snapshot.ToString());
    }

    [Fact]
    public void Optional_official_names_are_valid_on_every_numeric_stat_shape_and_survive_normalize()
    {
        var snapshot = Snapshot(NamedCharactersJson());

        Assert.True(HoyoLabGenshinBuildRules.IsValid(snapshot));
        var character = snapshot.Characters[0];
        Assert.Equal("Weapon ATK", character.GetProperty("weapon").GetProperty("main").GetProperty("name").GetString());
        Assert.Equal("Weapon Bonus", character.GetProperty("weapon").GetProperty("sub").GetProperty("name").GetString());
        Assert.Equal("Artifact Main", character.GetProperty("artifacts")[0].GetProperty("main").GetProperty("name").GetString());
        Assert.Equal("Artifact Sub", character.GetProperty("artifacts")[0].GetProperty("sub")[0].GetProperty("name").GetString());
        Assert.Equal("Displayed Property", character.GetProperty("properties")[0].GetProperty("name").GetString());

        var normalized = HoyoLabGenshinBuildRules.Normalize(snapshot);
        Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(snapshot, normalized));
        Assert.True(HoyoLabGenshinBuildRules.IsValid(normalized));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("extra")]
    [InlineData("control")]
    [InlineData("too-long")]
    [InlineData("trim")]
    [InlineData("duplicate")]
    public void Optional_official_names_reject_null_extra_control_oversize_trim_and_duplicate_fields(string mutation)
    {
        var json = NamedCharactersJson();
        if (mutation == "duplicate")
        {
            json = json.Replace(
                "\"name\":\"Weapon ATK\"",
                "\"name\":\"Weapon ATK\",\"name\":\"Other\"",
                StringComparison.Ordinal);
        }
        else
        {
            if (mutation == "null")
            {
                json = json.Replace("\"name\":\"Weapon ATK\"", "\"name\":null", StringComparison.Ordinal);
            }
            else
            {
                var root = JsonNode.Parse(json)!.AsArray();
                var stat = root[0]!.AsObject()["weapon"]!.AsObject()["main"]!.AsObject();
                switch (mutation)
                {
                    case "extra": stat["extra"] = "fixture"; break;
                    case "control": stat["name"] = "Weapon\u0000ATK"; break;
                    case "too-long": stat["name"] = new string('x', 129); break;
                    case "trim": stat["name"] = " Weapon ATK "; break;
                }
                json = root.ToJsonString();
            }
        }

        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot(json)));
    }

    [Theory]
    [InlineData("label")]
    [InlineData("element")]
    public void Raw_unpaired_utf16_escapes_are_rejected_without_throwing(string mutation)
    {
        var json = mutation == "label"
            ? NamedCharactersJson().Replace(
                "\"name\":\"Weapon ATK\"",
                "\"name\":\"Weapon\\uD800ATK\"",
                StringComparison.Ordinal)
            : CharactersJson.Replace(
                "\"element\":\"Ice\"",
                "\"element\":\"Ice\\uD800\"",
                StringComparison.Ordinal);

        Assert.Contains("\\uD800", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot(json)));
    }

    [Fact]
    public void Optional_official_names_accept_valid_supplementary_and_replacement_characters()
    {
        var supplementary = NamedCharactersJson().Replace(
            "\"name\":\"Weapon ATK\"",
            "\"name\":\"Weapon\\uD83D\\uDE00ATK\"",
            StringComparison.Ordinal);
        var replacement = NamedCharactersJson().Replace(
            "\"name\":\"Weapon ATK\"",
            "\"name\":\"Weapon\\uFFFDATK\"",
            StringComparison.Ordinal);

        Assert.True(HoyoLabGenshinBuildRules.IsValid(Snapshot(supplementary)));
        Assert.True(HoyoLabGenshinBuildRules.IsValid(Snapshot(replacement)));
        Assert.Equal("Weapon😀ATK", Snapshot(supplementary).Characters[0]
            .GetProperty("weapon").GetProperty("main").GetProperty("name").GetString());
        Assert.Equal("Weapon�ATK", Snapshot(replacement).Characters[0]
            .GetProperty("weapon").GetProperty("main").GetProperty("name").GetString());
    }

    [Fact]
    public void Known_empty_roster_is_different_from_absent_observation()
    {
        Assert.True(HoyoLabGenshinBuildRules.IsValid(Snapshot("[]")));
        Assert.False(HoyoLabGenshinBuildRules.IsValid(null));
        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot("null")));
    }

    [Theory]
    [InlineData("missing-level")]
    [InlineData("extra-source-text")]
    [InlineData("duplicate-character")]
    [InlineData("duplicate-slot")]
    [InlineData("duplicate-substat")]
    [InlineData("duplicate-property-group")]
    [InlineData("wrong-flag-type")]
    [InlineData("too-many-characters")]
    [InlineData("invalid-element-text")]
    [InlineData("zero-id")]
    [InlineData("not-a-number")]
    public void Rejects_incomplete_or_mixed_projection_without_silently_dropping_fields(string mutation)
    {
        var root = JsonNode.Parse(CharactersJson)!.AsArray();
        var character = root[0]!.AsObject();
        switch (mutation)
        {
            case "missing-level": character.Remove("level"); break;
            case "extra-source-text": character["sourceUrl"] = "https://example.invalid"; break;
            case "duplicate-character": root.Add(character.DeepClone()); break;
            case "duplicate-slot": character["artifacts"]!.AsArray().Add(character["artifacts"]![0]!.DeepClone()); break;
            case "duplicate-substat": character["artifacts"]![0]!["sub"]![1]!["id"] = 20; break;
            case "duplicate-property-group": character["properties"]![1]!["group"] = 0; break;
            case "wrong-flag-type": character["skills"]![0]!["unlocked"] = 1; break;
            case "invalid-element-text": character["element"] = "Ice\n"; break;
            case "zero-id": character["id"] = 0; break;
            case "not-a-number": character["properties"]![0]!["final"] = "13.6%"; break;
            case "too-many-characters":
                for (var index = 1; index <= HoyoLabGenshinBuildRules.MaximumCharacters; index++)
                {
                    var copy = character.DeepClone();
                    copy["id"] = 101 + index;
                    root.Add(copy);
                }
                break;
        }
        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Duplicate_json_members_and_nonfinite_numbers_are_rejected()
    {
        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot(
            CharactersJson.Replace("\"level\":95", "\"level\":95,\"level\":95", StringComparison.Ordinal))));
        Assert.False(HoyoLabGenshinBuildRules.IsValid(Snapshot(
            CharactersJson.Replace("\"final\":13.6", "\"final\":1e9999", StringComparison.Ordinal))));
    }

    [Fact]
    public void Integer_json_spellings_match_browser_number_semantics()
    {
        var snapshot = Snapshot(CharactersJson
            .Replace("\"id\":101", "\"id\":101.0", StringComparison.Ordinal)
            .Replace("\"level\":95", "\"level\":9.5e1", StringComparison.Ordinal));
        Assert.True(HoyoLabGenshinBuildRules.IsValid(snapshot));
        Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(snapshot, Snapshot()));
    }

    [Fact]
    public void Normalized_snapshot_owns_its_json_and_equal_time_comparison_is_semantic()
    {
        HoyoLabGenshinBuildSnapshot owned;
        HoyoLabGenshinBuildSnapshot borrowed;
        using (var document = JsonDocument.Parse(CharactersJson))
        {
            borrowed = new(document.RootElement);
            owned = HoyoLabGenshinBuildRules.Normalize(borrowed);
        }
        Assert.False(HoyoLabGenshinBuildRules.IsValid(borrowed));
        Assert.True(HoyoLabGenshinBuildRules.IsValid(owned));
        Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(owned, Snapshot()));
        Assert.False(HoyoLabGenshinBuildRules.ValuesEqual(owned, Snapshot(
            CharactersJson.Replace("\"level\":95", "\"level\":96", StringComparison.Ordinal))));
    }

    private static string NamedCharactersJson()
    {
        var root = JsonNode.Parse(CharactersJson)!.AsArray();
        var character = root[0]!.AsObject();
        var weapon = character["weapon"]!.AsObject();
        weapon["main"]!.AsObject()["name"] = "Weapon ATK";
        weapon["sub"] = new JsonObject
        {
            ["id"] = 5,
            ["base"] = 0,
            ["added"] = null,
            ["final"] = 0,
            ["percent"] = false,
            ["name"] = "Weapon Bonus",
        };
        var artifact = character["artifacts"]![0]!.AsObject();
        artifact["main"]!.AsObject()["name"] = "Artifact Main";
        artifact["sub"]![0]!.AsObject()["name"] = "Artifact Sub";
        character["properties"]![0]!.AsObject()["name"] = "Displayed Property";
        return root.ToJsonString();
    }
}
