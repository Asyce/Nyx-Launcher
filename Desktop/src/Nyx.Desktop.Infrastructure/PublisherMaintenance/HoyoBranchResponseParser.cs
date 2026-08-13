using System.Collections.ObjectModel;
using System.Text.Json;
using Nyx.Desktop.Core.PublisherMaintenance;

namespace Nyx.Desktop.Infrastructure.PublisherMaintenance;

internal sealed class HoyoBranchResponseParser
{
    internal const int MaximumResponseBytes = 256 * 1024;
    internal const int MaximumJsonDepth = 16;

    private static readonly IReadOnlyDictionary<string, GameIdentity> Identities =
        new Dictionary<string, GameIdentity>(StringComparer.Ordinal)
        {
            ["gopR6Cufr3"] = new("genshin", "hk4e_global"),
            ["4ziysqXOQ8"] = new("hsr", "hkrpg_global"),
            ["U5hbdsT9W7"] = new("zzz", "nap_global"),
        };

    public bool TryParse(ReadOnlyMemory<byte> body, out HoyoRemoteBranchBatch? batch)
    {
        batch = null;
        if (body.Length == 0 || body.Length > MaximumResponseBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !TryGetSingleRequired(document.RootElement, "retcode", out var retcode)
                || retcode.ValueKind is not JsonValueKind.Number
                || !retcode.TryGetInt32(out var retcodeValue)
                || retcodeValue != 0
                || !TryGetSingleRequired(document.RootElement, "data", out var data)
                || data.ValueKind is not JsonValueKind.Object
                || !TryGetSingleRequired(data, "game_branches", out var branches)
                || branches.ValueKind is not JsonValueKind.Array
                || branches.GetArrayLength() != Identities.Count)
            {
                return false;
            }

            var games = new Dictionary<string, HoyoRemoteGameBranch>(StringComparer.Ordinal);
            foreach (var entry in branches.EnumerateArray())
            {
                if (!TryParseEntry(entry, out var remote) || !games.TryAdd(remote!.GameId, remote))
                {
                    return false;
                }
            }

            if (games.Count != Identities.Count
                || Identities.Values.Any(identity => !games.ContainsKey(identity.GameId)))
            {
                return false;
            }

            batch = new(new ReadOnlyDictionary<string, HoyoRemoteGameBranch>(games));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseEntry(JsonElement entry, out HoyoRemoteGameBranch? remote)
    {
        remote = null;
        if (entry.ValueKind is not JsonValueKind.Object
            || !TryGetSingleRequired(entry, "game", out var game)
            || game.ValueKind is not JsonValueKind.Object
            || !TryGetExactString(game, "id", out var publisherId)
            || !Identities.TryGetValue(publisherId!, out var identity)
            || !TryGetExactString(game, "biz", out var biz)
            || !string.Equals(biz, identity.GameBiz, StringComparison.Ordinal)
            || !TryGetSingleRequired(entry, "main", out var main)
            || !TryParseRequiredBranch(main, "main", out var liveVersion)
            || !TryGetSingleRequired(entry, "pre_download", out var preDownload))
        {
            return false;
        }

        var preDownloadState = PublisherPreDownloadState.NotOffered;
        StrictVersion? preDownloadVersion = null;
        PublisherOptionalSignal preDiff = PublisherOptionalSignal.NotAdvertised;
        if (preDownload.ValueKind is not JsonValueKind.Null)
        {
            if (TryParseRequiredBranch(preDownload, "predownload", out var parsedPreVersion)
                && parsedPreVersion > liveVersion)
            {
                preDownloadState = PublisherPreDownloadState.Offered;
                preDownloadVersion = parsedPreVersion;
                preDiff = ParseDiffTags(preDownload);
            }
            else
            {
                preDownloadState = PublisherPreDownloadState.Unknown;
            }
        }

        var mainDiff = ParseDiffTags(main);
        var incremental = CombineOptionalDiffSignals(mainDiff, preDiff);
        var baseCapability = ParseOptionalBoolean(entry, "enable_base_pkg_predownload");
        remote = new(
            identity.GameId,
            liveVersion,
            preDownloadState,
            preDownloadVersion,
            incremental,
            baseCapability);
        return true;
    }

    private static bool TryParseRequiredBranch(
        JsonElement branchObject,
        string expectedBranch,
        out StrictVersion version)
    {
        version = default;
        return branchObject.ValueKind is JsonValueKind.Object
            && TryGetExactString(branchObject, "branch", out var branch)
            && string.Equals(branch, expectedBranch, StringComparison.Ordinal)
            && TryGetExactString(branchObject, "tag", out var tag)
            && StrictVersion.TryParse(tag, out version);
    }

    private static PublisherOptionalSignal ParseDiffTags(JsonElement branchObject)
    {
        var occurrences = branchObject
            .EnumerateObject()
            .Where(property => property.NameEquals("diff_tags"))
            .ToArray();
        if (occurrences.Length == 0)
        {
            return PublisherOptionalSignal.Unknown;
        }

        if (occurrences.Length != 1 || occurrences[0].Value.ValueKind is not JsonValueKind.Array)
        {
            return PublisherOptionalSignal.Unknown;
        }

        var any = false;
        foreach (var item in occurrences[0].Value.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String
                || !StrictVersion.TryParse(item.GetString(), out _))
            {
                return PublisherOptionalSignal.Unknown;
            }

            any = true;
        }

        return any
            ? PublisherOptionalSignal.Advertised
            : PublisherOptionalSignal.NotAdvertised;
    }

