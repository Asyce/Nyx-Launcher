using System.Globalization;
using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>The official account calendar, not earned currency, banners or full battle records.</summary>
public sealed record HoyoLabHsrEventsSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabHsrEventsSnapshot);
}

public static class HoyoLabHsrEventsRules
{
    public const int MaximumEvents = 512;

    public static bool IsValid(HoyoLabHsrEventsSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var data = snapshot.Data;
            return Fields(data, "activities", "challenges", "now", "version")
                && Timestamp(data, "now") && Text(data, "version", 1, 64)
                && Collection(data.GetProperty("activities"), Activity)
                && Collection(data.GetProperty("challenges"), Challenge);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static HoyoLabHsrEventsSnapshot Normalize(HoyoLabHsrEventsSnapshot snapshot) =>
        new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabHsrEventsSnapshot? left, HoyoLabHsrEventsSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool Collection(JsonElement rows, Func<JsonElement, bool> validate)
    {
        if (!Array(rows, MaximumEvents, validate)) return false;
        var seen = new HashSet<(string?, int)>();
        return rows.EnumerateArray().All(item =>
            seen.Add((item.GetProperty("kind").GetString(), (int)item.GetProperty("id").GetDouble())));
    }

    private static bool Activity(JsonElement item) =>
        Fields(item, "id", "version", "name", "kind", "status", "total", "progress", "time",
            "rewards", "specialReward", "finished", "showText", "timeKind", "description",
            "dropType", "dropTypes", "refreshType", "count", "multiplier", "afterVersion")
        && Integer(item, "id", 1) && Text(item, "version", 1, 64)
        && Text(item, "name", 1, 256) && Text(item, "kind", 1, 64) && Text(item, "status", 1, 64)
        && Integer(item, "total") && Integer(item, "progress") && Time(item.GetProperty("time"))
        && Array(item.GetProperty("rewards"), 1024, Reward)
        && SpecialReward(item.GetProperty("specialReward"))
        && Boolean(item, "finished") && Text(item, "showText", 0, 1024)
        && Text(item, "timeKind", 1, 64) && Text(item, "description", 0, 4096)
        && Integer(item, "dropType")
        && Array(item.GetProperty("dropTypes"), 64, value =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && number >= 0 && number <= int.MaxValue && number == Math.Truncate(number))
        && Integer(item, "refreshType") && Integer(item, "count") && Integer(item, "multiplier")
        && Boolean(item, "afterVersion");

    private static bool Challenge(JsonElement item) =>
        Fields(item, "id", "name", "kind", "status", "total", "progress", "extraProgress",
            "time", "rewards", "specialReward", "showText", "rankKind", "startVersion")
        && Integer(item, "id", 1) && Text(item, "name", 1, 256)
        && Text(item, "kind", 1, 64) && Text(item, "status", 1, 64)
        && Integer(item, "total") && Integer(item, "progress") && Integer(item, "extraProgress")
        && Time(item.GetProperty("time")) && Array(item.GetProperty("rewards"), 1024, Reward)
        && SpecialReward(item.GetProperty("specialReward")) && Text(item, "showText", 0, 1024)
        && Text(item, "rankKind", 0, 64) && Text(item, "startVersion", 0, 64);

    private static bool Reward(JsonElement item) =>
        RewardFields(item) && Integer(item, "id", 1) && Text(item, "name", 1, 256);

    private static bool RewardFields(JsonElement item) =>
        Fields(item, "id", "name", "quantity", "rarity", "kind")
        && Integer(item, "quantity") && Text(item, "rarity", 1, 64)
        && (item.GetProperty("kind").ValueKind == JsonValueKind.Null || Text(item, "kind", 1, 64));

    // The official double-reward row supplies a zero-ID/empty-name placeholder.
    // Retain that shape distinctly; it is not a zero-valued inventory item.
    private static bool SpecialReward(JsonElement item) =>
        item.ValueKind == JsonValueKind.Null || Reward(item)
        || RewardFields(item) && Integer(item, "id", 0, 0) && Text(item, "name", 0, 0)
            && Integer(item, "quantity", 0, 0);

    private static bool Time(JsonElement item) =>
        Fields(item, "startTimestamp", "endTimestamp", "startTime", "endTime", "now")
        && Timestamp(item, "startTimestamp") && Timestamp(item, "endTimestamp") && Timestamp(item, "now")
        && ServerTime(item, "startTime") && ServerTime(item, "endTime");

    private static bool ServerTime(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String
        && (item.GetProperty(name).GetString() == ""
            || item.GetProperty(name).GetString() is { Length: 19 } value
                && DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _));

    private static bool Timestamp(JsonElement item, string name)
    {
        if (item.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        var value = item.GetProperty(name).GetString();
        return value is { Length: > 0 and <= 12 } && (value == "0" || value[0] is >= '1' and <= '9')
            && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number <= 253402300799L;
    }

    private static bool Array(JsonElement items, int maximum, Func<JsonElement, bool> validate) =>
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= maximum
        && items.EnumerateArray().All(validate);
}
