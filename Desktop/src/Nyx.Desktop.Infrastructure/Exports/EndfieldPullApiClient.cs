using System.Net;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed record EndfieldPullAccount(
    string Uid,
    string RoleId,
    string ServerId,
    string ServerName);

internal sealed record EndfieldPullRecord(
    string Id,
    string RecordType,
    string SeqId,
    string PoolId,
    string PoolName,
    string PoolType,
    string ItemId,
    string Name,
    string ItemType,
    int Rarity,
    DateTimeOffset ObtainedAt,
    bool IsNew,
    bool IsFree,
    string? BatchId = null);

internal sealed record EndfieldPullArchive(
    EndfieldPullAccount Account,
    IReadOnlyList<EndfieldPullRecord> Records);

internal sealed record EndfieldPullLimits(
    int MaximumResponseBytes = 1024 * 1024,
    int MaximumRequests = 2_000,
    int MaximumRecords = 10_000,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(15);
}

internal sealed class EndfieldPullApiClient
{
    internal const string BasicPool = "basic";
    internal const string BeginnerPool = "beginner";
    internal const string CharteredPool = "chartered";
    internal const string FestJointPool = "fest-joint";
    internal const string ArsenalPool = "arsenal";
    private static readonly Uri RoleEndpoint = new("https://u8.gryphline.com/game/role/v1/query_role_list");
    private static readonly Uri CharacterEndpoint = new("https://ef-webview.gryphline.com/api/record/char");
    private static readonly Uri WeaponPoolEndpoint = new("https://ef-webview.gryphline.com/api/record/weapon/pool");
    private static readonly Uri WeaponEndpoint = new("https://ef-webview.gryphline.com/api/record/weapon");
    private static readonly IReadOnlyList<(string Upstream, string Contract)> CharacterPoolTypes =
    [
        ("E_CharacterGachaPoolType_Standard", BasicPool),
        ("E_CharacterGachaPoolType_Beginner", BeginnerPool),
        ("E_CharacterGachaPoolType_Special", CharteredPool),
        ("E_CharacterGachaPoolType_Joint", FestJointPool),
    ];

    private readonly HttpClient http;
    private readonly IPullRequestPacer pacer;
    private readonly EndfieldPullLimits limits;
    private readonly TimeProvider timeProvider;