    private static PublisherOptionalSignal ParseOptionalBoolean(JsonElement entry, string propertyName)
    {
        var occurrences = entry
            .EnumerateObject()
            .Where(property => property.NameEquals(propertyName))
            .ToArray();
        if (occurrences.Length != 1)
        {
            return PublisherOptionalSignal.Unknown;
        }

        return occurrences[0].Value.ValueKind switch
        {
            JsonValueKind.True => PublisherOptionalSignal.Advertised,
            JsonValueKind.False => PublisherOptionalSignal.NotAdvertised,
            _ => PublisherOptionalSignal.Unknown,
        };
    }

    private static PublisherOptionalSignal CombineOptionalDiffSignals(
        PublisherOptionalSignal main,
        PublisherOptionalSignal preDownload)
    {
        if (main is PublisherOptionalSignal.Unknown || preDownload is PublisherOptionalSignal.Unknown)
        {
            return PublisherOptionalSignal.Unknown;
        }

        return main is PublisherOptionalSignal.Advertised
            || preDownload is PublisherOptionalSignal.Advertised
            ? PublisherOptionalSignal.Advertised
            : PublisherOptionalSignal.NotAdvertised;
    }

    private static bool TryGetExactString(
        JsonElement parent,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!TryGetSingleRequired(parent, propertyName, out var property)
            || property.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null && value.Length > 0 && value.All(character => character <= 0x7F);
    }

    private static bool TryGetSingleRequired(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    private sealed record GameIdentity(string GameId, string GameBiz);
}

internal sealed record HoyoRemoteBranchBatch(
    IReadOnlyDictionary<string, HoyoRemoteGameBranch> Games);

internal sealed record HoyoRemoteGameBranch(
    string GameId,
    StrictVersion LiveVersion,
    PublisherPreDownloadState PreDownload,
    StrictVersion? PreDownloadVersion,
    PublisherOptionalSignal IncrementalPathAdvertised,
    PublisherOptionalSignal BasePackagePreDownloadCapability);

internal readonly record struct StrictVersion(int Major, int Minor, int Patch)
    : IComparable<StrictVersion>
{
    internal const int MaximumTextLength = 32;
    internal const int MaximumSegmentLength = 10;

    public static bool TryParse(string? value, out StrictVersion version)
    {
        version = default;
        if (value is null || value.Length is < 5 or > MaximumTextLength)
        {
            return false;
        }

        var span = value.AsSpan();
        var index = 0;
        if (!TryParseSegment(span, ref index, out var major)
            || !ConsumeSeparator(span, ref index)
            || !TryParseSegment(span, ref index, out var minor)
            || !ConsumeSeparator(span, ref index)
            || !TryParseSegment(span, ref index, out var patch)
            || index != span.Length)
        {
            return false;
        }

        version = new(major, minor, patch);
        return true;
    }

    private static bool TryParseSegment(
        ReadOnlySpan<char> text,
        ref int index,
        out int number)
    {
        number = 0;
        var start = index;
        while (index < text.Length && text[index] != '.')
        {
            var character = text[index];
            var digit = character - '0';
            var segmentLength = index - start + 1;
            if ((uint)digit > 9
                || segmentLength > MaximumSegmentLength
                || (segmentLength > 1 && text[start] == '0')
                || number > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            number = (number * 10) + digit;
            index++;
        }

        return index > start;
    }

    private static bool ConsumeSeparator(ReadOnlySpan<char> text, ref int index)
    {
        if (index >= text.Length || text[index] != '.')
        {
            return false;
        }

        index++;
        return true;
    }

    public int CompareTo(StrictVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");

    public static bool operator >(StrictVersion left, StrictVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(StrictVersion left, StrictVersion right) => left.CompareTo(right) < 0;
}
