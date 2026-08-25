using System.Net;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class WuwaPullExportProviderTests
{
    [Fact]
    public void HistoryReader_AcceptsOnlyCanonicalHttpsHistoryUrls()
    {
        var valid = Url();
        var candidates = WuwaPullHistoryLinkReader.ExtractCandidates("prefix\0" + valid + "\0suffix", 64);

        var candidate = Assert.Single(candidates);
        Assert.Equal("100000001", candidate.Url.PlayerId);
        Assert.Equal("record-a", candidate.Url.RecordId);
        Assert.Equal("server-a", candidate.Url.ServerId);
        Assert.Equal("en", candidate.Url.LanguageCode);
        Assert.Equal("resources-a", candidate.Url.ResourcesId);
        Assert.DoesNotContain("record-a", candidate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("server-a", candidate.Url.ToString(), StringComparison.Ordinal);

        Assert.Empty(WuwaPullHistoryLinkReader.ExtractCandidates(
            valid.Replace("aki-gm-resources-oversea.aki-game.net", "aki-gm-resources.aki-game.net", StringComparison.Ordinal),
            64));
        Assert.Empty(WuwaPullHistoryLinkReader.ExtractCandidates(
            valid.Replace(".aki-game.net", ".aki-game.com", StringComparison.Ordinal),
            64));
    }

    [Theory]
    [InlineData("playerId=100000001")]
    [InlineData("record_id=")]
    [InlineData("player_id=100000001&player_id=100000002")]
    [InlineData("player_id=100000001&record_id=record%ZZ")]
    [InlineData("player_id=100000001&record_id=record-a&svr_id=server-a&lang=en&resources_id=resources-a&evil=1")]
    [InlineData("player_id=100000001&record_id=record-a&svr_id=server-a&lang=en&resources_id=resources-a#fragment")]
    public void HistoryReader_RejectsMissingEmptyDuplicateMalformedAliasAndUnreviewedQuery(string replacement)
    {
        var query = replacement.Contains("resources_id", StringComparison.Ordinal)
            ? replacement
            : "record_id=record-a&svr_id=server-a&lang=en&resources_id=resources-a&" + replacement;
        var url = "https://aki-gm-resources-oversea.aki-game.net/aki/gacha/index.html#/record?" + query;

        Assert.Empty(WuwaPullHistoryLinkReader.ExtractCandidates(url, 64));
    }

    [Fact]
    public void HistoryReader_RejectsOversizedUrlAndControls()
    {
        var oversized = "https://aki-gm-resources-oversea.aki-game.net/aki/gacha/index.html#/record?"
            + "player_id=100000001&record_id=" + new string('x', 20_000);
        Assert.Empty(WuwaPullHistoryLinkReader.ExtractCandidates(oversized, 64));

        var found = WuwaPullHistoryLinkReader.ExtractCandidates(
            Url(recordId: "record-a") + "\r\nhttps://attacker.invalid/aki/gacha/index.html#/record?" +
            "player_id=100000001&record_id=record-b&svr_id=server-a&lang=en&resources_id=resources-a",
            64);
        Assert.Single(found);
    }

    [Fact]
    public void HistoryReader_ToleratesBinaryLogBytesButRejectsThemInsideTheUrl()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("Client.log");
        var url = Url();
        byte[] prefix = [0xff, 0x80, 0x00];
        File.WriteAllBytes(path, [.. prefix, .. Encoding.ASCII.GetBytes(url), 0x00, 0xfe]);

        var reader = new WuwaPullHistoryLinkReader(new PullExportSafetyLimits());
        var candidate = Assert.Single(reader.Read(path, default).Candidates);

        Assert.Equal(prefix.Length, candidate.StartOffset);
        Assert.Equal(prefix.Length + Encoding.ASCII.GetByteCount(url), candidate.EndOffset);

        var invalidUrl = Encoding.ASCII.GetBytes(url);
        invalidUrl[url.IndexOf("record-a", StringComparison.Ordinal)] = 0xff;
        File.WriteAllBytes(path, invalidUrl);
        Assert.Empty(reader.Read(path, default).Candidates);
    }

    [Fact]
    public void HistoryReader_DecodesTheCurrentMaskedClientLogInMemory()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("Client.log");
        var prefix = "ordinary-prefix\0";
        var url = Url(recordId: "record-masked");
        File.WriteAllBytes(path, MaskClientLog(prefix + url + "\0ordinary-suffix"));

        var observation = new WuwaPullHistoryLinkReader(new PullExportSafetyLimits()).Read(path, default);
        var candidate = Assert.Single(observation.Candidates);

        Assert.True(observation.IsMasked);
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), candidate.StartOffset);
        Assert.Equal("record-masked", candidate.Url.RecordId);
        Assert.DoesNotContain("record-masked", observation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_PrepareUsesExactLogAndExportRequiresFreshOccurrence()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.Provider.PrepareAsync("wuwa", default);
        Assert.Equal(
            Path.Combine(fixture.Root, "Wuthering Waves Game", "Client", "Saved", "Logs", "Client.log"),
            fixture.LogPath);

        File.AppendAllText(fixture.LogPath, "\0" + Url(recordId: "record-fresh"), Encoding.UTF8);
        var artifact = await session.ExportAsync(default);

        Assert.Equal(7, fixture.Requests.Count);
        Assert.All(fixture.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://gmserver-api.aki-game2.net/gacha/record/query", request.Uri.AbsoluteUri);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("application/json", request.Accept);
        });
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, fixture.Requests.Select(request => request.PoolType));
        Assert.Equal(
            "{\"playerId\":\"100000001\",\"cardPoolType\":1,\"cardPoolId\":\"resources-a\",\"languageCode\":\"en\",\"recordId\":\"record-fresh\",\"serverId\":\"server-a\"}",
            fixture.Requests[0].Body);
        Assert.EndsWith(".wwgf.json", artifact.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(artifact.OutputPath));
    }

    [Fact]
    public async Task Provider_StaleHistoryDoesNotCallApiOrCreateOutput()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.Provider.PrepareAsync("wuwa", default);

        var error = await Assert.ThrowsAsync<PullExportException>(async () => await session.ExportAsync(default));

        Assert.Equal(PullExportErrorCodes.HistoryNotUpdated, error.ErrorCode);
        Assert.Empty(fixture.Requests);
        Assert.DoesNotContain(fixture.Secret, error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Downloads, "Pengo Exports")));
    }

    [Fact]
    public async Task Provider_SameUrlOccurrenceAfterArmingIsFresh()
    {
        using var fixture = new Fixture();
        await using var session = await fixture.Provider.PrepareAsync("wuwa", default);

        File.AppendAllText(fixture.LogPath, "\0" + Url(), Encoding.UTF8);
        var artifact = await session.ExportAsync(default);

        Assert.Equal(7, fixture.Requests.Count);
        Assert.True(File.Exists(artifact.OutputPath));
    }

    [Fact]
    public async Task Provider_ReadsOnlyBoundedTailOfRealisticLargeLogAndCanArmWithoutOldUrl()
    {
        using var fixture = new Fixture();
        await using (var stream = new FileStream(fixture.LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            stream.SetLength(20L * 1024 * 1024);
        File.AppendAllText(fixture.LogPath, "ordinary log tail", Encoding.UTF8);

        await using var session = await fixture.Provider.PrepareAsync("wuwa", default);
        File.AppendAllText(fixture.LogPath, "\0" + Url(recordId: "record-after-arm"), Encoding.UTF8);
        var artifact = await session.ExportAsync(default);

        Assert.Equal(7, fixture.Requests.Count);
        Assert.True(File.Exists(artifact.OutputPath));
    }

    [Fact]
    public async Task Provider_AcceptsFreshMaskedHistoryAfterSameFileRollover()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(
            fixture.LogPath,
            MaskClientLog(Url(recordId: "record-baseline") + "\0" + new string('x', 4_096)));
        var reader = new WuwaPullHistoryLinkReader(new PullExportSafetyLimits());
        var baseline = reader.Read(fixture.LogPath, default);
        await using var session = await fixture.Provider.PrepareAsync("wuwa", default);

        File.WriteAllBytes(
            fixture.LogPath,
            MaskClientLog("new generation\0" + Url(recordId: "record-after-rollover")));
        File.SetLastWriteTimeUtc(fixture.LogPath, DateTime.UtcNow.AddSeconds(1));
        var current = reader.Read(fixture.LogPath, default);
        Assert.True(baseline.IsMasked && current.IsMasked);
        Assert.True(baseline.Stamp.SameIdentity(current.Stamp));
        Assert.True(current.Stamp.Length < baseline.Stamp.Length);
        Assert.True(current.Stamp.LastWriteTimeUtcTicks > baseline.Stamp.LastWriteTimeUtcTicks);
        var artifact = await session.ExportAsync(default);

        Assert.Equal(7, fixture.Requests.Count);
        Assert.Contains("record-after-rollover", fixture.Requests[0].Body, StringComparison.Ordinal);
        Assert.True(File.Exists(artifact.OutputPath));
    }

    [Fact]
    public async Task Provider_ReplacementAndTruncationAreRejected()
    {
        using var replacement = new Fixture();
        await using var replacementSession = await replacement.Provider.PrepareAsync("wuwa", default);
        File.Delete(replacement.LogPath);
        File.WriteAllText(replacement.LogPath, Url(recordId: "replacement"), Encoding.UTF8);
        var replacedError = await Assert.ThrowsAsync<PullExportException>(async () => await replacementSession.ExportAsync(default));
        Assert.Equal(PullExportErrorCodes.HistoryNotUpdated, replacedError.ErrorCode);
        Assert.Empty(replacement.Requests);

        using var truncated = new Fixture();
        await using var truncatedSession = await truncated.Provider.PrepareAsync("wuwa", default);
        File.WriteAllText(truncated.LogPath, "short", Encoding.UTF8);
        var truncatedError = await Assert.ThrowsAsync<PullExportException>(async () => await truncatedSession.ExportAsync(default));
        Assert.Equal(PullExportErrorCodes.HistoryNotUpdated, truncatedError.ErrorCode);
        Assert.Empty(truncated.Requests);
    }

    [Fact]
    public async Task Provider_MissingLockedAndOversizedLogsAreRejectedWithoutApiCalls()
    {
        using var missing = new Fixture();
        await using var missingSession = await missing.Provider.PrepareAsync("wuwa", default);
        File.Delete(missing.LogPath);
        var missingError = await Assert.ThrowsAsync<PullExportException>(async () => await missingSession.ExportAsync(default));
        Assert.Equal(PullExportErrorCodes.HistoryNotUpdated, missingError.ErrorCode);
        Assert.Empty(missing.Requests);

        using var locked = new Fixture();
        await using var lockedSession = await locked.Provider.PrepareAsync("wuwa", default);
        using (var handle = new FileStream(locked.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedError = await Assert.ThrowsAsync<PullExportException>(async () => await lockedSession.ExportAsync(default));
            Assert.Equal(PullExportErrorCodes.HistoryNotUpdated, lockedError.ErrorCode);
        }
        Assert.Empty(locked.Requests);

        using var oversized = new Fixture();
        await using var oversizedSession = await oversized.Provider.PrepareAsync("wuwa", default);
        await using (var stream = new FileStream(oversized.LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            stream.SetLength(33L * 1024 * 1024);
        var oversizedError = await Assert.ThrowsAsync<PullExportException>(async () => await oversizedSession.ExportAsync(default));
        Assert.Equal(PullExportErrorCodes.CacheTooLarge, oversizedError.ErrorCode);
        Assert.Empty(oversized.Requests);
    }

    [Fact]
    public async Task Api_DeduplicatesIdsButRejectsMixedPlayerAndPartialFailure()
    {
        var auth = new WuwaPullHistoryUrl("100000001", "record-a", "server-a", "en", "resources-a");
        var calls = new List<int>();
        using var http = new HttpClient(new DelegateHandler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var pool = body.RootElement.GetProperty("cardPoolType").GetInt32();
            calls.Add(pool);
            var data = pool == 1
                ? "[{\"id\":\"same\",\"cardPoolType\":1,\"resourceId\":\"r\",\"qualityLevel\":5,\"name\":\"Item\",\"resourceType\":\"Character\",\"time\":\"2026-07-17 12:34:56\",\"count\":1},{\"id\":\"same\",\"cardPoolType\":1,\"resourceId\":\"r\",\"qualityLevel\":5,\"name\":\"Item\",\"resourceType\":\"Character\",\"time\":\"2026-07-17 12:34:56\",\"count\":1}]"
                : "[]";
            return JsonResponse("{\"code\":0,\"data\":" + data + "}");
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WuwaPullApiClient(http, new PullExportSafetyLimits(), new NoWaitWuwaPullRequestPacer());

        var archive = await api.DownloadAsync(auth, default);

        Assert.Single(archive.Records);
        Assert.Equal(Enumerable.Range(1, 7), calls);
    }

    [Fact]
    public async Task Api_AcceptsSiteProvenLocalizedPoolLabelsAndPreservesSameSecondPullsWithoutIds()
    {
        var auth = new WuwaPullHistoryUrl("100000001", "record-a", "server-a", "en", "resources-a");
        using var http = new HttpClient(new DelegateHandler(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var requestedPool = body.RootElement.GetProperty("cardPoolType").GetInt32();
            var data = requestedPool == 1
                ? "[{\"cardPoolType\":\"Resonators Accurate Modulation\",\"resourceId\":\"r\",\"qualityLevel\":5,\"name\":\"Item\",\"resourceType\":\"Resonator\",\"time\":\"2026-07-17 12:34:56\",\"count\":1},{\"cardPoolType\":\"Resonators Accurate Modulation\",\"resourceId\":\"r\",\"qualityLevel\":5,\"name\":\"Item\",\"resourceType\":\"Resonator\",\"time\":\"2026-07-17 12:34:56\",\"count\":1}]"
                : "[]";
            return JsonResponse("{\"code\":0,\"data\":" + data + "}");
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WuwaPullApiClient(http, new PullExportSafetyLimits(), new NoWaitWuwaPullRequestPacer());

        var archive = await api.DownloadAsync(auth, default);

        Assert.Equal(2, archive.Records.Count);
        Assert.All(archive.Records, record => Assert.Equal(1, record.CardPoolType));
        Assert.Equal(2, archive.Records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith("-0001", archive.Records[0].Id, StringComparison.Ordinal);
        Assert.EndsWith("-0002", archive.Records[1].Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_RejectsMixedPlayerWithoutCompletingRemainingPools()
    {
        var auth = new WuwaPullHistoryUrl("100000001", "record-a", "server-a", "en", "resources-a");
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{\"code\":0,\"uid\":\"999999999\",\"data\":[]}"));
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WuwaPullApiClient(http, new PullExportSafetyLimits(), new NoWaitWuwaPullRequestPacer());

        var error = await Assert.ThrowsAsync<PullExportException>(async () => await api.DownloadAsync(auth, default));

        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, error.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Api_UsesPacingBeforeEveryPoolAndRejectsRedirectAndPartialFailure()
    {
        var auth = new WuwaPullHistoryUrl("100000001", "record-a", "server-a", "en", "resources-a");
        var pacer = new RecordingWuwaPacer();
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{\"code\":0,\"data\":[]}"));
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WuwaPullApiClient(http, new PullExportSafetyLimits(), pacer);
        var archive = await api.DownloadAsync(auth, default);
        Assert.Empty(archive.Records);
        Assert.Equal(7, pacer.Calls);
        Assert.Equal(7, calls);
        Assert.All(pacer.Delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(250), delay));

        using var redirectHttp = new HttpClient(new DelegateHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://attacker.invalid/steal");
            return Task.FromResult(redirect);
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var redirectApi = new WuwaPullApiClient(redirectHttp, new PullExportSafetyLimits(), new NoWaitWuwaPullRequestPacer());
        var redirectError = await Assert.ThrowsAsync<PullExportException>(async () => await redirectApi.DownloadAsync(auth, default));
        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, redirectError.ErrorCode);

        using var partialHttp = new HttpClient(new DelegateHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var pool = JsonDocument.Parse(body).RootElement.GetProperty("cardPoolType").GetInt32();
            return Task.FromResult(pool == 2
                ? JsonResponse("{\"code\":-1,\"data\":[]}")
                : JsonResponse("{\"code\":0,\"data\":[]}"));
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var partialApi = new WuwaPullApiClient(partialHttp, new PullExportSafetyLimits(), new NoWaitWuwaPullRequestPacer());
        var partialError = await Assert.ThrowsAsync<PullExportException>(async () => await partialApi.DownloadAsync(auth, default));
        Assert.Equal(PullExportErrorCodes.UpstreamRejected, partialError.ErrorCode);
    }

    [Fact]
    public async Task Api_CancellationAndOversizeProduceNoOutput()
    {
        var auth = new WuwaPullHistoryUrl("100000001", "record-a", "server-a", "en", "resources-a");
        using var http = new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("{}");
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WuwaPullApiClient(
            http,
            new PullExportSafetyLimits(RequestTimeout: TimeSpan.FromMilliseconds(100)),
            new NoWaitWuwaPullRequestPacer());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await api.DownloadAsync(auth, cancelled.Token));

        using var oversizeHttp = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 2_100_000), Encoding.UTF8, "application/json"),
            })))
        { Timeout = Timeout.InfiniteTimeSpan };
        var oversizeApi = new WuwaPullApiClient(
            oversizeHttp,
            new PullExportSafetyLimits(MaximumResponseBytes: 1_024),
            new NoWaitWuwaPullRequestPacer());
        var error = await Assert.ThrowsAsync<PullExportException>(async () => await oversizeApi.DownloadAsync(auth, default));
        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, error.ErrorCode);

        using var timeoutHttp = new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return JsonResponse("{\"code\":0,\"data\":[]}");
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var timeoutApi = new WuwaPullApiClient(
            timeoutHttp,
            new PullExportSafetyLimits(RequestTimeout: TimeSpan.FromMilliseconds(100)),
            new NoWaitWuwaPullRequestPacer());
        var timeoutError = await Assert.ThrowsAsync<PullExportException>(async () => await timeoutApi.DownloadAsync(auth, default));
        Assert.Equal(PullExportErrorCodes.UpstreamInvalid, timeoutError.ErrorCode);
    }

    [Fact]
    public void Writer_ProducesImporterCompatibleDeterministicSchema()
    {
        var archive = new WuwaPullArchive("100000001", [
            new("b", 2, "res-2", 4, "Weapon", "weapon", "2026-07-18 00:00:00", 1),
            new("a", 1, "res-1", 5, "Resonator", "resonator", "2026-07-17 00:00:00", 1),
        ]);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        WuwaPullExportWriter.WriteWwgf(first, archive);
        WuwaPullExportWriter.WriteWwgf(second, archive);

        Assert.Equal(first.ToArray(), second.ToArray());
        using var document = JsonDocument.Parse(first.ToArray());
        var root = document.RootElement;
        var account = Assert.Single(root.GetProperty("ww").EnumerateArray());
        Assert.Equal("100000001", account.GetProperty("uid").GetString());
        var record = account.GetProperty("list").EnumerateArray().First();
        Assert.Equal(
            new[] { "id", "cardPoolType", "resourceId", "qualityLevel", "name", "resourceType", "time", "count" },
            record.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Source_ContainsNoExternalAppOrSecretPersistencePrimitives()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var source = string.Join("\n", Directory.EnumerateFiles(
            Path.Combine(root!.FullName, "Desktop", "src", "Nyx.Desktop.Infrastructure", "Exports"),
            "WuwaPull*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("Process.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetClipboard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Clipboard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLogicalDrives", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("record-a", source, StringComparison.Ordinal);
    }

    private static string Url(
        string playerId = "100000001",
        string recordId = "record-a",
        string serverId = "server-a",
        string language = "en",
        string resources = "resources-a") =>
        "https://aki-gm-resources-oversea.aki-game.net/aki/gacha/index.html#/record?"
        + $"svr_id={serverId}&player_id={playerId}&lang={language}&record_id={recordId}&resources_id={resources}";

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record RequestCapture(
        HttpMethod Method,
        Uri Uri,
        string Body,
        int PoolType,
        string ContentType,
        string Accept);

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            this.handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class RecordingWuwaPacer : IWuwaPullRequestPacer
    {
        public int Calls { get; private set; }
        public List<TimeSpan> Delays { get; } = [];
        public ValueTask BeforeRequestAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Delays.Add(WuwaPullRequestPacer.RequestSpacing);
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] MaskClientLog(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        for (var index = 0; index < bytes.Length; index++)
        {
            var current = bytes[index];
            bytes[index] = (byte)(current ^ ((current & 1) != 0 ? 0xef : 0xa5));
        }
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory temp = new();
        private readonly HttpClient http;
        public Fixture()
        {
            Root = temp.Combine("install");
            LogPath = Path.Combine(Root, "Wuthering Waves Game", "Client", "Saved", "Logs", "Client.log");
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            Secret = "SECRET_RECORD_VALUE";
            File.WriteAllText(LogPath, Url(recordId: "record-baseline") + " " + Secret, Encoding.UTF8);
            Downloads = temp.Combine("downloads");
            Requests = [];
            http = new HttpClient(new DelegateHandler(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                Requests.Add(new(
                    request.Method,
                    request.RequestUri!,
                    body,
                    document.RootElement.GetProperty("cardPoolType").GetInt32(),
                    request.Content.Headers.ContentType?.MediaType ?? string.Empty,
                    request.Headers.Accept.Single().MediaType ?? string.Empty));
                return JsonResponse("{\"code\":0,\"data\":[]}");
            }));
            Provider = new WuwaPullExportProvider(
                http,
                Root,
                Downloads,
                new NoWaitWuwaPullRequestPacer(),
                new PullExportSafetyLimits(
                    TotalDuration: TimeSpan.FromSeconds(1),
                    CacheObservationDuration: TimeSpan.FromMilliseconds(80),
                    CachePollInterval: TimeSpan.FromMilliseconds(5)),
                ownsHttpClient: false);
        }

        public string Root { get; }
        public string LogPath { get; }
        public string Downloads { get; }
        public string Secret { get; }
        public List<RequestCapture> Requests { get; }
        public WuwaPullExportProvider Provider { get; }

        public void Dispose()
        {
            Provider.Dispose();
            http.Dispose();
            temp.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nyx-wuwa-pull-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string Combine(string value) => System.IO.Path.Combine(Path, value);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { }
        }
    }
}
