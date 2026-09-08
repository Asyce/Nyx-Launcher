using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGenshinEventsCaptureTests
{
    private const string RoleId = "123456789";
    private const string Server = "os_euro";
    private static readonly PublisherRoleBinding Role = new(RoleId, Server);

    [Fact]
    public void ParseResult_accepts_done_events_for_the_exact_saved_genshin_role_and_count()
    {
        var result = HoyoLabGenshinEventsCapture.ParseResult(Done(), Role);

        Assert.Equal(HoyoLabGenshinEventsReadStatus.Completed, result.Status);
        var snapshot = Assert.IsType<HoyoLabGenshinEventsSnapshot>(result.Snapshot);
        Assert.True(HoyoLabGenshinEventsRules.IsValid(snapshot));
        Assert.Equal(2, snapshot.Data.GetProperty("activities").GetArrayLength());
        Assert.Equal(2, snapshot.Data.GetProperty("fixed").GetArrayLength());
        Assert.Equal(4, snapshot.Data.GetProperty("activities").GetArrayLength()
            + snapshot.Data.GetProperty("fixed").GetArrayLength());
        Assert.Equal(nameof(HoyoLabGenshinEventsReadResult), result.ToString());
        Assert.Equal(nameof(HoyoLabGenshinEventsSnapshot), snapshot.ToString());
        Assert.DoesNotContain(RoleId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Server, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_accepts_an_explicit_empty_event_collection_but_rejects_partial_done_data()
    {
        var empty = Root(HoyoLabGenshinEventsSnapshotTests.DataJson);
        empty["activities"] = new JsonArray();
        empty["fixed"] = new JsonArray();
        empty["selected"] = new JsonArray();
        var emptyResult = HoyoLabGenshinEventsCapture.ParseResult(Done(empty.ToJsonString()), Role);
        Assert.Equal(HoyoLabGenshinEventsReadStatus.Completed, emptyResult.Status);
        Assert.Empty(Assert.IsType<HoyoLabGenshinEventsSnapshot>(emptyResult.Snapshot)
            .Data.GetProperty("activities").EnumerateArray());

        var partial = Root(HoyoLabGenshinEventsSnapshotTests.DataJson);
        partial["activities"]![0]!.AsObject().Remove("exploration");
        var partialResult = HoyoLabGenshinEventsCapture.ParseResult(Done(partial.ToJsonString()), Role);
        Assert.Equal(HoyoLabGenshinEventsReadStatus.NeedsReview, partialResult.Status);
        Assert.Null(partialResult.Snapshot);
    }

    [Theory]
    [InlineData("login-required", HoyoLabGenshinEventsReadStatus.LoginRequired)]
    [InlineData("canceled", HoyoLabGenshinEventsReadStatus.Canceled)]
    [InlineData("timed-out", HoyoLabGenshinEventsReadStatus.TimedOut)]
    [InlineData("too-large", HoyoLabGenshinEventsReadStatus.TooLarge)]
    public void ParseResult_maps_only_exact_terminal_status_objects(
        string status,
        HoyoLabGenshinEventsReadStatus expected)
    {
        var result = HoyoLabGenshinEventsCapture.ParseResult(
            $$"""{"status":"{{status}}"}""",
            Role);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult($$"""{"status":"{{status}}","roleId":"{{RoleId}}"}""", Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_malformed_duplicate_mismatched_or_source_mixed_json()
    {
        var missingStatus = Done();
        var missingRoot = JsonNode.Parse(missingStatus)!.AsObject();
        missingRoot.Remove("status");

        var wrongEvents = JsonNode.Parse(Done())!.AsObject();
        wrongEvents["events"] = null;

        var extraRoot = JsonNode.Parse(Done())!.AsObject();
        extraRoot["sourceText"] = "Synthetic source";

        var missingDetail = Root();
        missingDetail["activities"]![0]!.AsObject().Remove("exploration");

        var malformed = new[]
        {
            "{",
            "[]",
            missingRoot.ToJsonString(),
            "{\"status\":\"done\",\"status\":\"done\"}",
            wrongEvents.ToJsonString(),
            extraRoot.ToJsonString(),
            Done().Replace("\"count\":4", "\"count\":4.5", StringComparison.Ordinal),
            Done().Replace("\"count\":4", "\"count\":3", StringComparison.Ordinal),
            Done(missingDetail.ToJsonString()),
            Done().Replace("\"percentage\":1234.5", "\"percentage\":1e9999", StringComparison.Ordinal),
        };

        foreach (var json in malformed)
        {
            var result = HoyoLabGenshinEventsCapture.ParseResult(json, Role);
            Assert.Equal(HoyoLabGenshinEventsReadStatus.NeedsReview, result.Status);
            Assert.Null(result.Snapshot);
        }
    }

    [Fact]
    public void ParseResult_requires_the_saved_role_and_activity_plus_fixed_count()
    {
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(Done(roleId: "987654321"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(Done(server: "os_usa"), Role).Status);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(Done(count: 3), Role).Status);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(Done(count: -1), Role).Status);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(
                Done().Replace("\"count\":4", "\"count\":2147483648", StringComparison.Ordinal), Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_utf8_and_text_limits_before_admitting_a_snapshot()
    {
        var tooLong = new string('x', HoyoLabGenshinEventsCapture.MaximumResultBytes + 1);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(tooLong, Role).Status);

        var tooManyUtf8Bytes = new string('é', HoyoLabGenshinEventsCapture.MaximumResultBytes / 2 + 1);
        Assert.True(tooManyUtf8Bytes.Length <= HoyoLabGenshinEventsCapture.MaximumResultBytes);
        Assert.True(Encoding.UTF8.GetByteCount(tooManyUtf8Bytes) > HoyoLabGenshinEventsCapture.MaximumResultBytes);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(tooManyUtf8Bytes, Role).Status);
        Assert.Equal(
            HoyoLabGenshinEventsReadStatus.NeedsReview,
            HoyoLabGenshinEventsCapture.ParseResult(null!, Role).Status);
    }

    [Fact]
    public void CreateScript_embeds_the_exact_read_only_genshin_event_contract()
    {
        var script = HoyoLabGenshinEventsCapture.CreateScript(
            "__pengoNyxGenshinEvents_fixture",
            Role);

        Assert.Contains("\"roleId\":\"123456789\"", script, StringComparison.Ordinal);
        Assert.Contains("\"server\":\"os_euro\"", script, StringComparison.Ordinal);
        Assert.Contains("\"gameBiz\":\"hk4e_global\"", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumEvents\":512", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumResultBytes\":3145728", script, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":45000", script, StringComparison.Ordinal);
        Assert.Contains("api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken", script, StringComparison.Ordinal);
        Assert.Contains("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/", script, StringComparison.Ordinal);
        Assert.Contains("recordBase + 'act_calendar'", script, StringComparison.Ordinal);
        Assert.Contains("method: payload === null ? 'GET' : 'POST'", script, StringComparison.Ordinal);
        Assert.Contains("body: payload === null ? undefined : JSON.stringify(payload)", script, StringComparison.Ordinal);
        Assert.Contains("server:config.server, role_id:config.roleId", script, StringComparison.Ordinal);
        Assert.Contains("url.searchParams.set('game_biz', config.gameBiz)", script, StringComparison.Ordinal);
        Assert.Contains("url.searchParams.set('region', config.server)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("abc", "os_euro")]
    [InlineData("__pengoNyxGenshinEvents_\n", "os_euro")]
    [InlineData("__pengoNyxGenshinEvents_fixture", "unknown")]
    [InlineData("__pengoNyxGenshinEvents_fixture", "prod_official_eur")]
    public void CreateScript_rejects_untrusted_keys_and_non_genshin_roles(string key, string server)
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinEventsCapture.CreateScript(
            key,
            new PublisherRoleBinding(RoleId, server)));
    }

    [Fact]
    public void CreateScript_rejects_overlong_and_null_keys_or_roles()
    {
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinEventsCapture.CreateScript(
            "__pengoNyxGenshinEvents_" + new string('x', 61),
            Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinEventsCapture.CreateScript(null!, Role));
        Assert.Throws<ArgumentException>(() => HoyoLabGenshinEventsCapture.CreateScript(
            "__pengoNyxGenshinEvents_fixture",
            new PublisherRoleBinding("not-a-role", Server)));
    }

    private static string Done(
        string events = HoyoLabGenshinEventsSnapshotTests.DataJson,
        string? roleId = null,
        string? server = null,
        int? count = null)
    {
        using var document = JsonDocument.Parse(events);
        var eventData = document.RootElement;
        var inferredCount = eventData.GetProperty("activities").GetArrayLength()
            + eventData.GetProperty("fixed").GetArrayLength();
        var result = new JsonObject
        {
            ["status"] = "done",
            ["roleId"] = roleId ?? RoleId,
            ["server"] = server ?? Server,
            ["count"] = count ?? inferredCount,
            ["events"] = JsonNode.Parse(events),
        };
        return result.ToJsonString();
    }

    private static JsonObject Root(string json = HoyoLabGenshinEventsSnapshotTests.DataJson) =>
        JsonNode.Parse(json)!.AsObject();
}
