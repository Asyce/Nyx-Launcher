namespace Nyx.Desktop.Core.Games;

public sealed class GameDefinition
{
    internal GameDefinition(
        string id,
        string displayName,
        string railProvider,
        string? accountProvider,
        bool supportsDailyCheckIn,
        bool supports120Fps,
        bool supportsPulls,
        bool supportsAchievements,
        bool supportsScreenshots,
        bool supportsBackgrounds)
    {
        Id = id;
        DisplayName = displayName;
        RailProvider = railProvider;
        AccountProvider = accountProvider;
        SupportsDailyCheckIn = supportsDailyCheckIn;
        Supports120Fps = supports120Fps;
        SupportsPulls = supportsPulls;
        SupportsAchievements = supportsAchievements;
        SupportsScreenshots = supportsScreenshots;
        SupportsBackgrounds = supportsBackgrounds;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string RailProvider { get; }

    public string? AccountProvider { get; }

    public bool SupportsDailyCheckIn { get; }

    public bool Supports120Fps { get; }

    public bool SupportsPulls { get; }

    public bool SupportsAchievements { get; }

    public bool SupportsScreenshots { get; }

    public bool SupportsBackgrounds { get; }
}
