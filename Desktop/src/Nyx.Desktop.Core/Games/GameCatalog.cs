using System.Diagnostics.CodeAnalysis;

namespace Nyx.Desktop.Core.Games;

public static class GameCatalog
{
    private static readonly GameDefinition[] Definitions =
    [
        new("gi", "Genshin Impact", "HoYoPlay", "HoYoLAB", true, true, true, true, true, true),
        new("hsr", "Honkai: Star Rail", "HoYoPlay", "HoYoLAB", true, true, true, true, true, true),
        new("zzz", "Zenless Zone Zero", "HoYoPlay", "HoYoLAB", true, false, true, false, true, true),
        new("wuwa", "Wuthering Waves", "KURO GAMES", null, false, false, true, false, true, true),
        new("ae", "Arknights: Endfield", "GRYPHLINK", "SKPORT", true, false, false, false, true, true),
    ];

    private static readonly IReadOnlyDictionary<string, GameDefinition> ById =
        Definitions.ToDictionary(game => game.Id, StringComparer.Ordinal);

    public static IReadOnlyList<GameDefinition> All { get; } = Array.AsReadOnly(Definitions);

    public static bool TryGet(string? gameId, [NotNullWhen(true)] out GameDefinition? game)
    {
        if (gameId is null)
        {
            game = null;
            return false;
        }

        return ById.TryGetValue(gameId, out game);
    }

    public static GameDefinition GetRequired(string? gameId)
    {
        if (!TryGet(gameId, out var game))
        {
            throw new UnsupportedGameException(gameId);
        }

        return game;
    }
}
