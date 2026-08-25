using System.Diagnostics;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Tests.State;

public sealed class LauncherStateTests
{
    [Fact]
    public void Official_launch_options_round_trip_only_known_valid_entries()
    {
        var result = LauncherStateMigrations.Read(
            """{"version":4,"selectedGameId":"zzz","officialLaunchOptions":{"gi":{"rawArguments":"--name \"Traveler One\"","enabled":true},"hsr":{"rawArguments":"--saved while off","enabled":false},"zzz":{"rawArguments":"\"unterminated","enabled":true},"wuwa":7,"ae":{"rawArguments":"--future","enabled":true,"schemaVersion":2},"future":{"rawArguments":"--bad","enabled":true}}}""");

        Assert.Equal(LauncherStateReadStatus.Loaded, result.Status);
        Assert.Equal("zzz", result.State!.SelectedGameId);
        Assert.Equal(["gi", "hsr", "zzz", "wuwa", "ae"], result.State.OfficialLaunchOptions.Keys);
        Assert.Equal(new OfficialGameLaunchOptions { RawArguments = "--name \"Traveler One\"", Enabled = true }, result.State.OfficialLaunchOptions["gi"]);
        Assert.Equal(new OfficialGameLaunchOptions { RawArguments = "--saved while off", Enabled = false }, result.State.OfficialLaunchOptions["hsr"]);
        Assert.Equal(new OfficialGameLaunchOptions(), result.State.OfficialLaunchOptions["zzz"]);
        Assert.Equal(new OfficialGameLaunchOptions(), result.State.OfficialLaunchOptions["wuwa"]);
        Assert.Equal(new OfficialGameLaunchOptions(), result.State.OfficialLaunchOptions["ae"]);
        Assert.DoesNotContain("future", result.State.OfficialLaunchOptions.Keys);

        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(result.State));
        Assert.Equal(result.State.OfficialLaunchOptions, roundTrip.State!.OfficialLaunchOptions);
        Assert.Equal("--saved while off", roundTrip.State.OfficialLaunchOptions["hsr"].RawArguments);
    }

    [Fact]
    public void Malformed_or_ambiguous_official_launch_options_default_without_replacing_other_state()
    {
        var wrongShape = LauncherStateMigrations.Read(
            """{"version":4,"selectedGameId":"hsr","officialLaunchOptions":[{"enabled":true}]}""");
        var duplicate = LauncherStateMigrations.Read(
            """{"version":4,"selectedGameId":"ae","officialLaunchOptions":{"gi":{"rawArguments":"--one","enabled":true},"gi":{"rawArguments":"--two","enabled":true}}}""");

        Assert.Equal("hsr", wrongShape.State!.SelectedGameId);
        Assert.All(wrongShape.State.OfficialLaunchOptions.Values, option => Assert.Equal(new OfficialGameLaunchOptions(), option));
        Assert.Equal("ae", duplicate.State!.SelectedGameId);
        Assert.Equal(new OfficialGameLaunchOptions(), duplicate.State.OfficialLaunchOptions["gi"]);
    }

    [Fact]
    public void Official_argument_parser_preserves_quotes_and_rejects_malformed_or_unbounded_input()
    {
        Assert.True(CustomArgumentParser.TryParse("--name \"hello world\" \"\"", out var parsed));
        Assert.Equal(["--name", "hello world", ""], parsed);
        Assert.False(CustomArgumentParser.TryParse("\"unterminated", out _));
        Assert.False(CustomArgumentParser.TryParse("--line\nnext", out _));
        Assert.False(CustomArgumentParser.TryParse("--nul\0value", out _));
        Assert.False(CustomArgumentParser.TryParse(new string('x', CustomArgumentParser.MaximumRawLength + 1), out _));
        Assert.False(CustomArgumentParser.TryParse(new string(' ', CustomArgumentParser.MaximumRawLength + 1), out _));
        Assert.False(CustomArgumentParser.TryParse("\n", out _));
        Assert.False(CustomArgumentParser.TryParse(new string('x', CustomArgumentParser.MaximumArgumentLength + 1), out _));
        Assert.False(CustomArgumentParser.TryParse(
            string.Join(' ', Enumerable.Repeat("x", CustomArgumentParser.MaximumArgumentCount + 1)),
            out _));
        Assert.Throws<ArgumentException>(() => CustomArgumentParser.Parse("\"unterminated"));
    }

    [Fact]
    public void Automatic_daily_check_in_games_are_validated_and_round_trip()
    {
        var result = LauncherStateMigrations.Read(
            """{"version":3,"preferences":{"automaticDailyCheckInGames":["zzz","ae","gi","bad","gi"]}}""");

        Assert.Equal(["ae", "gi", "zzz"], result.State!.Preferences.AutomaticDailyCheckInGames);
        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(result.State));
        Assert.Equal(["ae", "gi", "zzz"], roundTrip.State!.Preferences.AutomaticDailyCheckInGames);
    }

    [Fact]
    public void Fresh_defaults_enable_supported_daily_check_ins_and_120_fps_without_changing_old_state()
    {
        var fresh = LauncherState.Defaults();
        var oldV4 = LauncherStateMigrations.Read("""{"version":4}""");

        Assert.True(fresh.Preferences.Genshin120FpsOnLaunch);
        Assert.True(fresh.Preferences.Hsr120FpsOnLaunch);
        Assert.Equal(["ae", "gi", "hsr", "zzz"], fresh.Preferences.AutomaticDailyCheckInGames);
        Assert.False(oldV4.State!.Preferences.Genshin120FpsOnLaunch);
        Assert.False(oldV4.State.Preferences.Hsr120FpsOnLaunch);
        Assert.Empty(oldV4.State.Preferences.AutomaticDailyCheckInGames);
    }

    [Fact]
    public void Per_game_panel_visibility_defaults_on_and_round_trips_only_official_games()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":4,"preferences":{"panelVisibility":{
          "gi":{"showBanners":false,"showRedemptionCodes":true,"showAccountAndExport":false},
          "future":{"showBanners":false}
        }}}
        """);

        Assert.False(result.State!.Preferences.VisibilityFor("gi").ShowBanners);
        Assert.True(result.State.Preferences.VisibilityFor("gi").ShowRedemptionCodes);
        Assert.False(result.State.Preferences.VisibilityFor("gi").ShowAccountAndExport);
        Assert.Equal(new LauncherPanelVisibility(), result.State.Preferences.VisibilityFor("hsr"));
        Assert.DoesNotContain("future", result.State.Preferences.PanelVisibility.Keys);

        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(result.State));
        Assert.Equal(result.State.Preferences.PanelVisibility, roundTrip.State!.Preferences.PanelVisibility);
    }

    [Fact]
    public void Settings_merge_combines_panel_visibility_with_a_concurrent_change()
    {
        var opened = LauncherState.Defaults();
        var latest = opened with
        {
            Preferences = opened.Preferences with
            {
                PanelVisibility = new Dictionary<string, LauncherPanelVisibility>
                {
                    ["gi"] = new() { ShowBanners = false },
                },
            },
        };
        var merged = LauncherSettingsStateMerge.Apply(
            latest,
            opened,
            SettingsEdit(opened, opened.RailOrder) with
            {
                OpenedPanelVisibility = new(),
                PanelVisibility = new() { ShowRedemptionCodes = false },
            });

        Assert.False(merged.Preferences.VisibilityFor("gi").ShowBanners);
        Assert.False(merged.Preferences.VisibilityFor("gi").ShowRedemptionCodes);
        Assert.True(merged.Preferences.VisibilityFor("gi").ShowAccountAndExport);
    }

    [Fact]
    public void Hsr_achievement_source_defaults_to_hoyolab_and_round_trips_game_choice()
    {
        var missingSource = LauncherStateMigrations.Read(
            """{"version":3,"export":{"games":{"hsr":{"achievementsArmed":true}}}}""");

        Assert.Equal(
            AchievementExportSources.HoyoLab,
            missingSource.State!.Export.Games["hsr"].AchievementSource);

        var selectedGame = missingSource.State with
        {
            Export = missingSource.State.Export with
            {
                Games = new Dictionary<string, ExportGameArming>
                {
                    ["hsr"] = new()
                    {
                        AchievementsArmed = true,
                        AchievementSource = AchievementExportSources.Game,
                    },
                },
            },
        };
        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(selectedGame));

        Assert.Equal(
            AchievementExportSources.Game,
            roundTrip.State!.Export.Games["hsr"].AchievementSource);
        Assert.True(roundTrip.State.Export.Games["hsr"].AchievementsArmed);
    }

    [Fact]
    public void Migration_quarantines_invalid_official_colliding_and_ambiguous_custom_ids()
    {
        var result = LauncherStateMigrations.Read("""
        {
          "version": 1,
          "selectedGameId": "custom-duplicate",
          "railOrder": ["evil", "gi", "custom-", "custom_bad", " custom-space ", "custom-duplicate", "custom-good"],
          "customGames": [
            {"id":"evil","name":"Evil","executablePath":"C:\\Games\\evil.exe","iconPath":"C:\\Games\\evil.png"},
            {"id":"gi","name":"Collision","executablePath":"C:\\Games\\gi.exe","iconPath":"C:\\Games\\gi.png"},
            {"id":"custom-","name":"Empty suffix","executablePath":"C:\\Games\\empty.exe","iconPath":"C:\\Games\\empty.png"},
            {"id":"custom_bad","name":"Bad syntax","executablePath":"C:\\Games\\bad.exe","iconPath":"C:\\Games\\bad.png"},
            {"id":" custom-space ","name":"Whitespace","executablePath":"C:\\Games\\space.exe","iconPath":"C:\\Games\\space.png"},
            {"id":"custom-duplicate","name":"First","executablePath":"C:\\Games\\first.exe","iconPath":"C:\\Games\\first.png","creationOrder":1},
            {"id":"custom-duplicate","name":"Second","executablePath":"C:\\Games\\second.exe","iconPath":"C:\\Games\\second.png","creationOrder":2},
            {"id":"custom-good","name":"Good","executablePath":"C:\\Games\\good.exe","iconPath":"C:\\Games\\good.png","creationOrder":3}
          ],
          "appearance": {
            "evil":{"artScale":150},
            "gi":{"artScale":120},
            "custom-duplicate":{"artScale":160},
            "custom-good":{"artScale":170}
          }
        }
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        var state = Assert.IsType<LauncherState>(result.State);
        var custom = Assert.Single(state.CustomGames);
        Assert.Equal("custom-good", custom.Id);
        Assert.DoesNotContain("evil", state.RailOrder);
        Assert.DoesNotContain("custom-duplicate", state.RailOrder);
        Assert.DoesNotContain("custom-duplicate", state.Appearance.Keys);
        Assert.Equal(new GameAppearanceState(), state.Appearance["gi"]);
        Assert.Equal(new GameAppearanceState(), state.Appearance["custom-good"]);
        Assert.Equal("gi", state.SelectedGameId);
    }

    [Fact]
    public void Migration_repairs_order_and_preserves_custom_creation_order_and_appearance()
    {
        var json = """
        {
          "version": 0,
          "selectedGameId": "custom-b",
          "railOrder": ["custom-b", "gi", "gi", "unknown"],
          "customGames": [
            {"id":"custom-b","name":"Beta","executablePath":"C:\\Games\\b.exe","iconPath":"C:\\Games\\b.png","creationOrder":2},
            {"id":"custom-a","name":"Alpha","executablePath":"C:\\Games\\a.exe","iconPath":"C:\\Games\\a.png","creationOrder":1}
          ],
          "appearance": {"gi":{"artScale":999,"artPinned":true},"unknown":{"artScale":5}},
          "export":{"isArmed":true,"outputPaths":{"gi":"C:\\out\\gi.json"}},
          "preferences":{"stayVisibleAfterLaunch":true}
        }
        """;

        var result = LauncherStateMigrations.Read(json);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        Assert.NotNull(result.State);
        Assert.Equal(["custom-b", "gi", "hsr", "zzz", "wuwa", "ae", "custom-a"], result.State!.RailOrder);
        Assert.Equal("custom-b", result.State.SelectedGameId);
        Assert.Equal(["custom-a", "custom-b"], result.State.CustomGames.Select(static game => game.Id));
        Assert.Equal(new GameAppearanceState(), result.State.Appearance["gi"]);
        Assert.True(result.State.Export.IsArmed);
        Assert.Null(result.State.Export.OutputDirectory);
        Assert.Empty(result.State.Export.OutputPaths);
    }

    [Fact]
    public void Malformed_and_future_state_fail_closed()
    {
        var malformed = LauncherStateMigrations.Read("{\"version\":1,\"appearance\":");
        var future = LauncherStateMigrations.Read("{\"version\":999}");

        Assert.Equal(LauncherStateReadStatus.Malformed, malformed.Status);
        Assert.Null(malformed.State);
        Assert.Equal(LauncherStateReadStatus.FutureVersion, future.Status);
        Assert.Null(future.State);
    }

    [Fact]
    public void Hsr_120_fps_preference_is_optional_in_v4_and_round_trips()
    {
        var oldV4 = LauncherStateMigrations.Read("""{"version":4,"selectedGameId":"hsr"}""");
        Assert.Equal(LauncherStateReadStatus.Loaded, oldV4.Status);
        Assert.False(oldV4.State!.Preferences.Hsr120FpsOnLaunch);
        Assert.False(oldV4.State.Preferences.Genshin120FpsOnLaunch);

        var enabled = oldV4.State with
        {
            Preferences = oldV4.State.Preferences with { Hsr120FpsOnLaunch = true },
        };
        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(enabled));

        Assert.Equal(LauncherStateReadStatus.Loaded, roundTrip.Status);
        Assert.True(roundTrip.State!.Preferences.Hsr120FpsOnLaunch);
    }

    [Fact]
    public void Genshin_120_fps_preference_is_optional_in_v4_and_round_trips()
    {
        var oldV4 = LauncherStateMigrations.Read("""{"version":4,"selectedGameId":"gi"}""");
        Assert.Equal(LauncherStateReadStatus.Loaded, oldV4.Status);
        Assert.False(oldV4.State!.Preferences.Genshin120FpsOnLaunch);

        var enabled = oldV4.State with
        {
            Preferences = oldV4.State.Preferences with { Genshin120FpsOnLaunch = true },
        };
        var written = LauncherStateMigrations.Write(enabled);
        var roundTrip = LauncherStateMigrations.Read(written);

        Assert.Equal(LauncherStateReadStatus.Loaded, roundTrip.Status);
        Assert.True(roundTrip.State!.Preferences.Genshin120FpsOnLaunch);
        Assert.Contains("\"genshin120FpsOnLaunch\": true", written, StringComparison.Ordinal);
        Assert.Equal(LauncherState.CurrentVersion, roundTrip.State.Version);
    }

    [Theory]
    [InlineData("\"unexpected\"")]
    [InlineData("{}")]
    [InlineData("120")]
    [InlineData("null")]
    public void Genshin_120_fps_malformed_optional_value_defaults_without_discarding_state(string value)
    {
        var json = "{\"version\":4,\"selectedGameId\":\"hsr\",\"preferences\":{\"hsr120FpsOnLaunch\":true,\"genshin120FpsOnLaunch\":"
            + value
            + "}}";
        var result = LauncherStateMigrations.Read(json);

        Assert.Equal(LauncherStateReadStatus.Loaded, result.Status);
        Assert.Equal(LauncherState.CurrentVersion, result.State!.Version);
        Assert.True(result.State.Preferences.Hsr120FpsOnLaunch);
        Assert.False(result.State.Preferences.Genshin120FpsOnLaunch);
    }

    [Fact]
    public void Genshin_120_fps_preference_survives_backup_recovery_after_restart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-genshin-120-fps-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    Genshin120FpsOnLaunch = true,
                },
            });
            store.Update(state => state with { SelectedGameId = "hsr" });
            File.WriteAllText(store.StatePath, "{bad");

            var restarted = new LauncherStateStore(directory).Load();

            Assert.Equal(LauncherStateReadStatus.Recovered, restarted.Status);
            Assert.True(restarted.State!.Preferences.Genshin120FpsOnLaunch);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_merge_preserves_a_concurrent_hsr_120_fps_change()
    {
        var opened = LauncherState.Defaults();
        var latest = opened with
        {
            Preferences = opened.Preferences with { Hsr120FpsOnLaunch = true },
        };
        var edit = new LauncherSettingsEdit
        {
            GameId = "gi",
            OpenedAppearance = new(),
            Appearance = new() { IconPath = @"C:\Edited\gi.png" },
            RailOrder = opened.RailOrder,
            OpenedManualInstallRoot = null,
            ManualInstallRoot = null,
            OpenedOfficialLaunchOptions = opened.OfficialLaunchOptions["gi"],
            OfficialLaunchOptions = opened.OfficialLaunchOptions["gi"],
            PublisherPasswordSavingEnabled = opened.Preferences.PublisherPasswordSavingEnabled,
        };

        var merged = LauncherSettingsStateMerge.Apply(latest, opened, edit);

        Assert.True(merged.Preferences.Hsr120FpsOnLaunch);
        Assert.Equal(@"C:\Edited\gi.png", merged.Appearance["gi"].IconPath);
    }

    [Fact]
    public void Export_arming_migrates_per_game_and_per_kind_without_cross_game_leakage()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":1,"export":{"games":{
          "gi":{"pullsArmed":true,"achievementsArmed":false},
          "hsr":{"pullsArmed":false,"achievementsArmed":true},
          "zzz":{"pullsArmed":true,"achievementsArmed":true},
          "wuwa":{"pullsArmed":true,"achievementsArmed":true},
          "ae":{"pullsArmed":true,"achievementsArmed":true}
        }}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        Assert.True(result.State!.Export.Games["gi"].PullsArmed);
        Assert.False(result.State.Export.Games["gi"].AchievementsArmed);
        Assert.False(result.State.Export.Games["hsr"].PullsArmed);
        Assert.True(result.State.Export.Games["hsr"].AchievementsArmed);
        Assert.True(result.State.Export.Games["zzz"].PullsArmed);
        Assert.False(result.State.Export.Games["zzz"].AchievementsArmed);
        Assert.True(result.State.Export.Games["wuwa"].PullsArmed);
        Assert.False(result.State.Export.Games["wuwa"].AchievementsArmed);
        Assert.DoesNotContain("ae", result.State.Export.Games.Keys);
    }

    [Fact]
    public void Persisted_export_paths_are_never_trusted_or_written_back()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":1,"export":{
          "outputDirectory":"\\\\attacker\\share",
          "outputPaths":{"gi:pulls":"C:\\outside\\pulls.json","hsr:achievements":"..\\escape.json"}
        }}
        """);

        Assert.True(result.IsUsable);
        Assert.Null(result.State!.Export.OutputDirectory);
        Assert.Empty(result.State.Export.OutputPaths);
        var written = LauncherStateMigrations.Write(result.State);
        Assert.DoesNotContain("attacker", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("escape", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_global_and_per_game_art_settings_load_but_disappear_after_save()
    {
        var result = LauncherStateMigrations.Read("""
        {
          "version":1,
          "appearance":{"gi":{
            "iconPath":"C:\\User\\gi.png",
            "backgroundPath":"C:\\User\\gi.jpg",
            "automaticArt":false,
            "artScale":325,
            "artX":12,
            "artY":-4,
            "artVariant":"old-banner",
            "artFit":"contain",
            "artPinned":true,
            "pinnedArtFile":"gi/old.webp"
          }},
          "preferences":{"featureFlags":{
            "remoteBannerManifest":false,
            "automaticArt":false,
            "giPulls":false
          }}
        }
        """);

        Assert.True(result.IsUsable);
        Assert.Equal(@"C:\User\gi.png", result.State!.Appearance["gi"].IconPath);
        Assert.Equal(@"C:\User\gi.jpg", result.State.Appearance["gi"].BackgroundPath);
        Assert.Equal(new GameAppearanceState
        {
            IconPath = @"C:\User\gi.png",
            BackgroundPath = @"C:\User\gi.jpg",
        }, result.State.Appearance["gi"]);

        var written = LauncherStateMigrations.Write(result.State);
        foreach (var retired in new[]
                 {
                     "remoteBannerManifest", "automaticArt", "artScale", "artX", "artY",
                     "artVariant", "artFit", "artPinned", "pinnedArtFile",
                 })
        {
            Assert.DoesNotContain(retired, written, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("iconPath", written, StringComparison.Ordinal);
        Assert.Contains("backgroundPath", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_migration_adds_safe_defaults_and_preserves_independent_flags()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":1,"preferences":{"safeNotifications":false,"featureFlags":{"giPulls":false,"hsrAchievements":true,"zzzPulls":true}}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        Assert.False(result.State!.Preferences.SafeNotifications);
        Assert.False(result.State.Preferences.FeatureFlags.GiPulls);
        Assert.True(result.State.Preferences.FeatureFlags.GiAchievements);
        Assert.True(result.State.Preferences.FeatureFlags.HsrAchievements);
        Assert.True(result.State.Preferences.FeatureFlags.ZzzPulls);
        Assert.False(result.State.Preferences.FeatureFlags.ZzzAchievements);
        Assert.False(result.State.Preferences.FeatureFlags.HoyoLabAccountAccess);
        Assert.False(result.State.Preferences.FeatureFlags.SkportAccountAccess);
        Assert.False(result.State.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        Assert.False(result.State.Preferences.FeatureFlags.SkportAccountCleanupPending);
        Assert.True(result.State.Preferences.PublisherPasswordSavingEnabled);
    }

    [Fact]
    public void Version_four_activates_proven_pull_lanes_once_and_then_preserves_user_choices()
    {
        var migrated = LauncherStateMigrations.Read("""
        {"version":3,"preferences":{"featureFlags":{"zzzPulls":false,"wuWaPulls":false}}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, migrated.Status);
        Assert.True(migrated.State!.Preferences.FeatureFlags.ZzzPulls);
        Assert.True(migrated.State.Preferences.FeatureFlags.WuWaPulls);

        var chosenOff = migrated.State with
        {
            Preferences = migrated.State.Preferences with
            {
                FeatureFlags = migrated.State.Preferences.FeatureFlags with
                {
                    ZzzPulls = false,
                    WuWaPulls = false,
                },
            },
        };
        var roundTrip = LauncherStateMigrations.Read(LauncherStateMigrations.Write(chosenOff));

        Assert.Equal(LauncherStateReadStatus.Loaded, roundTrip.Status);
        Assert.False(roundTrip.State!.Preferences.FeatureFlags.ZzzPulls);
        Assert.False(roundTrip.State.Preferences.FeatureFlags.WuWaPulls);
    }

    [Fact]
    public void Publisher_password_saving_defaults_on_for_new_state()
    {
        Assert.True(LauncherState.Defaults().Preferences.PublisherPasswordSavingEnabled);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{"publisherPasswordSavingEnabled":true}""", true)]
    [InlineData("""{"publisherPasswordSavingEnabled":false}""", false)]
    public void Publisher_password_saving_legacy_and_explicit_values_are_preserved(
        string preferences,
        bool expected)
    {
        var result = LauncherStateMigrations.Read(
            $$"""{"version":3,"preferences":{{preferences}}}""");

        Assert.Equal(expected, result.State!.Preferences.PublisherPasswordSavingEnabled);
        var written = LauncherStateMigrations.Write(result.State);
        Assert.Contains(
            $"\"publisherPasswordSavingEnabled\": {expected.ToString().ToLowerInvariant()}",
            written,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_modes_round_trip_only_for_their_exact_supported_games()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":3,"preferences":{"renderingModes":{
          "zzz":"dx12",
          "wuwa":"dx11",
          "hsr":"dx12",
          "ae":"dx11",
          "gi":"unexpected"
        }}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        Assert.Equal("dx12", result.State!.Preferences.RenderingModes["zzz"]);
        Assert.Equal("dx11", result.State.Preferences.RenderingModes["wuwa"]);
        Assert.Equal(2, result.State.Preferences.RenderingModes.Count);

        var roundTrip = LauncherStateMigrations.Read(
            LauncherStateMigrations.Write(result.State));
        Assert.Equal(result.State.Preferences.RenderingModes, roundTrip.State!.Preferences.RenderingModes);
    }

    [Fact]
    public void Publisher_consent_round_trips_as_booleans_without_account_material()
    {
        var enabled = LauncherState.Defaults() with
        {
            Preferences = LauncherState.Defaults().Preferences with
            {
                FeatureFlags = LauncherFeatureFlags.Defaults() with
                {
                    HoyoLabAccountAccess = true,
                    SkportAccountAccess = false,
                    SkportAccountCleanupPending = true,
                },
            },
        };

        var json = LauncherStateMigrations.Write(enabled);
        var read = LauncherStateMigrations.Read(json);

        Assert.True(read.State!.Preferences.FeatureFlags.HoyoLabAccountAccess);
        Assert.False(read.State.Preferences.FeatureFlags.SkportAccountAccess);
        Assert.True(read.State.Preferences.FeatureFlags.SkportAccountCleanupPending);
        Assert.Contains("\"hoyoLabAccountAccess\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"skportAccountAccess\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"skportAccountCleanupPending\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("roleId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pending_publisher_cleanup_preserves_preference_but_effective_consent_stays_off()
    {
        var result = LauncherStateMigrations.Read("""
        {
          "version": 3,
          "preferences": {
            "featureFlags": {
              "hoyoLabAccountAccess": true,
              "hoyoLabAccountCleanupPending": true,
              "skportAccountAccess": true,
              "skportAccountCleanupPending": false
            }
          }
        }
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        var flags = result.State!.Preferences.FeatureFlags;
        Assert.True(flags.HoyoLabAccountAccess);
        Assert.True(flags.HoyoLabAccountCleanupPending);
        Assert.True(flags.SkportAccountAccess);
        Assert.False(flags.SkportAccountCleanupPending);

        var effectiveConsent = new PublisherAccountConsentGate(
            flags.HoyoLabAccountAccess && !flags.HoyoLabAccountCleanupPending,
            flags.SkportAccountAccess && !flags.SkportAccountCleanupPending);
        Assert.False(effectiveConsent.IsEnabled("HoYoLAB"));
        Assert.True(effectiveConsent.IsEnabled("SKPORT"));
    }

    [Fact]
    public void Critical_cleanup_update_is_redundant_and_preserves_account_access_by_default()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-cleanup-redundant-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            var initial = LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = LauncherFeatureFlags.Defaults() with
                    {
                        HoyoLabAccountAccess = true,
                    },
                },
            };
            store.Save(initial);

            store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true);

            var primaryPending = LauncherStateMigrations.Read(
                File.ReadAllText(store.StatePath)).State!;
            var backupPending = LauncherStateMigrations.Read(
                File.ReadAllText(store.BackupPath)).State!;
            Assert.True(primaryPending.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(backupPending.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(primaryPending.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.True(backupPending.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);

            store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: false);

            var primaryClear = LauncherStateMigrations.Read(
                File.ReadAllText(store.StatePath)).State!;
            var backupClear = LauncherStateMigrations.Read(
                File.ReadAllText(store.BackupPath)).State!;
            Assert.True(primaryClear.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(backupClear.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.False(primaryClear.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.False(backupClear.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Primary_corruption_recovers_redundant_pending_cleanup_from_backup()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-cleanup-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = LauncherFeatureFlags.Defaults() with
                    {
                        HoyoLabAccountAccess = true,
                    },
                },
            });
            store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true);
            File.WriteAllText(store.StatePath, "{bad");

            var restarted = new LauncherStateStore(directory).Load();

            Assert.Equal(LauncherStateReadStatus.Recovered, restarted.Status);
            Assert.True(restarted.State!.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(restarted.State.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_treats_either_state_copy_pending_as_pending()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-cleanup-disagreement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults());
            store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true);
            var pending = store.Load().State!;
            var clear = pending with
            {
                Preferences = pending.Preferences with
                {
                    FeatureFlags = pending.Preferences.FeatureFlags with
                    {
                        HoyoLabAccountCleanupPending = false,
                    },
                },
            };

            File.WriteAllText(store.StatePath, LauncherStateMigrations.Write(clear));
            Assert.True(new LauncherStateStore(directory).Load()
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);

            File.WriteAllText(store.StatePath, LauncherStateMigrations.Write(pending));
            File.WriteAllText(store.BackupPath, LauncherStateMigrations.Write(clear));
            Assert.True(new LauncherStateStore(directory).Load()
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Partial_critical_set_survives_an_unrelated_ordinary_update()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-cleanup-sticky-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults());
            using (new FileStream(
                store.StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                Assert.Throws<IOException>(() =>
                    store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true));
            }

            store.Update(state => state with { SelectedGameId = "hsr" });
            var restarted = new LauncherStateStore(directory).Load();

            Assert.Equal("hsr", restarted.State!.SelectedGameId);
            Assert.True(restarted.State.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.True(LauncherStateMigrations.Read(File.ReadAllText(store.StatePath))
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.True(LauncherStateMigrations.Read(File.ReadAllText(store.BackupPath))
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Interrupted_explicit_opt_out_stays_off_after_startup_cleanup_completes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-opt-out-sticky-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = LauncherFeatureFlags.Defaults() with
                    {
                        HoyoLabAccountAccess = true,
                    },
                },
            });
            using (new FileStream(
                store.StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                Assert.Throws<IOException>(() =>
                    store.UpdatePublisherCleanupPending(
                        "HoYoLAB",
                        cleanupPending: true,
                        accountAccess: false));
            }

            var interrupted = new LauncherStateStore(directory).Load().State!;
            Assert.False(interrupted.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(interrupted.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);

            store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: false);
            var completed = new LauncherStateStore(directory).Load().State!;

            Assert.False(completed.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.False(completed.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("""{"version":999,"selectedGameId":"hsr"}""")]
    public void Critical_cleanup_update_preserves_an_unusable_backup(string backupPayload)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-backup-preservation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults());
            File.WriteAllText(store.BackupPath, backupPayload);

            Assert.Throws<IOException>(() =>
                store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true));
            Assert.Equal(backupPayload, File.ReadAllText(store.BackupPath));
            Assert.True(LauncherStateMigrations.Read(File.ReadAllText(store.StatePath))
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);

            Assert.Throws<IOException>(() =>
                store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: false));
            Assert.Equal(backupPayload, File.ReadAllText(store.BackupPath));
            Assert.True(LauncherStateMigrations.Read(File.ReadAllText(store.StatePath))
                .State!.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("""{"version":999,"selectedGameId":"hsr"}""")]
    public void Pending_set_preserves_usable_backup_bytes_when_primary_is_unusable(
        string primaryPayload)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nyx-critical-primary-preservation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with { SelectedGameId = "gi" });
            store.Save(LauncherState.Defaults() with { SelectedGameId = "zzz" });
            var backupBefore = File.ReadAllBytes(store.BackupPath);
            Assert.True(LauncherStateMigrations.Read(
                File.ReadAllText(store.BackupPath)).IsUsable);
            File.WriteAllText(store.StatePath, primaryPayload);
            var primaryBefore = File.ReadAllBytes(store.StatePath);

            Assert.Throws<IOException>(() =>
                store.UpdatePublisherCleanupPending("HoYoLAB", cleanupPending: true));

            Assert.Equal(primaryBefore, File.ReadAllBytes(store.StatePath));
            Assert.Equal(backupBefore, File.ReadAllBytes(store.BackupPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Retired_feature_flags_are_accepted_but_not_written_again()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":1,"preferences":{"featureFlags":{"officialNews":false,"remoteBannerManifest":true}}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        var state = Assert.IsType<LauncherState>(result.State);
        var written = LauncherStateMigrations.Write(state);
        Assert.DoesNotContain("officialNews", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remoteBannerManifest", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automaticArt", written, StringComparison.OrdinalIgnoreCase);
        Assert.True(state.Preferences.FeatureFlags.GiPulls);
    }

    [Fact]
    public void Manual_install_roots_round_trip_only_for_supported_games_and_safe_local_paths()
    {
        var result = LauncherStateMigrations.Read("""
        {"version":2,"preferences":{"manualInstallRoots":{
          "gi":"D:\\Games\\Genshin Impact Game",
          "wuwa":"D:\\Games\\Wuthering Waves",
          "evil":"D:\\Games\\Other",
          "hsr":"\\\\server\\share",
          "zzz":"..\\relative"
        }}}
        """);

        Assert.Equal(LauncherStateReadStatus.Migrated, result.Status);
        Assert.Equal(@"D:\Games\Genshin Impact Game", result.State!.Preferences.ManualInstallRoots["gi"]);
        Assert.Equal(@"D:\Games\Wuthering Waves", result.State.Preferences.ManualInstallRoots["wuwa"]);
        Assert.DoesNotContain("evil", result.State.Preferences.ManualInstallRoots.Keys);
        Assert.DoesNotContain("hsr", result.State.Preferences.ManualInstallRoots.Keys);
        Assert.DoesNotContain("zzz", result.State.Preferences.ManualInstallRoots.Keys);

        var written = LauncherStateMigrations.Write(result.State);
        Assert.Contains("manualInstallRoots", written, StringComparison.Ordinal);
        Assert.Contains("Genshin Impact Game", written, StringComparison.Ordinal);
        Assert.DoesNotContain("server", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_save_keeps_a_custom_game_added_by_another_instance_while_open()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-settings-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new LauncherStateStore(directory);
            settingsStore.Save(LauncherState.Defaults());
            var opened = settingsStore.Load().State!;
            var concurrentGame = CustomGame("custom-concurrent", 1);
            var otherInstance = new LauncherStateStore(directory);
            otherInstance.Update(state => state with
            {
                CustomGames = state.CustomGames.Append(concurrentGame).ToArray(),
                RailOrder = state.RailOrder.Append(concurrentGame.Id).ToArray(),
                Appearance = state.Appearance
                    .Append(new KeyValuePair<string, GameAppearanceState>("gi", new() { BackgroundPath = @"C:\Concurrent\gi.jpg" }))
                    .Append(new KeyValuePair<string, GameAppearanceState>("hsr", new() { IconPath = @"C:\Concurrent\hsr.png" }))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                Preferences = state.Preferences with
                {
                    StayVisibleAfterLaunch = false,
                    PublisherPasswordSavingEnabled = false,
                    DataDirectory = @"D:\NyxData",
                    FeatureFlags = state.Preferences.FeatureFlags with { GiPulls = false },
                },
            });

            settingsStore.Update(latest => LauncherSettingsStateMerge.Apply(
                latest,
                opened,
                SettingsEdit(opened, opened.RailOrder, iconPath: @"C:\Edited\gi.png")));

            var saved = settingsStore.Load().State!;
            Assert.Contains(saved.CustomGames, game => game.Id == concurrentGame.Id);
            Assert.Contains(concurrentGame.Id, saved.RailOrder);
            Assert.Equal(@"C:\Concurrent\hsr.png", saved.Appearance["hsr"].IconPath);
            Assert.Equal(@"C:\Edited\gi.png", saved.Appearance["gi"].IconPath);
            Assert.Equal(@"C:\Concurrent\gi.jpg", saved.Appearance["gi"].BackgroundPath);
            Assert.False(saved.Preferences.StayVisibleAfterLaunch);
            Assert.False(saved.Preferences.PublisherPasswordSavingEnabled);
            Assert.Equal(@"D:\NyxData", saved.Preferences.DataDirectory);
            Assert.False(saved.Preferences.FeatureFlags.GiPulls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_save_merges_manual_root_and_launch_options_field_by_field()
    {
        var openedOptions = LauncherState.Defaults().OfficialLaunchOptions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        openedOptions["ae"] = new OfficialGameLaunchOptions
        {
            RawArguments = "--opened",
            Enabled = false,
        };
        var opened = LauncherState.Defaults() with
        {
            OfficialLaunchOptions = openedOptions,
            Preferences = LauncherState.Defaults().Preferences with
            {
                StayVisibleAfterLaunch = true,
                RefreshContentOnStartup = true,
                SafeNotifications = true,
                EndfieldInstallRoot = @"D:\Opened\Endfield",
                ManualInstallRoots = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ae"] = @"D:\Opened\Endfield",
                },
            },
        };
        var latestOptions = openedOptions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        latestOptions["ae"] = new OfficialGameLaunchOptions
        {
            RawArguments = "--concurrent",
            Enabled = true,
        };
        var latest = opened with
        {
            OfficialLaunchOptions = latestOptions,
            Preferences = opened.Preferences with
            {
                StayVisibleAfterLaunch = false,
                RefreshContentOnStartup = false,
                SafeNotifications = false,
                EndfieldInstallRoot = @"D:\Concurrent\Endfield",
                ManualInstallRoots = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ae"] = @"D:\Concurrent\Endfield",
                },
            },
        };
        var editedOptions = new OfficialGameLaunchOptions
        {
            RawArguments = "--edited",
            Enabled = false,
        };

        var merged = LauncherSettingsStateMerge.Apply(
            latest,
            opened,
            new LauncherSettingsEdit
            {
                GameId = "ae",
                OpenedAppearance = new GameAppearanceState(),
                Appearance = new GameAppearanceState(),
                RailOrder = opened.RailOrder,
                OpenedManualInstallRoot = @"D:\Opened\Endfield",
                ManualInstallRoot = @"D:\Edited\Endfield",
                OpenedOfficialLaunchOptions = openedOptions["ae"],
                OfficialLaunchOptions = editedOptions,
                PublisherPasswordSavingEnabled = opened.Preferences.PublisherPasswordSavingEnabled,
            });

        Assert.Equal(@"D:\Edited\Endfield", merged.Preferences.ManualInstallRoots["ae"]);
        Assert.Equal(@"D:\Edited\Endfield", merged.Preferences.EndfieldInstallRoot);
        Assert.Equal("--edited", merged.OfficialLaunchOptions["ae"].RawArguments);
        Assert.True(merged.OfficialLaunchOptions["ae"].Enabled);
        Assert.False(merged.Preferences.StayVisibleAfterLaunch);
        Assert.False(merged.Preferences.RefreshContentOnStartup);
        Assert.False(merged.Preferences.SafeNotifications);
    }

    [Fact]
    public void Settings_reorder_keeps_rail_additions_from_another_instance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-settings-rail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new LauncherStateStore(directory);
            settingsStore.Save(LauncherState.Defaults());
            var opened = settingsStore.Load().State!;
            var concurrentGame = CustomGame("custom-new-rail", 2);
            new LauncherStateStore(directory).Update(state => state with
            {
                CustomGames = state.CustomGames.Append(concurrentGame).ToArray(),
                RailOrder = state.RailOrder.Append(concurrentGame.Id).ToArray(),
            });
            var localOrder = opened.RailOrder.Reverse().ToArray();

            settingsStore.Update(latest => LauncherSettingsStateMerge.Apply(
                latest,
                opened,
                SettingsEdit(opened, localOrder)));

            var saved = settingsStore.Load().State!;
            Assert.Equal(localOrder, saved.RailOrder.Take(localOrder.Length));
            Assert.Equal(concurrentGame.Id, saved.RailOrder.Last());
            Assert.Contains(saved.CustomGames, game => game.Id == concurrentGame.Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_save_does_not_resurrect_a_custom_game_deleted_by_another_instance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-settings-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new LauncherStateStore(directory);
            var custom = CustomGame("custom-concurrent-delete", 3);
            settingsStore.Save(LauncherState.Defaults() with
            {
                CustomGames = [custom],
                RailOrder = LauncherState.Defaults().RailOrder.Append(custom.Id).ToArray(),
                SelectedGameId = custom.Id,
            });
            var opened = settingsStore.Load().State!;
            new LauncherStateStore(directory).Update(state => state with
            {
                CustomGames = state.CustomGames.Where(game => game.Id != custom.Id).ToArray(),
                RailOrder = state.RailOrder.Where(id => id != custom.Id).ToArray(),
                SelectedGameId = "gi",
            });

            settingsStore.Update(latest => LauncherSettingsStateMerge.Apply(
                latest,
                opened,
                SettingsEdit(
                    opened,
                    opened.RailOrder,
                    gameId: custom.Id,
                    customGame: custom with { Name = "Locally edited name" })));

            var saved = settingsStore.Load().State!;
            Assert.DoesNotContain(saved.CustomGames, game => game.Id == custom.Id);
            Assert.DoesNotContain(custom.Id, saved.RailOrder);
            Assert.DoesNotContain(custom.Id, saved.Appearance.Keys);
            Assert.Equal("gi", saved.SelectedGameId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Concurrent_add_with_the_same_canonical_executable_fails_without_mutating_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-custom-add-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstInstance = new LauncherStateStore(directory);
            firstInstance.Save(LauncherState.Defaults());
            var otherGame = CustomGame("custom-add-winner", 4, @"C:\Games\Shared.exe");
            new LauncherStateStore(directory).Update(
                state => LauncherCustomGameStateMerge.Add(state, otherGame));
            var primaryBeforeConflict = File.ReadAllText(firstInstance.StatePath);
            var staleCandidate = CustomGame(
                "custom-add-stale",
                5,
                "c:/games/staging/../SHARED.exe");

            Assert.Throws<CustomGameExecutableConflictException>(() => firstInstance.Update(
                state => LauncherCustomGameStateMerge.Add(state, staleCandidate)));

            Assert.Equal(primaryBeforeConflict, File.ReadAllText(firstInstance.StatePath));
            var saved = firstInstance.Load().State!;
            var onlyGame = Assert.Single(saved.CustomGames);
            Assert.Equal(otherGame.Id, onlyGame.Id);
            Assert.DoesNotContain(saved.CustomGames, game => game.Id == staleCandidate.Id);
            AssertUniqueExecutableIdentities(saved.CustomGames);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Concurrent_settings_edit_to_an_owned_executable_fails_without_mutating_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-custom-edit-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsStore = new LauncherStateStore(directory);
            var editedGame = CustomGame("custom-edit-source", 6, @"C:\Games\Original.exe");
            settingsStore.Save(LauncherCustomGameStateMerge.Add(LauncherState.Defaults(), editedGame));
            var opened = settingsStore.Load().State!;
            var otherGame = CustomGame("custom-edit-winner", 7, @"C:\Games\Shared.exe");
            new LauncherStateStore(directory).Update(
                state => LauncherCustomGameStateMerge.Add(state, otherGame));
            var primaryBeforeConflict = File.ReadAllText(settingsStore.StatePath);

            Assert.Throws<CustomGameExecutableConflictException>(() => settingsStore.Update(
                latest => LauncherSettingsStateMerge.Apply(
                    latest,
                    opened,
                    SettingsEdit(
                        opened,
                        opened.RailOrder,
                        gameId: editedGame.Id,
                        customGame: editedGame with
                        {
                            Name = "Stale local edit",
                            ExecutablePath = "c:/GAMES/staging/../shared.exe",
                        }))));

            Assert.Equal(primaryBeforeConflict, File.ReadAllText(settingsStore.StatePath));
            var saved = settingsStore.Load().State!;
            Assert.Equal(2, saved.CustomGames.Count);
            Assert.Equal(
                @"C:\Games\Original.exe",
                saved.CustomGames.Single(game => game.Id == editedGame.Id).ExecutablePath);
            Assert.Equal(
                @"C:\Games\Shared.exe",
                saved.CustomGames.Single(game => game.Id == otherGame.Id).ExecutablePath);
            AssertUniqueExecutableIdentities(saved.CustomGames);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Store_recovers_backup_after_malformed_primary_and_is_safe_for_concurrent_writers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with { SelectedGameId = "hsr" });
            store.Save(LauncherState.Defaults() with { SelectedGameId = "zzz" });
            File.WriteAllText(store.StatePath, "{bad");

            var recovered = store.Load();
            Assert.Equal(LauncherStateReadStatus.Recovered, recovered.Status);
            Assert.Equal("hsr", recovered.State!.SelectedGameId);
            Assert.False(store.CanSave);
            Assert.True(store.RestoreLastKnownGood().IsUsable);

            Parallel.For(0, 16, index => store.Save(LauncherState.Defaults() with
            {
                SelectedGameId = index % 2 == 0 ? "gi" : "ae",
            }));
            var final = store.Load();
            Assert.True(final.IsUsable);
            Assert.Contains(final.State!.SelectedGameId, new[] { "gi", "ae" });
            Assert.True(File.Exists(store.BackupPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task State_updates_from_real_processes_keep_every_edit_and_valid_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-state-processes-" + Guid.NewGuid().ToString("N"));
        var barrier = Path.Combine(directory, "go");
        var processes = new List<Process>();
        try
        {
            Directory.CreateDirectory(directory);
            new LauncherStateStore(directory).Save(LauncherState.Defaults());
            for (var index = 0; index < 6; index++)
            {
                var id = $"custom-process-{index}";
                processes.Add(StartStateWorker(
                    directory,
                    id,
                    Path.Combine(directory, $"ready-{index}"),
                    barrier,
                    Path.Combine(directory, $"acquired-{index}"),
                    delayMilliseconds: 75));
            }

            await WaitForFilesAsync(
                Enumerable.Range(0, processes.Count).Select(index => Path.Combine(directory, $"ready-{index}")),
                TimeSpan.FromSeconds(10));
            File.WriteAllText(barrier, "go");
            await WaitForProcessesAsync(processes, TimeSpan.FromSeconds(20));

            Assert.All(processes, process => Assert.Equal(0, process.ExitCode));
            var payload = File.ReadAllText(Path.Combine(directory, "launcher-state-v1.json"));
            var parsed = LauncherStateMigrations.Read(payload);
            Assert.Equal(LauncherStateReadStatus.Loaded, parsed.Status);
            Assert.Equal(
                Enumerable.Range(0, 6).Select(index => $"custom-process-{index}").Order(),
                parsed.State!.CustomGames.Select(game => game.Id).Order());
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cross_process_state_lock_fails_in_bounded_time_without_changing_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-state-timeout-" + Guid.NewGuid().ToString("N"));
        Process? holder = null;
        try
        {
            Directory.CreateDirectory(directory);
            var baseline = LauncherState.Defaults() with { SelectedGameId = "hsr" };
            new LauncherStateStore(directory).Save(baseline);
            var barrier = Path.Combine(directory, "go");
            var ready = Path.Combine(directory, "ready");
            var acquired = Path.Combine(directory, "acquired");
            holder = StartStateWorker(
                directory,
                "custom-holder",
                ready,
                barrier,
                acquired,
                delayMilliseconds: 1_500);
            await WaitForFilesAsync([ready], TimeSpan.FromSeconds(10));
            File.WriteAllText(barrier, "go");
            await WaitForFilesAsync([acquired], TimeSpan.FromSeconds(10));

            var contender = new LauncherStateStore(directory, TimeSpan.FromMilliseconds(150));
            var stopwatch = Stopwatch.StartNew();
            var exception = Assert.ThrowsAny<IOException>(() => contender.Save(
                baseline with { SelectedGameId = "ae" }));
            stopwatch.Stop();

            Assert.Contains("busy", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            await WaitForProcessesAsync([holder], TimeSpan.FromSeconds(10));
            Assert.Equal(0, holder.ExitCode);
            var final = new LauncherStateStore(directory).Load();
            Assert.Equal("hsr", final.State!.SelectedGameId);
            Assert.Contains(final.State.CustomGames, game => game.Id == "custom-holder");
        }
        finally
        {
            if (holder is not null)
            {
                if (!holder.HasExited) holder.Kill(entireProcessTree: true);
                holder.Dispose();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"version\":999,\"selectedGameId\":\"hsr\"}")]
    public void Unusable_primary_blocks_ordinary_writes_until_explicit_reset(string unusablePayload)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-state-block-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new LauncherStateStore(directory);
            File.WriteAllText(store.StatePath, unusablePayload);

            Assert.False(store.CanSave);
            Assert.Throws<IOException>(() => store.Save(
                LauncherState.Defaults() with { SelectedGameId = "ae" }));
            Assert.Equal(unusablePayload, File.ReadAllText(store.StatePath));

            var reset = store.ResetToDefaults();
            Assert.True(reset.IsUsable);
            var recoveryCopy = Assert.Single(Directory.GetFiles(
                directory,
                "launcher-state-v1.json.recovery.*"));
            Assert.Equal(unusablePayload, File.ReadAllText(recoveryCopy));
            Assert.True(store.CanSave);
            store.Save(LauncherState.Defaults() with { SelectedGameId = "hsr" });
            Assert.Equal("hsr", store.Load().State!.SelectedGameId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovered_backup_does_not_silently_authorize_replacing_bad_primary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-state-recovery-block-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with { SelectedGameId = "hsr" });
            store.Save(LauncherState.Defaults() with { SelectedGameId = "zzz" });
            const string malformed = "{bad";
            File.WriteAllText(store.StatePath, malformed);
            var backupBefore = File.ReadAllText(store.BackupPath);

            var recovered = store.Load();
            Assert.Equal(LauncherStateReadStatus.Recovered, recovered.Status);
            Assert.Equal("hsr", recovered.State!.SelectedGameId);
            Assert.False(store.CanSave);
            Assert.Throws<IOException>(() => store.Save(recovered.State));
            Assert.Equal(malformed, File.ReadAllText(store.StatePath));
            Assert.Equal(backupBefore, File.ReadAllText(store.BackupPath));

            var restored = store.RestoreLastKnownGood();
            Assert.Equal(LauncherStateReadStatus.Recovered, restored.Status);
            var recoveryCopy = Assert.Single(Directory.GetFiles(
                directory,
                "launcher-state-v1.json.recovery.*"));
            Assert.Equal(malformed, File.ReadAllText(recoveryCopy));
            Assert.True(store.CanSave);
            Assert.Equal("hsr", store.Load().State!.SelectedGameId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Legacy_user_data_is_moved_whole_to_the_canonical_root()
    {
        var local = Path.Combine(Path.GetTempPath(), "nyx-data-root-" + Guid.NewGuid().ToString("N"));
        var legacy = NyxUserDataPaths.LegacyRoot(local);
        var canonical = NyxUserDataPaths.CanonicalRoot(local);
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "UserAssets"));
            File.WriteAllText(Path.Combine(legacy, "launcher-state-v1.json"), "state");
            File.WriteAllText(Path.Combine(legacy, "UserAssets", "keep.png"), "asset");

            var result = NyxUserDataRootMigration.PrepareCanonicalRoot(local);

            Assert.Equal(canonical, result);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal("state", File.ReadAllText(Path.Combine(canonical, "launcher-state-v1.json")));
            Assert.Equal("asset", File.ReadAllText(Path.Combine(canonical, "UserAssets", "keep.png")));
        }
        finally
        {
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public void Migration_refuses_to_merge_two_roots_and_preserves_both()
    {
        var local = Path.Combine(Path.GetTempPath(), "nyx-data-conflict-" + Guid.NewGuid().ToString("N"));
        var legacy = NyxUserDataPaths.LegacyRoot(local);
        var canonical = NyxUserDataPaths.CanonicalRoot(local);
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(canonical);
            File.WriteAllText(Path.Combine(legacy, "legacy.txt"), "legacy");
            File.WriteAllText(Path.Combine(canonical, "canonical.txt"), "canonical");

            Assert.Throws<IOException>(() => NyxUserDataRootMigration.PrepareCanonicalRoot(local));

            Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "legacy.txt")));
            Assert.Equal("canonical", File.ReadAllText(Path.Combine(canonical, "canonical.txt")));
        }
        finally
        {
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public void Migration_rejects_a_link_inside_legacy_data_without_moving_it()
    {
        var local = Path.Combine(Path.GetTempPath(), "nyx-data-link-" + Guid.NewGuid().ToString("N"));
        var legacy = NyxUserDataPaths.LegacyRoot(local);
        var outside = Path.Combine(local, "outside");
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            Directory.CreateSymbolicLink(Path.Combine(legacy, "linked"), outside);

            Assert.Throws<IOException>(() => NyxUserDataRootMigration.PrepareCanonicalRoot(local));

            Assert.True(Directory.Exists(legacy));
            Assert.False(Directory.Exists(NyxUserDataPaths.CanonicalRoot(local)));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }

    private static Process StartStateWorker(
        string root,
        string id,
        string readyPath,
        string goPath,
        string acquiredPath,
        int delayMilliseconds)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(FindStateWorker());
        start.ArgumentList.Add("append");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(id);
        start.ArgumentList.Add(readyPath);
        start.ArgumentList.Add(goPath);
        start.ArgumentList.Add(acquiredPath);
        start.ArgumentList.Add(delayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the state worker.");
    }

    [Fact]
    public void Reset_order_restores_official_then_custom_creation_order_without_deleting_data()
    {
        var first = CustomGame("custom-first", 10);
        var second = CustomGame("custom-second", 20);
        var state = LauncherState.Defaults() with
        {
            SelectedGameId = second.Id,
            RailOrder = [second.Id, "zzz", "gi", first.Id, "ae", "wuwa", "hsr"],
            CustomGames = [second, first],
            Appearance = new Dictionary<string, GameAppearanceState>
            {
                [second.Id] = new() { BackgroundPath = @"C:\Art\second.jpg" },
            },
        };

        var reset = LauncherSettingsStateMerge.ResetRailOrder(state);

        Assert.Equal(["gi", "hsr", "zzz", "wuwa", "ae", first.Id, second.Id], reset.RailOrder);
        Assert.Equal(second.Id, reset.SelectedGameId);
        Assert.Equal(state.CustomGames, reset.CustomGames);
        Assert.Equal(@"C:\Art\second.jpg", reset.Appearance[second.Id].BackgroundPath);
    }

    [Fact]
    public void Reset_launcher_state_requires_confirmation_and_has_default_only_scope()
    {
        var custom = CustomGame("custom-safe", 1);
        var current = LauncherState.Defaults() with
        {
            SelectedGameId = custom.Id,
            RailOrder = LauncherState.Defaults().RailOrder.Append(custom.Id).ToArray(),
            CustomGames = [custom],
            Preferences = LauncherState.Defaults().Preferences with { DataDirectory = @"D:\NyxData" },
        };

        Assert.Same(current, LauncherSettingsStateMerge.ResetLauncherState(current, confirmed: false));
        var reset = LauncherSettingsStateMerge.ResetLauncherState(current, confirmed: true);
        Assert.Equal(LauncherState.Defaults().RailOrder, reset.RailOrder);
        Assert.Empty(reset.CustomGames);
        Assert.Empty(reset.Appearance);
        Assert.Contains(reset.SelectedGameId, reset.RailOrder);
    }

    private static CustomGameDefinition CustomGame(
        string id,
        long creationOrder,
        string? executablePath = null) => new()
        {
            Id = id,
            Name = id,
            ExecutablePath = executablePath ?? $@"C:\Games\{id}.exe",
            IconPath = $@"C:\Games\{id}.png",
            CreationOrder = creationOrder,
        };

    private static void AssertUniqueExecutableIdentities(IReadOnlyList<CustomGameDefinition> games)
    {
        var identities = games
            .Select(game => LauncherCustomGameStateMerge.CanonicalExecutableIdentity(game.ExecutablePath))
            .ToArray();
        Assert.Equal(
            identities.Length,
            identities.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static LauncherSettingsEdit SettingsEdit(
        LauncherState opened,
        IReadOnlyList<string> railOrder,
        string? iconPath = null,
        string gameId = "gi",
        CustomGameDefinition? customGame = null) => new()
        {
            GameId = gameId,
            OpenedAppearance = opened.Appearance.TryGetValue(gameId, out var appearance)
            ? appearance
            : new GameAppearanceState(),
            Appearance = new GameAppearanceState { IconPath = iconPath },
            CustomGame = customGame,
            RailOrder = railOrder,
            OpenedManualInstallRoot = opened.Preferences.ManualInstallRoots.TryGetValue(gameId, out var root)
            ? root
            : gameId == "ae" ? opened.Preferences.EndfieldInstallRoot : null,
            ManualInstallRoot = opened.Preferences.ManualInstallRoots.TryGetValue(gameId, out root)
            ? root
            : gameId == "ae" ? opened.Preferences.EndfieldInstallRoot : null,
            OpenedOfficialLaunchOptions = opened.OfficialLaunchOptions.TryGetValue(gameId, out var options)
            ? options
            : null,
            OfficialLaunchOptions = opened.OfficialLaunchOptions.TryGetValue(gameId, out options)
            ? options
            : null,
            PublisherPasswordSavingEnabled = opened.Preferences.PublisherPasswordSavingEnabled,
        };

    private static string FindStateWorker()
    {
        var root = FindWorkspaceRoot();
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = targetFramework.Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not identify the test build configuration.");
        var path = Path.Combine(
            root,
            "Desktop",
            "tests",
            "Nyx.Desktop.StateWorker",
            "bin",
            configuration,
            "net10.0",
            "Nyx.Desktop.StateWorker.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The state worker was not built.", path);
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.Core")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }

    private static async Task WaitForFilesAsync(IEnumerable<string> paths, TimeSpan timeout)
    {
        var expected = paths.ToArray();
        var deadline = DateTime.UtcNow + timeout;
        while (expected.Any(path => !File.Exists(path)))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for worker readiness.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitForProcessesAsync(IEnumerable<Process> processes, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(cancellation.Token)));
        foreach (var process in processes)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellation.Token);
            Assert.True(process.ExitCode == 0, error);
        }
    }
}
