using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabHsrEventsCaptureTests
{
    private const string RoleId = "123456789";
    private const string Server = "prod_official_eur";
    private static readonly PublisherRoleBinding Role = new(RoleId, Server);

    [Fact]
    public void ParseResult_accepts_done_events_for_the_exact_saved_star_rail_role_and_count()
    {
        var result = HoyoLabHsrEventsCapture.ParseResult(Done(), Role);

        Assert.Equal(HoyoLabHsrEventsReadStatus.Completed, result.Status);
        var snapshot = Assert.IsType<HoyoLabHsrEventsSnapshot>(result.Snapshot);
        Assert.True(HoyoLabHsrEventsRules.IsValid(snapshot));
        Assert.Equal(2, snapshot.Data.GetProperty("activities").GetArrayLength());
        Assert.Equal(1, snapshot.Data.GetProperty("challenges").GetArrayLength());
        Assert.Equal("0.0%", snapshot.Data.GetProperty("activities")[0].GetProperty("status").GetString());
        Assert.Equal(nameof(HoyoLabHsrEventsReadResult), result.ToString());
        Assert.Equal(nameof(HoyoLabHsrEventsSnapshot), snapshot.ToString());
        Assert.DoesNotContain(RoleId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Server, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_accepts_an_explicit_empty_collection_but_never_a_partial_done_result()
    {
        var empty = Root();
        empty["activities"] = new JsonArray();
        empty["challenges"] = new JsonArray();
        var emptyResult = HoyoLabHsrEventsCapture.ParseResult(Done(empty.ToJsonString()), Role);
        Assert.Equal(HoyoLabHsrEventsReadStatus.Completed, emptyResult.Status);
        var emptySnapshot = Assert.IsType<HoyoLabHsrEventsSnapshot>(emptyResult.Snapshot);
        Assert.Empty(emptySnapshot.Data.GetProperty("activities").EnumerateArray());
        Assert.Empty(emptySnapshot.Data.GetProperty("challenges").EnumerateArray());

        var partial = Root();
        partial["activities"]![0]!.AsObject().Remove("dropTypes");
        var partialResult = HoyoLabHsrEventsCapture.ParseResult(Done(partial.ToJsonString()), Role);
        Assert.Equal(HoyoLabHsrEventsReadStatus.NeedsReview, partialResult.Status);
        Assert.Null(partialResult.Snapshot);
    }

    [Theory]
    [InlineData("login-required", HoyoLabHsrEventsReadStatus.LoginRequired)]
    [InlineData("canceled", HoyoLabHsrEventsReadStatus.Canceled)]
    [InlineData("timed-out", HoyoLabHsrEventsReadStatus.TimedOut)]
    [InlineData("too-large", HoyoLabHsrEventsReadStatus.TooLarge)]
    public void ParseResult_maps_only_exact_terminal_status_objects(
        string status,
        HoyoLabHsrEventsReadStatus expected)
    {
        var result = HoyoLabHsrEventsCapture.ParseResult(
            $$"""{"status":"{{status}}"}""",
            Role);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult($$"""{"status":"{{status}}","roleId":"{{RoleId}}"}""", Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_malformed_duplicate_mismatched_or_source_mixed_json()
    {
        var missingStatus = JsonNode.Parse(Done())!.AsObject();
        missingStatus.Remove("status");

        var wrongEvents = JsonNode.Parse(Done())!.AsObject();
        wrongEvents["events"] = null;

        var extraRoot = JsonNode.Parse(Done())!.AsObject();
        extraRoot["sourceText"] = "Synthetic source";

        var missingDetail = Root();
        missingDetail["activities"]![0]!.AsObject().Remove("dropTypes");

        var malformed = new[]
        {
            "{",
            "[]",
            missingStatus.ToJsonString(),
            "{\"status\":\"done\",\"status\":\"done\"}",
            wrongEvents.ToJsonString(),
            extraRoot.ToJsonString(),
            Done().Replace("\"count\":3", "\"count\":3.5", StringComparison.Ordinal),
            Done().Replace("\"count\":3", "\"count\":2", StringComparison.Ordinal),
            Done(missingDetail.ToJsonString()),
        };

        foreach (var json in malformed)
        {
            var result = HoyoLabHsrEventsCapture.ParseResult(json, Role);
            Assert.Equal(HoyoLabHsrEventsReadStatus.NeedsReview, result.Status);
            Assert.Null(result.Snapshot);
        }
    }

    [Fact]
    public void ParseResult_requires_the_saved_role_and_activity_plus_challenge_count()
    {
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(Done(roleId: "987654321"), Role).Status);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(Done(server: "prod_official_usa"), Role).Status);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(Done(count: 2), Role).Status);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(Done(count: -1), Role).Status);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(
                Done().Replace("\"count\":3", "\"count\":2147483648", StringComparison.Ordinal), Role).Status);
    }

    [Fact]
    public void ParseResult_rejects_utf8_and_text_limits_before_admitting_a_snapshot()
    {
        var tooLong = new string('x', HoyoLabHsrEventsCapture.MaximumResultBytes + 1);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(tooLong, Role).Status);

        var tooManyUtf8Bytes = new string('é', HoyoLabHsrEventsCapture.MaximumResultBytes / 2 + 1);
        Assert.True(tooManyUtf8Bytes.Length <= HoyoLabHsrEventsCapture.MaximumResultBytes);
        Assert.True(Encoding.UTF8.GetByteCount(tooManyUtf8Bytes) > HoyoLabHsrEventsCapture.MaximumResultBytes);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(tooManyUtf8Bytes, Role).Status);
        Assert.Equal(
            HoyoLabHsrEventsReadStatus.NeedsReview,
            HoyoLabHsrEventsCapture.ParseResult(null!, Role).Status);
    }

    [Fact]
    public void CreateScript_embeds_the_exact_read_only_star_rail_calendar_contract()
    {
        var script = HoyoLabHsrEventsCapture.CreateScript(
            "__pengoNyxHsrEvents_fixture",
            Role);

        Assert.Contains("\"roleId\":\"123456789\"", script, StringComparison.Ordinal);
        Assert.Contains("\"server\":\"prod_official_eur\"", script, StringComparison.Ordinal);
        Assert.Contains("\"gameBiz\":\"hkrpg_global\"", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumEvents\":512", script, StringComparison.Ordinal);
        Assert.Contains("\"maximumResultBytes\":3145728", script, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":45000", script, StringComparison.Ordinal);
        Assert.Contains("api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken", script, StringComparison.Ordinal);
        Assert.Contains("https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/", script, StringComparison.Ordinal);
        Assert.Contains("get_act_calender", script, StringComparison.Ordinal);
        Assert.Contains("method: 'GET'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("get_act_calendar", script, StringComparison.Ordinal);
        Assert.DoesNotContain("POST", script, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("abc", "prod_official_eur")]
    [InlineData("__pengoNyxHsrEvents_\n", "prod_official_eur")]
    [InlineData("__pengoNyxHsrEvents_fixture", "unknown")]
    [InlineData("__pengoNyxHsrEvents_fixture", "os_euro")]
    public void CreateScript_rejects_untrusted_keys_and_non_star_rail_roles(string key, string server)
    {
        Assert.Throws<ArgumentException>(() => HoyoLabHsrEventsCapture.CreateScript(
            key,
            new PublisherRoleBinding(RoleId, server)));
    }

    [Fact]
    public void CreateScript_rejects_overlong_and_null_keys_or_roles()
    {
        Assert.Throws<ArgumentException>(() => HoyoLabHsrEventsCapture.CreateScript(
            "__pengoNyxHsrEvents_" + new string('x', 61),
            Role));
        Assert.Throws<ArgumentException>(() => HoyoLabHsrEventsCapture.CreateScript(null!, Role));
        Assert.Throws<ArgumentException>(() => HoyoLabHsrEventsCapture.CreateScript(
            "__pengoNyxHsrEvents_fixture",
            new PublisherRoleBinding("not-a-role", Server)));
    }

    private static string Done(
        string events = HoyoLabHsrEventsSnapshotTests.DataJson,
        string? roleId = null,
        string? server = null,
        int? count = null)
    {
        using var document = JsonDocument.Parse(events);
        var eventData = document.RootElement;
        var inferredCount = eventData.GetProperty("activities").GetArrayLength()
            + eventData.GetProperty("challenges").GetArrayLength();
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

    private static JsonObject Root(string json = HoyoLabHsrEventsSnapshotTests.DataJson) =>
        JsonNode.Parse(json)!.AsObject();
}
