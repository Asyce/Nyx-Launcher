using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void Catalog_contains_only_the_five_canonical_ids()
    {
        Assert.Equal(["gi", "hsr", "zzz", "wuwa", "ae"], GameCatalog.All.Select(game => game.Id));
    }

    [Theory]
    [InlineData("gi", "HoYoPlay", "HoYoLAB", true, true, true, true, true, true, true)]
    [InlineData("hsr", "HoYoPlay", "HoYoLAB", true, true, true, true, true, true, true)]
    [InlineData("zzz", "HoYoPlay", "HoYoLAB", true, true, false, true, false, true, true)]
    [InlineData("wuwa", "KURO GAMES", "KURO GAMES", false, true, false, true, false, true, true)]
    [InlineData("ae", "GRYPHLINK", "SKPORT", true, false, false, false, false, true, true)]
    public void Catalog_contains_the_approved_capability_matrix(
        string gameId,
        string railProvider,
        string? accountProvider,
        bool supportsDailyCheckIn,
        bool supportsNumericResource,
        bool supports120Fps,
        bool supportsPulls,
        bool supportsAchievements,
        bool supportsScreenshots,
        bool supportsBackgrounds)
    {
        var game = GameCatalog.GetRequired(gameId);

        Assert.Equal(railProvider, game.RailProvider);
        Assert.Equal(accountProvider, game.AccountProvider);
        Assert.Equal(supportsDailyCheckIn, game.SupportsDailyCheckIn);
        Assert.Equal(supportsNumericResource, game.SupportsNumericResource);
        Assert.Equal(supports120Fps, game.Supports120Fps);
        Assert.Equal(supportsPulls, game.SupportsPulls);
        Assert.Equal(supportsAchievements, game.SupportsAchievements);
        Assert.Equal(supportsScreenshots, game.SupportsScreenshots);
        Assert.Equal(supportsBackgrounds, game.SupportsBackgrounds);
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
    [InlineData("custom-1")]
    [InlineData("")]
    [InlineData(" gi")]
    [InlineData("gi ")]
    [InlineData(null)]
    public void Aliases_and_unsupported_ids_are_rejected(string? gameId)
    {
        Assert.False(GameCatalog.TryGet(gameId, out var game));
        Assert.Null(game);
        Assert.Throws<UnsupportedGameException>(() => GameCatalog.GetRequired(gameId));
    }
}
