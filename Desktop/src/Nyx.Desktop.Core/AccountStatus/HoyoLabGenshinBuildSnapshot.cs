using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>
/// Nyx's game-specific projection of an owned roster and equipped gear.
/// This is not an artifact/weapon inventory or an authenticated response body.
/// </summary>
public sealed record HoyoLabGenshinBuildSnapshot(JsonElement Characters)
{
    public override string ToString() => nameof(HoyoLabGenshinBuildSnapshot);
}

public static class HoyoLabGenshinBuildRules
{
    public const int MaximumCharacters = 512;

    public static bool IsValid(HoyoLabGenshinBuildSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            return Array(snapshot.Characters, MaximumCharacters, Character, "id");
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static HoyoLabGenshinBuildSnapshot Normalize(HoyoLabGenshinBuildSnapshot snapshot) =>
        new(snapshot.Characters.Clone());

    public static bool ValuesEqual(HoyoLabGenshinBuildSnapshot? left, HoyoLabGenshinBuildSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Characters, right.Characters);

    private static bool Character(JsonElement item) =>
        Fields(item, "id", "level", "promotion", "friendship", "element", "weapon", "skills",
            "constellations", "artifacts", "properties")
        && Integer(item, "id", 1)
        && Integer(item, "level", 1)
        && NullableInteger(item, "promotion")
        && NullableInteger(item, "friendship")
        && item.GetProperty("element").ValueKind == JsonValueKind.String
        && item.GetProperty("element").GetString() is { Length: > 0 and <= 32 } element
        && element.All(static character => char.IsAsciiLetter(character))
        && Weapon(item.GetProperty("weapon"))
        && Array(item.GetProperty("skills"), 64, Skill, "id")
        && Array(item.GetProperty("constellations"), 64, Constellation, "position", "id")
        && Array(item.GetProperty("artifacts"), 5, Artifact, "slot")
        && Array(item.GetProperty("properties"), 256, Property);

    private static bool Weapon(JsonElement item) =>
        Fields(item, "id", "level", "promotion", "refinement", "main", "sub")
        && Integer(item, "id", 1)
        && Integer(item, "level", 1)
        && NullableInteger(item, "promotion")
        && Integer(item, "refinement", 1)
        && Stat(item.GetProperty("main"))
        && (item.GetProperty("sub").ValueKind == JsonValueKind.Null || Stat(item.GetProperty("sub")));

    private static bool Skill(JsonElement item) =>
        Fields(item, "id", "type", "level", "unlocked")
        && Integer(item, "id", 1) && Integer(item, "type") && Integer(item, "level")
        && Boolean(item, "unlocked");

    private static bool Constellation(JsonElement item) =>
        Fields(item, "id", "position", "active")
        && Integer(item, "id", 1) && Integer(item, "position", 1) && Boolean(item, "active");

    private static bool Artifact(JsonElement item) =>
        Fields(item, "id", "setId", "slot", "rarity", "level", "main", "sub")
        && Integer(item, "id", 1) && Integer(item, "setId", 1)
        && Integer(item, "slot", 1, 5) && Integer(item, "rarity", 1) && Integer(item, "level")
        && ArtifactStat(item.GetProperty("main"))
        && Array(item.GetProperty("sub"), 4, ArtifactStat, "id");

    private static bool ArtifactStat(JsonElement item) =>
        StatFields(item, "id", "value", "percent", "rolls")
        && Integer(item, "id", 1) && Number(item.GetProperty("value"))
        && Boolean(item, "percent") && NullableInteger(item, "rolls");

    private static bool Property(JsonElement item) =>
        StatFields(item, "group", "id", "base", "added", "final", "percent")
        && Integer(item, "group", 0, 3) && StatValues(item);

    private static bool Stat(JsonElement item) =>
        StatFields(item, "id", "base", "added", "final", "percent") && StatValues(item);

    // Optional official property-map labels are display text, never markup or keys.
    // Older numeric-only projections remain valid and render with ID fallbacks.
    private static bool StatFields(JsonElement item, params string[] expected) =>
        Fields(item, expected)
        || (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && name.GetString() is { Length: > 0 and <= 128 } text
            && text == text.Trim()
            && text.EnumerateRunes().All(static character => Rune.GetUnicodeCategory(character) is not
                (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
            && Fields(item, [.. expected, "name"]));

    private static bool StatValues(JsonElement item) =>
        Integer(item, "id", 1)
        && NullableNumber(item.GetProperty("base"))
        && NullableNumber(item.GetProperty("added"))
        && Number(item.GetProperty("final"))
        && Boolean(item, "percent");

    private static bool Integer(JsonElement item, string name, int minimum = 0, int maximum = int.MaxValue) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Number
        && item.GetProperty(name).TryGetDouble(out var value)
        && value >= minimum && value <= maximum && value == Math.Truncate(value);

    private static bool NullableInteger(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Null || Integer(item, name);

    private static bool Boolean(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool NullableNumber(JsonElement item) => item.ValueKind == JsonValueKind.Null || Number(item);

    private static bool Number(JsonElement item) =>
        item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value) && double.IsFinite(value);

    private static bool Fields(JsonElement item, params string[] expected)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool Array(
        JsonElement items,
        int maximum,
        Func<JsonElement, bool> validate,
        params string[] identities)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = identities.Select(static _ => new HashSet<int>()).ToArray();
        var propertyIdentities = new HashSet<(int Group, int Id)>();
        foreach (var item in items.EnumerateArray())
        {
            if (!validate(item)) return false;
            for (var index = 0; index < identities.Length; index++)
                if (!seen[index].Add((int)item.GetProperty(identities[index]).GetDouble())) return false;
            if (identities.Length == 0
                && !propertyIdentities.Add(((int)item.GetProperty("group").GetDouble(), (int)item.GetProperty("id").GetDouble())))
                return false;
        }
        return true;
    }
}
