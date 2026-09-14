using System.Text.Json;
using static Nyx.Desktop.Core.AccountStatus.HoyoLabSnapshotJson;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>World exploration only: no inventory, achievements, or endgame records.</summary>
public sealed record HoyoLabGenshinExplorationSnapshot(JsonElement Data)
{
    public override string ToString() => nameof(HoyoLabGenshinExplorationSnapshot);
}

public static class HoyoLabGenshinExplorationRules
{
    public const int MaximumWorlds = 512;
    internal static readonly string[] CountNames =
    [
        "anemoculi", "geoculi", "electroculi", "dendroculi", "hydroculi", "pyroculi", "lunoculi", "cryoculi",
        "commonChests", "exquisiteChests", "preciousChests", "luxuriousChests", "remarkableChests",
        "waypoints", "domains",
    ];

    public static bool IsValid(HoyoLabGenshinExplorationSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            var data = snapshot.Data;
            if (!Fields(data, "counts", "worlds", "displayGroups")
                || !Fields(data.GetProperty("counts"), CountNames)
                || !CountNames.All(name => Integer(data.GetProperty("counts"), name))
                || !Array(data.GetProperty("worlds"), MaximumWorlds, World, "id")
                || !Array(data.GetProperty("displayGroups"), MaximumWorlds, DisplayGroup, "id"))
                return false;

            var parents = data.GetProperty("worlds").EnumerateArray().ToDictionary(
                world => (int)world.GetProperty("id").GetDouble(),
                world => (int)world.GetProperty("parentId").GetDouble());
            foreach (var id in parents.Keys)
            {
                var seen = new HashSet<int>();
                var current = id;
                while (current != 0)
                {
                    if (!seen.Add(current) || !parents.TryGetValue(current, out current)) return false;
                }
            }
            foreach (var group in data.GetProperty("displayGroups").EnumerateArray())
            {
                if (!parents.ContainsKey((int)group.GetProperty("id").GetDouble())) return false;
                foreach (var area in group.GetProperty("areas").EnumerateArray())
                    foreach (var id in area.GetProperty("ids").EnumerateArray())
                        if (!parents.ContainsKey((int)id.GetDouble())) return false;
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static HoyoLabGenshinExplorationSnapshot Normalize(HoyoLabGenshinExplorationSnapshot snapshot) =>
        new(snapshot.Data.Clone());

    public static bool ValuesEqual(HoyoLabGenshinExplorationSnapshot? left, HoyoLabGenshinExplorationSnapshot? right) =>
        left is null ? right is null : right is not null && JsonElement.DeepEquals(left.Data, right.Data);

    private static bool World(JsonElement item) =>
        Fields(item, "id", "parentId", "name", "kind", "worldType", "level", "percentage", "statueLevel",
            "indexActive", "detailActive", "offerings", "areas", "bosses", "tribes")
        && Integer(item, "id", 1) && Integer(item, "parentId") && Text(item, "name", 1, 256)
        && Text(item, "kind", 1, 64) && Integer(item, "worldType") && Integer(item, "level")
        && Integer(item, "percentage") && Integer(item, "statueLevel")
        && Boolean(item, "indexActive") && Boolean(item, "detailActive")
        && Array(item.GetProperty("offerings"), 128, Offering)
        && Array(item.GetProperty("areas"), 512, Area)
        && Array(item.GetProperty("bosses"), 256, Boss)
        && (item.GetProperty("tribes").ValueKind == JsonValueKind.Null
            || Array(item.GetProperty("tribes"), 64, Tribe, "id"));

    private static bool Offering(JsonElement item) =>
        Fields(item, "name", "level", "state") && Text(item, "name", 1, 256)
        && Integer(item, "level") && Text(item, "state", 1, 64);

    private static bool Area(JsonElement item) =>
        Fields(item, "name", "percentage") && Text(item, "name", 1, 256) && Integer(item, "percentage");

    private static bool Boss(JsonElement item) =>
        Fields(item, "name", "kills") && Text(item, "name", 1, 256) && Integer(item, "kills");

    private static bool Tribe(JsonElement item) =>
        Fields(item, "id", "name", "level") && Integer(item, "id", 1)
        && Text(item, "name", 1, 256) && Integer(item, "level");

    // The source uses tenths of a percent, which may exceed 1000. Keep the raw
    // integer and the supplied group total; do not sum or average subregions.
    private static bool DisplayGroup(JsonElement item) =>
        Fields(item, "id", "areas") && Integer(item, "id", 1)
        && Array(item.GetProperty("areas"), MaximumWorlds, GroupArea);

    private static bool GroupArea(JsonElement item)
    {
        if (!Fields(item, "ids", "percentage") || !Integer(item, "percentage")) return false;
        var ids = item.GetProperty("ids");
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MaximumWorlds) return false;
        var seen = new HashSet<int>();
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetDouble(out var value)
                || value < 1 || value > int.MaxValue || value != Math.Truncate(value)
                || !seen.Add((int)value)) return false;
        }
        return true;
    }

    private static bool Array(JsonElement items, int maximum, Func<JsonElement, bool> validate, string? identity = null)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > maximum) return false;
        var seen = new HashSet<int>();
        foreach (var item in items.EnumerateArray())
            if (!validate(item) || identity is not null && !seen.Add((int)item.GetProperty(identity).GetDouble()))
                return false;
        return true;
    }
}
