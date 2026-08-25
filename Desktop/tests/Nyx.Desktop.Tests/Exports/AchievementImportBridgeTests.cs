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
    public async Task Wrong_origin_and_nonce_reveal_nothing_but_do_not_consume_capability()
    {
        using var temp = new TemporaryDirectory();
        await using var session = await new AchievementImportBridge().StartAsync(
            "gi",
            temp.Write("gi", rows: """{"id":1,"status":"complete"}"""));
        var capability = ParseCapability(session.BrowserUri);

        var wrongOrigin = await SendAsync(
            capability.Port,
            $"""
            GET /v1/achievement-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://evil.example


            """);
        Assert.StartsWith("HTTP/1.1 403 Forbidden", wrongOrigin, StringComparison.Ordinal);
        Assert.DoesNotContain("pengo-achievements", wrongOrigin, StringComparison.Ordinal);
        var wrongNonce = await SendAsync(
            capability.Port,
            $"""
            GET /v1/achievement-import/not-the-code HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg


            """);
        Assert.StartsWith("HTTP/1.1 404 Not Found", wrongNonce, StringComparison.Ordinal);
        Assert.DoesNotContain("pengo-achievements", wrongNonce, StringComparison.Ordinal);

        var valid = await SendAsync(
            capability.Port,
            $"""
            GET /v1/achievement-import/{capability.Nonce} HTTP/1.1
            Host: 127.0.0.1:{capability.Port}
            Origin: https://pengo.gg


            """);
        Assert.StartsWith("HTTP/1.1 200 OK", valid, StringComparison.Ordinal);
        Assert.Equal(
            AchievementImportDeliveryState.Delivered,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
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

    private static async Task<string> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        await using var stream = client.GetStream();
        var normalized = request
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(normalized));
        await stream.FlushAsync();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static (int Port, string Nonce) ParseCapability(Uri browserUri)
    {
        var values = browserUri.Fragment.TrimStart('#')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .ToDictionary(static part => part[0], static part => part[1], StringComparer.Ordinal);
        Assert.Equal("v1", values["nyx-import"]);
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

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
