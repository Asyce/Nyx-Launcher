using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed record WuwaPullRecord(
    string Id,
    int CardPoolType,
    string ResourceId,
    int QualityLevel,
    string Name,
    string ResourceType,
    string Time,
    int Count)
{
    public override string ToString() => nameof(WuwaPullRecord);
}

internal sealed record WuwaPullArchive(
    string Uid,
    IReadOnlyList<WuwaPullRecord> Records)
{
    public override string ToString() => nameof(WuwaPullArchive);
}

internal interface IWuwaPullRequestPacer
{
    ValueTask BeforeRequestAsync(CancellationToken cancellationToken);
}

/// <summary>Applies the fixed WuWa request spacing before every request.</summary>
internal sealed class WuwaPullRequestPacer : IWuwaPullRequestPacer
{
    internal static TimeSpan RequestSpacing { get; } = TimeSpan.FromMilliseconds(250);
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delay;

    public WuwaPullRequestPacer()
        : this(static async (duration, cancellationToken) =>
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false))
    {
    }

    internal WuwaPullRequestPacer(Func<TimeSpan, CancellationToken, ValueTask> delay) =>
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));

    public ValueTask BeforeRequestAsync(CancellationToken cancellationToken) =>
        delay(RequestSpacing, cancellationToken);
}

internal sealed class NoWaitWuwaPullRequestPacer : IWuwaPullRequestPacer
{
    public ValueTask BeforeRequestAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class WuwaPullApiClient(
    HttpClient httpClient,
    PullExportSafetyLimits limits,
    IWuwaPullRequestPacer pacer)
{
    internal static readonly Uri Endpoint = new("https://gmserver-api.aki-game2.net/gacha/record/query");
    private const int MaximumPoolType = 7;

    public async ValueTask<WuwaPullArchive> DownloadAsync(
        WuwaPullHistoryUrl auth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var records = new List<WuwaPullRecord>(Math.Min(limits.MaximumRecords, 4096));
        var byId = new Dictionary<string, WuwaPullRecord>(StringComparer.Ordinal);
        for (var poolType = 1; poolType <= MaximumPoolType; poolType++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await FetchPoolAsync(auth, poolType, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records)
            {
                if (!MatchesPlayer(page.PlayerId, auth.PlayerId)
                    || !MatchesPlayer(page.Uid, auth.PlayerId))
                    throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                if (byId.TryGetValue(record.Id, out var existing))
                {
                    if (existing != record)
                        throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                    continue;
                }
                if (records.Count >= limits.MaximumRecords)
                    throw new PullExportException(PullExportErrorCodes.SafetyLimit);
                byId.Add(record.Id, record);
                records.Add(record);
            }
        }

        return new(auth.PlayerId, records);
    }

    private async ValueTask<WuwaPoolPage> FetchPoolAsync(
        WuwaPullHistoryUrl auth,
        int poolType,
        CancellationToken cancellationToken)
    {
        await pacer.BeforeRequestAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.EffectiveRequestTimeout);
        try
        {
            var body = BuildBody(auth, poolType);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content = content;
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
                    throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                if (response.Content.Headers.ContentLength is > 0 and var length
                    && length > limits.MaximumResponseBytes)
                    throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var bounded = new MemoryStream(Math.Min(limits.MaximumResponseBytes, 64 * 1024));
                var buffer = new byte[32 * 1024];
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        if (bounded.Length + read > limits.MaximumResponseBytes)
                            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                        await bounded.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
                finally { Array.Clear(buffer); }
                bounded.Position = 0;
                return ParsePage(bounded, poolType, auth.PlayerId);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new PullExportException(PullExportErrorCodes.UpstreamInvalid); }
        catch (PullExportException) { throw; }
        catch (Exception) { throw new PullExportException(PullExportErrorCodes.UpstreamInvalid); }
    }

