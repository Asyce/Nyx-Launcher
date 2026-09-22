using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Both available periods of Forgotten Hall, Pure Fiction and Apocalyptic Shadow.</summary>
public sealed record HoyoLabHsrEndgameSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabHsrEndgameSnapshot);
}

public static class HoyoLabHsrEndgameRules
{
    public const int MaximumFloors = 128;
    private static readonly string[] Kinds = ["forgotten-hall", "pure-fiction", "apocalyptic-shadow"];

    public static bool IsValid(HoyoLabHsrEndgameSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var data = snapshot.Data;
            if (!Fields(data, "challenges") || !Rows(data.GetProperty("challenges"), 6, Period)
                || data.GetProperty("challenges").GetArrayLength() != 6) return false;
            var periods = data.GetProperty("challenges");
            for (var index = 0; index < 6; index++)
                if (periods[index].GetProperty("kind").GetString() != Kinds[index / 2]
                    || Number(periods[index], "period") != index % 2 + 1) return false;
            for (var index = 0; index < 6; index += 2)
                if (!JsonElement.DeepEquals(periods[index].GetProperty("groups"), periods[index + 1].GetProperty("groups"))) return false;
            return Number(periods[0], "id") != Number(periods[1], "id");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static HoyoLabHsrEndgameSnapshot Normalize(HoyoLabHsrEndgameSnapshot snapshot) => new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabHsrEndgameSnapshot? left, HoyoLabHsrEndgameSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool Period(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var kindValue)
            || kindValue.ValueKind != JsonValueKind.String || kindValue.GetString() is not { } kind
            || !Kinds.Contains(kind, StringComparer.Ordinal)) return false;
        var fields = new List<string> { "kind", "period", "groups", "stars", "extraStars", "maxFloor", "maxFloorId", "battles", "hasData", "floors" };
        if (kind == "forgotten-hall") fields.AddRange(["id", "start", "end"]);
        if (!Fields(item, fields.ToArray()) || !Integer(item, "period", 1, 2)
            || !Integer(item, "stars") || !Integer(item, "extraStars") || !Integer(item, "battles")
            || !Integer(item, "maxFloorId") || !Text(item, "maxFloor", 0, 256) || !Boolean(item, "hasData")
            || !Rows(item.GetProperty("groups"), 2, Group, "id") || item.GetProperty("groups").GetArrayLength() != 2
            || !Rows(item.GetProperty("floors"), MaximumFloors, row => Floor(row, kind), "id")) return false;
        if (kind == "forgotten-hall" && (!Integer(item, "id", 1)
            || !Calendar(item.GetProperty("start")) || !Calendar(item.GetProperty("end")))) return false;
        if (kind == "forgotten-hall")
        {
            var group = item.GetProperty("groups")[Number(item, "period") - 1];
            if (Number(item, "id") != Number(group, "id")
                || !JsonElement.DeepEquals(item.GetProperty("start"), group.GetProperty("start"))
                || !JsonElement.DeepEquals(item.GetProperty("end"), group.GetProperty("end"))) return false;
        }
        var floors = item.GetProperty("floors");
        // Floor stars include the Starward bonus; the root's base-star total does not.
        return floors.EnumerateArray().Sum(row => (long)Number(row, "stars"))
                == (long)Number(item, "stars") + Number(item, "extraStars")
            && floors.EnumerateArray().Sum(row => (long)Number(row, "extraStars")) == Number(item, "extraStars");
    }

    private static bool Group(JsonElement item) =>
        Fields(item, "id", "start", "end", "status", "name", "upperBoss", "lowerBoss", "thirdBoss")
        && Integer(item, "id", 1) && Calendar(item.GetProperty("start")) && Calendar(item.GetProperty("end"))
        && Text(item, "status", 1, 64) && Text(item, "name", 0, 256)
        && Boss(item.GetProperty("upperBoss")) && Boss(item.GetProperty("lowerBoss")) && Boss(item.GetProperty("thirdBoss"));

    private static bool Boss(JsonElement item) => item.ValueKind == JsonValueKind.Null
        || Fields(item, "id", "name") && Integer(item, "id", 1) && Text(item, "name", 1, 256);

    private static bool Floor(JsonElement item, string kind)
    {
        var fields = new List<string> { "id", "name", "stars", "extraStars", "fast", "starward", "nodes" };
        if (kind == "apocalyptic-shadow") fields.Add("updatedAt");
        else fields.Add("rounds");
        if (kind == "forgotten-hall") fields.Add("chaos");
        if (!Fields(item, fields.ToArray()) || !Integer(item, "id", 1) || !Text(item, "name", 1, 256)
            || !Integer(item, "stars") || !Integer(item, "extraStars") || Number(item, "extraStars") > Number(item, "stars")
            || !Boolean(item, "fast") || !Boolean(item, "starward")) return false;
        if (kind == "apocalyptic-shadow" ? !Calendar(item.GetProperty("updatedAt")) : !Integer(item, "rounds")) return false;
        if (kind == "forgotten-hall" && !Boolean(item, "chaos")) return false;
        var nodes = item.GetProperty("nodes");
        return nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() == 3
            && Node(nodes[0], kind) && Node(nodes[1], kind)
            && (nodes[2].ValueKind == JsonValueKind.Null || Node(nodes[2], kind));
    }

    private static bool Node(JsonElement item, string kind)
    {
        var fields = new List<string> { "time", "avatars" };
        if (kind != "forgotten-hall") fields.AddRange(["score", "buff"]);
        if (kind == "apocalyptic-shadow") fields.Add("bossDefeated");
        if (!Fields(item, fields.ToArray()) || !Calendar(item.GetProperty("time"), nullable: kind == "apocalyptic-shadow")
            || !Rows(item.GetProperty("avatars"), 4, Avatar, "id")) return false;
        if (kind != "forgotten-hall" && (!Decimal(item.GetProperty("score")) || !Buff(item.GetProperty("buff"), kind))) return false;
        return kind != "apocalyptic-shadow" || Boolean(item, "bossDefeated");
    }

    private static bool Avatar(JsonElement item) =>
        Fields(item, "id", "level", "rarity", "rank", "element", "returnAssist")
        && Integer(item, "id", 1) && Integer(item, "level", 1, 100) && Integer(item, "rarity", 1, 5)
        && Integer(item, "rank", 0, 6) && Text(item, "element", 1, 64) && Boolean(item, "returnAssist");

    private static bool Buff(JsonElement item, string kind) => item.ValueKind == JsonValueKind.Null
        || Fields(item, kind == "pure-fiction" ? ["id", "name", "description", "summary"] : ["id", "name", "description"])
        && Integer(item, "id", 1) && Text(item, "name", 1, 256) && Text(item, "description", 0, 8192)
        && (kind != "pure-fiction" || Text(item, "summary", 0, 8192));

    private static bool Decimal(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.String) return false;
        var value = item.GetString();
        return value is { Length: > 0 and <= 40 } && value.All(char.IsAsciiDigit)
            && (value == "0" || value[0] is >= '1' and <= '9');
    }

    private static bool Calendar(JsonElement item, bool nullable = false)
    {
        if (nullable && item.ValueKind == JsonValueKind.Null) return true;
        return Fields(item, "year", "month", "day", "hour", "minute")
            && Integer(item, "year", 1, 9999) && Integer(item, "month", 1, 12) && Integer(item, "day", 1, 31)
            && Integer(item, "hour", 0, 23) && Integer(item, "minute", 0, 59)
            && Number(item, "day") <= DateTime.DaysInMonth(Number(item, "year"), Number(item, "month"));
    }

    private static int Number(JsonElement item, string name) => (int)item.GetProperty(name).GetDouble();

    private static bool Rows(JsonElement items, int maximum, Func<JsonElement, bool> validate, string? identity = null)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = new HashSet<int>();
        return items.EnumerateArray().All(item => validate(item) && (identity is null || seen.Add(Number(item, identity))));
    }
}
