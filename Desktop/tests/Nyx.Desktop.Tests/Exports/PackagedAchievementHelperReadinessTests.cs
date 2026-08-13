using System.Security.Cryptography;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Tests.Exports;

public sealed class PackagedAchievementHelperReadinessTests
{
    [Theory]
    [InlineData(AchievementExportSources.HoyoLab, false, false, false)]
    [InlineData(AchievementExportSources.HoyoLab, false, true, false)]
    [InlineData(AchievementExportSources.HoyoLab, true, false, true)]
    [InlineData(AchievementExportSources.HoyoLab, true, true, true)]
    [InlineData(AchievementExportSources.Game, false, false, false)]
    [InlineData(AchievementExportSources.Game, false, true, true)]
    [InlineData(AchievementExportSources.Game, true, false, false)]
    [InlineData(AchievementExportSources.Game, true, true, true)]
    public void Star_rail_achievement_capability_uses_only_the_saved_source_requirement(
        string source,
        bool hoyoLabConsent,
        bool achievementHelperReady,
        bool expected)
    {
        var flags = LauncherFeatureFlags.Defaults() with
        {
            HsrAchievements = true,
            HoyoLabAccountAccess = hoyoLabConsent,
            AchievementHelperReady = achievementHelperReady,
        };

        var capability = ExportProviderCatalog.GetEnabled("hsr", flags, source);

        Assert.Equal(expected, capability.Supports(ExportKind.Achievements));
    }

    [Fact]
    public void Pending_hoyolab_cleanup_disables_hoyolab_achievement_actions()
    {
        var flags = LauncherFeatureFlags.Defaults() with
        {
            HsrAchievements = true,
            HoyoLabAccountAccess = true,
            HoyoLabAccountCleanupPending = true,
        };

        var capability = ExportProviderCatalog.GetEnabled(
            "hsr",
            flags,
            AchievementExportSources.HoyoLab);

        Assert.False(capability.Supports(ExportKind.Achievements));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Genshin_achievement_capability_remains_game_helper_only(
        bool achievementHelperReady,
        bool expected)
    {
        var flags = LauncherFeatureFlags.Defaults() with
        {
            GiAchievements = true,
            HoyoLabAccountAccess = !achievementHelperReady,
            AchievementHelperReady = achievementHelperReady,
        };

        Assert.Equal(
            expected,
            ExportProviderCatalog.GetEnabled(
                "gi",
                flags,
                AchievementExportSources.HoyoLab).Supports(ExportKind.Achievements));
    }

    [Theory]
    [InlineData("zzz")]
    [InlineData("wuwa")]
    public void Dormant_games_never_gain_achievement_capability(string gameId)
    {
        var flags = LauncherFeatureFlags.Defaults() with
        {
            ZzzAchievements = true,
            WuWaAchievements = true,
            HoyoLabAccountAccess = true,
            AchievementHelperReady = true,
        };

        Assert.False(ExportProviderCatalog.GetEnabled(
            gameId,
            flags,
            AchievementExportSources.Game).Supports(ExportKind.Achievements));
    }

    [Fact]
    public void Unsupported_endfield_export_slot_remains_empty_even_when_feature_flags_are_on()
    {
        const string gameId = "ae";
        var slot = ExportProviderCatalog.Get(gameId);
        var flags = LauncherFeatureFlags.Defaults() with
        {
            ZzzPulls = true,
            ZzzAchievements = true,
            WuWaPulls = true,
            WuWaAchievements = true,
            EndfieldPulls = true,
            EndfieldAchievements = true,
        };

        Assert.Equal(ExportKind.None, slot.SupportedKinds);
        Assert.Equal(
            ExportKind.None,
            ExportProviderCatalog.GetEnabled(
                gameId,
                flags,
                AchievementExportSources.Game).SupportedKinds);
    }

    [Fact]
    public void Wuwa_exposes_only_its_proven_pull_lane()
    {
        var slot = ExportProviderCatalog.Get("wuwa");
        var enabled = ExportProviderCatalog.GetEnabled(
            "wuwa",
            LauncherFeatureFlags.Defaults() with { WuWaAchievements = true },
            AchievementExportSources.Game);

        Assert.Equal(ExportKind.Pulls, slot.SupportedKinds);
        Assert.Equal(ExportKind.Pulls, enabled.SupportedKinds);
    }

    [Fact]
    public void Zzz_exposes_only_its_proven_pull_lane()
    {
        var slot = ExportProviderCatalog.Get("zzz");
        var enabled = ExportProviderCatalog.GetEnabled(
            "zzz",
            LauncherFeatureFlags.Defaults() with { ZzzAchievements = true },
            AchievementExportSources.HoyoLab);

        Assert.Equal(ExportKind.Pulls, slot.SupportedKinds);
        Assert.Equal(ExportKind.Pulls, enabled.SupportedKinds);
    }

    [Fact]
    public void Exact_packaged_helper_and_hash_enable_only_achievement_capability()
    {
        using var temp = new TemporaryDirectory();
        var tools = Path.Combine(temp.Path, "Assets", "Tools");
        Directory.CreateDirectory(tools);
        var helper = Path.Combine(tools, PackagedAchievementHelperReadiness.HelperFileName);
        File.WriteAllBytes(helper, "reviewed packaged helper"u8.ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper))).ToLowerInvariant();