    private static byte[] BuildBody(WuwaPullHistoryUrl auth, int poolType)
    {
        using var stream = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("playerId", auth.PlayerId);
            json.WriteNumber("cardPoolType", poolType);
            json.WriteString("cardPoolId", auth.ResourcesId);
            json.WriteString("languageCode", auth.LanguageCode);
            json.WriteString("recordId", auth.RecordId);
            json.WriteString("serverId", auth.ServerId);
            json.WriteEndObject();
            json.Flush();
        }
        return stream.ToArray();
    }

    private WuwaPoolPage ParsePage(Stream stream, int requestedPoolType, string expectedPlayerId)
    {
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code)
                || !TryGetInt(code, out var numericCode))
                throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
            if (numericCode != 0)
                throw new PullExportException(PullExportErrorCodes.UpstreamRejected);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
            if (data.GetArrayLength() > limits.MaximumRecords)
                throw new PullExportException(PullExportErrorCodes.SafetyLimit);

            var playerId = ReadOptionalIdentity(root, "playerId");
            var uid = ReadOptionalIdentity(root, "uid");
            if (!MatchesPlayer(playerId, expectedPlayerId) || !MatchesPlayer(uid, expectedPlayerId))
                throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
            var records = new List<WuwaPullRecord>(data.GetArrayLength());
            var syntheticOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                var itemPlayerId = ReadOptionalIdentity(item, "playerId");
                var itemUid = ReadOptionalIdentity(item, "uid");
                if (!MatchesPlayer(itemPlayerId, expectedPlayerId)
                    || !MatchesPlayer(itemUid, expectedPlayerId))
                    throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                var record = ParseRecord(item, requestedPoolType, syntheticOccurrences);
                records.Add(record);
            }
            return new(playerId, uid, records);
        }
        catch (JsonException) { throw new PullExportException(PullExportErrorCodes.UpstreamInvalid); }
    }

    private static WuwaPullRecord ParseRecord(
        JsonElement item,
        int requestedPoolType,
        Dictionary<string, int> syntheticOccurrences)
    {
        var id = ReadString(item, "id", allowMissing: true);
        var upstreamPoolLabel = ReadString(item, "cardPoolType", allowMissing: true);
        if (requestedPoolType is < 1 or > MaximumPoolType)
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        var resourceId = ReadString(item, "resourceId");
        var qualityLevel = ReadInt(item, "qualityLevel", allowMissing: true, fallback: 0);
        var name = ReadString(item, "name", allowMissing: true);
        var resourceType = ReadString(item, "resourceType", allowMissing: true);
        var time = ReadString(item, "time");
        var count = ReadInt(item, "count", allowMissing: true, fallback: 1);

        EnsureSafeText(id, 128, allowEmpty: true);
        EnsureSafeText(upstreamPoolLabel, 256, allowEmpty: true);
        EnsureSafeText(resourceId, 128);
        EnsureSafeText(name, 512, allowEmpty: true);
        EnsureSafeText(resourceType, 128, allowEmpty: true);
        EnsureSafeTime(time);
        if (qualityLevel is < 0 or > 10 || count is < 1 or > 999)
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        if (id.Length == 0)
        {
            var basis = BuildSyntheticId(requestedPoolType, time, resourceId, count);
            syntheticOccurrences.TryGetValue(basis, out var occurrence);
            syntheticOccurrences[basis] = ++occurrence;
            id = $"{basis}-{occurrence.ToString("D4", CultureInfo.InvariantCulture)}";
        }
        return new(id, requestedPoolType, resourceId, qualityLevel, name, resourceType, time, count);
    }

    private static string BuildSyntheticId(int poolType, string time, string resourceId, int count) =>
        $"{poolType}-{new string(time.Where(char.IsAsciiDigit).ToArray())}-{resourceId}-{count}";

    private static string ReadString(JsonElement item, string property, bool allowMissing = false)
    {
        if (!item.TryGetProperty(property, out var value))
        {
            if (allowMissing) return string.Empty;
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        }
        var result = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        return result;
    }

    private static int ReadInt(
        JsonElement item,
        string property,
        bool allowMissing = false,
        int fallback = 0)
    {
        if (!item.TryGetProperty(property, out var value))
        {
            if (allowMissing) return fallback;
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return number;
        throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
    }

    private static bool TryGetInt(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out number);
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static string? ReadOptionalIdentity(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        var result = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (string.IsNullOrWhiteSpace(result))
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
        EnsureSafeText(result!, 128);
        return result;
    }

    private static bool MatchesPlayer(string? value, string expected) =>
        value is null || value.Length == 0 || value.Equals(expected, StringComparison.Ordinal);

    private static void EnsureSafeText(string value, int maximum, bool allowEmpty = false)
    {
        if ((!allowEmpty && value.Length == 0) || value.Length > maximum
            || value.Any(static character => char.IsControl(character)))
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
    }

    private static void EnsureSafeTime(string value)
    {
        EnsureSafeText(value, 64);
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out _))
            throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and <= 399;

    private sealed record WuwaPoolPage(
        string? PlayerId,
        string? Uid,
        IReadOnlyList<WuwaPullRecord> Records);
}
