using Nyx.Desktop.Core.Sessions;
using Nyx_Desktop_App.ViewModels;

namespace Nyx.Desktop.Tests.UI;

public sealed class PrimaryGameStatusProjectorTests
{
    [Theory]
    [InlineData(GameRailSignalKind.Running, "Running", PrimaryGameStatusAction.None)]
    [InlineData(GameRailSignalKind.Starting, "Starting…", PrimaryGameStatusAction.None)]
    [InlineData(GameRailSignalKind.RetryAvailable, "Launch failed · try again", PrimaryGameStatusAction.RetryLaunch)]
    [InlineData(GameRailSignalKind.NeedsReview, "Needs attention · open Recovery", PrimaryGameStatusAction.OpenRecovery)]
    [InlineData(GameRailSignalKind.UpdateAvailable, "Update available · use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher)]
    [InlineData(GameRailSignalKind.UpdateAndPreDownload, "Update available · use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher)]
    [InlineData(GameRailSignalKind.PreDownloadAvailable, "Pre-download available · use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher)]
    [InlineData(GameRailSignalKind.Ready, "Ready to play", PrimaryGameStatusAction.None)]
    public void Projects_the_approved_primary_status(
        GameRailSignalKind signal,
        string expectedText,
        PrimaryGameStatusAction expectedAction)
    {
        var result = PrimaryGameStatusProjector.Project(signal, supportsFolderPicker: true);

        Assert.Equal(expectedText, result.Text);
        Assert.Equal(expectedAction, result.Action);
    }

    [Fact]
    public void Missing_built_in_game_routes_to_folder_picker()
    {
        var result = PrimaryGameStatusProjector.Project(
            GameRailSignalKind.NotFound,
            supportsFolderPicker: true);

        Assert.Equal("Game not found · choose folder", result.Text);
        Assert.Equal(PrimaryGameStatusAction.ChooseGameFolder, result.Action);
    }

    [Fact]
    public void Missing_custom_game_routes_to_recovery()
    {
        var result = PrimaryGameStatusProjector.Project(
            GameRailSignalKind.NotFound,
            supportsFolderPicker: false);

        Assert.Equal("Game not found · open Recovery", result.Text);
        Assert.Equal(PrimaryGameStatusAction.OpenRecovery, result.Action);
    }
}