        var ready = PackagedAchievementHelperReadiness.IsReady(temp.Path, hash);
        Assert.True(ready);
        AssertCapabilities(
            achievementHelperReady: ready,
            genshinAchievementsExpected: true,
            starRailAchievementsExpected: true);
    }

    [Fact]
    public void Missing_mismatched_or_unstamped_helper_is_visibly_unavailable_while_pulls_remain_ready()
    {
        using var temp = new TemporaryDirectory();
        var tools = Path.Combine(temp.Path, "Assets", "Tools");
        Directory.CreateDirectory(tools);
        var helper = Path.Combine(tools, PackagedAchievementHelperReadiness.HelperFileName);
        File.WriteAllBytes(helper, "unverified helper"u8.ToArray());

        var ready = PackagedAchievementHelperReadiness.IsReady(temp.Path, new string('0', 64));
        Assert.False(ready);
        Assert.False(PackagedAchievementHelperReadiness.IsReady(temp.Path, "NOT-A-HASH"));
        File.Delete(helper);
        Assert.False(PackagedAchievementHelperReadiness.IsReady(temp.Path, new string('0', 64)));
        AssertCapabilities(
            achievementHelperReady: ready,
            genshinAchievementsExpected: false,
            starRailAchievementsExpected: true);
    }

    private static void AssertCapabilities(
        bool achievementHelperReady,
        bool genshinAchievementsExpected,
        bool starRailAchievementsExpected)
    {
        var flags = LauncherFeatureFlags.Defaults() with
        {
            GiPulls = true,
            HsrPulls = true,
            GiAchievements = true,
            HsrAchievements = true,
            HoyoLabAccountAccess = true,
            AchievementHelperReady = achievementHelperReady,
        };

        var genshin = ExportProviderCatalog.GetEnabled(
            "gi",
            flags,
            AchievementExportSources.Game);
        var starRail = ExportProviderCatalog.GetEnabled(
            "hsr",
            flags,
            AchievementExportSources.HoyoLab);
        Assert.True(genshin.Supports(ExportKind.Pulls));
        Assert.True(starRail.Supports(ExportKind.Pulls));
        Assert.Equal(genshinAchievementsExpected, genshin.Supports(ExportKind.Achievements));
        Assert.Equal(starRailAchievementsExpected, starRail.Supports(ExportKind.Achievements));
        Assert.False(ExportProviderCatalog.GetEnabled(
            "hsr",
            flags with { HoyoLabAccountAccess = false },
            AchievementExportSources.HoyoLab).Supports(ExportKind.Achievements));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-helper-readiness-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
