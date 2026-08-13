using System.Security.Cryptography;
using System.Text.Json;
using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal sealed class WuWaPublicEvidenceParser
{
    internal const int MaximumConfigBytes = 4 * 1024;
    internal const int MaximumResourceBytes = 1024 * 1024;
    internal const int MaximumResourceEntries = 10_000;
    internal const long MaximumRuntimeBytes = 256L * 1024 * 1024;
    internal const string ExpectedRuntimeDestination =
        "Client/Binaries/Win64/Client-Win64-Shipping.exe";

    public EvidenceReadResult<WuWaDownloadConfig> ReadConfig(string path)
    {
        var bounded = ReadBounded(
            path,
            MaximumConfigBytes,
            PublisherGameInspectionReason.ConfigMissing,
            PublisherGameInspectionReason.ConfigTooLarge);
        if (bounded.Reason is not PublisherGameInspectionReason.None)
        {
            return new(bounded.Reason);
        }

        try
        {
            using var document = JsonDocument.Parse(
                bounded.Bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !TryGetExactUnique(root, "version", out var versionElement)
                || versionElement.ValueKind is not JsonValueKind.String
                || !StrictThreePartVersion.TryParse(versionElement.GetString(), out var version)
                || !TryGetExactUnique(root, "isPreDownload", out var preDownload)
                || preDownload.ValueKind is not JsonValueKind.False
                || !TryGetExactUnique(root, "appId", out var appId)
                || appId.ValueKind is not JsonValueKind.String
                || !string.Equals(appId.GetString(), "50004", StringComparison.Ordinal))
            {
                return new(PublisherGameInspectionReason.ConfigMalformed);
            }

            return new(
                PublisherGameInspectionReason.None,
                new(version.ToString(), IsPreDownload: false, "50004"),
                bounded.Fingerprint,
                bounded.Snapshot);
        }
        catch (JsonException)
        {
            return new(PublisherGameInspectionReason.ConfigMalformed);
        }
    }

    public EvidenceReadResult<WuWaResourceEvidence> ReadResource(string path)
    {
        var bounded = ReadBounded(
            path,
            MaximumResourceBytes,
            PublisherGameInspectionReason.ResourceEvidenceMissing,
            PublisherGameInspectionReason.ResourceEvidenceTooLarge);
        if (bounded.Reason is not PublisherGameInspectionReason.None)
        {
            return new(bounded.Reason);
        }

        try
        {
            using var document = JsonDocument.Parse(
                bounded.Bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !TryGetExactUnique(root, "resource", out var resources)
                || resources.ValueKind is not JsonValueKind.Array
                || resources.GetArrayLength() > MaximumResourceEntries)
            {
                return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
            }

            WuWaResourceEvidence? runtimeEvidence = null;
            var runtimeMatches = 0;
            foreach (var entry in resources.EnumerateArray())
            {
                if (entry.ValueKind is not JsonValueKind.Object
                    || !TryGetExactUnique(entry, "dest", out var destination)
                    || destination.ValueKind is not JsonValueKind.String
                    || !TryGetExactUnique(entry, "fromFolder", out var fromFolder))
                {
                    return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
                }

                if (!string.Equals(
                        destination.GetString(),
                        ExpectedRuntimeDestination,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                runtimeMatches++;
                if (runtimeMatches > 1
                    || !TryGetExactUnique(entry, "size", out var size)
                    || size.ValueKind is not JsonValueKind.Number
                    || !size.TryGetInt64(out var runtimeSize)
                    || runtimeSize <= 0
                    || runtimeSize > MaximumRuntimeBytes
                    || !TryGetExactUnique(entry, "md5", out var md5)
                    || md5.ValueKind is not JsonValueKind.String
                    || !TryParseLowerHexDigest(md5.GetString(), 16, out var runtimeMd5))
                {
                    return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
                }

                string? runtimeVersion = null;
                if (fromFolder.ValueKind is JsonValueKind.String
                    && !TryExtractSingleVersionSegment(fromFolder.GetString(), out runtimeVersion))
                {
                    return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
                }
                if (fromFolder.ValueKind is not JsonValueKind.String
                    and not JsonValueKind.Null)
                {
                    return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
                }

                runtimeEvidence = new(runtimeVersion, runtimeSize, runtimeMd5);
            }

            if (runtimeMatches != 1 || runtimeEvidence is null)
            {
                return new(PublisherGameInspectionReason.ResourceEvidenceMissing);
            }

            return new(
                PublisherGameInspectionReason.None,
                runtimeEvidence,
                bounded.Fingerprint,
                bounded.Snapshot);
        }
        catch (JsonException)
        {
            return new(PublisherGameInspectionReason.ResourceEvidenceMalformed);
        }
    }

    private static BoundedEvidence ReadBounded(
        string path,
        int maximumBytes,
        PublisherGameInspectionReason missingReason,
        PublisherGameInspectionReason tooLargeReason)
    {
        if (PublisherGamePathGuard.PathOrParentsHaveReparsePoint(path))
        {
            return new(PublisherGameInspectionReason.ReparsePointFound);
        }

        if (!File.Exists(path))
        {
            return new(missingReason);
        }

        var before = PublisherFileSnapshot.Capture(path);
        if (before.Length < 0 || before.Length > maximumBytes)
        {
            return new(tooLargeReason);
        }

        var bytes = new byte[checked((int)before.Length)];
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.SequentialScan))
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return new(tooLargeReason);
            }
        }

        var after = PublisherFileSnapshot.Capture(path);
        if (after != before)
        {
            return new(PublisherGameInspectionReason.TargetChangedDuringInspection);
        }

        return new(
            PublisherGameInspectionReason.None,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)),
            after);
    }

    private static bool TryGetExactUnique(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && !property.NameEquals(propertyName))
            {
                return false;
            }

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

    private static bool TryExtractSingleVersionSegment(string? path, out string? version)
    {
        version = null;
        if (string.IsNullOrEmpty(path) || path.Length > 2048)
        {
            return false;
        }

        var span = path.AsSpan();
        var start = 0;
        var matchCount = 0;
        while (start <= span.Length)
        {
            var remaining = span[start..];
            var separator = remaining.IndexOfAny('/', '\\');
            var segment = separator < 0 ? remaining : remaining[..separator];
            if (StrictThreePartVersion.TryParse(segment, out var parsed))
            {
                matchCount++;
                version = parsed.ToString();
            }

            if (separator < 0)
            {
                break;
            }

            start += separator + 1;
        }

        return matchCount == 1;
    }

    private static bool TryParseLowerHexDigest(
        string? value,
        int expectedBytes,
        out byte[] digest)
    {
        digest = [];
        if (value is null
            || value.Length != expectedBytes * 2
            || value.Any(character =>
                !char.IsAsciiDigit(character)
                && character is not (>= 'a' and <= 'f')))
        {
            return false;
        }

        digest = Convert.FromHexString(value);
        return digest.Length == expectedBytes;
    }

    private sealed record BoundedEvidence(
        PublisherGameInspectionReason Reason,
        ReadOnlyMemory<byte> Bytes = default,
        string? Fingerprint = null,
        PublisherFileSnapshot? Snapshot = null);
}

