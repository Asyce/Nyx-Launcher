using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Playtime;

namespace Nyx.Desktop.Core.State;

/// <summary>Versioned, user-owned launcher state. The record contains no process or UI state.</summary>
public sealed record LauncherState
{
    public const int CurrentVersion = 6;

    public int Version { get; init; } = CurrentVersion;
    public string SelectedGameId { get; init; } = "gi";
    public IReadOnlyList<string> RailOrder { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CustomGameDefinition> CustomGames { get; init; } = Array.Empty<CustomGameDefinition>();
    public IReadOnlyDictionary<string, OfficialGameLaunchOptions> OfficialLaunchOptions { get; init; } =
        OfficialGameLaunchOptions.Defaults();
    public IReadOnlyDictionary<string, GameAppearanceState> Appearance { get; init; } =
        new ReadOnlyDictionary<string, GameAppearanceState>(new Dictionary<string, GameAppearanceState>(StringComparer.Ordinal));
    public ExportArmingState Export { get; init; } = new();
    public LauncherGlobalPreferences Preferences { get; init; } = new();
    public EndfieldPlaytimeState EndfieldPlaytime { get; init; } = new();

    public static LauncherState Defaults() => new()
    {
        RailOrder = GameCatalog.All.Select(static game => game.Id).ToArray(),
        Preferences = LauncherGlobalPreferences.FreshDefaults(),
    };
}

/// <summary>Persisted Endfield playtime data contains only normalized intervals and live state.</summary>
public sealed record EndfieldPlaytimeState
{
    public IReadOnlyList<EndfieldPlaytimeInterval> Intervals { get; init; } = Array.Empty<EndfieldPlaytimeInterval>();
    public EndfieldPlaytimePendingStart? PendingStart { get; init; }
}

public sealed record EndfieldPlaytimePendingStart
{
    public DateTimeOffset StartedAt { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
}

public sealed record OfficialGameLaunchOptions
{
    public string RawArguments { get; init; } = string.Empty;
    public bool Enabled { get; init; }

    public static IReadOnlyDictionary<string, OfficialGameLaunchOptions> Defaults() =>
        new ReadOnlyDictionary<string, OfficialGameLaunchOptions>(
            new Dictionary<string, OfficialGameLaunchOptions>(StringComparer.Ordinal)
            {
                ["gi"] = new(),
                ["hsr"] = new(),
                ["zzz"] = new(),
                ["wuwa"] = new(),
                ["ae"] = new(),
            });
}

public sealed record GameAppearanceState
{
    public string? IconPath { get; init; }
    public string? BackgroundPath { get; init; }
}

public sealed record ExportArmingState
{
    /// <summary>Legacy/global arm bit retained for v0 readers. New callers use Games.</summary>
    public bool IsArmed { get; init; }
    public IReadOnlyDictionary<string, ExportGameArming> Games { get; init; } =
        new ReadOnlyDictionary<string, ExportGameArming>(new Dictionary<string, ExportGameArming>(StringComparer.Ordinal));
    public string? OutputDirectory { get; init; }
    public IReadOnlyDictionary<string, string> OutputPaths { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed record ExportGameArming
{
    public bool PullsArmed { get; init; }
    public bool AchievementsArmed { get; init; }
    public string AchievementSource { get; init; } = AchievementExportSources.Game;
}

public static class AchievementExportSources
{
    public const string Game = "game";
    public const string HoyoLab = "hoyolab";

    public static string Normalize(string gameId, string? source) =>
        gameId == "hsr"
            ? string.Equals(source, Game, StringComparison.Ordinal) ? Game : HoyoLab
            : Game;
}

public sealed record LauncherGlobalPreferences
{
    public bool Hsr120FpsOnLaunch { get; init; }
    public bool Genshin120FpsOnLaunch { get; init; }
    public bool StayVisibleAfterLaunch { get; init; } = true;
    public bool RefreshContentOnStartup { get; init; } = true;
    public bool SafeNotifications { get; init; } = true;
    public bool PublisherPasswordSavingEnabled { get; init; } = true;
    public string? DataDirectory { get; init; }
    public string? EndfieldInstallRoot { get; init; }
    public IReadOnlyDictionary<string, string> ManualInstallRoots { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CopiedRedemptionCodes { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, string> RenderingModes { get; init; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));
    public IReadOnlyList<string> AutomaticDailyCheckInGames { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, LauncherPanelVisibility> PanelVisibility { get; init; } =
        new ReadOnlyDictionary<string, LauncherPanelVisibility>(
            new Dictionary<string, LauncherPanelVisibility>(StringComparer.Ordinal));
    public LauncherFeatureFlags FeatureFlags { get; init; } = LauncherFeatureFlags.Defaults();

    public LauncherPanelVisibility VisibilityFor(string gameId) =>
        PanelVisibility.TryGetValue(gameId, out var visibility) ? visibility : new();

    public static LauncherGlobalPreferences FreshDefaults() => new()
    {
        Hsr120FpsOnLaunch = true,
        Genshin120FpsOnLaunch = true,
        AutomaticDailyCheckInGames = Array.AsReadOnly(["ae", "gi", "hsr", "zzz"]),
    };
}

public sealed record LauncherPanelVisibility
{
    public bool ShowBanners { get; init; } = true;
    public bool ShowRedemptionCodes { get; init; } = true;
    public bool ShowAccountAndExport { get; init; } = true;
}

public enum LauncherStateReadStatus
{
    Loaded,
    Migrated,
    Recovered,
    DefaultsUsed,
    Malformed,
    FutureVersion,
}

public sealed record LauncherStateReadResult(
    LauncherStateReadStatus Status,
    LauncherState? State,
    string? Error = null)
{
    public bool IsUsable => State is not null;
}
