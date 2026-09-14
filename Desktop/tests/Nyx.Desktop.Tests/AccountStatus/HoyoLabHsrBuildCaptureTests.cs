using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabHsrBuildCaptureTests
{
    private const string RoleId = "123456789";
    private const string Server = "prod_official_eur";
    private static readonly PublisherRoleBinding Role = new(RoleId, Server);

    [Fact]
    public void ParseResult_accepts_done_snapshot_for_the_exact_saved_star_rail_role()
    {
        var result = HoyoLabHsrBuildCapture.ParseResult(Done(), Role);

        Assert.Equal(HoyoLabHsrBuildReadStatus.Completed, result.Status);
        var snapshot = Assert.IsType<HoyoLabHsrBuildSnapshot>(result.Snapshot);
        Assert.True(HoyoLabHsrBuildRules.IsValid(snapshot));
        Assert.Equal(1, snapshot.Characters.GetArrayLength());
        Assert.Equal(80, snapshot.Characters[0].GetProperty("level").GetInt32());
        Assert.Equal("linked-skill-main", snapshot.Characters[0].GetProperty("traces")[0]
            .GetProperty("stages")[0].GetProperty("linkedSkillId").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.Characters[0].GetProperty("traces")[0]
            .GetProperty("stages")[0].GetProperty("linkedAvatars").ValueKind);
        Assert.Equal(nameof(HoyoLabHsrBuildReadResult), result.ToString());
        Assert.Equal(nameof(HoyoLabHsrBuildSnapshot), snapshot.ToString());
    }

    [Fact]
    public void ParseResult_accepts_an_explicit_empty_roster_but_never_a_partial_done_result()
    {
        var empty = HoyoLabHsrBuildCapture.ParseResult(Done("[]"), Role);
        Assert.Equal(HoyoLabHsrBuildReadStatus.Completed, empty.Status);
        Assert.Empty(Assert.IsType<HoyoLabHsrBuildSnapshot>(empty.Snapshot).Characters.EnumerateArray());

        foreach (var malformed in new[]
        {
            Done().Replace("\"count\":1", "\"count\":0", StringComparison.Ordinal),
            Done().Replace("\"characters\":", "\"characters\":[{}],\"sourceText\":", StringComparison.Ordinal),
        })
        {
            var result = HoyoLabHsrBuildCapture.ParseResult(malformed, Role);
            Assert.Equal(HoyoLabHsrBuildReadStatus.NeedsReview, result.Status);
            Assert.Null(result.Snapshot);
        }
    }

    [Theory]
    [InlineData("login-required", HoyoLabHsrBuildReadStatus.LoginRequired)]
    [InlineData("canceled", HoyoLabHsrBuildReadStatus.Canceled)]
    [InlineData("timed-out", HoyoLabHsrBuildReadStatus.TimedOut)]
    [InlineData("too-large", HoyoLabHsrBuildReadStatus.TooLarge)]
    public void ParseResult_maps_only_exact_terminal_status_objects(
        string status,
        HoyoLabHsrBuildReadStatus expected)
    {
        var result = HoyoLabHsrBuildCapture.ParseResult(
            $$"""{"status":"{{status}}"}""",
            Role);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("{", "malformed")]
    [InlineData("[]", "wrong-root")]
    [InlineData("{\"roleId\":\"123456789\",\"server\":\"prod_official_eur\",\"count\":0,\"characters\":[]}", "missing-status")]
    [InlineData("{\"status\":\"done\",\"status\":\"done\"}", "duplicate-root")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"prod_official_eur\",\"count\":1,\"characters\":null}", "wrong-characters")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"prod_official_eur\",\"count\":1.5,\"characters\":[]}", "fractional-count")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"prod_official_eur\",\"count\":1,\"characters\":[{\"id\":1,\"id\":1}]}", "duplicate-character-field")]
    [InlineData("{\"status\":\"done\",\"roleId\":\"123456789\",\"server\":\"prod_official_eur\",\"count\":1,\"characters\":[{}]}", "incomplete-character")]
    public void ParseResult_rejects_malformed_duplicate_mismatched_or_partial_json(
        string json,
        string _)
    {
        var result = HoyoLabHsrBuildCapture.ParseResult(json, Role);

        Assert.Equal(HoyoLabHsrBuildReadStatus.NeedsReview, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void ParseResult_requires_the_saved_role_count_and_finite_values()
    {
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(Done(roleId: "987654321"), Role).Status);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(Done(server: "prod_official_usa"), Role).Status);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(
                Done().Replace("\"final\":\"12,345\"", "\"final\":1e9999", StringComparison.Ordinal),
                Role).Status);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(
                Done().Replace("\"count\":1", "\"count\":2", StringComparison.Ordinal),
                Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_utf8_and_text_limits_before_admitting_a_snapshot()
    {
        var tooLong = new string('x', HoyoLabHsrBuildCapture.MaximumResultBytes + 1);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(tooLong, Role).Status);

        var tooManyUtf8Bytes = new string('é', HoyoLabHsrBuildCapture.MaximumResultBytes / 2 + 1);
        Assert.True(tooManyUtf8Bytes.Length <= HoyoLabHsrBuildCapture.MaximumResultBytes);
        Assert.True(Encoding.UTF8.GetByteCount(tooManyUtf8Bytes) > HoyoLabHsrBuildCapture.MaximumResultBytes);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(tooManyUtf8Bytes, Role).Status);
        Assert.Equal(
            HoyoLabHsrBuildReadStatus.NeedsReview,
            HoyoLabHsrBuildCapture.ParseResult(null!, Role).Status);
    }

    [Fact]
    public void CreateScript_embeds_the_exact_star_rail_contract_without_permissions_or_source_logging()
    {
        var script = HoyoLabHsrBuildCapture.CreateScript(
            "__pengoNyxHsrBuilds_fixture",
            Role);

        Assert.Contains("\"roleId\":\"123456789\"", script, StringComparison.Ordinal);
        Assert.Contains("\"server\":\"prod_official_eur\"", script, StringComparison.Ordinal);
        Assert.Contains("\"gameBiz\":\"hkrpg_global\"", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumCharacters\":512", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumResultBytes\":3145728", script, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":45000", script, StringComparison.Ordinal);
        Assert.Contains("api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken", script, StringComparison.Ordinal);
        Assert.Contains("https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/", script, StringComparison.Ordinal);
        Assert.Contains("recordUrl('index')", script, StringComparison.Ordinal);
        Assert.Contains("recordUrl('avatar/info')", script, StringComparison.Ordinal);
        Assert.Contains("need_wiki", script, StringComparison.Ordinal);
        Assert.Contains("servant_detail", script, StringComparison.Ordinal);
        Assert.Contains("property_info", script, StringComparison.Ordinal);
        Assert.Contains("linked_avatar", script, StringComparison.Ordinal);
        Assert.Contains("linked_skill_id", script, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RoleId, HoyoLabHsrBuildCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Server, HoyoLabHsrBuildCapture.ParseResult(Done(), Role).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc", "prod_official_eur")]
    [InlineData("__pengoNyxHsrBuilds_\n", "prod_official_eur")]
    [InlineData("__pengoNyxHsrBuilds_fixture", "unknown")]
    [InlineData("__pengoNyxHsrBuilds_fixture", "os_euro")]
    public void CreateScript_rejects_untrusted_keys_and_non_star_rail_roles(string key, string server)
    {
        Assert.Throws<ArgumentException>(() => HoyoLabHsrBuildCapture.CreateScript(
            key,
            new PublisherRoleBinding(RoleId, server)));
    }

    [Fact]
    public void CreateScript_rejects_overlong_and_null_keys_or_roles()
    {
        Assert.Throws<ArgumentException>(() => HoyoLabHsrBuildCapture.CreateScript(
            "__pengoNyxHsrBuilds_" + new string('x', 61),
            Role));
        Assert.Throws<ArgumentException>(() => HoyoLabHsrBuildCapture.CreateScript(null!, Role));
        Assert.Throws<ArgumentException>(() => HoyoLabHsrBuildCapture.CreateScript(
            "__pengoNyxHsrBuilds_fixture",
            new PublisherRoleBinding("not-a-role", Server)));
    }

    private static string Done(
        string characters = HoyoLabHsrBuildSnapshotTests.CharactersJson,
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
