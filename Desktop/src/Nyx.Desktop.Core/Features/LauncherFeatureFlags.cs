using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Core.Features;

/// <summary>
/// Independent opt-in lanes. A disabled lane is never allowed to disable launch
/// or another lane. Future providers default off until they are verified.
/// </summary>
public enum LauncherFeatureFlag
{
    RemoteBannerManifest,
    AutomaticArt,
    GiPulls,
    GiAchievements,
    HsrPulls,
    HsrAchievements,
    ZzzPulls,
    ZzzAchievements,
    WuWaPulls,
    WuWaAchievements,
    WuWaAccountStatus,
    HoyoLabAccountAccess,
    SkportAccountAccess,
    EndfieldPulls,
    EndfieldAchievements,
}

public sealed record LauncherFeatureFlags
{
    public bool RemoteBannerManifest { get; init; } = true;
    public bool AutomaticArt { get; init; } = true;
    public bool GiPulls { get; init; } = true;
    public bool GiAchievements { get; init; } = true;
    public bool HsrPulls { get; init; } = true;
    public bool HsrAchievements { get; init; } = true;
    public bool ZzzPulls { get; init; } = true;
    public bool ZzzAchievements { get; init; }
    public bool WuWaPulls { get; init; } = true;
    public bool WuWaAchievements { get; init; }
    public bool WuWaAccountStatus { get; init; }
    public bool HoyoLabAccountAccess { get; init; }
    public bool SkportAccountAccess { get; init; }
    public bool HoyoLabAccountCleanupPending { get; init; }
    public bool SkportAccountCleanupPending { get; init; }
    public bool EndfieldPulls { get; init; }
    public bool EndfieldAchievements { get; init; }

    [JsonIgnore]
    public bool AchievementHelperReady { get; init; } =
        PackagedAchievementHelperReadiness.IsCurrentProcessReady();

    public static LauncherFeatureFlags Defaults() => new();

    public bool IsEnabled(LauncherFeatureFlag flag) => flag switch
    {
        LauncherFeatureFlag.RemoteBannerManifest => RemoteBannerManifest,
        LauncherFeatureFlag.AutomaticArt => AutomaticArt,
        LauncherFeatureFlag.GiPulls => GiPulls,
        LauncherFeatureFlag.GiAchievements => GiAchievements,
        LauncherFeatureFlag.HsrPulls => HsrPulls,
        LauncherFeatureFlag.HsrAchievements => HsrAchievements,
        LauncherFeatureFlag.ZzzPulls => ZzzPulls,
        LauncherFeatureFlag.ZzzAchievements => ZzzAchievements,
        LauncherFeatureFlag.WuWaPulls => WuWaPulls,
        LauncherFeatureFlag.WuWaAchievements => WuWaAchievements,
        LauncherFeatureFlag.WuWaAccountStatus => WuWaAccountStatus,
        LauncherFeatureFlag.HoyoLabAccountAccess => HoyoLabAccountAccess,
        LauncherFeatureFlag.SkportAccountAccess => SkportAccountAccess,
        LauncherFeatureFlag.EndfieldPulls => EndfieldPulls,
        LauncherFeatureFlag.EndfieldAchievements => EndfieldAchievements,
        _ => false,
    };

    public IReadOnlyDictionary<string, bool> AsCapabilityMap()
    {
        var map = Enum.GetValues<LauncherFeatureFlag>()
            .ToDictionary(static flag => flag.ToString(), IsEnabled, StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, bool>(map);
    }
}
