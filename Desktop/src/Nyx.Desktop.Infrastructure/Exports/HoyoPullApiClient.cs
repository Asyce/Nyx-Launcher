using System.Net;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

internal interface IPullRequestPacer
{
    ValueTask BeforeRequestAsync(CancellationToken cancellationToken);
}

internal sealed class PullRequestPacer : IPullRequestPacer
{
    internal static TimeSpan RequestSpacing { get; } = TimeSpan.FromMilliseconds(250);
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delay;
    private int requestCount;

    public PullRequestPacer()
        : this(static async (duration, cancellationToken) =>
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false))
    {
    }

    internal PullRequestPacer(Func<TimeSpan, CancellationToken, ValueTask> delay) =>
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));

    public async ValueTask BeforeRequestAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref requestCount) > 1)
            await delay(RequestSpacing, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class NoWaitPullRequestPacer : IPullRequestPacer
{
    public ValueTask BeforeRequestAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed record HoyoPullRecord(
    string GachaId,
    string GachaType,
    string ItemId,
    string Count,
    string Time,
    string Name,
    string Language,
    string ItemType,
    string RankType,
    string Id);

internal sealed record HoyoPullArchive(
    HoyoPullGameConfiguration Game,
    string Uid,
    int Timezone,
    string Language,
    IReadOnlyList<HoyoPullRecord> Records);

internal sealed class HoyoPullApiClient(
    HttpClient httpClient,
    PullExportSafetyLimits limits,
    IPullRequestPacer pacer)
{
    public async ValueTask<HoyoPullArchive> DownloadNewestValidAsync(
        HoyoPullGameConfiguration game,
        IReadOnlyList<HoyoAuthQuery> candidates,
        CancellationToken cancellationToken)
    {
        HoyoAuthQuery? selected = null;
        HoyoPage? firstPage = null;
        var sawRejected = false;
        foreach (var candidate in candidates)
        {
            var result = await FetchPageAsync(game, candidate, game.GachaTypes[0], "0", cancellationToken).ConfigureAwait(false);
            if (result.Kind == PageResultKind.Success)
            {
                selected = candidate;
                firstPage = result.Page;
                break;
            }
            sawRejected |= result.Kind == PageResultKind.Rejected;
        }
        if (selected is null || firstPage is null)
            throw new PullExportException(sawRejected ? PullExportErrorCodes.UpstreamRejected : PullExportErrorCodes.UpstreamInvalid);

        var records = new List<HoyoPullRecord>(Math.Min(limits.MaximumRecords, 4_096));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string uid = string.Empty;
        var timezone = 8;
        var language = NormalizeLanguage(selected.Language);

        for (var typeIndex = 0; typeIndex < game.GachaTypes.Count; typeIndex++)
        {
            var gachaType = game.GachaTypes[typeIndex];
            var endId = "0";
            HoyoPage? page = typeIndex == 0 ? firstPage : null;
            for (var pageNumber = 0; pageNumber < limits.MaximumPagesPerType; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PageFetchResult result;
                if (page is null)
                {
                    result = await FetchPageAsync(game, selected, gachaType, endId, cancellationToken).ConfigureAwait(false);
                    if (result.Kind == PageResultKind.Rejected && game.GameId == "hsr" && gachaType is "21" or "22") break;
                    if (result.Kind != PageResultKind.Success || result.Page is null)
                        throw new PullExportException(result.Kind == PageResultKind.Rejected
                            ? PullExportErrorCodes.UpstreamRejected
                            : PullExportErrorCodes.UpstreamInvalid);
                    page = result.Page;
                }

                timezone = page.Timezone ?? timezone;
                if (page.Records.Count == 0) break;
                foreach (var record in page.Records)
                {
                    if (!IsRecordSafe(record))
                        throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                    if (!game.GachaTypes.Contains(record.GachaType, StringComparer.Ordinal)
                        || (game.GameId == "hsr" && record.GachaId.Length == 0))
                        throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                    if (uid.Length == 0) uid = record.Uid;
                    else if (!uid.Equals(record.Uid, StringComparison.Ordinal))
                        throw new PullExportException(PullExportErrorCodes.UpstreamInvalid);
                    if (!ids.Add(record.Id)) continue;
                    if (records.Count >= limits.MaximumRecords)
                        throw new PullExportException(PullExportErrorCodes.SafetyLimit);
                    records.Add(record);
                }

                var nextId = page.Records[^1].Id;
                if (nextId == endId || page.Records.Count < 20) break;
                endId = nextId;
                page = null;
                if (pageNumber == limits.MaximumPagesPerType - 1)
                    throw new PullExportException(PullExportErrorCodes.SafetyLimit);
            }
        }

        return new HoyoPullArchive(game, uid, timezone, language, records);
    }

    private async ValueTask<PageFetchResult> FetchPageAsync(
        HoyoPullGameConfiguration game,
        HoyoAuthQuery auth,
        string gachaType,
        string endId,
        CancellationToken cancellationToken)
    {
        await pacer.BeforeRequestAsync(cancellationToken).ConfigureAwait(false);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(limits.EffectiveRequestTimeout);
        try
        {
            var uri = auth.BuildRequestUri(
                game.Endpoint,
                gachaType,
                endId,
                20,
                game.RequiresRealGachaType);
            if (!IsOfficialEndpoint(uri, game.Endpoint)) return new(PageResultKind.Invalid, null);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode) return new(PageResultKind.Invalid, null);
            if (response.Content.Headers.ContentLength is > 0 and var length && length > limits.MaximumResponseBytes)
                return new(PageResultKind.Invalid, null);
            await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream(Math.Min(limits.MaximumResponseBytes, 64 * 1024));
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (bounded.Length + read > limits.MaximumResponseBytes) return new(PageResultKind.Invalid, null);
                await bounded.WriteAsync(buffer.AsMemory(0, read), requestTimeout.Token).ConfigureAwait(false);
            }
            Array.Clear(buffer);
            bounded.Position = 0;
            return ParsePage(bounded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(PageResultKind.Invalid, null); }
        catch (Exception) { return new(PageResultKind.Invalid, null); }
    }

    internal static bool IsOfficialEndpoint(Uri candidate, Uri expected) =>
        candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && candidate.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
        && candidate.IsDefaultPort
        && candidate.UserInfo.Length == 0
        && candidate.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal)
        && candidate.Fragment.Length == 0;

    private static PageFetchResult ParsePage(Stream stream)
    {
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("retcode", out var retcode) || !retcode.TryGetInt32(out var code))
                return new(PageResultKind.Invalid, null);
            if (code != 0) return new(PageResultKind.Rejected, null);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() > 20)
                return new(PageResultKind.Invalid, null);

            int? timezone = null;
            if (data.TryGetProperty("region_time_zone", out var timezoneElement))
            {
                if (timezoneElement.ValueKind == JsonValueKind.Number && timezoneElement.TryGetInt32(out var numeric)) timezone = numeric;
                else if (timezoneElement.ValueKind == JsonValueKind.String && int.TryParse(timezoneElement.GetString(), out numeric)) timezone = numeric;
                if (timezone is < -12 or > 14) return new(PageResultKind.Invalid, null);
            }

            var records = new List<RawHoyoPullRecord>(list.GetArrayLength());
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return new(PageResultKind.Invalid, null);
                records.Add(new(
                    GetString(item, "uid"), GetString(item, "gacha_id"), GetString(item, "gacha_type"),
                    GetString(item, "item_id"), GetString(item, "count", "1"), GetString(item, "time"),
                    GetString(item, "name"), GetString(item, "lang"), GetString(item, "item_type"),
                    GetString(item, "rank_type"), GetString(item, "id")));
            }
            return new(PageResultKind.Success, new HoyoPage(timezone, records));
        }
        catch (JsonException) { return new(PageResultKind.Invalid, null); }
    }

    private static string GetString(JsonElement item, string name, string fallback = "")
    {
        if (!item.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback,
        };
    }

    private static bool IsRecordSafe(RawHoyoPullRecord value) =>
        IsDigits(value.Id, 19) && IsDigits(value.ItemId, 32) && IsDigits(value.Uid, 20)
        && value.GachaType.Length is > 0 and <= 8
        && value.GachaId.Length <= 128
        && value.Time.Length == 19
        && DateTime.TryParseExact(value.Time, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)
        && value.Count.Length <= 8 && value.Name.Length <= 512 && value.Language.Length <= 32
        && value.ItemType.Length <= 128 && value.RankType.Length <= 8;

    private static bool IsDigits(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength && value.All(char.IsAsciiDigit);

    private static string NormalizeLanguage(string value) => value.ToLowerInvariant() switch
    {
        "de-de" or "en-us" or "es-es" or "fr-fr" or "id-id" or "it-it" or "ja-jp" or
        "ko-kr" or "pt-pt" or "ru-ru" or "th-th" or "tr-tr" or "vi-vn" or "zh-cn" or "zh-tw" => value.ToLowerInvariant(),
        _ => "en-us",
    };

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and <= 399;

    private enum PageResultKind { Success, Rejected, Invalid }
    private sealed record PageFetchResult(PageResultKind Kind, HoyoPage? Page);
    private sealed record HoyoPage(int? Timezone, IReadOnlyList<RawHoyoPullRecord> Records);
    private sealed record RawHoyoPullRecord(
        string Uid, string GachaId, string GachaType, string ItemId, string Count, string Time,
        string Name, string Language, string ItemType, string RankType, string Id)
    {
        public static implicit operator HoyoPullRecord(RawHoyoPullRecord value) => new(
            value.GachaId, value.GachaType, value.ItemId, value.Count, value.Time, value.Name,
            value.Language, value.ItemType, value.RankType, value.Id);
    }
}
