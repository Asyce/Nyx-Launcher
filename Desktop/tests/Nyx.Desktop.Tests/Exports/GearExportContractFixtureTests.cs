using System.Security.Cryptography;
using System.Text.Json;

namespace Nyx.Desktop.Tests.Exports;

public sealed class GearExportContractFixtureTests
{
    private const string HsrFixtureSha256 =
        "8b22587549c236134d6f3acba9b96b11ca000ad7273bdc1053cc903ec96ad9dc";
    private const string GenshinFixtureSha256 =
        "3f91ecb188798db18de8e782ce88360a4f37864d37c55285957a91af8f8d1f64";

    private static string ContractsRoot => Path.Combine(AppContext.BaseDirectory, "Contracts");

    [Fact]
    public void Pinned_consumers_and_sanitized_fixtures_keep_the_minimum_contracts()
    {
        var hsr = ReadFixture("gear-export-hsr-fribbels-v4.fixture.json", HsrFixtureSha256);
        var hsrRoot = hsr.RootElement;
        Assert.Equal("reliquary_archiver", hsrRoot.GetProperty("source").GetString());
        Assert.Equal("v1.5.0-nyx-launcher-fixture", hsrRoot.GetProperty("build").GetString());
        Assert.Equal(4, hsrRoot.GetProperty("version").GetInt32());
        Assert.Equal(0, hsrRoot.GetProperty("metadata").GetProperty("uid").GetInt32());
        Assert.Equal("Stelle", hsrRoot.GetProperty("metadata").GetProperty("trailblazer").GetString());
        Assert.Empty(hsrRoot.GetProperty("materials").EnumerateArray());
        Assert.Empty(hsrRoot.GetProperty("characters").EnumerateArray());
        Assert.Empty(hsrRoot.GetProperty("light_cones").EnumerateArray());

        var relic = Assert.Single(hsrRoot.GetProperty("relics").EnumerateArray());
        Assert.Equal("101", relic.GetProperty("set_id").GetString());
        Assert.Equal(5, relic.GetProperty("rarity").GetInt32());
        Assert.Equal(15, relic.GetProperty("level").GetInt32());
        Assert.Equal("Head", relic.GetProperty("slot").GetString());
        Assert.Equal("HP", relic.GetProperty("mainstat").GetString());
        Assert.False(relic.GetProperty("lock").GetBoolean());
        Assert.False(relic.GetProperty("discard").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(relic.GetProperty("_uid").GetString()));
        Assert.NotEmpty(relic.GetProperty("substats").EnumerateArray());
        foreach (var substat in relic.GetProperty("substats").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(substat.GetProperty("key").GetString()));
            Assert.True(substat.GetProperty("value").GetDouble() >= 0);
            Assert.True(substat.GetProperty("count").GetInt32() > 0);
            Assert.InRange(substat.GetProperty("step").GetInt32(), 0, 2);
        }
        Assert.NotEmpty(relic.GetProperty("reroll_substats").EnumerateArray());
        Assert.NotEmpty(relic.GetProperty("preview_substats").EnumerateArray());

        var good = ReadFixture("gear-export-genshin-good-v3.fixture.json", GenshinFixtureSha256);
        var goodRoot = good.RootElement;
        Assert.Equal("GOOD", goodRoot.GetProperty("format").GetString());
        Assert.Equal("Nyx Launcher", goodRoot.GetProperty("source").GetString());
        Assert.Equal(3, goodRoot.GetProperty("version").GetInt32());
        Assert.False(goodRoot.TryGetProperty("characters", out _));
        Assert.False(goodRoot.TryGetProperty("weapons", out _));

        var artifact = Assert.Single(goodRoot.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("EmblemOfSeveredFate", artifact.GetProperty("setKey").GetString());
        Assert.Equal("flower", artifact.GetProperty("slotKey").GetString());
        Assert.Equal(5, artifact.GetProperty("rarity").GetInt32());
        Assert.Equal(0, artifact.GetProperty("level").GetInt32());
        Assert.Equal("hp", artifact.GetProperty("mainStatKey").GetString());
        Assert.False(artifact.GetProperty("lock").GetBoolean());
        Assert.NotEmpty(artifact.GetProperty("substats").EnumerateArray());
        foreach (var substat in artifact.GetProperty("substats").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(substat.GetProperty("key").GetString()));
            Assert.True(substat.GetProperty("value").GetDouble() >= 0);
            Assert.False(substat.TryGetProperty("initialValue", out _));
        }
        Assert.Equal(4, artifact.GetProperty("totalRolls").GetInt32());
        Assert.False(artifact.GetProperty("astralMark").GetBoolean());
        Assert.False(artifact.GetProperty("elixirCrafted").GetBoolean());
        Assert.Empty(artifact.GetProperty("unactivatedSubstats").EnumerateArray());
    }

    private static JsonDocument ReadFixture(string name, string expectedSha256)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ContractsRoot, name));
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return JsonDocument.Parse(bytes);
    }
}
