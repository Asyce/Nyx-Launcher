using System.Globalization;
using System.Text;
using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>The two Spiral Abyss periods exposed by HoYoLAB, including its explicit skip markers.</summary>
public sealed record HoyoLabGenshinEndgameSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabGenshinEndgameSnapshot);
}

public static class HoyoLabGenshinEndgameRules
{
    public const int MaximumFloors = 128;
    public const int MaximumChambers = 16;
    private static readonly string[] RankingNames = ["reveal", "defeat", "damage", "damageTaken", "normalSkills", "elementalBursts"];

    public static bool IsValid(HoyoLabGenshinEndgameSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var data = snapshot.Data;
            if (!Fields(data, "abyss") || !Rows(data.GetProperty("abyss"), 2, Period)
                || data.GetProperty("abyss").GetArrayLength() != 2) return false;
            var periods = data.GetProperty("abyss");
            return ((int)periods[0].GetProperty("period").GetDouble()) == 1
                && ((int)periods[1].GetProperty("period").GetDouble()) == 2
                && ((int)periods[0].GetProperty("id").GetDouble()) != ((int)periods[1].GetProperty("id").GetDouble());
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static HoyoLabGenshinEndgameSnapshot Normalize(HoyoLabGenshinEndgameSnapshot snapshot) => new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabGenshinEndgameSnapshot? left, HoyoLabGenshinEndgameSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool Period(JsonElement item)
    {
        if (!Fields(item, "period", "id", "start", "end", "battles", "wins", "maxFloor", "stars", "unlocked",
                "justSkipped", "skippedFloor", "rankings", "floors")
            || !Integer(item, "period", 1, 2) || !Integer(item, "id", 1)
            || !Timestamp(item, "start") || !Timestamp(item, "end")
            || long.Parse(item.GetProperty("start").GetString()!, CultureInfo.InvariantCulture)
                >= long.Parse(item.GetProperty("end").GetString()!, CultureInfo.InvariantCulture)
            || !Integer(item, "battles") || !Integer(item, "wins")
            || !Text(item, "maxFloor", 0, 32) || !Text(item, "skippedFloor", 0, 32)
            || !Integer(item, "stars") || !Boolean(item, "unlocked") || !Boolean(item, "justSkipped")
            || !Fields(item.GetProperty("rankings"), RankingNames)
            || !RankingNames.All(name => Rows(item.GetProperty("rankings").GetProperty(name), 512, Ranking, "id"))
            || !Rows(item.GetProperty("floors"), MaximumFloors, Floor, "index")) return false;
        // Skipped floors contribute to the official total without synthetic battle rows.
        return item.GetProperty("floors").EnumerateArray().Sum(floor => (long)((int)floor.GetProperty("stars").GetDouble()))
            <= ((int)item.GetProperty("stars").GetDouble());
    }

    private static bool Ranking(JsonElement item) => Fields(item, "id", "rarity", "value")
        && Integer(item, "id", 1) && Integer(item, "rarity", 1, 5) && Integer(item, "value");

    private static bool Floor(JsonElement item)
    {
        if (!Fields(item, "index", "unlocked", "stars", "maxStars", "settledAt", "settledTime", "effects",
                "upperEffects", "lowerEffects", "chambers")
            || !Integer(item, "index", 1, MaximumFloors) || !Boolean(item, "unlocked")
            || !Stars(item) || !Timestamp(item, "settledAt") || !CalendarTime(item.GetProperty("settledTime"), nullable: true)
            || !Texts(item.GetProperty("effects")) || !Texts(item.GetProperty("upperEffects")) || !Texts(item.GetProperty("lowerEffects"))
            || !Rows(item.GetProperty("chambers"), MaximumChambers, Chamber, "index")) return false;
        var chambers = item.GetProperty("chambers");
        return chambers.EnumerateArray().Sum(chamber => (long)((int)chamber.GetProperty("stars").GetDouble())) == ((int)item.GetProperty("stars").GetDouble())
            && chambers.EnumerateArray().Sum(chamber => (long)((int)chamber.GetProperty("maxStars").GetDouble())) <= ((int)item.GetProperty("maxStars").GetDouble());
    }

    private static bool Chamber(JsonElement item) =>
        Fields(item, "index", "stars", "maxStars", "upperEnemies", "lowerEnemies", "battles")
        && Integer(item, "index", 1, MaximumChambers) && Stars(item)
        && Rows(item.GetProperty("upperEnemies"), 256, Enemy) && Rows(item.GetProperty("lowerEnemies"), 256, Enemy)
        && Rows(item.GetProperty("battles"), 2, Battle, "index");

    private static bool Enemy(JsonElement item) => Fields(item, "name", "level")
        && Text(item, "name", 1, 256) && Integer(item, "level", 1, 1000);

    private static bool Battle(JsonElement item) => Fields(item, "index", "timestamp", "settledTime", "avatars")
        && Integer(item, "index", 1, 2) && Timestamp(item, "timestamp") && CalendarTime(item.GetProperty("settledTime"))
        && Rows(item.GetProperty("avatars"), 4, Avatar, "id") && item.GetProperty("avatars").GetArrayLength() > 0;

    private static bool Avatar(JsonElement item) => Fields(item, "id", "level", "rarity")
        && Integer(item, "id", 1) && Integer(item, "level", 1, 100) && Integer(item, "rarity", 1, 5);

    private static bool Stars(JsonElement item) => Integer(item, "stars") && Integer(item, "maxStars")
        && ((int)item.GetProperty("stars").GetDouble()) <= ((int)item.GetProperty("maxStars").GetDouble());

    private static bool Texts(JsonElement items) => Rows(items, 64, item =>
        item.ValueKind == JsonValueKind.String && item.GetString() is { Length: <= 4096 } value
        && value.EnumerateRunes().All(character => character.Value is 9 or 10 or 13
            || Rune.GetUnicodeCategory(character) is not (UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)));

    private static bool Timestamp(JsonElement item, string name)
    {
        if (item.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        var value = item.GetProperty(name).GetString();
        return value is { Length: > 0 and <= 12 } && (value == "0" || value[0] is >= '1' and <= '9')
            && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number <= 253402300799L;
    }

    private static bool CalendarTime(JsonElement item, bool nullable = false)
    {
        if (nullable && item.ValueKind == JsonValueKind.Null) return true;
        if (!Fields(item, "year", "month", "day", "hour", "minute", "second")
            || !Integer(item, "year", 1, 9999) || !Integer(item, "month", 1, 12)
            || !Integer(item, "day", 1, 31) || !Integer(item, "hour", 0, 23)
            || !Integer(item, "minute", 0, 59) || !Integer(item, "second", 0, 59)) return false;
        return ((int)item.GetProperty("day").GetDouble()) <= DateTime.DaysInMonth(((int)item.GetProperty("year").GetDouble()), ((int)item.GetProperty("month").GetDouble()));
    }

    private static bool Rows(JsonElement items, int maximum, Func<JsonElement, bool> validate, string? identity = null)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = new HashSet<int>();
        return items.EnumerateArray().All(item => validate(item) && (identity is null || seen.Add((int)item.GetProperty(identity).GetDouble())));
    }
}
