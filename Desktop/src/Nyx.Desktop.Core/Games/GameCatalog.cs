using System.Diagnostics.CodeAnalysis;

namespace Nyx.Desktop.Core.Games;

public static class GameCatalog
{
    private static readonly GameDefinition[] Definitions =
    [
        new("gi", "Genshin Impact"),
        new("hsr", "Honkai: Star Rail"),
        new("zzz", "Zenless Zone Zero"),
        new("wuwa", "Wuthering Waves"),
        new("ae", "Arknights: Endfield"),
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
