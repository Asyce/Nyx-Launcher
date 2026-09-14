using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinBuildCaptureTests
{
    private const string RoleId = "123456789";
    private const string Server = "os_euro";
    private static readonly PublisherRoleBinding Role = new(RoleId, Server);

    [Fact]
    public void ParseResult_accepts_a_done_snapshot_for_the_exact_saved_genshin_role()
    {
        var result = HoyoLabGenshinBuildCapture.ParseResult(Done(), Role);

        Assert.Equal(HoyoLabGenshinBuildReadStatus.Completed, result.Status);
        var snapshot = Assert.IsType<HoyoLabGenshinBuildSnapshot>(result.Snapshot);
        Assert.True(HoyoLabGenshinBuildRules.IsValid(snapshot));
        Assert.Equal(1, snapshot.Characters.GetArrayLength());
        Assert.Equal(95, snapshot.Characters[0].GetProperty("level").GetInt32());
        Assert.Equal(13, snapshot.Characters[0].GetProperty("skills")[0].GetProperty("level").GetInt32());
        Assert.Equal(RoleId, Role.RoleId);
        Assert.Equal(Server, Role.Server);
        Assert.Equal(nameof(HoyoLabGenshinBuildReadResult), result.ToString());
        Assert.Equal(nameof(HoyoLabGenshinBuildSnapshot), snapshot.ToString());
    }

    [Fact]
    public void ParseResult_accepts_an_explicit_empty_roster_but_never_a_partial_done_result()
    {
        var empty = HoyoLabGenshinBuildCapture.ParseResult(Done("[]"), Role);
        Assert.Equal(HoyoLabGenshinBuildReadStatus.Completed, empty.Status);
        Assert.Empty(Assert.IsType<HoyoLabGenshinBuildSnapshot>(empty.Snapshot).Characters.EnumerateArray());

        foreach (var malformed in new[]
        {
            Done().Replace("\"count\":1", "\"count\":0", StringComparison.Ordinal),
            Done().Replace("\"characters\":", "\"characters\":[{}],\"sourceText\":", StringComparison.Ordinal),
        })
        {
            var result = HoyoLabGenshinBuildCapture.ParseResult(malformed, Role);
            Assert.Equal(HoyoLabGenshinBuildReadStatus.NeedsReview, result.Status);
            Assert.Null(result.Snapshot);
        }
    }

    [Theory]
    [InlineData("login-required", HoyoLabGenshinBuildReadStatus.LoginRequired)]
    [InlineData("canceled", HoyoLabGenshinBuildReadStatus.Canceled)]
    [InlineData("timed-out", HoyoLabGenshinBuildReadStatus.TimedOut)]
    [InlineData("too-large", HoyoLabGenshinBuildReadStatus.TooLarge)]
    public void ParseResult_maps_only_exact_terminal_status_objects(
        string status,
        HoyoLabGenshinBuildReadStatus expected)
    {
        var result = HoyoLabGenshinBuildCapture.ParseResult(
            $$"""{"status":"{{status}}"}""",
            Role);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{", "malformed")]
    [InlineData("[]", "wrong-root")]
    [InlineData("{\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":0,\"characters\":[]}", "missing-status")]
    [InlineData("{\"status\":\"done\",\"status\":\"done\"}", "duplicate-root")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"characters\":[]}", "wrong-count")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"characters\":[],\"source\":\"fixture\"}", "extra-root")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"characters\":null}", "wrong-characters")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1.5,\"characters\":[]}", "fractional-count")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"characters\":[{\"id\":1,\"id\":1}]}", "duplicate-character-field")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"os_euro\",\"count\":1,\"characters\":[{\"id\":1,\"level\":1,\"promotion\":null,\"friendship\":null,\"element\":\"Ice\",\"weapon\":null,\"skills\":[],\"constellations\":[],\"artifacts\":[],\"properties\":[]}]}", "incomplete-character")]
    public void ParseResult_rejects_malformed_duplicate_mismatched_or_source_mixed_json(
        string json,
        string _)
    {
        var result = HoyoLabGenshinBuildCapture.ParseResult(json, Role);

        Assert.Equal(HoyoLabGenshinBuildReadStatus.NeedsReview, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void ParseResult_requires_the_saved_role_and_rejects_nonfinite_values()
    {
        Assert.Equal(
            HoyoLabGenshinBuildReadStatus.NeedsReview,
            HoyoLabGenshinBuildCapture.ParseResult(Done(roleId: "987654321"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinBuildReadStatus.NeedsReview,
            HoyoLabGenshinBuildCapture.ParseResult(Done(server: "os_usa"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinBuildReadStatus.NeedsReview,
            HoyoLabGenshinBuildCapture.ParseResult(
                Done().Replace("13.6", "1e9999", StringComparison.Ordinal),
                Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_utf8_and_text_limits_before_admitting_a_snapshot()
    {
        var tooLong = new string('x', HoyoLabGenshinBuildCapture.MaximumResultBytes + 1);
        Assert.Equal(
            HoyoLabGenshinBuildReadStatus.NeedsReview,
            HoyoLabGenshinBuildCapture.ParseResult(tooLong, Role).Status);

        var tooManyUtf8Bytes = new string('é', HoyoLabGenshinBuildCapture.MaximumResultBytes / 2 + 1);
        Assert.True(tooManyUtf8Bytes.Length <= HoyoLabGenshinBuildCapture.MaximumResultBytes);
        Assert.True(Encoding.UTF8.GetByteCount(tooManyUtf8Bytes) > HoyoLabGenshinBuildCapture.MaximumResultBytes);
        Assert.Equal(
            HoyoLabGenshinBuildReadStatus.NeedsReview,
            HoyoLabGenshinBuildCapture.ParseResult(tooManyUtf8Bytes, Role).Status);
    }

    [Fact]
    public void CreateScript_embeds_the_exact_genshin_contract_without_permission_or_source_logging()
    {
        var script = HoyoLabGenshinBuildCapture.CreateScript(
            "__pengoNyxGenshinBuilds_fixture",
            Role);

        Assert.Contains("\"roleId\":\"123456789\"", script, StringComparison.Ordinal);
        Assert.Contains("\"server\":\"os_euro\"", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumCharacters\":512", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumResultBytes\":3145728", script, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":90000", script, StringComparison.Ordinal);
        Assert.Contains("const bind = { role_id: config.roleId, server: config.server }", script, StringComparison.Ordinal);
        Assert.Contains("game_biz", script, StringComparison.Ordinal);
        Assert.Contains("hk4e_global", script, StringComparison.Ordinal);
        Assert.Contains("api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken", script, StringComparison.Ordinal);
        Assert.Contains("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/", script, StringComparison.Ordinal);
        Assert.Contains("recordBase + 'character/list'", script, StringComparison.Ordinal);
        Assert.Contains("recordBase + 'character/detail'", script, StringComparison.Ordinal);
        Assert.Contains("event/e20200928calculate/v1/sync/avatar/list", script, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RoleId, HoyoLabGenshinBuildCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Server, HoyoLabGenshinBuildCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc", "os_euro")]
    [InlineData("__pengoNyxGenshinBuilds_\n", "os_euro")]
    [InlineData("__pengoNyxGenshinBuilds_fixture", "unknown")]
    public void CreateScript_rejects_untrusted_keys_and_non_genshin_roles(string key, string server)
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinBuildCapture.CreateScript(
            key,
            new PublisherRoleBinding(RoleId, server)));
    }

    [Fact]
    public void CreateScript_rejects_overlong_and_null_keys()
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinBuildCapture.CreateScript(
            "__pengoNyxGenshinBuilds_" + new string('x', 60),
            Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinBuildCapture.CreateScript(null!, Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinBuildCapture.CreateScript(
            "__pengoNyxGenshinBuilds_fixture",
            new PublisherRoleBinding("not-a-role", Server)));
    }

    private static string Done(
        string characters = HoyoLabGenshinBuildSnapshotTests.CharactersJson,
        string? roleId = null,
        string? server = null)
    {
        using var document = JsonDocument.Parse(characters);
        var result = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = roleId ?? RoleId,
            ["server"] = server ?? Server,
            ["count"] = document.RootElement.GetArrayLength(),
            ["characters"] = JsonNode.Parse(characters),
        };
        return result.ToJsonString();
    }
}
