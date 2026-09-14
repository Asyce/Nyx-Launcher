using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinExplorationCaptureTests
{
    private const string RoleId = "123456789";
    private const string Server = "os_euro";
    private static readonly PublisherRoleBinding Role = new(RoleId, Server);

    [Fact]
    public void ParseResult_accepts_done_exploration_for_the_exact_saved_genshin_role()
    {
        var result = HoyoLabGenshinExplorationCapture.ParseResult(Done(), Role);

        Assert.Equal(HoyoLabGenshinExplorationReadStatus.Completed, result.Status);
        var snapshot = Assert.IsType<HoyoLabGenshinExplorationSnapshot>(result.Snapshot);
        Assert.True(HoyoLabGenshinExplorationRules.IsValid(snapshot));
        Assert.Equal(3, snapshot.Data.GetProperty("worlds").GetArrayLength());
        Assert.Equal(15, snapshot.Data.GetProperty("counts").EnumerateObject().Count());
        Assert.Equal(nameof(HoyoLabGenshinExplorationReadResult), result.ToString());
        Assert.Equal(nameof(HoyoLabGenshinExplorationSnapshot), snapshot.ToString());
    }

    [Fact]
    public void ParseResult_accepts_an_explicit_empty_world_collection_but_rejects_partial_done_data()
    {
        var empty = HoyoLabGenshinExplorationCapture.ParseResult(Done(EmptyDataJson()), Role);
        Assert.Equal(HoyoLabGenshinExplorationReadStatus.Completed, empty.Status);
        Assert.Empty(Assert.IsType<HoyoLabGenshinExplorationSnapshot>(empty.Snapshot).Data.GetProperty("worlds").EnumerateArray());

        foreach (var malformed in new[]
        {
            Done().Replace("\"count\":3", "\"count\":2", StringComparison.Ordinal),
            Done().Replace("\"exploration\":", "\"exploration\":{\"counts\":{},\"worlds\":[],\"displayGroups\":[]},\"sourceText\":", StringComparison.Ordinal),
        })
        {
            var result = HoyoLabGenshinExplorationCapture.ParseResult(malformed, Role);
            Assert.Equal(HoyoLabGenshinExplorationReadStatus.NeedsReview, result.Status);
            Assert.Null(result.Snapshot);
        }
    }

    [Theory]
    [InlineData("login-required", HoyoLabGenshinExplorationReadStatus.LoginRequired)]
    [InlineData("canceled", HoyoLabGenshinExplorationReadStatus.Canceled)]
    [InlineData("timed-out", HoyoLabGenshinExplorationReadStatus.TimedOut)]
    [InlineData("too-large", HoyoLabGenshinExplorationReadStatus.TooLarge)]
    public void ParseResult_maps_only_exact_terminal_status_objects(
        string status,
        HoyoLabGenshinExplorationReadStatus expected)
    {
        var result = HoyoLabGenshinExplorationCapture.ParseResult(
            $$"""{"status":"{{status}}"}""",
            Role);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{", "malformed")]
    [InlineData("[]", "wrong-root")]
    [InlineData("{\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":0,\"exploration\":{}}", "missing-status")]
    [InlineData("{\"status\":\"done\",\"status\":\"done\"}", "duplicate-root")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"exploration\":null}", "wrong-exploration")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1.5,\"exploration\":{}}", "fractional-count")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"exploration\":{\"counts\":{},\"worlds\":[],\"displayGroups\":[]},\"inventory\":[]}", "extra-root")]
    public void ParseResult_rejects_malformed_duplicate_mismatched_or_source_mixed_json(
        string json,
        string _)
    {
        var result = HoyoLabGenshinExplorationCapture.ParseResult(json, Role);

        Assert.Equal(HoyoLabGenshinExplorationReadStatus.NeedsReview, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void ParseResult_requires_the_saved_role_count_and_finite_projection_values()
    {
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(Done(roleId: "987654321"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(Done(server: "os_usa"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(
                Done().Replace("\"percentage\":1234", "\"percentage\":1e9999", StringComparison.Ordinal),
                Role).Status);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(
                Done().Replace("\"count\":3", "\"count\":4", StringComparison.Ordinal),
                Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_utf8_and_text_limits_before_admitting_a_snapshot()
    {
        var tooLong = new string('x', HoyoLabGenshinExplorationCapture.MaximumResultBytes + 1);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(tooLong, Role).Status);

        var tooManyUtf8Bytes = new string('é', HoyoLabGenshinExplorationCapture.MaximumResultBytes / 2 + 1);
        Assert.True(tooManyUtf8Bytes.Length <= HoyoLabGenshinExplorationCapture.MaximumResultBytes);
        Assert.True(Encoding.UTF8.GetByteCount(tooManyUtf8Bytes) > HoyoLabGenshinExplorationCapture.MaximumResultBytes);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(tooManyUtf8Bytes, Role).Status);
        Assert.Equal(
            HoyoLabGenshinExplorationReadStatus.NeedsReview,
            HoyoLabGenshinExplorationCapture.ParseResult(null!, Role).Status);
    }

    [Fact]
    public void CreateScript_embeds_the_exact_read_only_genshin_exploration_contract()
    {
        var script = HoyoLabGenshinExplorationCapture.CreateScript(
            "__pengoNyxGenshinExploration_fixture",
            Role);

        Assert.Contains("\"roleId\":\"123456789\"", script, StringComparison.Ordinal);
        Assert.Contains("\"server\":\"os_euro\"", script, StringComparison.Ordinal);
        Assert.Contains("\"gameBiz\":\"hk4e_global\"", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumWorlds\":512", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumResultBytes\":3145728", script, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":45000", script, StringComparison.Ordinal);
        Assert.Contains("api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken", script, StringComparison.Ordinal);
        Assert.Contains("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/", script, StringComparison.Ordinal);
        Assert.Contains("recordUrl('index')", script, StringComparison.Ordinal);
        Assert.Contains("world_explorations", script, StringComparison.Ordinal);
        Assert.Contains("world_exploration_display", script, StringComparison.Ordinal);
        Assert.Contains("natan_reputation", script, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RoleId, HoyoLabGenshinExplorationCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Server, HoyoLabGenshinExplorationCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc", "os_euro")]
    [InlineData("__pengoNyxGenshinExploration_\n", "os_euro")]
    [InlineData("__pengoNyxGenshinExploration_fixture", "unknown")]
    [InlineData("__pengoNyxGenshinExploration_fixture", "prod_official_eur")]
    public void CreateScript_rejects_untrusted_keys_and_non_genshin_roles(string key, string server)
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinExplorationCapture.CreateScript(
            key,
            new PublisherRoleBinding(RoleId, server)));
    }

    [Fact]
    public void CreateScript_rejects_overlong_and_null_keys_or_roles()
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinExplorationCapture.CreateScript(
            "__pengoNyxGenshinExploration_" + new string('x', 61),
            Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinExplorationCapture.CreateScript(null!, Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinExplorationCapture.CreateScript(
            "__pengoNyxGenshinExploration_fixture",
            new PublisherRoleBinding("not-a-role", Server)));
    }

    private static string Done(
        string exploration = HoyoLabGenshinExplorationSnapshotTests.DataJson,
        string? roleId = null,
        string? server = null)
    {
        using var document = JsonDocument.Parse(exploration);
        var result = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = roleId ?? RoleId,
            ["server"] = server ?? Server,
            ["count"] = document.RootElement.GetProperty("worlds").GetArrayLength(),
            ["exploration"] = JsonNode.Parse(exploration),
        };
        return result.ToJsonString();
    }

    private static string EmptyDataJson()
    {
        var root = JsonNode.Parse(HoyoLabGenshinExplorationSnapshotTests.DataJson)!.AsObject();
        root["worlds"] = new JsonArray();
        root["displayGroups"] = new JsonArray();
        return root.ToJsonString();
    }
}
