using System.Net;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class EndfieldPullExportProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "test-secret-token";
    private const string HistoryUrl =
        "https://ef-webview.gryphline.com/page/gacha_char?u8_token=" + Token + "&server_id=2&lang=en-us";

    [Fact]
    public async Task Fresh_history_view_exports_strict_contract_without_informational_rows()
    {
        using var temp = new TemporaryDirectory();
        var sourcePath = temp.Combine("data_1");
        File.WriteAllText(sourcePath, "baseline", Encoding.Latin1);
        using var handler = new SequenceHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("u8.gryphline.com", request.RequestUri!.Host);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains(Token, body, StringComparison.Ordinal);
                return Json("""{"status":0,"data":{"uid":"10001","roles":[{"roleId":"20002","serverId":"2","serverName":"Europe"}]}}""");
            },
            request =>
            {
                AssertQuery(request, "pool_type", "E_CharacterGachaPoolType_Standard");
                return Json("""{"code":0,"data":{"list":[{"gachaTs":"1760000002000","kind":"gift_intel_book","nameText":"Never export this","poolId":"BASIC","poolName":"Basic","seqId":"12"},{"charId":"101","charName":"Character","gachaTs":"1760000001000","isFree":true,"isNew":true,"kind":"draw","nameText":"Character","poolId":"BASIC","poolName":"Basic","rarity":6,"seqId":"11"}],"hasMore":false}}""");
            },
            request => EmptyCharacterHistory(request, "E_CharacterGachaPoolType_Beginner"),
            request => EmptyCharacterHistory(request, "E_CharacterGachaPoolType_Special"),
            request => EmptyCharacterHistory(request, "E_CharacterGachaPoolType_Joint"),
            request =>
            {
                Assert.Equal("/api/record/weapon/pool", request.RequestUri!.AbsolutePath);
                return Json("""{"code":0,"data":[{"poolId":"ISSUE_1","poolName":"Issue One"}]}""");
            },
            request =>
            {
                AssertQuery(request, "pool_id", "ISSUE_1");
                return Json("""{"code":0,"data":{"list":[{"poolId":"ISSUE_1","poolName":"Issue One","weaponId":"501","weaponName":"Weapon","weaponType":"Sword","rarity":6,"isNew":false,"kind":"draw","nameText":"Weapon","gachaTs":"1760000000000","seqId":"10"}],"hasMore":false}}""");
            });
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = CreateProvider(temp, sourcePath, http);
        await using var session = await provider.PrepareAsync("ae", CancellationToken.None);
        File.AppendAllText(sourcePath, "\n" + HistoryUrl, Encoding.Latin1);

        var artifact = await session.ExportAsync(CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(artifact.OutputPath!);

        EndfieldPullContract.Validate(bytes);
        Assert.Equal(2, artifact.ItemCount);
        Assert.Equal(bytes.Length, artifact.ByteCount);
        Assert.Equal(7, handler.Calls);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("Never export this", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal("pengo-pulls", root.GetProperty("kind").GetString());
        Assert.Equal("ae", root.GetProperty("game").GetString());
        var account = root.GetProperty("account");
        Assert.Equal("ae:2:20002", $"ae:{account.GetProperty("serverId").GetString()}:{account.GetProperty("roleId").GetString()}");
        var records = root.GetProperty("records").EnumerateArray().ToArray();
        Assert.Contains(records, row => row.GetProperty("recordType").GetString() == "character"
            && row.GetProperty("poolType").GetString() == EndfieldPullApiClient.BasicPool
            && row.GetProperty("isFree").GetBoolean());
        Assert.Contains(records, row => row.GetProperty("recordType").GetString() == "weapon"
            && row.GetProperty("poolType").GetString() == EndfieldPullApiClient.ArsenalPool
            && row.GetProperty("batchId").GetString() == "ISSUE_1");
    }

    [Fact]
    public void Source_reader_uses_only_the_final_eight_mebibytes_and_returns_no_raw_url()
    {
        using var temp = new TemporaryDirectory();
        var sourcePath = temp.Combine("HGWebview.log");
        using (var stream = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(Encoding.Latin1.GetBytes(HistoryUrl));
            stream.Write(new byte[8 * 1024 * 1024 + 1]);
        }
        var reader = new EndfieldPullHistoryLinkReader();
        var source = new EndfieldPullSource(sourcePath, temp.Path);

        Assert.Empty(reader.Read(source, CancellationToken.None).Candidates);
        File.AppendAllText(sourcePath, HistoryUrl, Encoding.Latin1);
        var candidate = Assert.Single(reader.Read(source, CancellationToken.None).Candidates);

        Assert.Equal("2", candidate.Credential.ServerId);
        Assert.Equal(nameof(EndfieldPullHistoryCandidate), candidate.ToString());
        Assert.DoesNotContain(Token, candidate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_response_limit_and_caller_cancellation_fail_without_secrets()
    {
        var candidate = Assert.Single(EndfieldPullHistoryLinkReader.ExtractCandidates(HistoryUrl));
        using var handler = new SequenceHandler(_ =>
        {
            var content = new ByteArrayContent(new byte[1_025]);
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new EndfieldPullApiClient(
            http,
            new NoWaitPullRequestPacer(),
            new EndfieldPullLimits(MaximumResponseBytes: 1_024));

        var limited = await Assert.ThrowsAsync<PullExportException>(async () =>
            await api.DownloadNewestValidAsync([candidate], CancellationToken.None));
        Assert.Equal(PullExportErrorCodes.SafetyLimit, limited.ErrorCode);
        Assert.DoesNotContain(Token, limited.ToString(), StringComparison.Ordinal);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await api.DownloadNewestValidAsync([candidate], canceled.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"status":0,"status":0,"data":{"uid":"10001","roles":[]}}""")]
    [InlineData("""{"status":0,"data":{"uid":"10001","uid":"10001","roles":[]}}""")]
    [InlineData("""{"status":0,"data":{"uid":"bad uid","roles":[]}}""")]
    public async Task Identity_rejects_duplicate_fields_and_non_identifier_uid(string response)
    {
        var candidate = Assert.Single(EndfieldPullHistoryLinkReader.ExtractCandidates(HistoryUrl));
        using var handler = new SequenceHandler(_ => Json(response));
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new EndfieldPullApiClient(
            http,
            new NoWaitPullRequestPacer(),
            new EndfieldPullLimits(MaximumResponseBytes: 64 * 1024, RequestTimeout: TimeSpan.FromSeconds(1)),
            new FixedTimeProvider(Now));

        var error = await Assert.ThrowsAsync<PullExportException>(async () =>
            await api.DownloadNewestValidAsync([candidate], CancellationToken.None));

        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, error.ErrorCode);
    }

    [Fact]
    public async Task Upstream_failure_after_a_partial_page_leaves_no_export_file()
    {
        using var temp = new TemporaryDirectory();
        var sourcePath = temp.Combine("data_1");
        File.WriteAllText(sourcePath, "baseline", Encoding.Latin1);
        using var handler = new SequenceHandler(
            _ => Json("""{"status":0,"data":{"uid":"10001","roles":[{"roleId":"20002","serverId":"2","serverName":"Europe"}]}}"""),
            _ => Json("""{"code":0,"data":{"list":[{"charId":"101","charName":"Character","gachaTs":"1760000001000","isFree":false,"isNew":true,"kind":"draw","nameText":"Character","poolId":"BASIC","poolName":"Basic","rarity":6,"seqId":"11"}],"hasMore":true}}"""),
            _ => Json("""{"code":0,"data":{"list":[],"hasMore":true}}"""));
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = CreateProvider(temp, sourcePath, http);
        await using var session = await provider.PrepareAsync("ae", CancellationToken.None);
        File.AppendAllText(sourcePath, "\n" + HistoryUrl, Encoding.Latin1);

        var error = await Assert.ThrowsAsync<PullExportException>(async () =>
            await session.ExportAsync(CancellationToken.None));

        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, error.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Atomic_writer_never_overwrites_and_cancellation_keeps_no_partial()
    {
        using var temp = new TemporaryDirectory();
        var writer = new EndfieldPullExportWriter(temp.Path, new FixedTimeProvider(Now));
        var target = temp.Combine("Pengo Exports", "Arknights Endfield", "fixed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "keep");

        var failure = await Assert.ThrowsAsync<PullExportException>(async () =>
            await writer.WriteAsync(ValidArchive(), target, CancellationToken.None));
        Assert.Equal(PullExportErrorCodes.OutputFailed, failure.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));

        var canceledTarget = temp.Combine("Pengo Exports", "Arknights Endfield", "canceled.json");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync(ValidArchive(), canceledTarget, canceled.Token));
        Assert.False(File.Exists(canceledTarget));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Contract_rejects_unknown_fields_non_utc_times_and_too_many_records()
    {
        var bytes = EndfieldPullContract.Serialize(ValidArchive(), Now);
        var text = Encoding.UTF8.GetString(bytes);
        var unknown = Encoding.UTF8.GetBytes(text.Replace(
            "\"records\": [",
            "\"secret\": \"no\",\n  \"records\": [",
            StringComparison.Ordinal));
        Assert.Equal(
            PullExportErrorCodes.UpstreamInvalid,
            Assert.Throws<PullExportException>(() => EndfieldPullContract.Validate(unknown)).ErrorCode);
        var nonUtc = Encoding.UTF8.GetBytes(text.Replace("\\u002B00:00", "-05:00", StringComparison.Ordinal));
        Assert.Equal(
            PullExportErrorCodes.UpstreamInvalid,
            Assert.Throws<PullExportException>(() => EndfieldPullContract.Validate(nonUtc)).ErrorCode);
        var invalidUid = Encoding.UTF8.GetBytes(text.Replace("\"uid\": \"10001\"", "\"uid\": \"bad uid\"", StringComparison.Ordinal));
        Assert.Equal(
            PullExportErrorCodes.UpstreamInvalid,
            Assert.Throws<PullExportException>(() => EndfieldPullContract.Validate(invalidUid)).ErrorCode);

        var template = ValidArchive().Records[0];
        var records = Enumerable.Range(1, EndfieldPullContract.MaximumRecords + 1)
            .Select(index => template with
            {
                Id = $"character:BASIC:{index}",
                SeqId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .ToArray();
        Assert.Equal(
            PullExportErrorCodes.SafetyLimit,
            Assert.Throws<PullExportException>(() => EndfieldPullContract.Serialize(
                ValidArchive() with { Records = records },
                Now)).ErrorCode);
    }

    private static EndfieldPullExportProvider CreateProvider(
        TemporaryDirectory temp,
        string sourcePath,
        HttpClient http) =>
        new(
            http,
            [new(sourcePath, temp.Path)],
            temp.Path,
            new NoWaitPullRequestPacer(),
            new EndfieldPullLimits(
                MaximumResponseBytes: 64 * 1024,
                MaximumRequests: 32,
                MaximumRecords: 32,
                RequestTimeout: TimeSpan.FromSeconds(1)),
            new FixedTimeProvider(Now),
            totalDuration: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(1));

    private static EndfieldPullArchive ValidArchive() =>
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
                DateTimeOffset.FromUnixTimeMilliseconds(1760000001000),
                true,
                false)]);

    private static HttpResponseMessage EmptyCharacterHistory(HttpRequestMessage request, string poolType)
    {
        AssertQuery(request, "pool_type", poolType);
        return Json("""{"code":0,"data":{"list":[],"hasMore":false}}""");
    }

    private static void AssertQuery(HttpRequestMessage request, string key, string expected)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        var query = request.RequestUri!.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static pair => Uri.UnescapeDataString(pair[0]),
                static pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);
        Assert.Equal(Token, query["token"]);
        Assert.Equal("2", query["server_id"]);
        Assert.Equal("en-us", query["lang"]);
        Assert.Equal(expected, query[key]);
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        : HttpMessageHandler
    {
        private int calls;
        public int Calls => calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (calls >= steps.Length) throw new InvalidOperationException("Unexpected request.");
            return Task.FromResult(steps[calls++](request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-endfield-pulls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string Combine(params string[] parts) =>
            parts.Aggregate(Path, System.IO.Path.Combine);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { }
        }
    }
}
