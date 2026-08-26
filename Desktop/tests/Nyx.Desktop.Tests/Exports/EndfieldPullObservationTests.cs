using System.Net;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Infrastructure.Exports;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.Exports;

public sealed class EndfieldPullObservationTests
{
    private const int TailBytes = 8 * 1024 * 1024;
    private const int MaximumUrlBytes = 16 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumCandidates = 64;
    private const int MaximumPages = 2_000;
    private const int MaximumRecords = 10_000;
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private static readonly Uri RoleEndpoint = new("https://u8.gryphline.com/game/role/v1/query_role_list");
    private static readonly Uri CharacterEndpoint = new("https://ef-webview.gryphline.com/api/record/char");
    private static readonly Uri WeaponPoolEndpoint = new("https://ef-webview.gryphline.com/api/record/weapon/pool");
    private static readonly Uri WeaponEndpoint = new("https://ef-webview.gryphline.com/api/record/weapon");
    private static readonly string[] CharacterPoolTypes =
    [
        "E_CharacterGachaPoolType_Standard",
        "E_CharacterGachaPoolType_Beginner",
        "E_CharacterGachaPoolType_Special",
        "E_CharacterGachaPoolType_Joint",
    ];
    private static readonly HashSet<string> PageKeys = new(StringComparer.Ordinal)
    {
        "u8_token", "server", "server_id", "lang", "platform", "channel", "subChannel", "pool_id",
    };
    private static readonly HashSet<string> ApiKeys = new(StringComparer.Ordinal)
    {
        "token", "server_id", "lang", "pool_type", "pool_id", "seq_id",
    };

