using System.Net.Sockets;
using System.Text;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class AchievementImportBridgeTests
{
    [Fact]
    public async Task Exact_preflight_and_get_deliver_once_from_fragment_only_capability()
    {
        using var temp = new TemporaryDirectory();
        var artifact = temp.Write("hsr", rows: """{"id":4010101,"status":"complete"}""");
        await using var session = await new AchievementImportBridge().StartAsync(
            "hsr",
            artifact);
        var capability = ParseCapability(session.BrowserUri);

        Assert.Equal("https", session.BrowserUri.Scheme);
        Assert.Equal("pengo.gg", session.BrowserUri.Host);
        Assert.Equal("/hsr/achievements", session.BrowserUri.AbsolutePath);
        Assert.Empty(session.BrowserUri.Query);
        Assert.DoesNotContain(capability.Nonce, session.BrowserUri.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

        var preflight = await SendAsync(
            capability.Port,
            $"""
            OPTIONS /v1/achievement-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg
            Access-Control-Request-Method: GET
            Access-Control-Request-Private-Network: true


            """);
        Assert.StartsWith("HTTP/1.1 204 No Content", preflight, StringComparison.Ordinal);
        Assert.Contains("Access-Control-Allow-Origin: https://pengo.gg\r\n", preflight, StringComparison.Ordinal);
        Assert.Contains("Access-Control-Allow-Private-Network: true\r\n", preflight, StringComparison.Ordinal);

        var response = await SendAsync(
            capability.Port,
            $"""
            GET /v1/achievement-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg


            """);
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.Contains("\"game\":\"hsr\"", response, StringComparison.Ordinal);
        Assert.Equal(
            AchievementImportDeliveryState.Delivered,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Endfield_pull_bridge_serves_the_saved_bytes_once_with_v2_capability()
    {
        using var temp = new TemporaryDirectory();
        var bytes = EndfieldPullContract.Serialize(
            new(
                new("10001", "20002", "2", "Europe"),
                [new(
                    "character:BASIC:11",
                    "character",
                    "11",
                    "BASIC",
                    "Basic",
                    EndfieldPullApiClient.BasicPool,
                    "101",
                    "Character",
                    "character",
                    6,
                    DateTimeOffset.Parse("2026-08-27T01:00:00+00:00"),
                    true,
                    false)]),
            DateTimeOffset.Parse("2026-08-28T12:00:00+00:00"));
        var artifact = temp.WriteBytes(bytes);
        await using var session = await new AchievementImportBridge(
            endfieldPullLifetime: TimeSpan.FromSeconds(2)).StartEndfieldPullAsync(artifact);
        var capability = ParseCapability(session.BrowserUri, "v2");

        Assert.Equal("/endfield", session.BrowserUri.AbsolutePath);
        Assert.Empty(session.BrowserUri.Query);
        Assert.Contains("type=pulls", session.BrowserUri.Fragment, StringComparison.Ordinal);
        var preflight = await SendAsync(
            capability.Port,
            $"""
            OPTIONS /v2/pull-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg
            Access-Control-Request-Method: GET
            Access-Control-Request-Private-Network: true


            """);
        Assert.StartsWith("HTTP/1.1 204 No Content", preflight, StringComparison.Ordinal);

        var response = await SendBytesAsync(
            capability.Port,
            $"""
            GET /v2/pull-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg


            """);

        var responseText = Encoding.ASCII.GetString(response);
        Assert.Contains("Content-Type: application/json; charset=utf-8\r\n", responseText, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store\r\n", responseText, StringComparison.Ordinal);
        Assert.Equal(bytes, ResponseBody(response));
        Assert.Equal(
            AchievementImportDeliveryState.Delivered,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_wrong_origin_and_nonce_reveal_nothing_but_do_not_consume_capability(
        bool endfield)
    {
        using var temp = new TemporaryDirectory();
        var bridge = new AchievementImportBridge(
            lifetime: TimeSpan.FromSeconds(5),
            endfieldPullLifetime: TimeSpan.FromSeconds(5));
        var artifact = endfield
            ? temp.WriteBytes(EndfieldPullContract.Serialize(
                new(new("10001", "20002", "2", "Europe"), []),
                DateTimeOffset.Parse("2026-08-28T12:00:00+00:00")))
            : temp.Write("gi", rows: """{"id":1,"status":"complete"}""");
        await using var session = endfield
            ? await bridge.StartEndfieldPullAsync(artifact)
            : await bridge.StartAsync("gi", artifact);
        var capability = ParseCapability(session.BrowserUri, endfield ? "v2" : "v1");
        var path = endfield ? "v2/pull-import" : "v1/achievement-import";

        await ResetConnectionAsync(capability.Port);

        for (var index = 0; index < 8; index++)
        {
            var wrongOrigin = index % 2 == 0;
            var response = await SendAsync(
                capability.Port,
                $"""
                GET /{path}/{(wrongOrigin ? capability.Nonce : "not-the-code")} HTTP/1.1
                Host: 127.0.0.1:{capability.Port}
                Origin: {(wrongOrigin ? "https://evil.example" : "https://pengo.gg")}


                """);
            Assert.StartsWith(
                wrongOrigin ? "HTTP/1.1 403 Forbidden" : "HTTP/1.1 404 Not Found",
                response,
                StringComparison.Ordinal);
            Assert.DoesNotContain(endfield ? "pengo-pulls" : "pengo-achievements", response, StringComparison.Ordinal);
        }

        var valid = await SendAsync(
            capability.Port,
            $"""
            GET /{path}/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg


            """);
        Assert.StartsWith("HTTP/1.1 200 OK", valid, StringComparison.Ordinal);
        Assert.Equal(
            AchievementImportDeliveryState.Delivered,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Site_origin_allowlist_is_exact_and_only_the_development_channel_accepts_local_origin()
    {
        _ = new AchievementImportBridge(new Uri("https://pengo.gg"));
        foreach (var origin in new[]
        {
            "http://pengo.gg",
            "https://pengo.gg:444",
            "https://pengo.gg/path",
            "http://localhost:5173",
        })
            Assert.Throws<ArgumentException>(() => new AchievementImportBridge(new Uri(origin)));

        Assert.Throws<ArgumentException>(() =>
            new AchievementImportBridge(new Uri("http://127.0.0.1:5173")));
        Assert.Throws<ArgumentException>(() =>
            new AchievementImportBridge(
                new Uri("http://127.0.0.1:5173"),
                releaseChannel: "preview"));
        _ = new AchievementImportBridge(
            new Uri("http://127.0.0.1:5173"),
            releaseChannel: "development");
    }

    [Fact]
    public async Task Expired_bridge_closes_without_delivering()
    {
        using var temp = new TemporaryDirectory();
        await using var session = await new AchievementImportBridge(
            lifetime: TimeSpan.FromMilliseconds(50)).StartAsync(
                "hsr",
                temp.Write("hsr", rows: string.Empty));

        Assert.Equal(
            AchievementImportDeliveryState.Expired,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("gi", "hsr", """{"id":1,"status":"complete"}""")]
    [InlineData("hsr", "hsr", """{"id":2,"status":"complete"},{"id":1,"status":"complete"}""")]
    [InlineData("hsr", "hsr", """{"id":1,"status":"partial"}""")]
    public async Task Wrong_game_or_unfamiliar_artifact_is_never_served(
        string expectedGame,
        string artifactGame,
        string rows)
    {
        using var temp = new TemporaryDirectory();
        var failure = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await new AchievementImportBridge().StartAsync(
                expectedGame,
                temp.Write(artifactGame, rows)));

        Assert.Equal("achievement-handoff-invalid", failure.Code);
    }

    [Fact]
    public async Task Extra_root_data_is_rejected_to_prevent_arbitrary_file_exposure()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "extra.json");
        File.WriteAllText(
            path,
            $$"""{"kind":"pengo-achievements","version":1,"game":"hsr","catalogVersion":"{{AchievementCatalogVersions.StarRail}}","exportedAt":"2026-07-27T00:00:00Z","achievements":[],"secret":"must-not-leak"}""");

        var failure = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await new AchievementImportBridge().StartAsync("hsr", path));

        Assert.Equal("achievement-handoff-invalid", failure.Code);
    }

    [Fact]
    public async Task Invalid_or_expired_Endfield_pull_artifact_is_never_delivered()
    {
        using var temp = new TemporaryDirectory();
        var invalid = temp.WriteBytes(
            Encoding.UTF8.GetBytes("""{"kind":"pengo-pulls","version":1,"game":"ae","secret":"no"}"""));
        var failure = await Assert.ThrowsAsync<ExportProviderException>(async () =>
            await new AchievementImportBridge().StartEndfieldPullAsync(invalid));
        Assert.Equal("pull-handoff-invalid", failure.Code);

        var valid = EndfieldPullContract.Serialize(
            new(new("10001", "20002", "2", "Europe"), []),
            DateTimeOffset.Parse("2026-08-28T12:00:00+00:00"));
        await using var expired = await new AchievementImportBridge(
            endfieldPullLifetime: TimeSpan.FromMilliseconds(50)).StartEndfieldPullAsync(
                temp.WriteBytes(valid));
        Assert.Equal(
            AchievementImportDeliveryState.Expired,
            await expired.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static async Task<string> SendAsync(int port, string request)
        => Encoding.UTF8.GetString(await SendBytesAsync(port, request));

    private static async Task<byte[]> SendBytesAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        await using var stream = client.GetStream();
        var normalized = request
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(normalized));
        await stream.FlushAsync();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(2));
        return output.ToArray();
    }

    private static async Task ResetConnectionAsync(int port)
    {
        using var client = new TcpClient();
        client.Client.LingerState = new(true, 0);
        await client.ConnectAsync("127.0.0.1", port);
    }

    private static byte[] ResponseBody(byte[] response)
    {
        var separator = response.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(separator >= 0);
        return response[(separator + 4)..];
    }

    private static (int Port, string Nonce) ParseCapability(Uri browserUri, string version = "v1")
    {
        var values = browserUri.Fragment.TrimStart('#')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .ToDictionary(static part => part[0], static part => part[1], StringComparer.Ordinal);
        Assert.Equal(version, values["nyx-import"]);
        return (int.Parse(values["port"]), values["nonce"]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-achievement-bridge-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string game, string rows)
        {
            var path = System.IO.Path.Combine(Path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                $$"""{"kind":"pengo-achievements","version":1,"game":"{{game}}","catalogVersion":"{{game}}-fixture","exportedAt":"2026-07-27T00:00:00Z","achievements":[{{rows}}]}""");
            return path;
        }

        public string WriteBytes(byte[] bytes)
        {
            var path = System.IO.Path.Combine(Path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
