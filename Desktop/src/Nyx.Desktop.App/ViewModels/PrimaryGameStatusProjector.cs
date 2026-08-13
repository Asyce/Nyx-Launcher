using Nyx.Desktop.Core.Sessions;

namespace Nyx_Desktop_App.ViewModels;

public enum PrimaryGameStatusAction
{
    None,
    OpenOfficialLauncher,
    ChooseGameFolder,
    OpenRecovery,
    RetryLaunch,
}

public sealed record PrimaryGameStatusProjection(
    string Text,
    PrimaryGameStatusAction Action);

public static class PrimaryGameStatusProjector
{
    public static PrimaryGameStatusProjection Project(
        GameRailSignalKind signal,
        bool supportsFolderPicker)
    {
        return signal switch
        {
            GameRailSignalKind.Running =>
                new("Running", PrimaryGameStatusAction.None),
            GameRailSignalKind.Starting =>
                new("Starting…", PrimaryGameStatusAction.None),
            GameRailSignalKind.RetryAvailable =>
                new("Launch failed · try again", PrimaryGameStatusAction.RetryLaunch),
            GameRailSignalKind.NeedsReview =>
                new("Needs attention · open Recovery", PrimaryGameStatusAction.OpenRecovery),
            GameRailSignalKind.NotFound when supportsFolderPicker =>
                new("Game not found · choose folder", PrimaryGameStatusAction.ChooseGameFolder),
            GameRailSignalKind.NotFound =>
                new("Game not found · open Recovery", PrimaryGameStatusAction.OpenRecovery),
            GameRailSignalKind.UpdateAndPreDownload or GameRailSignalKind.UpdateAvailable =>
                new("Update available · use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher),
            GameRailSignalKind.PreDownloadAvailable =>
                new("Pre-download available · use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher),
            GameRailSignalKind.Ready =>
                new("Ready to play", PrimaryGameStatusAction.None),
            GameRailSignalKind.Unsupported =>
                new("Use Official Launcher", PrimaryGameStatusAction.OpenOfficialLauncher),
            _ =>
                new("Checking game…", PrimaryGameStatusAction.None),
        };
    }
}
