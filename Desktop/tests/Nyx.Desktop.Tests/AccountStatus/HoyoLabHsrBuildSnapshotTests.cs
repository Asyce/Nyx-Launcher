using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabHsrBuildSnapshotTests
{
    // Synthetic contract values only; this is not an account capture.
    internal const string CharactersJson = """
        [{
          "id":1001,"name":"Synthetic Trailblazer","level":80,"rarity":5,
          "element":"Quantum","path":3,"rank":1,"enhancedId":1001001,"avatarType":"Girl",
          "lightCone":{"id":2001,"name":"Synthetic Cone","level":80,"rarity":5,"rank":2},
          "eidolons":[{"id":6001,"name":"First Fixture Eidolon","position":1,"active":true}],
          "relics":[
            {"id":3001,"name":"Synthetic Head","slot":1,"rarity":5,"level":15,
             "main":{"id":7001,"name":"HP","value":"705","times":0,"preview":true},
             "sub":[{"id":7010,"name":"CRIT Rate","value":"0.0%","times":0,"preview":true},
                    {"id":7011,"name":"ATK","value":"1,234.5","times":2,"preview":false}]}
          ],
          "ornaments":[
            {"id":4001,"name":"Synthetic Sphere","slot":5,"rarity":5,"level":15,
             "main":{"id":7101,"name":"Quantum DMG","value":"10.0%","times":0,"preview":true},
             "sub":[]}
          ],
          "properties":[
            {"id":1,"name":"HP","base":"12,345","added":"0","final":"12,345"},
            {"id":2,"name":"CRIT Rate","base":"5.0%","added":null,"final":"5.0%"},
            {"id":3,"name":"Hidden","base":"< 1.0%","added":"≈ 2.0%","final":"< 3.0%"}
          ],
          "traces":[
            {"id":"point-main","type":1,"level":10,"active":true,"rankWorks":true,
             "parent":"","anchor":"origin","specialType":"","stages":[
               {"id":"stage-main","name":"Main Stage","level":10,"active":true,"rankWorks":true,
                 "specialType":"","exclusiveName":"",
                 "linkedAvatars":null,
                 "linkedAvatar":{"id":"avatar-1","name":"Linked Fixture Avatar"},
                 "linkedSkillId":"linked-skill-main","elationPriority":null}]},
            {"id":"point-shared","type":2,"level":8,"active":true,"rankWorks":false,
             "parent":"point-main","anchor":"branch","specialType":"","stages":[]}
          ],
          "specialTraces":[
            {"id":"point-shared","type":7,"level":1,"active":false,"rankWorks":true,
             "parent":"","anchor":"special","specialType":"memosprite","stages":[
               {"id":"stage-special","name":"Special Stage","level":1,"active":false,"rankWorks":true,
                 "specialType":"memosprite","exclusiveName":"Hidden Stage",
                 "linkedAvatars":[],"linkedAvatar":null,"linkedSkillId":null,"elationPriority":""}]}
          ],
          "memosprite":{
            "id":9001,"name":"Synthetic Memosprite","healthHidden":true,
            "properties":[{"id":9002,"name":"Memosprite HP","base":"0.0%","added":null,"final":"0.0%"}],
            "traces":[
              {"id":"sprite-point","type":1,"level":1,"active":true,"rankWorks":true,
               "parent":"","anchor":"","specialType":"","stages":[]}
            ]
          }
        }]
        """;

    [Fact]
    public void Empty_roster_is_a_valid_observation_and_null_is_not()
    {
        Assert.True(HoyoLabHsrBuildRules.IsValid(Snapshot("[]")));
        Assert.False(HoyoLabHsrBuildRules.IsValid(null));
        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot("null")));
    }

    [Fact]
    public void Complete_character_preserves_memosprite_traces_relic_preview_and_display_strings()
    {
        var snapshot = Snapshot();

        Assert.True(HoyoLabHsrBuildRules.IsValid(snapshot));
        var character = snapshot.Characters[0];
        Assert.Equal(1001001, character.GetProperty("enhancedId").GetInt32());
        Assert.Equal("Girl", character.GetProperty("avatarType").GetString());
        Assert.Equal("Synthetic Memosprite", character.GetProperty("memosprite").GetProperty("name").GetString());
        Assert.Equal(2, character.GetProperty("traces").GetArrayLength());
        Assert.Equal("point-shared", character.GetProperty("traces")[1].GetProperty("id").GetString());
        Assert.Equal("point-shared", character.GetProperty("specialTraces")[0].GetProperty("id").GetString());
        var mainStage = character.GetProperty("traces")[0].GetProperty("stages")[0];
        Assert.Equal("Linked Fixture Avatar", mainStage.GetProperty("linkedAvatar").GetProperty("name").GetString());
        Assert.Equal("linked-skill-main", mainStage.GetProperty("linkedSkillId").GetString());
        Assert.Equal(JsonValueKind.Null, mainStage.GetProperty("linkedAvatars").ValueKind);
        Assert.Equal(JsonValueKind.Null, mainStage.GetProperty("elationPriority").ValueKind);
        var specialStage = character.GetProperty("specialTraces")[0].GetProperty("stages")[0];
        Assert.Equal(0, specialStage.GetProperty("linkedAvatars").GetArrayLength());
        Assert.Equal("", specialStage.GetProperty("elationPriority").GetString());
        Assert.Equal(0, character.GetProperty("relics")[0].GetProperty("main").GetProperty("times").GetInt32());
        Assert.True(character.GetProperty("relics")[0].GetProperty("main").GetProperty("preview").GetBoolean());
        Assert.Equal("0.0%", character.GetProperty("relics")[0].GetProperty("sub")[0].GetProperty("value").GetString());
        Assert.Equal("1,234.5", character.GetProperty("relics")[0].GetProperty("sub")[1].GetProperty("value").GetString());
        Assert.Equal("< 3.0%", character.GetProperty("properties")[2].GetProperty("final").GetString());
        Assert.Equal("≈ 2.0%", character.GetProperty("properties")[2].GetProperty("added").GetString());
        Assert.Equal(nameof(HoyoLabHsrBuildSnapshot), snapshot.ToString());
    }

    [Fact]
    public void Optional_light_cone_and_memosprite_nulls_are_distinct_from_missing_fields()
    {
        var root = Root();
        var character = root[0]!.AsObject();
        character["lightCone"] = JsonValue.Create<string?>(null);
        character["memosprite"] = JsonValue.Create<string?>(null);
        var snapshot = Snapshot(root.ToJsonString());

        Assert.Equal(JsonValueKind.Null, snapshot.Characters[0].GetProperty("lightCone").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.Characters[0].GetProperty("memosprite").ValueKind);
        Assert.True(HoyoLabHsrBuildRules.IsValid(snapshot));

        character.Remove("memosprite");
        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("duplicate-numeric-id")]
    public void Unknown_duplicate_and_numeric_equivalent_identity_fields_are_rejected(string mutation)
    {
        var json = CharactersJson;
        if (mutation == "unknown-field")
        {
            var root = Root();
            root[0]!.AsObject()["sourceText"] = "fixture";
            json = root.ToJsonString();
        }
        else if (mutation == "duplicate-field")
        {
            json = json.Replace("\"level\":80", "\"level\":80,\"level\":80", StringComparison.Ordinal);
        }
        else
        {
            json = json.Replace("\"id\":2,\"name\":\"CRIT Rate\"", "\"id\":1.0,\"name\":\"CRIT Rate\"", StringComparison.Ordinal);
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(json)));
    }

    [Theory]
    [InlineData("relic-slot-in-ornament-range")]
    [InlineData("ornament-slot-in-relic-range")]
    [InlineData("duplicate-trace-id")]
    [InlineData("duplicate-artifact-substat-id")]
    public void Group_boundaries_and_per_group_numeric_identities_are_enforced(string mutation)
    {
        var root = Root();
        var character = root[0]!.AsObject();
        switch (mutation)
        {
            case "relic-slot-in-ornament-range": character["relics"]![0]!["slot"] = 5; break;
            case "ornament-slot-in-relic-range": character["ornaments"]![0]!["slot"] = 4; break;
            case "duplicate-trace-id": character["traces"]!.AsArray().Add(character["traces"]![0]!.DeepClone()); break;
            case "duplicate-artifact-substat-id": character["relics"]![0]!["sub"]![1]!["id"] = 7010; break;
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Theory]
    [InlineData("missing-enhanced-id")]
    [InlineData("missing-relic-main")]
    [InlineData("missing-trace-stages")]
    [InlineData("missing-memosprite-property")]
    public void Required_fields_cannot_be_dropped(string mutation)
    {
        var root = Root();
        var character = root[0]!.AsObject();
        switch (mutation)
        {
            case "missing-enhanced-id": character.Remove("enhancedId"); break;
            case "missing-relic-main": character["relics"]![0]!.AsObject().Remove("main"); break;
            case "missing-trace-stages": character["traces"]![0]!.AsObject().Remove("stages"); break;
            case "missing-memosprite-property": character["memosprite"]!.AsObject().Remove("properties"); break;
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Theory]
    [InlineData("relic-times-null")]
    [InlineData("property-final-null")]
    [InlineData("rank-null")]
    public void Required_zero_or_text_values_do_not_become_nullable(string mutation)
    {
        var root = Root();
        var character = root[0]!.AsObject();
        switch (mutation)
        {
            case "relic-times-null": character["relics"]![0]!["main"]!["times"] = JsonValue.Create<string?>(null); break;
            case "property-final-null": character["properties"]![0]!["final"] = JsonValue.Create<string?>(null); break;
            case "rank-null": character["rank"] = JsonValue.Create<string?>(null); break;
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
        Assert.True(HoyoLabHsrBuildRules.IsValid(Snapshot()));
    }

    [Theory]
    [InlineData("control")]
    [InlineData("format")]
    [InlineData("supplementary-format")]
    public void Display_text_rejects_control_and_format_unicode(string mutation)
    {
        var root = Root();
        var character = root[0]!.AsObject();
        var value = mutation switch
        {
            "control" => "Synthetic\u0000Name",
            "format" => "Synthetic\u200BName",
            "supplementary-format" => "Synthetic\U000E0001Name",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        character["name"] = value;

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Display_text_rejects_a_raw_unpaired_utf16_surrogate_without_throwing()
    {
        // JsonNode serialization replaces a lone surrogate with U+FFFD; keep the
        // escape in raw JSON so JsonDocument must expose the original UTF-16 unit.
        var json = CharactersJson.Replace(
            "\"name\":\"Synthetic Trailblazer\"",
            "\"name\":\"Synthetic\\uD800Name\"",
            StringComparison.Ordinal);
        Assert.Contains("\\uD800", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var snapshot = new HoyoLabHsrBuildSnapshot(document.RootElement.Clone());
        var valid = false;
        var exception = Record.Exception(() => valid = HoyoLabHsrBuildRules.IsValid(snapshot));

        Assert.Null(exception);
        Assert.False(valid);
    }

    [Fact]
    public void Display_text_accepts_valid_supplementary_and_replacement_characters()
    {
        var supplementary = CharactersJson.Replace(
            "\"name\":\"Synthetic Trailblazer\"",
            "\"name\":\"Synthetic\\uD83D\\uDE00Name\"",
            StringComparison.Ordinal);
        var replacement = CharactersJson.Replace(
            "\"name\":\"Synthetic Trailblazer\"",
            "\"name\":\"Synthetic\\uFFFDName\"",
            StringComparison.Ordinal);

        Assert.True(HoyoLabHsrBuildRules.IsValid(Snapshot(supplementary)));
        Assert.True(HoyoLabHsrBuildRules.IsValid(Snapshot(replacement)));
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("traces")]
    [InlineData("relic-substats")]
    [InlineData("linked-avatars")]
    public void Excessive_arrays_are_rejected(string mutation)
    {
        var root = Root();
        var character = root[0]!.AsObject();
        switch (mutation)
        {
            case "characters":
                for (var index = 1; index <= HoyoLabHsrBuildRules.MaximumCharacters; index++)
                {
                    var copy = character.DeepClone();
                    copy["id"] = 2000 + index;
                    root.Add(copy);
                }
                break;
            case "traces":
                for (var index = 1; index <= 127; index++)
                {
                    var copy = character["traces"]![0]!.DeepClone();
                    copy["id"] = "extra-trace-" + index;
                    character["traces"]!.AsArray().Add(copy);
                }
                break;
            case "relic-substats":
                for (var index = 1; index <= 3; index++)
                {
                    var copy = character["relics"]![0]!["sub"]![0]!.DeepClone();
                    copy["id"] = 8000 + index;
                    character["relics"]![0]!["sub"]!.AsArray().Add(copy);
                }
                break;
            case "linked-avatars":
                for (var index = 1; index <= 33; index++)
                    character["specialTraces"]![0]!["stages"]![0]!["linkedAvatars"]!.AsArray().Add(
                        new JsonObject { ["id"] = "extra-avatar-" + index, ["name"] = "Fixture Avatar" });
                break;
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(Snapshot(root.ToJsonString())));
    }

    [Fact]
    public void Normalize_owns_a_deep_clone_and_values_equal_uses_numeric_semantics()
    {
        HoyoLabHsrBuildSnapshot owned;
        HoyoLabHsrBuildSnapshot borrowed;
        using (var document = JsonDocument.Parse(CharactersJson))
        {
            borrowed = new(document.RootElement);
            owned = HoyoLabHsrBuildRules.Normalize(borrowed);
        }

        Assert.False(HoyoLabHsrBuildRules.IsValid(borrowed));
        Assert.True(HoyoLabHsrBuildRules.IsValid(owned));
        Assert.True(HoyoLabHsrBuildRules.ValuesEqual(owned, Snapshot()));
        Assert.True(HoyoLabHsrBuildRules.ValuesEqual(
            Snapshot(),
            Snapshot(CharactersJson.Replace("\"level\":80", "\"level\":8.0e1", StringComparison.Ordinal))));
        Assert.False(HoyoLabHsrBuildRules.ValuesEqual(
            owned,
            Snapshot(CharactersJson.Replace("\"level\":80", "\"level\":81", StringComparison.Ordinal))));
    }

    private static HoyoLabHsrBuildSnapshot Snapshot(string json = CharactersJson)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }

    private static JsonArray Root(string json = CharactersJson) => JsonNode.Parse(json)!.AsArray();
}
