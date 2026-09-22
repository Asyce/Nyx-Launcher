using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Complete owned-Agent details and currently equipped gear, not full-bag inventory.</summary>
public sealed record HoyoLabZzzBuildSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabZzzBuildSnapshot);
}

public static partial class HoyoLabZzzBuildRules
{
    public const int MaximumAvatars = 512;

    public static bool IsValid(HoyoLabZzzBuildSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            return Fields(snapshot.Data, "avatars")
                && Rows(snapshot.Data.GetProperty("avatars"), MaximumAvatars, Avatar, "id");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static HoyoLabZzzBuildSnapshot Normalize(HoyoLabZzzBuildSnapshot snapshot) => new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabZzzBuildSnapshot? left, HoyoLabZzzBuildSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool Avatar(JsonElement item) =>
        Fields(item, "id", "name", "fullName", "englishName", "camp", "level", "rank", "rarity", "element", "subElement",
            "profession", "awakenState", "stats", "equipment", "weapon", "skills", "ranks", "plan", "skins", "awakening")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Text(item, "fullName", 0, 256)
        && Text(item, "englishName", 0, 256) && Text(item, "camp", 0, 256)
        && Integer(item, "level", 1, 100) && Integer(item, "rank", 0, 6) && Rarity(item)
        && Integer(item, "element") && Integer(item, "subElement") && Integer(item, "profession")
        && Text(item, "awakenState", 1, 64)
        && Rows(item.GetProperty("stats"), 128, Stat, "id")
        && Rows(item.GetProperty("equipment"), 6, Equipment, "position")
        && Weapon(item.GetProperty("weapon"))
        && Rows(item.GetProperty("skills"), 64, Skill, "type")
        && Rows(item.GetProperty("ranks"), 6, Rank, "position")
        && Plan(item.GetProperty("plan"))
        && Rows(item.GetProperty("skins"), 64, Skin, "id")
        && Awakening(item.GetProperty("awakening"));

    private static bool Stat(JsonElement item) =>
        Fields(item, "id", "name", "base", "added", "final") && Integer(item, "id", 1)
        && Text(item, "name", 1, 256) && StatValue(item, "base", true) && StatValue(item, "added", true)
        && StatValue(item, "final", false);

    private static bool StatValue(JsonElement item, string name, bool empty) =>
        Text(item, name, empty ? 0 : 1, 64)
        && (empty && item.GetProperty(name).GetString() == "" || StatPattern().IsMatch(item.GetProperty(name).GetString()!));

    [GeneratedRegex(@"^[+-]?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?%?$", RegexOptions.CultureInvariant)]
    private static partial Regex StatPattern();

    private static bool GearStat(JsonElement item) =>
        Fields(item, "id", "name", "base", "added", "level", "systemId", "valid")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && StatValue(item, "base", false)
        && Finite(item, "added", -1_000_000_000, 1_000_000_000)
        && Integer(item, "level", 0, 100) && Integer(item, "systemId") && Boolean(item, "valid");

    private static bool Equipment(JsonElement item) =>
        Fields(item, "id", "name", "rarity", "level", "position", "allHit", "invalidProperties", "mainStats", "stats", "set")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Rarity(item) && Integer(item, "level", 0, 100)
        && Integer(item, "position", 1, 6) && Boolean(item, "allHit") && Integer(item, "invalidProperties", 0, 1000)
        && Rows(item.GetProperty("mainStats"), 16, GearStat, "id") && Rows(item.GetProperty("stats"), 16, GearStat, "id")
        && EquipmentSet(item.GetProperty("set"));

    private static bool EquipmentSet(JsonElement item) =>
        Fields(item, "id", "name", "count", "twoPiece", "fourPiece")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Integer(item, "count", 0, 6)
        && SourceText(item, "twoPiece", 8192) && SourceText(item, "fourPiece", 8192);

    private static bool Weapon(JsonElement item) => item.ValueKind == JsonValueKind.Null
        || Fields(item, "id", "name", "rarity", "level", "profession", "stars", "mainStats", "stats", "effectName", "effect")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Rarity(item) && Integer(item, "level", 1, 100)
        && Integer(item, "profession") && Integer(item, "stars", 1, 5)
        && Rows(item.GetProperty("mainStats"), 16, GearStat, "id") && Rows(item.GetProperty("stats"), 16, GearStat, "id")
        && Text(item, "effectName", 0, 256) && SourceText(item, "effect", 8192);

    private static bool Skill(JsonElement item) =>
        Fields(item, "type", "level", "awakenState", "items") && Integer(item, "type", 0, 64)
        && Integer(item, "level", 0, 100) && Text(item, "awakenState", 1, 64)
        && Rows(item.GetProperty("items"), 64, row => Fields(row, "awakened", "title", "text")
            && Boolean(row, "awakened") && Text(row, "title", 0, 256) && SourceText(row, "text", 16384));

    private static bool Rank(JsonElement item) =>
        Fields(item, "id", "position", "name", "description", "unlocked") && Integer(item, "id", 1)
        && Integer(item, "position", 1, 6) && Text(item, "name", 1, 256)
        && SourceText(item, "description", 8192) && Boolean(item, "unlocked");

    private static bool Plan(JsonElement item) => item.ValueKind == JsonValueKind.Null
        || Fields(item, "type", "rating", "score", "validProperties", "onlySpecial", "defaults", "custom", "effective", "cultivation")
        && Integer(item, "type") && Text(item, "rating", 0, 64) && Finite(item, "score", 0, 10000)
        && Integer(item, "validProperties", 0, 10000) && Boolean(item, "onlySpecial")
        && Rows(item.GetProperty("defaults"), 128, PlanProperty, "id")
        && Rows(item.GetProperty("custom"), 128, PlanProperty, "id")
        && Rows(item.GetProperty("effective"), 128, PlanProperty, "id")
        && Cultivation(item.GetProperty("cultivation"));

    private static bool PlanProperty(JsonElement item) =>
        Fields(item, "id", "name", "fullName", "systemId", "selected")
        && Integer(item, "id", 1) && Text(item, "name", 0, 256) && Text(item, "fullName", 0, 256)
        && Integer(item, "systemId") && Boolean(item, "selected");

    private static bool Cultivation(JsonElement item) =>
        Fields(item, "id", "name", "deleted", "old") && Text(item, "id", 0, 256)
        && SourceText(item, "name", 256) && Boolean(item, "deleted") && Boolean(item, "old");

    private static bool Skin(JsonElement item) =>
        Fields(item, "id", "name", "rarity", "original", "unlocked") && Integer(item, "id")
        && Text(item, "name", 1, 256) && Rarity(item) && Boolean(item, "original") && Boolean(item, "unlocked");

    private static bool Awakening(JsonElement item) =>
        Fields(item, "available", "level", "maxLevel", "levels") && Boolean(item, "available")
        && Integer(item, "level", 0, 100) && Integer(item, "maxLevel", 0, 100)
        && Number(item, "level") <= Number(item, "maxLevel")
        && Rows(item.GetProperty("levels"), 64, AwakeningLevel, "level");

    private static bool AwakeningLevel(JsonElement item) =>
        Fields(item, "level", "name", "skills") && Integer(item, "level", 1, 100) && Text(item, "name", 0, 256)
        && Rows(item.GetProperty("skills"), 64, row => Fields(row, "type", "summary", "items")
            && Integer(row, "type", 0, 64) && SourceText(row, "summary", 8192)
            && Rows(row.GetProperty("items"), 64, part => Fields(part, "title", "text")
                && Text(part, "title", 0, 256) && SourceText(part, "text", 16384)), "type");

    private static bool Rarity(JsonElement item) => Text(item, "rarity", 1, 16);

    // Source effect text may contain publisher markup and trailing whitespace.
    // It is inert data; consumers must never render it as executable HTML.
    private static bool SourceText(JsonElement item, string name, int maximum) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String && item.GetProperty(name).GetString() is { } value
        && value.Length <= maximum && value.EnumerateRunes().All(static character => Rune.GetUnicodeCategory(character) is not
            (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));

    private static bool Finite(JsonElement item, string name, double minimum, double maximum) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Number && item.GetProperty(name).TryGetDouble(out var value)
        && double.IsFinite(value) && value >= minimum && value <= maximum;

    private static int Number(JsonElement item, string name) => (int)item.GetProperty(name).GetDouble();

    private static bool Rows(JsonElement items, int maximum, Func<JsonElement, bool> validate, string? identity = null)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = new HashSet<int>();
        return items.EnumerateArray().All(item => validate(item) && (identity is null || seen.Add(Number(item, identity))));
    }
}
