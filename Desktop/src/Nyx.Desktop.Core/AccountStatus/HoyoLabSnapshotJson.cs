using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

internal static class HoyoLabSnapshotJson
{
    internal static bool Integer(JsonElement item, string name, int minimum = 0, int maximum = int.MaxValue) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Number
        && item.GetProperty(name).TryGetDouble(out var value)
        && value >= minimum && value <= maximum && value == Math.Truncate(value);

    internal static bool Boolean(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False;

    internal static bool Text(JsonElement item, string name, int minimum, int maximum) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String
        && item.GetProperty(name).GetString() is { } text && text.Length >= minimum && text.Length <= maximum
        && text == text.Trim()
        && text.EnumerateRunes().All(static character => Rune.GetUnicodeCategory(character) is not
            (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator));

    internal static bool Fields(JsonElement item, params string[] expected)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }
}
