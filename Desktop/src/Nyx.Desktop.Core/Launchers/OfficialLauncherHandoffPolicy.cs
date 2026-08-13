using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Launchers;

public sealed class OfficialLauncherHandoffDecision
{
    internal OfficialLauncherHandoffDecision(
        GameDefinition game,
        bool canOpenOfficialLauncher,
        string guidance)
    {
        Game = game;
        CanOpenOfficialLauncher = canOpenOfficialLauncher;
        Guidance = guidance;
    }

    public GameDefinition Game { get; }

    public bool CanOpenOfficialLauncher { get; }

    public bool RequiresUserInteraction => true;

    public bool AllowsDirectUpdate => false;

    public string Guidance { get; }
}

public static class OfficialLauncherHandoffPolicy
{
    public static OfficialLauncherHandoffDecision Decide(
        string? gameId,
        bool officialLauncherIsRegistered)
    {
        var game = GameCatalog.GetRequired(gameId);

        return officialLauncherIsRegistered
            ? new(
                game,
                canOpenOfficialLauncher: true,
                "Open the registered official launcher. The user must review and finish the update there.")
            : new(
                game,
                canOpenOfficialLauncher: false,
                "No official launcher is registered. Ask the user to locate or install it; do not update game files directly.");
    }
}
