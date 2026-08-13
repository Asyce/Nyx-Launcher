namespace Nyx.Desktop.Core.PublisherGames;

public static class WuWaMaintenanceInteractionPolicy
{
    public static bool AllowsActivationRefresh(bool actionInFlight) =>
        !actionInFlight;

    public static bool AllowsOpenOfficial(
        bool maintenanceReady,
        bool actionInFlight,
        bool hasRequest) =>
        !actionInFlight && hasRequest;
}