    public EndfieldPullApiClient(
        HttpClient http,
        IPullRequestPacer pacer,
        EndfieldPullLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));
        this.limits = limits ?? new EndfieldPullLimits();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.limits.MaximumResponseBytes is < 1_024 or > 1024 * 1024
            || this.limits.MaximumRequests is < 1 or > 2_000
            || this.limits.MaximumRecords is < 1 or > 10_000
            || this.limits.EffectiveRequestTimeout <= TimeSpan.Zero
            || this.limits.EffectiveRequestTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async ValueTask<EndfieldPullArchive> DownloadNewestValidAsync(
        IReadOnlyList<EndfieldPullHistoryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        var counters = new Counters(limits);
        PullExportException? last = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadAsync(candidate.Credential, counters, cancellationToken).ConfigureAwait(false);
            }
            catch (PullExportException exception) when (
                exception.ErrorCode is PullExportErrorCodes.UpstreamRejected or PullExportErrorCodes.UpstreamInvalid)
            {
                last = exception;
            }
        }
        throw last ?? new PullExportException(PullExportErrorCodes.UpstreamInvalid);
    }

    private async ValueTask<EndfieldPullArchive> DownloadAsync(
        EndfieldPullCredential credential,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var account = await ReadIdentityAsync(credential, counters, cancellationToken).ConfigureAwait(false);
        var records = new List<EndfieldPullRecord>();
        var characterPools = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pool in CharacterPoolTypes)
        {
            await ReadHistoryAsync(
                credential,
                counters,
                CharacterEndpoint,
                [new("pool_type", pool.Upstream)],
                pool.Contract,
                expectedPoolId: null,
                expectedPoolName: null,
                records,
                characterPools,
                cancellationToken).ConfigureAwait(false);
        }

        var weaponPools = await ReadWeaponPoolsAsync(credential, counters, cancellationToken).ConfigureAwait(false);
        foreach (var pool in weaponPools.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            await ReadHistoryAsync(
                credential,
                counters,
                WeaponEndpoint,
                [new("pool_id", pool.Key)],
                ArsenalPool,
                pool.Key,
                pool.Value,
                records,
                characterPools,
                cancellationToken).ConfigureAwait(false);
        }

        if (records.Count > limits.MaximumRecords
            || records.Select(static record => record.Id).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        return new(account, records);
    }

    private async ValueTask<EndfieldPullAccount> ReadIdentityAsync(
        EndfieldPullCredential credential,
        Counters counters,
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
            response = await SendAsync(HttpMethod.Post, RoleEndpoint, [], body, counters, cancellationToken)
                .ConfigureAwait(false);
            using var document = Parse(response);
            var root = RequireObject(document.RootElement);
            if (!root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Number
                || !status.TryGetInt32(out var code)
                || code != 0
                || !root.TryGetProperty("data", out var data))
                throw Invalid();
            data = RequireObject(data);
            var uid = RequiredIdentifier(data, "uid");
            if (!data.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array
                || roles.GetArrayLength() is 0 or > 32)
                throw Invalid();

            EndfieldPullAccount? match = null;
            foreach (var value in roles.EnumerateArray())
            {
                var role = RequireObject(value);
                var serverId = RequiredIdentifier(role, "serverId");
                if (!serverId.Equals(credential.ServerId, StringComparison.Ordinal)) continue;
                if (match is not null) throw Invalid();
                match = new(
                    uid,
                    RequiredIdentifier(role, "roleId"),
                    serverId,
                    RequiredText(role, "serverName", 256));
            }
            return match ?? throw Invalid();
        }
        finally
        {
            Array.Clear(body);
            if (response is not null) Array.Clear(response);
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> ReadWeaponPoolsAsync(
        EndfieldPullCredential credential,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            WeaponPoolEndpoint,
            CredentialQuery(credential),
            null,
            counters,
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = Parse(response);
            var root = RequireObject(document.RootElement);
            RequireZeroCode(root);
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() is 0 or > 512)
                throw Invalid();
            var pools = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in data.EnumerateArray())
            {
                var pool = RequireObject(value);
                if (!pools.TryAdd(
                    RequiredIdentifier(pool, "poolId"),
                    RequiredText(pool, "poolName", 256)))
                    throw Invalid();
            }
            return pools;
        }
        finally { Array.Clear(response); }
    }

    private async ValueTask ReadHistoryAsync(
        EndfieldPullCredential credential,
        Counters counters,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> fixedQuery,
        string poolType,
        string? expectedPoolId,
        string? expectedPoolName,
        List<EndfieldPullRecord> output,
        Dictionary<string, string> characterPools,
        CancellationToken cancellationToken)
    {
        ulong? previousSequence = null;
        long? previousTimestamp = null;
        string? cursor = null;
        while (true)
        {
            var query = CredentialQuery(credential).Concat(fixedQuery).ToList();
            if (cursor is not null) query.Add(new("seq_id", cursor));
            var response = await SendAsync(HttpMethod.Get, endpoint, query, null, counters, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using var document = Parse(response);
                var root = RequireObject(document.RootElement);
                RequireZeroCode(root);
                if (!root.TryGetProperty("data", out var data)) throw Invalid();
                data = RequireObject(data);
                if (!data.TryGetProperty("list", out var list)
                    || list.ValueKind != JsonValueKind.Array
                    || !data.TryGetProperty("hasMore", out var hasMoreValue)
                    || hasMoreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Invalid();

                string? lastSequence = null;
                foreach (var value in list.EnumerateArray())
                {
                    counters.AddRecord();
                    var record = RequireObject(value);
                    var seqId = RequiredIdentifier(record, "seqId");
                    if (!ulong.TryParse(seqId, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var sequence)
                        || previousSequence is not null && sequence >= previousSequence)
                        throw Invalid();
                    previousSequence = sequence;
                    lastSequence = seqId;

                    var timestampText = RequiredIdentifier(record, "gachaTs");
                    if (!long.TryParse(timestampText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var timestamp)
                        || timestamp <= 10_000_000_000
                        || previousTimestamp is not null && timestamp > previousTimestamp)
                        throw Invalid();
                    var obtainedAt = Timestamp(timestamp);
                    previousTimestamp = timestamp;

                    var poolId = RequiredIdentifier(record, "poolId");
                    var poolName = RequiredText(record, "poolName", 256);
                    if (expectedPoolId is not null
                        && (!poolId.Equals(expectedPoolId, StringComparison.Ordinal)
                            || !poolName.Equals(expectedPoolName, StringComparison.Ordinal)))
                        throw Invalid();
                    if (expectedPoolId is null)
                    {
                        if (characterPools.TryGetValue(poolId, out var existingType)
                            && !existingType.Equals(poolType, StringComparison.Ordinal))
                            throw Invalid();
                        characterPools[poolId] = poolType;
                    }

                    var kind = RequiredIdentifier(record, "kind");
                    _ = RequiredText(record, "nameText", 256);
                    if (!kind.Equals("draw", StringComparison.Ordinal))
                    {
                        if (expectedPoolId is null && kind.Equals("gift_intel_book", StringComparison.Ordinal))
                            continue;
                        throw Invalid();
                    }

                    var character = expectedPoolId is null;
                    var itemId = RequiredIdentifier(record, character ? "charId" : "weaponId");
                    var name = RequiredText(record, character ? "charName" : "weaponName", 256);
                    var rarity = RequiredRarity(record);
                    var isNew = RequiredBoolean(record, "isNew");
                    var isFree = character && RequiredBoolean(record, "isFree");
                    var itemType = character ? "character" : RequiredText(record, "weaponType", 128);
                    var recordType = character ? "character" : "weapon";
                    output.Add(new(
                        $"{recordType}:{poolId}:{seqId}",
                        recordType,
                        seqId,
                        poolId,
                        poolName,
                        poolType,
                        itemId,
                        name,
                        itemType,
                        rarity,
                        obtainedAt,
                        isNew,
                        isFree,
                        character ? null : poolId));
                }

                if (!hasMoreValue.GetBoolean()) return;
                if (lastSequence is null || lastSequence.Equals(cursor, StringComparison.Ordinal))
                    throw Invalid();
                cursor = lastSequence;
            }
            finally { Array.Clear(response); }
        }
    }

    private async ValueTask<byte[]> SendAsync(
        HttpMethod method,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> query,
        byte[]? body,
        Counters counters,
        CancellationToken cancellationToken)
    {
        ValidateOutboundRequest(method, endpoint, query, body);
        using var request = new HttpRequestMessage(method, BuildUri(endpoint, query));
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        }
        counters.AddRequest();
        await pacer.BeforeRequestAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.EffectiveRequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new PullExportException(PullExportErrorCodes.UpstreamRejected);
        }
        catch (HttpRequestException)
        {
            throw new PullExportException(PullExportErrorCodes.UpstreamRejected);
        }
    }

    private void ValidateOutboundRequest(
        HttpMethod method,
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> query,
        byte[]? body)
    {
        if (method == HttpMethod.Post)
        {
            if (!SameEndpoint(endpoint, RoleEndpoint) || query.Count != 0 || body is null) throw Invalid();
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
                    || !EndfieldPullHistoryLinkReader.IsSafeValue("token", tokenText)
                    || !EndfieldPullHistoryLinkReader.IsSafeValue("server_id", serverText))
                    throw Invalid();
                return;
            }
            catch (JsonException) { throw Invalid(); }
        }

        if (method != HttpMethod.Get || body is not null) throw Invalid();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query)
            if (!values.TryAdd(pair.Key, pair.Value)) throw Invalid();
        if (!values.TryGetValue("token", out var tokenValue)
            || !values.TryGetValue("server_id", out var serverId)
            || !values.TryGetValue("lang", out var language)
            || !EndfieldPullHistoryLinkReader.IsSafeValue("token", tokenValue)
            || !EndfieldPullHistoryLinkReader.IsSafeValue("server_id", serverId)
            || !EndfieldPullHistoryLinkReader.IsSafeValue("lang", language))
            throw Invalid();

        string[] required;
        var cursorAllowed = false;
        if (SameEndpoint(endpoint, CharacterEndpoint))
        {
            cursorAllowed = true;
            required = ["token", "server_id", "lang", "pool_type"];
            if (!values.TryGetValue("pool_type", out var poolType)
                || !CharacterPoolTypes.Any(pool => pool.Upstream.Equals(poolType, StringComparison.Ordinal)))
                throw Invalid();
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
                || !EndfieldPullHistoryLinkReader.IsSafeValue("pool_id", poolId))
                throw Invalid();
        }
        else throw Invalid();

        if (values.ContainsKey("seq_id") && !cursorAllowed) throw Invalid();
        var expectedCount = required.Length + (values.ContainsKey("seq_id") ? 1 : 0);
        if (values.Count != expectedCount || required.Any(key => !values.ContainsKey(key))) throw Invalid();
        if (values.TryGetValue("seq_id", out var sequence)
            && !ulong.TryParse(sequence, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw Invalid();
    }

    private async ValueTask<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and <= 399 || !response.IsSuccessStatusCode)
            throw new PullExportException(PullExportErrorCodes.UpstreamRejected);
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > limits.MaximumResponseBytes)
            throw new PullExportException(PullExportErrorCodes.SafetyLimit);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(limits.MaximumResponseBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > limits.MaximumResponseBytes)
                    throw new PullExportException(PullExportErrorCodes.SafetyLimit);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        finally { Array.Clear(buffer); }
    }

    private DateTimeOffset Timestamp(long milliseconds)
    {
        DateTimeOffset value;
        try { value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
        if (value < new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
            || value > timeProvider.GetUtcNow().AddDays(1))
            throw Invalid();
        return value.ToUniversalTime();
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw Invalid();
        return value;
    }

    private static string RequiredText(JsonElement value, string name, int maximumLength)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text
            || text.Length is 0
            || text.Length > maximumLength
            || text.Any(static character => char.IsControl(character)))
            throw Invalid();
        return text;
    }

    private static string RequiredIdentifier(JsonElement value, string name)
    {
        var text = RequiredText(value, name, 128);
        if (text.Any(static character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')))
            throw Invalid();
        return text;
    }

    private static bool RequiredBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid();
        return property.GetBoolean();
    }

    private static int RequiredRarity(JsonElement value)
    {
        if (!value.TryGetProperty("rarity", out var rarity)
            || rarity.ValueKind != JsonValueKind.Number
            || !rarity.TryGetInt32(out var number)
            || number is < 1 or > 6)
            throw Invalid();
        return number;
    }

    private static void RequireZeroCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.Number
            || !code.TryGetInt32(out var number)
            || number != 0)
            throw Invalid();
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

    private static IReadOnlyList<KeyValuePair<string, string>> CredentialQuery(EndfieldPullCredential credential) =>
    [
        new("token", credential.Token),
        new("server_id", credential.ServerId),
        new("lang", credential.Language),
    ];

    private static bool SameEndpoint(Uri actual, Uri expected) =>
        actual.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
        && actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
        && actual.IsDefaultPort
        && actual.UserInfo.Length == 0
        && actual.Fragment.Length == 0
        && actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal);

    private static PullExportException Invalid() =>
        new(PullExportErrorCodes.UpstreamInvalid);

    private sealed class Counters(EndfieldPullLimits limits)
    {
        private int requests;
        private int records;

        public void AddRequest()
        {
            if (requests >= limits.MaximumRequests)
                throw new PullExportException(PullExportErrorCodes.SafetyLimit);
            requests++;
        }

        public void AddRecord()
        {
            if (records >= limits.MaximumRecords)
                throw new PullExportException(PullExportErrorCodes.SafetyLimit);
            records++;
        }
    }
}