    [Fact]
    public void Tail_reader_reads_only_the_final_eight_mebibytes()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("large.log");
        var prefix = Enumerable.Repeat((byte)'x', 1024).ToArray();
        var tail = Enumerable.Range(0, TailBytes).Select(static value => (byte)(value % 251)).ToArray();
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(prefix);
            stream.Write(tail);
        }

        var result = ReadTail(path, temp.Path, default);
        try
        {
            Assert.Equal(TailBytes, result.Count);
            Assert.Equal(prefix.Length, result.SourceOffset);
            Assert.Equal(tail, result.Bytes);
        }
        finally
        {
            Array.Clear(result.Bytes);
            Array.Clear(prefix);
            Array.Clear(tail);
        }
    }

    [Fact]
    public void Tail_reader_accepts_a_short_file_and_rejects_a_directory()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("short.log");
        File.WriteAllText(path, "safe", Encoding.ASCII);

        var result = ReadTail(path, temp.Path, default);
        try
        {
            Assert.Equal(4, result.Count);
            Assert.Equal(0, result.SourceOffset);
        }
        finally { Array.Clear(result.Bytes); }

        var error = Assert.Throws<InvalidDataException>(() => ReadTail(temp.Path, temp.Path, default));
        Assert.Equal("phase7-source-invalid", error.Message);
        Assert.DoesNotContain(temp.Path, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Candidate_parser_returns_newest_exact_official_url_without_exposing_it()
    {
        var old = "https://ef-webview.gryphline.com/page/gacha_char?u8_token=OLD_MARKER&server=global&lang=en-us";
        var newest = "https://ef-webview.gryphline.com/api/record/char?token=NEW_MARKER&server_id=global&lang=en-us&pool_type=E_CharacterGachaPoolType_Standard";

        var candidates = ExtractCandidates(old + "\0" + newest, 0);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("NEW_MARKER", candidates[0].Credential.Token);
        Assert.Equal("global", candidates[0].Credential.ServerId);
        Assert.Equal(nameof(EndfieldCredential), candidates[0].Credential.ToString());
        Assert.DoesNotContain("MARKER", candidates[0].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://ef-webview.gryphline.com/page/gacha_char?u8_token=X&server=global")]
    [InlineData("https://ef-webview.gryphline.com.evil.invalid/page/gacha_char?u8_token=X&server=global")]
    [InlineData("https://ef-webview.gryphline.com:444/page/gacha_char?u8_token=X&server=global")]
    [InlineData("https://user@ef-webview.gryphline.com/page/gacha_char?u8_token=X&server=global")]
    [InlineData("https://ef-webview.gryphline.com/page/gacha_char?u8_token=X&server=global#fragment")]
    [InlineData("https://ef-webview.gryphline.com/page/gacha_char?u8_token=X&u8_token=Y&server=global")]
    [InlineData("https://ef-webview.gryphline.com/page/gacha_char?u8_token=X&server=global&evil=1")]
    [InlineData("https://ef-webview.gryphline.com/page/gacha_char/extra?u8_token=X&server=global")]
    [InlineData("https://ef-webview.gryphline.com/api/record/char?token=X&server_id=global&pool_type=ok&pool_type=again")]
    public void Candidate_parser_rejects_unreviewed_shapes(string value) =>
        Assert.Empty(ExtractCandidates(value, 0));

    [Fact]
    public void Candidate_parser_rejects_oversized_url_and_value()
    {
        Assert.Empty(ExtractCandidates(
            "https://ef-webview.gryphline.com/page/gacha_char?u8_token=" + new string('x', MaximumUrlBytes),
            0));
        Assert.Empty(ExtractCandidates(
            "https://ef-webview.gryphline.com/page/gacha_char?u8_token=" + new string('x', 4_097) + "&server=global",
            0));
    }

    [Fact]
    public void Request_allowlist_is_exact_in_method_host_and_path()
    {
        Assert.True(IsAllowedRequest(HttpMethod.Post, RoleEndpoint));
        Assert.True(IsAllowedRequest(HttpMethod.Get, CharacterEndpoint));
        Assert.True(IsAllowedRequest(HttpMethod.Get, WeaponPoolEndpoint));
        Assert.True(IsAllowedRequest(HttpMethod.Get, WeaponEndpoint));

        Assert.False(IsAllowedRequest(HttpMethod.Get, RoleEndpoint));
        Assert.False(IsAllowedRequest(HttpMethod.Post, CharacterEndpoint));
        Assert.False(IsAllowedRequest(HttpMethod.Get, new Uri("https://ef-webview.gryphline.com/api/record/weapon/extra")));
        Assert.False(IsAllowedRequest(HttpMethod.Get, new Uri("https://ef-webview.gryphline.com.evil.invalid/api/record/weapon")));
        Assert.False(IsAllowedRequest(HttpMethod.Get, new Uri("https://ef-webview.gryphline.com:444/api/record/weapon")));
    }

    [Fact]
    public async Task Pull_request_pacer_uses_exactly_250_milliseconds_between_requests()
    {
        var delays = new List<TimeSpan>();
        var pacer = new PullRequestPacer((duration, _) =>
        {
            delays.Add(duration);
            return ValueTask.CompletedTask;
        });

        await pacer.BeforeRequestAsync(default);
        await pacer.BeforeRequestAsync(default);

        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
        Assert.Equal(TimeSpan.FromMilliseconds(250), PullRequestPacer.RequestSpacing);
    }

    [Fact]
    public async Task Bounded_response_rejects_redirect_declared_and_streamed_overflow()
    {
        using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        Assert.Equal("phase7-response-redirect", (await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadBoundedAsync(redirect, default))).Message);

        using var rejected = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.Equal("phase7-response-http-401", (await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadBoundedAsync(rejected, default))).Message);

        using var declared = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[MaximumResponseBytes + 1]),
        };
        Assert.Equal("phase7-response-too-large", (await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadBoundedAsync(declared, default))).Message);

        using var streamed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(MaximumResponseBytes + 1),
        };
        Assert.Equal("phase7-response-too-large", (await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadBoundedAsync(streamed, default))).Message);
    }

    [Fact]
    public async Task Bounded_response_honors_caller_cancellation()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(32),
        };
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadBoundedAsync(response, canceled.Token));
    }

    [Fact]
    public void Global_counters_fail_at_page_2001_and_record_10001()
    {
        var limits = new ObservationCounters();
        for (var page = 0; page < MaximumPages; page++) limits.AddPage();
        Assert.Equal("phase7-page-limit", Assert.Throws<InvalidDataException>(limits.AddPage).Message);

        var records = new ObservationCounters();
        records.AddRecords(MaximumRecords);
        Assert.Equal("phase7-record-limit", Assert.Throws<InvalidDataException>(() => records.AddRecords(1)).Message);
    }

    [Fact]
    public void Sanitized_evidence_rejects_secret_collisions_and_bounds_field_names()
    {
        const string token = "PRIVATE_TOKEN_MARKER";
        var clean = SerializeSanitized(
            new ObservationSummary("candidate_validated", 1, ["code", "data", "hasMore"]),
            [token, "C:\\private-path", "PRIVATE_UID"]);

        Assert.DoesNotContain("PRIVATE", clean, StringComparison.Ordinal);
        Assert.Contains("candidate_validated", clean, StringComparison.Ordinal);

        Assert.Equal("phase7-secret-collision", Assert.Throws<InvalidDataException>(() => SerializeSanitized(
            new ObservationSummary(token, 1, ["code"]),
            [token])).Message);
        Assert.Equal("phase7-secret-collision", Assert.Throws<InvalidDataException>(() => SerializeSanitized(
            new ObservationSummary("C:\\private-path", 1, ["code"]),
            ["C:\\private-path"])).Message);
        Assert.Equal("phase7-field-name-invalid", Assert.Throws<InvalidDataException>(() => SerializeSanitized(
            new ObservationSummary("safe", 1, ["bad\nfield"]),
            [])).Message);
    }

    [Fact]
    public void Consent_gate_skips_before_any_private_path_or_http_work()
    {
        Assert.False(HasConsent("not-consent"));
        Assert.True(HasConsent("I_CONSENT"));
    }

    [Fact]
    public async Task Synthetic_contract_observation_proves_cursor_identity_and_weapon_grouping()
    {
        var handler = new SequenceHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(RoleEndpoint, request.RequestUri);
                return JsonResponse("""{"status":0,"msg":"ok","data":{"uid":"10001","roles":[{"roleId":"20002","serverId":"2","nickName":"Test","serverName":"Test"}]}}""");
            },
            request =>
            {
                Assert.Equal(CharacterEndpoint.AbsolutePath, request.RequestUri!.AbsolutePath);
                Assert.DoesNotContain("seq_id", request.RequestUri.Query, StringComparison.Ordinal);
                return JsonResponse("""{"code":0,"msg":"ok","data":{"list":[{"charId":"1","charName":"A","gachaTs":"1760000000","isFree":false,"isNew":true,"poolId":"LIMIT_1","poolName":"Test","rarity":6,"seqId":"10"},{"charId":"2","charName":"B","gachaTs":"1759999999","isFree":true,"isNew":false,"poolId":"LIMIT_1","poolName":"Test","rarity":5,"seqId":"9"}],"hasMore":true}}""");
            },
            request =>
            {
                Assert.Contains("seq_id=9", request.RequestUri!.Query, StringComparison.Ordinal);
                return JsonResponse("""{"code":0,"msg":"ok","data":{"list":[{"charId":"3","charName":"C","gachaTs":"1759999998","isFree":false,"isNew":false,"poolId":"LIMIT_1","poolName":"Test","rarity":4,"seqId":"8"}],"hasMore":false}}""");
            },
            request =>
            {
                Assert.Equal(WeaponPoolEndpoint.AbsolutePath, request.RequestUri!.AbsolutePath);
                return JsonResponse("""{"code":0,"msg":"ok","data":[{"poolId":"WEAPON_1","poolName":"Issue"}]}""");
            },
            request =>
            {
                Assert.Equal(WeaponEndpoint.AbsolutePath, request.RequestUri!.AbsolutePath);
                Assert.Contains("pool_id=WEAPON_1", request.RequestUri.Query, StringComparison.Ordinal);
                return JsonResponse("""{"code":0,"msg":"ok","data":{"list":[{"poolId":"WEAPON_1","poolName":"Issue","weaponId":"11","weaponName":"Weapon","weaponType":"Sword","rarity":6,"isNew":true,"gachaTs":"1760000000","seqId":"7"}],"hasMore":false}}""");
            });
        using var http = new HttpClient(handler);
        var pacer = new PullRequestPacer(static (_, _) => ValueTask.CompletedTask);
        var counters = new ObservationCounters();
        var credential = new EndfieldCredential("TEST_TOKEN", "2", "en-us");

        var identity = await ReadIdentityAsync(http, pacer, counters, credential, default);
        var characters = await ReadHistoryAsync(
            http, pacer, counters, CharacterEndpoint, credential,
            [new("pool_type", CharacterPoolTypes[2])], "charId", "charName", true, null, default);
        var pools = await ReadWeaponPoolsAsync(http, pacer, counters, credential, default);
        var weapons = await ReadHistoryAsync(
            http, pacer, counters, WeaponEndpoint, credential,
            [new("pool_id", pools.PoolIds[0])], "weaponId", "weaponName", false, pools.PoolIds[0], default);

        Assert.Equal("10001", identity.Uid);
        Assert.Equal("20002", identity.RoleId);
        Assert.Equal(2, characters.Pages);
        Assert.Equal(3, characters.Records);
        Assert.Equal(["LIMIT_1"], characters.PoolIds);
        Assert.Equal(["WEAPON_1"], pools.PoolIds);
        Assert.Single(weapons.PoolIds);
        Assert.Equal(5, counters.Pages);
        Assert.Equal(4, counters.Records);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task Request_shape_and_page_limit_fail_before_transport()
    {
        var handler = new SequenceHandler();
        using var http = new HttpClient(handler);
        var pacer = new PullRequestPacer(static (_, _) => ValueTask.CompletedTask);
        var exhausted = new ObservationCounters();
        for (var index = 0; index < MaximumPages; index++) exhausted.AddPage();
        var validQuery = new KeyValuePair<string, string>[]
        {
            new("token", "TEST_TOKEN"),
            new("server_id", "2"),
            new("lang", "en-us"),
            new("pool_type", CharacterPoolTypes[0]),
        };

        Assert.Equal("phase7-page-limit", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            SendAsync(http, pacer, exhausted, HttpMethod.Get, CharacterEndpoint, validQuery, null, default))).Message);
        Assert.Equal(MaximumPages, exhausted.Pages);

        var fresh = new ObservationCounters();
        Assert.Equal("phase7-request-invalid", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            SendAsync(http, pacer, fresh, HttpMethod.Get, CharacterEndpoint, validQuery[..3], null, default))).Message);
        var invalidPoolQuery = validQuery[..3].Append(new KeyValuePair<string, string>("seq_id", "1")).ToArray();
        Assert.Equal("phase7-request-invalid", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            SendAsync(http, pacer, fresh, HttpMethod.Get, WeaponPoolEndpoint, invalidPoolQuery, null, default))).Message);
        Assert.Equal(0, fresh.Pages);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Consented_observation()
    {
        if (!HasConsent(Environment.GetEnvironmentVariable("PENGO_PHASE7_OBSERVE"))) return;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new[]
        {
            (Root: local, Path: Path.Combine(local, "PlatformProcess", "Cache", "data_1")),
            (Root: profile, Path: Path.Combine(profile, "AppData", "LocalLow", "Gryphline", "Endfield", "sdklogs", "HGWebview.log")),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var opened = new bool[2];
        var tailCounts = new int[2];
        var candidateCounts = new int[2];
        var candidates = new List<(DateTime LastWriteUtc, EndfieldCandidate Candidate)>();
        for (var index = 0; index < paths.Length; index++)
        {
            TailRead tail;
            try { tail = ReadTail(paths[index].Path, paths[index].Root, timeout.Token); }
            catch (InvalidDataException) { continue; }
            opened[index] = true;
            tailCounts[index] = tail.Count;
            try
            {
                var text = Encoding.Latin1.GetString(tail.Bytes, 0, tail.Count);
                var found = ExtractCandidates(text, tail.SourceOffset);
                candidateCounts[index] = found.Count;
                candidates.AddRange(found.Select(candidate => (tail.LastWriteUtc, candidate)));
            }
            finally { Array.Clear(tail.Bytes); }
        }

        var newest = candidates
            .OrderByDescending(static value => value.LastWriteUtc)
            .ThenByDescending(static value => value.Candidate.EndOffset)
            .Select(static value => value.Candidate)
            .FirstOrDefault();
        if (newest is null) throw new InvalidDataException("phase7-candidate-unproven");
        var secrets = paths.Select(static source => source.Path).ToList();
        secrets.Add(newest.Credential.Token);
        try
        {
            var contract = await ObserveContractAsync(newest.Credential, timeout.Token);
            secrets.Add(contract.Uid);
            secrets.Add(contract.RoleId);
            Console.WriteLine(SerializeSanitized(new
            {
                schema = "nyx-endfield-pull-observation-v1",
                status = "contract_observed",
                sourceOpened = opened,
                tailBytesRead = tailCounts,
                candidateCount = candidateCounts,
                newestCandidateValidated = true,
                queryFields = newest.QueryFields,
                networkCalled = true,
                contract.Summary,
                secretScan = "clean",
            }, secrets));
        }
        catch (OperationCanceledException)
        {
            throw new InvalidDataException("phase7-observation-timeout");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception)
        {
            throw new InvalidDataException("phase7-observation-failed");
        }
    }

    private static async Task<ContractObservation> ObserveContractAsync(
        EndfieldCredential credential,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var pacer = new PullRequestPacer();
        var counters = new ObservationCounters();
        var identity = await ReadIdentityAsync(http, pacer, counters, credential, cancellationToken);

        var characterParts = new List<HistoryObservation>();
        foreach (var poolType in CharacterPoolTypes)
        {
            characterParts.Add(await ReadHistoryAsync(
                http,
                pacer,
                counters,
                CharacterEndpoint,
                credential,
                [new("pool_type", poolType)],
                "charId",
                "charName",
                requiresFreeFlag: true,
                expectedPoolId: null,
                cancellationToken));
        }

        var weaponPools = await ReadWeaponPoolsAsync(http, pacer, counters, credential, cancellationToken);
        var weaponParts = new List<HistoryObservation>();
        foreach (var poolId in weaponPools.PoolIds)
        {
            weaponParts.Add(await ReadHistoryAsync(
                http,
                pacer,
                counters,
                WeaponEndpoint,
                credential,
                [new("pool_id", poolId)],
                "weaponId",
                "weaponName",
                requiresFreeFlag: false,
                expectedPoolId: poolId,
                cancellationToken));
        }

        return new(identity.Uid, identity.RoleId, new(
            counters.Pages,
            counters.Records,
            identity.Summary,
            MergeHistory(CharacterPoolTypes, characterParts),
            weaponPools.Summary,
            MergeHistory(weaponPools.PoolIds, weaponParts)));
    }

    private static async Task<IdentityObservation> ReadIdentityAsync(
        HttpClient http,
        PullRequestPacer pacer,
        ObservationCounters counters,
        EndfieldCredential credential,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            token = credential.Token,
            serverId = credential.ServerId,
        });
        byte[]? response = null;
        try
        {
            response = await SendAsync(
                http, pacer, counters, HttpMethod.Post, RoleEndpoint, [], body, cancellationToken);
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            var root = RequireObject(document.RootElement);
            var rootFields = FieldNames(root);
            if (!root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Number
                || !status.TryGetInt32(out var code)
                || code != 0
                || !root.TryGetProperty("data", out var data))
                throw new InvalidDataException("phase7-identity-invalid");
            data = RequireObject(data);
            var dataFields = FieldNames(data);
            var uid = RequiredString(data, "uid", 128);
            if (!data.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array
                || roles.GetArrayLength() is 0 or > 32)
                throw new InvalidDataException("phase7-identity-invalid");

            var roleFields = new HashSet<string>(StringComparer.Ordinal);
            string? roleId = null;
            foreach (var roleValue in roles.EnumerateArray())
            {
                var role = RequireObject(roleValue);
                roleFields.UnionWith(FieldNames(role));
                var serverId = RequiredString(role, "serverId", 128);
                if (!serverId.Equals(credential.ServerId, StringComparison.Ordinal)) continue;
                if (roleId is not null) throw new InvalidDataException("phase7-identity-invalid");
                roleId = RequiredString(role, "roleId", 128);
            }
            if (roleId is null) throw new InvalidDataException("phase7-identity-invalid");
            return new(uid, roleId, new(
                roles.GetArrayLength(),
                true,
                rootFields,
                dataFields,
                Sorted(roleFields)));
        }
        catch (JsonException) { throw new InvalidDataException("phase7-identity-invalid"); }
        finally
        {
            Array.Clear(body);
            if (response is not null) Array.Clear(response);
        }
    }

    private static async Task<WeaponPoolObservation> ReadWeaponPoolsAsync(
        HttpClient http,
        PullRequestPacer pacer,
        ObservationCounters counters,
        EndfieldCredential credential,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            http,
            pacer,
            counters,
            HttpMethod.Get,
            WeaponPoolEndpoint,
            CredentialQuery(credential),
            null,
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            var root = RequireObject(document.RootElement);
            var rootFields = FieldNames(root);
            RequireZeroCode(root, "phase7-weapon-pools-invalid");
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() is 0 or > 512)
                throw new InvalidDataException("phase7-weapon-pools-invalid");

            var poolIds = new HashSet<string>(StringComparer.Ordinal);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in data.EnumerateArray())
            {
                var pool = RequireObject(value);
                fields.UnionWith(FieldNames(pool));
                var poolId = RequiredIdentifier(pool, "poolId");
                _ = RequiredString(pool, "poolName", 256);
                if (!poolIds.Add(poolId)) throw new InvalidDataException("phase7-weapon-pools-invalid");
            }
            var sortedPoolIds = Sorted(poolIds);
            return new(sortedPoolIds, new(sortedPoolIds.Length, rootFields, Sorted(fields)));
        }
        catch (JsonException) { throw new InvalidDataException("phase7-weapon-pools-invalid"); }
        finally { Array.Clear(response); }
    }

    private static async Task<HistoryObservation> ReadHistoryAsync(
        HttpClient http,
        PullRequestPacer pacer,
        ObservationCounters counters,
        Uri endpoint,
        EndfieldCredential credential,
        IReadOnlyList<KeyValuePair<string, string>> fixedQuery,
        string itemIdField,
        string itemNameField,
        bool requiresFreeFlag,
        string? expectedPoolId,
        CancellationToken cancellationToken)
    {
        var rootFields = new HashSet<string>(StringComparer.Ordinal);
        var dataFields = new HashSet<string>(StringComparer.Ordinal);
        var recordFields = new HashSet<string>(StringComparer.Ordinal);
        var poolIds = new HashSet<string>(StringComparer.Ordinal);
        ulong? previousSequence = null;
        long? previousTimestamp = null;
        string? cursor = null;
        var pages = 0;
        var records = 0;

        while (true)
        {
            var query = CredentialQuery(credential).Concat(fixedQuery).ToList();
            if (cursor is not null) query.Add(new("seq_id", cursor));
            var response = await SendAsync(
                http, pacer, counters, HttpMethod.Get, endpoint, query, null, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
                var root = RequireObject(document.RootElement);
                rootFields.UnionWith(FieldNames(root));
                RequireZeroCode(root, "phase7-history-invalid");
                if (!root.TryGetProperty("data", out var data))
                    throw new InvalidDataException("phase7-history-invalid");
                data = RequireObject(data);
                dataFields.UnionWith(FieldNames(data));
                if (!data.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array
                    || !data.TryGetProperty("hasMore", out var hasMoreValue)
                    || hasMoreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("phase7-history-invalid");
                var hasMore = hasMoreValue.GetBoolean();
                pages++;
                string? lastSequence = null;
                foreach (var value in list.EnumerateArray())
                {
                    var record = RequireObject(value);
                    recordFields.UnionWith(FieldNames(record));
                    var seqId = RequiredIdentifier(record, "seqId");
                    if (!ulong.TryParse(seqId, out var sequence)
                        || previousSequence is not null && sequence >= previousSequence)
                        throw new InvalidDataException("phase7-sequence-invalid");
                    previousSequence = sequence;
                    lastSequence = seqId;

                    var timestampText = RequiredIdentifier(record, "gachaTs");
                    if (!long.TryParse(timestampText, out var timestamp)
                        || !IsPlausibleTimestamp(timestamp)
                        || previousTimestamp is not null && timestamp > previousTimestamp)
                        throw new InvalidDataException("phase7-timestamp-invalid");
                    previousTimestamp = timestamp;

                    var poolId = RequiredIdentifier(record, "poolId");
                    if (expectedPoolId is not null && !poolId.Equals(expectedPoolId, StringComparison.Ordinal))
                        throw new InvalidDataException("phase7-weapon-group-invalid");
                    poolIds.Add(poolId);
                    _ = RequiredString(record, "poolName", 256);
                    _ = RequiredIdentifier(record, itemIdField);
                    _ = RequiredString(record, itemNameField, 256);
                    RequireRarity(record);
                    RequireBoolean(record, "isNew");
                    if (requiresFreeFlag) RequireBoolean(record, "isFree");
                    else _ = RequiredString(record, "weaponType", 128);
                    counters.AddRecords(1);
                    records++;
                }
                if (!hasMore) break;
                if (lastSequence is null || lastSequence.Equals(cursor, StringComparison.Ordinal))
                    throw new InvalidDataException("phase7-pagination-invalid");
                cursor = lastSequence;
            }
            catch (JsonException) { throw new InvalidDataException("phase7-history-invalid"); }
            finally { Array.Clear(response); }
        }

        return new(
            pages,
            records,
            Sorted(poolIds),
            Sorted(rootFields),
            Sorted(dataFields),
            Sorted(recordFields));
    }

    private static HistorySummary MergeHistory(
        IReadOnlyList<string> requestedPools,
        IReadOnlyList<HistoryObservation> parts) =>
        new(
            requestedPools.Count,
            parts.Sum(static part => part.Pages),
            parts.Sum(static part => part.Records),
            parts.Any(static part => part.Pages > 1),
            parts.Any(static part => part.Pages > 1),
            Sorted(parts.SelectMany(static part => part.PoolIds)).Length,
            parts.All(static part => part.Records == 0 || part.PoolIds.Count > 0),
            Sorted(parts.SelectMany(static part => part.RootFields)),
            Sorted(parts.SelectMany(static part => part.DataFields)),
            Sorted(parts.SelectMany(static part => part.RecordFields)),
            true,
            true,
            true);

    private static async Task<byte[]> SendAsync(
        HttpClient http,
        PullRequestPacer pacer,
        ObservationCounters counters,
        HttpMethod method,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> query,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        ValidateOutboundRequest(method, endpoint, query, body);
        var uri = BuildUri(endpoint, query);
        if (!IsAllowedRequest(method, uri)) throw new InvalidDataException("phase7-request-invalid");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        }
        counters.AddPage();
        await pacer.BeforeRequestAsync(cancellationToken);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return await ReadBoundedAsync(response, cancellationToken);
    }

    private static void ValidateOutboundRequest(
        HttpMethod method,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> query,
        byte[]? body)
    {
        if (method == HttpMethod.Post)
        {
            if (!SameEndpoint(endpoint, RoleEndpoint) || query.Count != 0 || body is null)
                throw new InvalidDataException("phase7-request-invalid");
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
                var root = RequireObject(document.RootElement);
                if (root.EnumerateObject().Count() != 2
                    || !root.TryGetProperty("token", out var token)
                    || token.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("serverId", out var server)
                    || server.ValueKind != JsonValueKind.String
                    || token.GetString() is not { } tokenText
                    || server.GetString() is not { } serverText
                    || !IsSafeValue("token", tokenText)
                    || !IsSafeValue("server_id", serverText))
                    throw new InvalidDataException("phase7-request-invalid");
                return;
            }
            catch (JsonException) { throw new InvalidDataException("phase7-request-invalid"); }
        }

        if (method != HttpMethod.Get || body is not null)
            throw new InvalidDataException("phase7-request-invalid");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query)
            if (!values.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException("phase7-request-invalid");
        if (!values.TryGetValue("token", out var credentialToken)
            || !values.TryGetValue("server_id", out var serverId)
            || !values.TryGetValue("lang", out var language)
            || !IsSafeValue("token", credentialToken)
            || !IsSafeValue("server_id", serverId)
            || !IsSafeValue("lang", language))
            throw new InvalidDataException("phase7-request-invalid");

        string[] required;
        var cursorAllowed = false;
        if (SameEndpoint(endpoint, CharacterEndpoint))
        {
            cursorAllowed = true;
            required = ["token", "server_id", "lang", "pool_type"];
            if (!values.TryGetValue("pool_type", out var poolType)
                || !CharacterPoolTypes.Contains(poolType, StringComparer.Ordinal))
                throw new InvalidDataException("phase7-request-invalid");
        }
        else if (SameEndpoint(endpoint, WeaponPoolEndpoint))
        {
            required = ["token", "server_id", "lang"];
        }
        else if (SameEndpoint(endpoint, WeaponEndpoint))
        {
            cursorAllowed = true;
            required = ["token", "server_id", "lang", "pool_id"];
            if (!values.TryGetValue("pool_id", out var poolId)
                || !IsSafeValue("pool_id", poolId))
                throw new InvalidDataException("phase7-request-invalid");
        }
        else
        {
            throw new InvalidDataException("phase7-request-invalid");
        }

        if (values.ContainsKey("seq_id") && !cursorAllowed)
            throw new InvalidDataException("phase7-request-invalid");
        var expectedCount = required.Length + (values.ContainsKey("seq_id") ? 1 : 0);
        if (values.Count != expectedCount || required.Any(key => !values.ContainsKey(key)))
            throw new InvalidDataException("phase7-request-invalid");
        if (values.TryGetValue("seq_id", out var sequence) && !ulong.TryParse(sequence, out _))
            throw new InvalidDataException("phase7-request-invalid");
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        if (query.Count == 0) return endpoint;
        var builder = new StringBuilder(endpoint.AbsoluteUri).Append('?');
        for (var index = 0; index < query.Count; index++)
        {
            if (index != 0) builder.Append('&');
            builder.Append(Uri.EscapeDataString(query[index].Key))
                .Append('=')
                .Append(Uri.EscapeDataString(query[index].Value));
        }
        return new(builder.ToString(), UriKind.Absolute);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CredentialQuery(EndfieldCredential credential) =>
    [
        new("token", credential.Token),
        new("server_id", credential.ServerId),
        new("lang", credential.Language),
    ];

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("phase7-json-invalid");
        return value;
    }

    private static string[] FieldNames(JsonElement value)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!IsSafeEvidenceName(property.Name))
                throw new InvalidDataException("phase7-field-name-invalid");
            fields.Add(property.Name);
        }
        return Sorted(fields);
    }

    private static string RequiredString(JsonElement value, string name, int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text
            || text.Length is 0
            || text.Length > maximumLength
            || text.Any(static character => char.IsControl(character)))
            throw new InvalidDataException("phase7-field-invalid");
        return text;
    }

    private static string RequiredIdentifier(JsonElement value, string name)
    {
        var text = RequiredString(value, name, 128);
        if (text.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')))
            throw new InvalidDataException("phase7-field-invalid");
        return text;
    }

    private static void RequireBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("phase7-field-invalid");
    }

    private static void RequireRarity(JsonElement value)
    {
        if (!value.TryGetProperty("rarity", out var rarity)
            || rarity.ValueKind != JsonValueKind.Number
            || !rarity.TryGetInt32(out var number)
            || number is < 1 or > 6)
            throw new InvalidDataException("phase7-field-invalid");
    }

    private static void RequireZeroCode(JsonElement root, string error)
    {
        if (!root.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.Number
            || !code.TryGetInt32(out var number)
            || number != 0)
            throw new InvalidDataException(error);
    }

    private static bool IsPlausibleTimestamp(long timestamp)
    {
        try
        {
            var value = timestamp > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return value >= new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
                && value <= DateTimeOffset.UtcNow.AddDays(1);
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool IsSafeEvidenceName(string value) =>
        value.Length is > 0 and <= 64
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string[] Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static HttpResponseMessage JsonResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private static TailRead ReadTail(string path, string bindingRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes = null;
        try
        {
            using var ancestors = PublisherAncestorDirectoryBinding.Open(bindingRoot, path);
            using var handle = NativeMethods.CreateFileW(
                path,
                GenericRead,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid) throw new InvalidDataException("phase7-source-invalid");
            var before = CaptureTailStamp(handle);
            if ((before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("phase7-source-invalid");
            using var source = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            var initialLength = before.Length;
            var sourceOffset = Math.Max(0, initialLength - TailBytes);
            source.Position = sourceOffset;
            bytes = new byte[checked((int)(initialLength - sourceOffset))];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var after = CaptureTailStamp(source.SafeFileHandle);
            if (after != before || source.Length != initialLength)
                throw new InvalidDataException("phase7-source-changed");
            if (count != bytes.Length) throw new InvalidDataException("phase7-source-changed");
            var result = bytes;
            bytes = null;
            return new(result, count, sourceOffset, before.LastWriteUtc);
        }
        catch (InvalidDataException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InvalidDataException("phase7-source-invalid"); }
        finally
        {
            if (bytes is not null) Array.Clear(bytes);
        }
    }

    private static TailStamp CaptureTailStamp(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            throw new InvalidDataException("phase7-source-invalid");
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var lastWrite = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        DateTime lastWriteUtc;
        try { lastWriteUtc = DateTime.FromFileTimeUtc(lastWrite); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("phase7-source-invalid"); }
        return new(
            (FileAttributes)information.FileAttributes,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            length,
            lastWriteUtc);
    }

    private static IReadOnlyList<EndfieldCandidate> ExtractCandidates(
        string text,
        long sourceOffset)
    {
        if (text is null) return [];
        var found = new Queue<EndfieldCandidate>(MaximumCandidates);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf("https://", cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = start;
            while (end < text.Length && !IsUrlTerminator(text[end]))
            {
                if (end - start >= MaximumUrlBytes) { end = -1; break; }
                end++;
            }
            cursor = end < 0 ? start + 8 : Math.Max(start + 8, end);
            if (end <= start) continue;
            if (!TryParseCandidate(text[start..end], out var candidate)) continue;
            if (found.Count == MaximumCandidates) found.Dequeue();
            found.Enqueue(candidate! with { StartOffset = sourceOffset + start, EndOffset = sourceOffset + end });
        }
        return found.Reverse().ToArray();
    }

    private static bool TryParseCandidate(string raw, out EndfieldCandidate? candidate)
    {
        candidate = null;
        if (raw.Length is 0 or > MaximumUrlBytes
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.Host.Equals("ef-webview.gryphline.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || uri.Query.Length <= 1)
            return false;

        var page = uri.AbsolutePath.Equals("/page/gacha_char", StringComparison.Ordinal);
        var api = uri.AbsolutePath.Equals(CharacterEndpoint.AbsolutePath, StringComparison.Ordinal)
            || uri.AbsolutePath.Equals(WeaponPoolEndpoint.AbsolutePath, StringComparison.Ordinal)
            || uri.AbsolutePath.Equals(WeaponEndpoint.AbsolutePath, StringComparison.Ordinal);
        if (!page && !api) return false;
        var allowed = page ? PageKeys : ApiKeys;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in uri.Query[1..].Split('&', StringSplitOptions.None))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0 || equals == segment.Length - 1) return false;
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(segment[..equals]);
                value = Uri.UnescapeDataString(segment[(equals + 1)..].Replace('+', ' '));
            }
            catch (Exception) { return false; }
            if (!allowed.Contains(key) || !values.TryAdd(key, value) || !IsSafeValue(key, value)) return false;
        }

        var tokenKey = page ? "u8_token" : "token";
        if (!values.TryGetValue(tokenKey, out var token)) return false;
        var serverKeys = page ? new[] { "server", "server_id" } : ["server_id"];
        var servers = serverKeys.Where(values.ContainsKey).ToArray();
        if (servers.Length != 1) return false;
        var serverId = values[servers[0]];
        var language = values.GetValueOrDefault("lang", "en-us");
        candidate = new(new(token, serverId, language), values.Keys.Order(StringComparer.Ordinal).ToArray(), 0, 0);
        return true;
    }

    private static bool IsSafeValue(string key, string value)
    {
        var maximum = key is "u8_token" or "token" ? 4_096 : 512;
        if (value.Length is 0 || value.Length > maximum || value.Any(static c => c > 0x7f || char.IsControl(c) || char.IsWhiteSpace(c)))
            return false;
        if (key is "u8_token" or "token")
            return value.All(static c => c is >= '!' and <= '~' && c is not '&' and not '#' and not '?' and not '\\');
        return value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '~');
    }

    private static bool IsAllowedRequest(HttpMethod method, Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0)
            return false;
        if (method == HttpMethod.Post) return SameEndpoint(uri, RoleEndpoint);
        return method == HttpMethod.Get
            && (SameEndpoint(uri, CharacterEndpoint)
                || SameEndpoint(uri, WeaponPoolEndpoint)
                || SameEndpoint(uri, WeaponEndpoint));
    }

    private static bool SameEndpoint(Uri actual, Uri expected) =>
        actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
        && actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
            throw new InvalidDataException("phase7-response-redirect");
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"phase7-response-http-{(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("phase7-response-too-large");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(MaximumResponseBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (output.Length + count > MaximumResponseBytes)
                    throw new InvalidDataException("phase7-response-too-large");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            return output.ToArray();
        }
        finally { Array.Clear(buffer); }
    }

    private static string SerializeSanitized<T>(T value, IEnumerable<string> secrets)
    {
        ValidateFieldNames(JsonSerializer.SerializeToElement(value));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var secret in secrets.Where(static item => !string.IsNullOrEmpty(item)))
                if (text.Contains(secret, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(JsonEncodedText.Encode(secret).ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("phase7-secret-collision");
            return text;
        }
        finally { Array.Clear(bytes); }
    }

    private static void ValidateFieldNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Length is 0 or > 64
                    || property.Name.Any(static c => c > 0x7f || !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
                    throw new InvalidDataException("phase7-field-name-invalid");
                ValidateFieldNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateFieldNames(item);
        }
        else if (value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && text.Length <= 64
            && text.Contains("field", StringComparison.OrdinalIgnoreCase)
            && text.Any(static c => c > 0x7f || char.IsControl(c)))
        {
            throw new InvalidDataException("phase7-field-name-invalid");
        }
    }

    private static bool HasConsent(string? value) =>
        string.Equals(value, "I_CONSENT", StringComparison.Ordinal);

    private static bool IsUrlTerminator(char value) =>
        value is '\0' or '\r' or '\n' or ' ' or '\t' or '"' or '\'' or '<' or '>' or ',';

    private sealed record TailRead(byte[] Bytes, int Count, long SourceOffset, DateTime LastWriteUtc);
    private sealed record TailStamp(
        FileAttributes Attributes,
        uint VolumeSerialNumber,
        ulong FileId,
        uint NumberOfLinks,
        long Length,
        DateTime LastWriteUtc);
    private sealed record EndfieldCredential(string Token, string ServerId, string Language)
    {
        public override string ToString() => nameof(EndfieldCredential);
    }
    private sealed record EndfieldCandidate(
        EndfieldCredential Credential,
        IReadOnlyList<string> QueryFields,
        long StartOffset,
        long EndOffset)
    {
        public override string ToString() => nameof(EndfieldCandidate);
    }
    private sealed record ContractObservation(string Uid, string RoleId, ContractSummary Summary);
    private sealed record ContractSummary(
        int RequestCount,
        int RecordCount,
        IdentitySummary Identity,
        HistorySummary Characters,
        WeaponPoolSummary WeaponPools,
        HistorySummary Weapons);
    private sealed record IdentityObservation(string Uid, string RoleId, IdentitySummary Summary);
    private sealed record IdentitySummary(
        int RoleCount,
        bool RequestedServerMatched,
        IReadOnlyList<string> RootFields,
        IReadOnlyList<string> DataFields,
        IReadOnlyList<string> RoleFields);
    private sealed record WeaponPoolObservation(
        IReadOnlyList<string> PoolIds,
        WeaponPoolSummary Summary);
    private sealed record WeaponPoolSummary(
        int PoolCount,
        IReadOnlyList<string> RootFields,
        IReadOnlyList<string> RecordFields);
    private sealed record HistoryObservation(
        int Pages,
        int Records,
        IReadOnlyList<string> PoolIds,
        IReadOnlyList<string> RootFields,
        IReadOnlyList<string> DataFields,
        IReadOnlyList<string> RecordFields);
    private sealed record HistorySummary(
        int RequestedPoolCount,
        int PageCount,
        int RecordCount,
        bool MultiplePagesObserved,
        bool CursorProgressed,
        int DistinctPoolCount,
        bool PoolCodesPresent,
        IReadOnlyList<string> RootFields,
        IReadOnlyList<string> DataFields,
        IReadOnlyList<string> RecordFields,
        bool SequenceStrictlyDescending,
        bool TimestampOrderValid,
        bool PoolGroupingValid);
    private sealed record ObservationSummary(string Status, int RequestCount, IReadOnlyList<string> FieldNames);

    private sealed class ObservationCounters
    {
        private int pages;
        private int records;
        public int Pages => pages;
        public int Records => records;
        public void AddPage()
        {
            if (pages >= MaximumPages) throw new InvalidDataException("phase7-page-limit");
            pages++;
        }
        public void AddRecords(int count)
        {
            if (count < 0 || records > MaximumRecords - count)
                throw new InvalidDataException("phase7-record-limit");
            records += count;
        }
    }

    private sealed class UnknownLengthContent(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[length]).AsTask();
        protected override bool TryComputeLength(out long lengthValue)
        {
            lengthValue = 0;
            return false;
        }
    }

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
            if (calls >= steps.Length) throw new InvalidOperationException("Unexpected synthetic request.");
            return Task.FromResult(steps[calls++](request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nyx-phase7-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public string Combine(string value) => System.IO.Path.Combine(Path, value);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
