using System.Globalization;
using System.Text;
using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Both available Shiyu v2 periods, limited to Fourth and Fifth Frontier.</summary>
public sealed record HoyoLabZzzEndgameSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabZzzEndgameSnapshot);
}

public static class HoyoLabZzzEndgameRules
{
    public static bool IsValid(HoyoLabZzzEndgameSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            if (!Fields(snapshot.Data, "shiyu")) return false;
            var periods = snapshot.Data.GetProperty("shiyu");
            return Rows(periods, 2, Period, "zoneId") && periods.GetArrayLength() == 2
                && Number(periods[0], "period") == 1 && Number(periods[1], "period") == 2;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static HoyoLabZzzEndgameSnapshot Normalize(HoyoLabZzzEndgameSnapshot snapshot) => new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabZzzEndgameSnapshot? left, HoyoLabZzzEndgameSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool Period(JsonElement item)
    {
        if (!Fields(item, "period", "zoneId", "start", "end", "startEpoch", "endEpoch", "passedFifth", "summary", "fourth", "fifth")
            || !Integer(item, "period", 1, 2) || !Integer(item, "zoneId", 1)
            || !Calendar(item.GetProperty("start")) || !Calendar(item.GetProperty("end"))
            || !Epoch(item, "startEpoch") || !Epoch(item, "endEpoch")
            || string.CompareOrdinal(item.GetProperty("startEpoch").GetString(), item.GetProperty("endEpoch").GetString()) >= 0
            || !Boolean(item, "passedFifth") || !Summary(item.GetProperty("summary"))) return false;
        var fourth = item.GetProperty("fourth");
        var fifth = item.GetProperty("fifth");
        // Empty/incomplete source variants have not yet been qualified. Fail closed rather than erase an older record.
        if (!Fields(fourth, "buff", "time", "rating", "teams") || !Buff(fourth.GetProperty("buff"))
            || !Calendar(fourth.GetProperty("time")) || !Text(fourth, "rating", 1, 64)
            || !Rows(fourth.GetProperty("teams"), 2, row => Team(row, false), "id")
            || fourth.GetProperty("teams").GetArrayLength() != 2
            || !Fields(fifth, "teams") || !Rows(fifth.GetProperty("teams"), 3, row => Team(row, true), "id")
            || fifth.GetProperty("teams").GetArrayLength() != 3) return false;
        var teams = fifth.GetProperty("teams");
        var summary = item.GetProperty("summary");
        return fourth.GetProperty("teams").EnumerateArray().Concat(teams.EnumerateArray()).Select(row => Number(row, "id")).Distinct().Count() == 5
            && teams.EnumerateArray().Sum(row => (long)Number(row, "score")) == Number(summary, "score")
            && teams.EnumerateArray().Sum(row => (long)Number(row, "maxScore")) == Number(summary, "maxScore");
    }

    private static bool Summary(JsonElement item) =>
        Fields(item, "layerCount", "score", "maxScore", "rankPercentHundredths", "rating")
        && Integer(item, "layerCount") && Integer(item, "score") && Integer(item, "maxScore")
        && Number(item, "score") <= Number(item, "maxScore")
        && Integer(item, "rankPercentHundredths", 0, 10000) && Text(item, "rating", 1, 64);

    private static bool Team(JsonElement item, bool fifth) =>
        Fields(item, fifth ? ["id", "time", "avatars", "buddy", "buff", "score", "maxScore", "rating"] : ["id", "time", "avatars", "buddy"])
        && Integer(item, "id", 1) && Calendar(item.GetProperty("time"))
        && Rows(item.GetProperty("avatars"), 3, Avatar, "id") && item.GetProperty("avatars").GetArrayLength() > 0
        && Buddy(item.GetProperty("buddy"))
        && (!fifth || Buff(item.GetProperty("buff")) && Integer(item, "score") && Integer(item, "maxScore")
            && Number(item, "score") <= Number(item, "maxScore") && Text(item, "rating", 1, 64));

    private static bool Avatar(JsonElement item) =>
        Fields(item, "id", "level", "rank", "rarity", "element", "subElement", "profession")
        && Integer(item, "id", 1) && Integer(item, "level", 1, 100) && Integer(item, "rank", 0, 6)
        && Text(item, "rarity", 1, 16) && Integer(item, "element") && Integer(item, "subElement") && Integer(item, "profession");

    private static bool Buddy(JsonElement item) => Fields(item, "id", "level", "rarity")
        && Integer(item, "id", 1) && Integer(item, "level", 1, 100) && Text(item, "rarity", 1, 16);

    private static bool Buff(JsonElement item) => Fields(item, "title", "text") && Text(item, "title", 0, 256)
        && item.GetProperty("text").ValueKind == JsonValueKind.String && item.GetProperty("text").GetString() is { } value
        && value.Length <= 16384 && value.EnumerateRunes().All(static character => Rune.GetUnicodeCategory(character) is not
            (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));

    private static bool Epoch(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String && item.GetProperty(name).GetString() is { Length: 10 } value
        && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private static bool Calendar(JsonElement item) =>
        Fields(item, "year", "month", "day", "hour", "minute", "second")
        && Integer(item, "year", 1, 9999) && Integer(item, "month", 1, 12) && Integer(item, "day", 1, 31)
        && Integer(item, "hour", 0, 23) && Integer(item, "minute", 0, 59) && Integer(item, "second", 0, 59)
        && Number(item, "day") <= DateTime.DaysInMonth(Number(item, "year"), Number(item, "month"));

    private static int Number(JsonElement item, string name) => (int)item.GetProperty(name).GetDouble();

    private static bool Rows(JsonElement items, int maximum, Func<JsonElement, bool> validate, string? identity = null)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = new HashSet<int>();
        return items.EnumerateArray().All(item => validate(item) && (identity is null || seen.Add(Number(item, identity))));
    }
}
