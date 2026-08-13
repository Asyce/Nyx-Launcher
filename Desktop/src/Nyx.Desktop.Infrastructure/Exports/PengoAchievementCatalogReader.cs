using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public sealed record PengoAchievementCatalogSnapshot(
    string GameId,
    string ExportVersion,
    IReadOnlySet<long> AchievementIds);

public sealed class PengoAchievementCatalogReader
{
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private readonly string hsrCatalogPath;

    public PengoAchievementCatalogReader(string hsrCatalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hsrCatalogPath);
        if (!Path.IsPathFullyQualified(hsrCatalogPath)
            || hsrCatalogPath.StartsWith("\\\\", StringComparison.Ordinal)
            || hsrCatalogPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || hsrCatalogPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException(
                "The achievement catalog path must be an absolute local path.",
                nameof(hsrCatalogPath));
        this.hsrCatalogPath = Path.GetFullPath(hsrCatalogPath);
    }

    public async ValueTask<PengoAchievementCatalogSnapshot> ReadCurrentHsrAsync(
        string expectedExportVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedExportVersion)
            || expectedExportVersion.Length > 80)
            throw new ExportProviderException("achievement-catalog-invalid");

        try
        {
            if (!File.Exists(hsrCatalogPath)
                || (File.GetAttributes(hsrCatalogPath) & FileAttributes.ReparsePoint) != 0)
                throw new ExportProviderException("achievement-catalog-invalid");

            await using var stream = new FileStream(
                hsrCatalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumCatalogBytes)
                throw new ExportProviderException("achievement-catalog-invalid");

            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                },
                cancellationToken);
            return Parse(document.RootElement, expectedExportVersion);
        }
        catch (ExportProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            throw new ExportProviderException("achievement-catalog-invalid");
        }
    }

    private static PengoAchievementCatalogSnapshot Parse(
        JsonElement root,
        string expectedExportVersion)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                root,
                "schemaVersion",
                "game",
                "catalogVersion",
                "releasedVersion",
                "generatedAt",
                "dataTimestamp",
                "source",
                "categoryCount",
                "achievementCount",
                "count",
                "categories",
                "achievements",
                "rewardCurrency")
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var schemaVersionValue)
            || schemaVersionValue != 1
            || !root.TryGetProperty("game", out var game)
            || game.ValueKind != JsonValueKind.String
            || game.GetString() != "hsr"
            || !root.TryGetProperty("catalogVersion", out var catalogVersion)
            || catalogVersion.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("releasedVersion", out var releasedVersion)
            || releasedVersion.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("generatedAt", out var generatedAt)
            || !IsTimestamp(generatedAt)
            || !root.TryGetProperty("dataTimestamp", out var dataTimestamp)
            || !IsTimestamp(dataTimestamp)
            || !root.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("categoryCount", out var categoryCount)
            || categoryCount.ValueKind != JsonValueKind.Number
            || !categoryCount.TryGetInt32(out var categoryCountValue)
            || categoryCountValue <= 0
            || !root.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array
            || categories.GetArrayLength() != categoryCountValue
            || !root.TryGetProperty("achievementCount", out var achievementCount)
            || achievementCount.ValueKind != JsonValueKind.Number
            || !achievementCount.TryGetInt32(out var achievementCountValue)
            || achievementCountValue <= 0
            || achievementCountValue > HoyoLabHsrAchievementResultParser.MaximumAchievementCount
            || !root.TryGetProperty("count", out var count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out var countValue)
            || countValue != achievementCountValue
            || !root.TryGetProperty("achievements", out var achievements)
            || achievements.ValueKind != JsonValueKind.Array
            || achievements.GetArrayLength() != achievementCountValue
            || !root.TryGetProperty("rewardCurrency", out var rewardCurrency)
            || rewardCurrency.ValueKind != JsonValueKind.Object)
            throw new ExportProviderException("achievement-catalog-invalid");

        var rawCatalogVersion = catalogVersion.GetString() ?? string.Empty;
        var rawReleasedVersion = releasedVersion.GetString() ?? string.Empty;
        if (!TryParseVersion(rawCatalogVersion, out var release)
            || rawReleasedVersion != rawCatalogVersion)
            throw new ExportProviderException("achievement-catalog-invalid");
        var exportVersion = $"hsr-{rawCatalogVersion}";
        if (!string.Equals(exportVersion, expectedExportVersion, StringComparison.Ordinal))
            throw new ExportProviderException("achievement-catalog-stale");

        var ids = new HashSet<long>();
        foreach (var achievement in achievements.EnumerateArray())
        {
            if (achievement.ValueKind != JsonValueKind.Object
                || !HasExactProperties(
                    achievement,
                    "id",
                    "categoryId",
                    "name",
                    "description",
                    "reward",
                    "rarity",
                    "version",
                    "sortOrder")
                || !achievement.TryGetProperty("id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String
                || !TryParseCanonicalId(idProperty.GetString(), out var id)
                || !ids.Add(id)
                || !achievement.TryGetProperty("categoryId", out var categoryId)
                || !IsNonEmptyString(categoryId, 80)
                || !achievement.TryGetProperty("name", out var name)
                || !IsNonEmptyString(name, 512)
                || !achievement.TryGetProperty("description", out var description)
                || description.ValueKind != JsonValueKind.String
                || description.GetString() is not { Length: <= 4096 }
                || !achievement.TryGetProperty("reward", out var reward)
                || reward.ValueKind != JsonValueKind.Number
                || !reward.TryGetInt32(out var rewardValue)
                || rewardValue < 0
                || !achievement.TryGetProperty("rarity", out var rarity)
                || !IsNonEmptyString(rarity, 32)
                || !achievement.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String
                || !TryParseVersion(version.GetString(), out var achievementVersion)
                || IsNewer(achievementVersion, release)
                || !achievement.TryGetProperty("sortOrder", out var sortOrder)
                || sortOrder.ValueKind != JsonValueKind.Number
                || !sortOrder.TryGetInt32(out _))
                throw new ExportProviderException("achievement-catalog-invalid");
        }

        return new("hsr", exportVersion, ids.ToFrozenSet());
    }

    private static bool TryParseCanonicalId(string? raw, out long id)
    {
        id = 0;
        return raw is { Length: >= 1 and <= 16 }
            && raw[0] is >= '1' and <= '9'
            && raw.All(char.IsAsciiDigit)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id <= HoyoLabHsrAchievementResultParser.MaximumAchievementId
            && id.ToString(CultureInfo.InvariantCulture) == raw;
    }

    private static bool TryParseVersion(string? raw, out (int Major, int Minor) version)
    {
        version = default;
        if (raw is not { Length: >= 3 and <= 12 }) return false;
        var parts = raw.Split('.', StringSplitOptions.None);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out version.Major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version.Minor)
            && version.Major >= 0
            && version.Minor >= 0
            && $"{version.Major}.{version.Minor}" == raw;
    }

    private static bool IsTimestamp(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);

    private static bool IsNewer(
        (int Major, int Minor) candidate,
        (int Major, int Minor) ceiling) =>
        candidate.Major > ceiling.Major
        || candidate.Major == ceiling.Major && candidate.Minor > ceiling.Minor;

    private static bool IsNonEmptyString(JsonElement value, int maximumLength) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: >= 1 } text
        && text.Length <= maximumLength;

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }
        return seen.SetEquals(expected);
    }
}
