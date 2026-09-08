using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Owned characters and their equipped builds, not a relic inventory.</summary>
public sealed record HoyoLabHsrBuildSnapshot(JsonElement Characters)
{
    public override string ToString() => nameof(HoyoLabHsrBuildSnapshot);
}

public static class HoyoLabHsrBuildRules
{
    public const int MaximumCharacters = 512;

    public static bool IsValid(HoyoLabHsrBuildSnapshot? snapshot)
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

    public static HoyoLabHsrBuildSnapshot Normalize(HoyoLabHsrBuildSnapshot snapshot) =>
        new(snapshot.Characters.Clone());

    public static bool ValuesEqual(HoyoLabHsrBuildSnapshot? left, HoyoLabHsrBuildSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Characters, right.Characters);

    private static bool Character(JsonElement item) =>
        Fields(item, "id", "name", "level", "rarity", "element", "path", "rank", "enhancedId", "avatarType",
            "lightCone", "eidolons", "relics", "ornaments", "properties", "traces", "specialTraces", "memosprite")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256)
        && Integer(item, "level", 1) && Integer(item, "rarity", 1)
        && Text(item, "element", 1, 32) && Integer(item, "path", 1)
        && Integer(item, "rank") && Integer(item, "enhancedId") && Text(item, "avatarType", 0, 64)
        && (item.GetProperty("lightCone").ValueKind == JsonValueKind.Null || LightCone(item.GetProperty("lightCone")))
        && Array(item.GetProperty("eidolons"), 64, Eidolon, "id", "position")
        && Array(item.GetProperty("relics"), 4, value => Relic(value, 1, 4), "slot")
        && Array(item.GetProperty("ornaments"), 2, value => Relic(value, 5, 6), "slot")
        && Array(item.GetProperty("properties"), 128, Property, "id")
        && Array(item.GetProperty("traces"), 128, Trace, "id")
        && Array(item.GetProperty("specialTraces"), 128, Trace, "id")
        && (item.GetProperty("memosprite").ValueKind == JsonValueKind.Null || Memosprite(item.GetProperty("memosprite")));

    private static bool LightCone(JsonElement item) =>
        Fields(item, "id", "name", "level", "rarity", "rank")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256)
        && Integer(item, "level", 1) && Integer(item, "rarity", 1) && Integer(item, "rank", 1);

    private static bool Eidolon(JsonElement item) =>
        Fields(item, "id", "name", "position", "active")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256)
        && Integer(item, "position", 1) && Boolean(item, "active");

    private static bool Relic(JsonElement item, int minimumSlot, int maximumSlot) =>
        Fields(item, "id", "name", "slot", "rarity", "level", "main", "sub")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256)
        && Integer(item, "slot", minimumSlot, maximumSlot)
        && Integer(item, "rarity", 1) && Integer(item, "level")
        && RelicProperty(item.GetProperty("main"))
        && Array(item.GetProperty("sub"), 4, RelicProperty, "id");

    private static bool RelicProperty(JsonElement item) =>
        Fields(item, "id", "name", "value", "times", "preview")
        && Integer(item, "id", 1) && Text(item, "name", 1, 128)
        && Text(item, "value", 1, 80) && Integer(item, "times") && Boolean(item, "preview");

    // These are official display strings, including any hidden-value notation.
    // Do not round, recompute totals, or coerce absent components to zero.
    private static bool Property(JsonElement item) =>
        Fields(item, "id", "name", "base", "added", "final")
        && Integer(item, "id", 1) && Text(item, "name", 1, 128)
        && NullableText(item, "base", 80) && NullableText(item, "added", 80)
        && Text(item, "final", 1, 80);

    private static bool Trace(JsonElement item) =>
        Fields(item, "id", "type", "level", "active", "rankWorks", "parent", "anchor", "specialType", "stages")
        && Text(item, "id", 1, 64) && Integer(item, "type") && Integer(item, "level")
        && Boolean(item, "active") && Boolean(item, "rankWorks")
        && Text(item, "parent", 0, 64) && Text(item, "anchor", 0, 64) && Text(item, "specialType", 0, 64)
        && Array(item.GetProperty("stages"), 64, Stage);

    private static bool Stage(JsonElement item) =>
        Fields(item, "id", "name", "level", "active", "rankWorks", "specialType", "exclusiveName",
            "linkedAvatars", "linkedAvatar", "linkedSkillId", "elationPriority")
        && Text(item, "id", 1, 64) && Text(item, "name", 0, 256) && Integer(item, "level")
        && Boolean(item, "active") && Boolean(item, "rankWorks") && Text(item, "specialType", 0, 64)
        && (item.GetProperty("exclusiveName").ValueKind == JsonValueKind.Null || Text(item, "exclusiveName", 0, 256))
        && (item.GetProperty("linkedAvatars").ValueKind == JsonValueKind.Null || Array(item.GetProperty("linkedAvatars"), 32, LinkedAvatar, "id"))
        && (item.GetProperty("linkedAvatar").ValueKind == JsonValueKind.Null || LinkedAvatar(item.GetProperty("linkedAvatar")))
        && NullableText(item, "linkedSkillId", 64)
        && (item.GetProperty("elationPriority").ValueKind == JsonValueKind.Null || Text(item, "elationPriority", 0, 64));

    private static bool LinkedAvatar(JsonElement item) =>
        Fields(item, "id", "name") && Text(item, "id", 1, 64) && Text(item, "name", 1, 256);

    private static bool Memosprite(JsonElement item) =>
        Fields(item, "id", "name", "healthHidden", "properties", "traces")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Boolean(item, "healthHidden")
        && Array(item.GetProperty("properties"), 128, Property, "id")
        && Array(item.GetProperty("traces"), 128, Trace, "id");

    private static bool Integer(JsonElement item, string name, int minimum = 0, int maximum = int.MaxValue) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Number
        && item.GetProperty(name).TryGetDouble(out var value)
        && value >= minimum && value <= maximum && value == Math.Truncate(value);

    private static bool Boolean(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool NullableText(JsonElement item, string name, int maximum) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Null || Text(item, name, 1, maximum);

    private static bool Text(JsonElement item, string name, int minimum, int maximum) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String
        && item.GetProperty(name).GetString() is { } text && text.Length >= minimum && text.Length <= maximum
        && text == text.Trim()
        && text.EnumerateRunes().All(static character => Rune.GetUnicodeCategory(character) is not
            (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator));

    private static bool Fields(JsonElement item, params string[] expected)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool Array(JsonElement items, int maximum, Func<JsonElement, bool> validate, params string[] identities)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = identities.Select(static _ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        foreach (var item in items.EnumerateArray())
        {
            if (!validate(item)) return false;
            for (var index = 0; index < identities.Length; index++)
            {
                var identity = item.GetProperty(identities[index]);
                var key = identity.ValueKind == JsonValueKind.String
                    ? identity.GetString()!
                    : identity.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!seen[index].Add(key)) return false;
            }
        }
        return true;
    }
}