internal sealed record WuWaDownloadConfig(string Version, bool IsPreDownload, string AppId);

internal sealed record WuWaResourceEvidence(
    string? Version,
    long RuntimeSize,
    byte[] RuntimeMd5);

internal sealed record EvidenceReadResult<T>(
    PublisherGameInspectionReason Reason,
    T? Value = default,
    string? Fingerprint = null,
    PublisherFileSnapshot? Snapshot = null);

internal readonly record struct StrictThreePartVersion(int Major, int Minor, int Patch)
{
    public static bool TryParse(string? value, out StrictThreePartVersion version) =>
        TryParse(value.AsSpan(), out version);

    public static bool TryParse(ReadOnlySpan<char> value, out StrictThreePartVersion version)
    {
        version = default;
        if (value.Length is < 5 or > 32)
        {
            return false;
        }

        var index = 0;
        if (!TrySegment(value, ref index, out var major)
            || !ConsumeDot(value, ref index)
            || !TrySegment(value, ref index, out var minor)
            || !ConsumeDot(value, ref index)
            || !TrySegment(value, ref index, out var patch)
            || index != value.Length)
        {
            return false;
        }

        version = new(major, minor, patch);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private static bool TrySegment(ReadOnlySpan<char> value, ref int index, out int number)
    {
        number = 0;
        var start = index;
        while (index < value.Length && value[index] != '.')
        {
            var digit = value[index] - '0';
            var length = index - start + 1;
            if ((uint)digit > 9
                || length > 10
                || (length > 1 && value[start] == '0')
                || number > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            number = (number * 10) + digit;
            index++;
        }

        return index > start;
    }

    private static bool ConsumeDot(ReadOnlySpan<char> value, ref int index)
    {
        if (index >= value.Length || value[index] != '.')
        {
            return false;
        }

        index++;
        return true;
    }
}
