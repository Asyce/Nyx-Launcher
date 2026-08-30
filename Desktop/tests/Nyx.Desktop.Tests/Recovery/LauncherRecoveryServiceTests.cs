using Nyx.Desktop.Core.Recovery;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Infrastructure.Cache;
using Nyx.Desktop.Infrastructure.Recovery;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Tests.Recovery;

public sealed class LauncherRecoveryServiceTests
{
    [Fact]
    public async Task Reset_appearance_and_restore_backup_are_local_and_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(root);
            store.Save(LauncherState.Defaults() with
            {
                Appearance = new Dictionary<string, GameAppearanceState>
                {
                    ["gi"] = new() { IconPath = @"C:\Art\gi.png" },
                },
            });
            store.Save(LauncherState.Defaults() with { SelectedGameId = "hsr" });
            File.WriteAllText(store.StatePath, "{bad");
            var service = new LauncherRecoveryService(store, new LauncherCacheService(root));

            var resetWhileBlocked = await service.ResetSelectedAppearanceAsync("gi");
            Assert.False(resetWhileBlocked.Succeeded);
            Assert.Equal("{bad", File.ReadAllText(store.StatePath));

            var restored = await service.RestoreLastKnownGoodSettingsAsync();
            Assert.True(restored.Succeeded);
            Assert.Equal("gi", store.Load().State!.SelectedGameId);

            var reset = await service.ResetSelectedAppearanceAsync("gi");
            Assert.True(reset.Succeeded);
            Assert.False(store.Load().State!.Appearance.ContainsKey("gi"));

            var invalid = await service.RepairCustomPathAsync("C:\\private\\game.exe");
            Assert.False(invalid.Succeeded);
            Assert.Equal("invalid", invalid.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reset_appearance_keeps_unrelated_edits_from_another_instance()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-recovery-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(root);
            store.Save(LauncherState.Defaults() with
            {
                Appearance = new Dictionary<string, GameAppearanceState>
                {
                    ["gi"] = new() { IconPath = @"C:\Art\gi.png" },
                },
            });
            var service = new LauncherRecoveryService(store, new LauncherCacheService(root));
            var otherInstance = new LauncherStateStore(root);
            otherInstance.Update(state => state with
            {
                SelectedGameId = "hsr",
                Appearance = state.Appearance
                    .Append(new KeyValuePair<string, GameAppearanceState>("hsr", new() { IconPath = @"C:\Art\hsr.png" }))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                Preferences = state.Preferences with
                {
                    DataDirectory = @"D:\ConcurrentNyxData",
                    FeatureFlags = state.Preferences.FeatureFlags with { HsrAchievements = false },
                },
            });

            var result = await service.ResetSelectedAppearanceAsync("gi");

            Assert.True(result.Succeeded);
            var saved = store.Load().State!;
            Assert.False(saved.Appearance.ContainsKey("gi"));
            Assert.Equal(@"C:\Art\hsr.png", saved.Appearance["hsr"].IconPath);
            Assert.Equal("hsr", saved.SelectedGameId);
            Assert.Equal(@"D:\ConcurrentNyxData", saved.Preferences.DataDirectory);
            Assert.False(saved.Preferences.FeatureFlags.HsrAchievements);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_last_known_good_keeps_newest_playtime_and_cleanup_safety()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-recovery-playtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(root);
            var defaults = LauncherState.Defaults();
            var protectedFlags = LauncherFeatureFlags.Defaults() with
            {
                HoyoLabAccountAccess = true,
            };
            store.Save(defaults with
            {
                SelectedGameId = "gi",
                PlaytimeSecondsByGame = new Dictionary<string, long> { ["gi"] = 10 },
                Preferences = defaults.Preferences with { FeatureFlags = protectedFlags },
            });
            store.Save(defaults with
            {
                SelectedGameId = "hsr",
                PlaytimeSecondsByGame = new Dictionary<string, long> { ["gi"] = 20 },
                Preferences = defaults.Preferences with { FeatureFlags = protectedFlags },
            });

            var currentPrimary = defaults with
            {
                SelectedGameId = "ae",
                PlaytimeSecondsByGame = new Dictionary<string, long>
                {
                    ["gi"] = 20,
                    ["hsr"] = 3,
                },
                Preferences = defaults.Preferences with
                {
                    FeatureFlags = protectedFlags with
                    {
                        HoyoLabAccountCleanupPending = true,
                    },
                },
            };
            File.WriteAllText(store.StatePath, LauncherStateMigrations.Write(currentPrimary));

            var newestInMemory = new Dictionary<string, long>
            {
                ["gi"] = 99,
                ["hsr"] = 4,
                ["custom-removed"] = 12,
            };
            var service = new LauncherRecoveryService(
                store,
                new LauncherCacheService(root),
                currentPlaytimeTotals: () => newestInMemory);

            var result = await service.RestoreLastKnownGoodSettingsAsync();

            Assert.True(result.Succeeded);
            var restored = store.Load().State!;
            Assert.Equal("gi", restored.SelectedGameId);
            Assert.Equal(
                new Dictionary<string, long> { ["gi"] = 99, ["hsr"] = 4 },
                restored.PlaytimeSecondsByGame);
            Assert.True(restored.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(restored.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
