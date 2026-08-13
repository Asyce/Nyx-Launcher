using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void Catalog_contains_only_the_five_canonical_ids()
    {
        Assert.Equal(["gi", "hsr", "zzz", "wuwa", "ae"], GameCatalog.All.Select(game => game.Id));
    }

    [Fact]
    public void Catalog_and_definitions_cannot_be_mutated_by_callers()
    {
        var list = Assert.IsAssignableFrom<IList<GameDefinition>>(GameCatalog.All);
        var definition = GameCatalog.GetRequired("gi");

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Empty(typeof(GameDefinition).GetConstructors());
        Assert.All(
            typeof(GameDefinition).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.Equal("gi", definition.Id);
    }

    [Theory]
    [InlineData("genshin")]
    [InlineData("genshin-impact")]
    [InlineData("GI")]
    [InlineData("ww")]
    [InlineData("endfield")]
    [InlineData("")]
    [InlineData(" gi")]
    [InlineData("gi ")]
    [InlineData(null)]
    public void Aliases_and_unsupported_ids_are_rejected(string? gameId)
    {
        Assert.False(GameCatalog.TryGet(gameId, out _));
        Assert.Throws<UnsupportedGameException>(() => GameCatalog.GetRequired(gameId));
    }
}
