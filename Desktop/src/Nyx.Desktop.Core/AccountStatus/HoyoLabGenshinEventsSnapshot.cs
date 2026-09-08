using System.Globalization;
using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Account event-calendar summaries, not banners or full endgame battle records.</summary>
public sealed record HoyoLabGenshinEventsSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabGenshinEventsSnapshot);
}

public static class HoyoLabGenshinEventsRules
{
    public const int MaximumEvents = 512;

    public static bool IsValid(HoyoLabGenshinEventsSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var data = snapshot.Data;
            if (!Fields(data, "activities", "fixed", "selected")
                || !Array(data.GetProperty("activities"), MaximumEvents, Activity)
                || !Array(data.GetProperty("fixed"), MaximumEvents, Activity)
                || !Array(data.GetProperty("selected"), MaximumEvents, Selection)) return false;

            // Fixed activities share ID zero. Their source type is part of identity.
            var identities = new HashSet<(string?, int)>();
            foreach (var item in data.GetProperty("activities").EnumerateArray()
                .Concat(data.GetProperty("fixed").EnumerateArray()))
                if (!identities.Add(Identity(item))) return false;
            var selected = new HashSet<(string?, int)>();
            return data.GetProperty("selected").EnumerateArray()
                .All(item => selected.Add(Identity(item)) && identities.Contains(Identity(item)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static HoyoLabGenshinEventsSnapshot Normalize(HoyoLabGenshinEventsSnapshot snapshot) =>
        new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabGenshinEventsSnapshot? left, HoyoLabGenshinEventsSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static (string?, int) Identity(JsonElement item) =>
        (item.GetProperty("kind").GetString(), (int)item.GetProperty("id").GetDouble());

    private static bool Selection(JsonElement item) =>
        Fields(item, "id", "kind") && Integer(item, "id") && Text(item, "kind", 1, 64);

    private static bool Activity(JsonElement item) =>
        Fields(item, "id", "kind", "name", "startTimestamp", "endTimestamp", "startTime", "endTime",
            "countdown", "status", "finished", "rewards", "exploration", "double", "abyss", "theater", "onslaught")
        && Integer(item, "id") && Text(item, "kind", 1, 64) && Text(item, "name", 1, 256)
        && Timestamp(item, "startTimestamp") && Timestamp(item, "endTimestamp")
        && CalendarTime(item.GetProperty("startTime")) && CalendarTime(item.GetProperty("endTime"))
        && Integer(item, "countdown") && Integer(item, "status") && Boolean(item, "finished")
        && Array(item.GetProperty("rewards"), 1024, Reward)
        && Optional(item.GetProperty("exploration"), Exploration)
        && Optional(item.GetProperty("double"), DoubleReward)
        && Optional(item.GetProperty("abyss"), Abyss)
        && Optional(item.GetProperty("theater"), Theater)
        && Optional(item.GetProperty("onslaught"), Onslaught);

    private static bool Reward(JsonElement item) =>
        Fields(item, "id", "name", "quantity", "rarity", "overview")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Integer(item, "quantity")
        && Text(item, "rarity", 1, 64) && Boolean(item, "overview");

    private static bool Exploration(JsonElement item) =>
        Fields(item, "percentage", "finished") && Number(item, "percentage") && Boolean(item, "finished");

    private static bool DoubleReward(JsonElement item) =>
        Fields(item, "total", "remaining") && Integer(item, "total") && Integer(item, "remaining");

    private static bool Abyss(JsonElement item) =>
        Fields(item, "unlocked", "maximumStars", "stars", "hasData") && Boolean(item, "unlocked")
        && Integer(item, "maximumStars") && Integer(item, "stars") && Boolean(item, "hasData");

    private static bool Theater(JsonElement item) =>
        Fields(item, "unlocked", "maximumRound", "hasData", "tarotFinished", "difficulty")
        && Boolean(item, "unlocked") && Integer(item, "maximumRound") && Boolean(item, "hasData")
        && Integer(item, "tarotFinished") && Integer(item, "difficulty");

    private static bool Onslaught(JsonElement item) =>
        Fields(item, "unlocked", "difficulty", "seconds", "sub") && Boolean(item, "unlocked")
        && Integer(item, "difficulty") && Integer(item, "seconds")
        && Fields(item.GetProperty("sub"), "seconds", "x", "y")
        && Number(item.GetProperty("sub"), "seconds") && Number(item.GetProperty("sub"), "x")
        && Number(item.GetProperty("sub"), "y");

    private static bool Number(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Number
        && item.GetProperty(name).TryGetDouble(out var value) && double.IsFinite(value)
        && value >= 0 && value <= int.MaxValue;

    private static bool Timestamp(JsonElement item, string name)
    {
        if (item.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        var text = item.GetProperty(name).GetString();
        return text is { Length: > 0 and <= 12 } && (text == "0" || text[0] is >= '1' and <= '9')
            && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value <= 253402300799L;
    }

    // Preserve both the source Unix timestamp and its explicit server-calendar fields.
    // An unscheduled event can supply "0" and null; do not turn it into January 1970.
    private static bool CalendarTime(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Null) return true;
        return Fields(item, "year", "month", "day", "hour", "minute", "second")
            && Integer(item, "year", 1, 9999) && Integer(item, "month", 1, 12)
            && Integer(item, "day", 1, 31) && Integer(item, "hour", 0, 23)
            && Integer(item, "minute", 0, 59) && Integer(item, "second", 0, 59)
            && item.GetProperty("day").GetDouble() <= DateTime.DaysInMonth(
                (int)item.GetProperty("year").GetDouble(), (int)item.GetProperty("month").GetDouble());
    }

    private static bool Optional(JsonElement item, Func<JsonElement, bool> validate) =>
        item.ValueKind == JsonValueKind.Null || validate(item);

    private static bool Array(JsonElement items, int maximum, Func<JsonElement, bool> validate) =>
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= maximum
        && items.EnumerateArray().All(validate);
}
