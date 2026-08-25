using System.Text.RegularExpressions;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.State;
using Nyx_Desktop_App.ViewModels;

namespace Nyx.Desktop.Tests.UI;

public sealed class IrisLauncherShellTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Account_and_export_status_keep_polite_live_regions_and_dynamic_accessible_names()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            SliceElement(xaml, "x:Name=\"LaunchDetail\""),
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            SliceElement(xaml, "x:Name=\"StableExportStatusText\""),
            StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StablePullExportToggle, pullAccessibilityName)", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StableAchievementExportToggle, achievementAccessibilityName)", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StableExportStatusText, StableExportStatusText.Text)", code, StringComparison.Ordinal);
        Assert.Contains("SetStableExportStatus(NyxToolsStatusText.Text)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_launch_detail_names_verified_files_before_optional_version()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var genshin = Slice(code, "private void RenderGenshin()", "private void RenderHoyo(");
        var hoyo = Slice(code, "private void RenderHoyo(", "private void RenderPublisherSession(");
        var publisher = Slice(code, "private void RenderPublisherSession(", "private void RenderEndfield(");
        var helper = Slice(code, "private static string VersionOnly", "private void GameSelector_ContainerContentChanging");

        Assert.Contains("SetLaunchControls(true, \"LAUNCH\", VerifiedFilesDetail(gameVersion)", genshin, StringComparison.Ordinal);
        Assert.Contains("SetLaunchControls(true, \"LAUNCH\", VerifiedFilesDetail(version)", hoyo, StringComparison.Ordinal);
        Assert.Contains("SetLaunchControls(true, \"LAUNCH\", \"Official files verified.\"", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLaunchControls(true, \"LAUNCH\", VersionOnly", genshin + hoyo, StringComparison.Ordinal);
        Assert.Contains("private static string VerifiedFilesDetail(string? version)", helper, StringComparison.Ordinal);
        Assert.Contains("? \"Official files verified.\"", helper, StringComparison.Ordinal);
        Assert.Contains(": $\"Official files verified \\u00B7 {version}\"", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_codes_and_account_use_session_only_chevron_collapse_controls()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var layout = ReadAppFile("ViewModels", "LauncherLayoutState.cs");
        var handler = Slice(code, "private void SectionCollapseButton_Click", "private async void AchievementSource_Click");

        foreach (var (name, section, label) in new[]
                 {
                     ("BannerCollapseButton", "banners", "Banners"),
                     ("CodesCollapseButton", "codes", "Codes"),
                     ("AccountCollapseButton", "account", "Account"),
                 })
        {
            var button = SliceElement(xaml, $"x:Name=\"{name}\"");
            Assert.Contains("Click=\"SectionCollapseButton_Click\"", button, StringComparison.Ordinal);
            Assert.Contains($"Tag=\"{section}\"", button, StringComparison.Ordinal);
            Assert.Contains($"AutomationProperties.Name=\"Collapse {label}\"", button, StringComparison.Ordinal);
            Assert.Contains($"ToolTipService.ToolTip=\"Collapse {label}\"", button, StringComparison.Ordinal);
            Assert.Contains("Content=\"&#xE70D;\"", button, StringComparison.Ordinal);
        }

        Assert.Contains("BannerCycleColumns.Visibility", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleRegion.Height =", handler, StringComparison.Ordinal);
        Assert.Contains("SignalPanel.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("AccountSectionContent.Visibility", handler, StringComparison.Ordinal);
        Assert.Contains("accountSectionExpanded = expanded", handler, StringComparison.Ordinal);
        Assert.Contains("AccountAndToolsIdentityText.Visibility = Visibility.Collapsed", handler, StringComparison.Ordinal);
        Assert.Contains("RenderHoyoLabAccountIdentity(selected)", handler, StringComparison.Ordinal);
        Assert.Contains("button.Content = expanded ? \"\\uE70D\" : \"\\uE70E\"", handler, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName", handler, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("launcherState", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Collapse", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_game_has_status_accessibility_copy_without_obsolete_local_hero_art()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        foreach (var gameId in new[] { "gi", "hsr", "zzz", "wuwa", "ae" })
        {
            Assert.Contains($"[\"{gameId}\"]", code, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(
                WorkspaceRoot,
                "Desktop",
                "src",
                "Nyx.Desktop.App",
                "Assets",
                "Iris",
                $"{gameId}-hero.png")));
        }

        Assert.Contains("StatusDescription", code, StringComparison.Ordinal);
        Assert.Contains("AccessibleName", code, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding AccessibilityName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContainerContentChanging=\"GameSelector_ContainerContentChanging\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetName(item, game.AccessibleName)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "$\"{DisplayName}. {StatusDescription}. Select game.\"",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "GameSelector.SelectionChanged += GameSelector_SelectionChanged",
            code,
            StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode", code, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Rail_marks_refresh_for_every_game_and_keep_accessibility_names_current()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("RefreshGameRailSignals();", code, StringComparison.Ordinal);
        Assert.Contains("foreach (var game in Games)", code, StringComparison.Ordinal);
        Assert.Contains("sessions.GetSnapshot(game.Id)", code, StringComparison.Ordinal);
        Assert.Contains("publisherStatus.Current", code, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalProjector.Project", code, StringComparison.Ordinal);
        Assert.Contains("game.UpdateStatus(RailSignalGlyphs[signal.Kind]", code, StringComparison.Ordinal);
        Assert.Contains("ContainerFromItem(game)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(StatusGlyph)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(StatusDescription)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(AccessibleName)", code, StringComparison.Ordinal);
        Assert.Contains("SessionRefresh_Refreshed", code, StringComparison.Ordinal);
        Assert.Contains("PublisherStatus_Updated", code, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalKind.Running] = \"▶\"", code, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalKind.UpdateAvailable] = \"↑\"", code, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalKind.PreDownloadAvailable] = \"↓\"", code, StringComparison.Ordinal);
        Assert.Contains("GameRailSignalKind.Unsupported] = \"○\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_palette_lookup_uses_application_resources()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains(
            "Application.Current.Resources[\"MistBrush\"]",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(Brush)Resources[brushKey]",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void System_focus_visuals_and_high_contrast_resource_dictionary_are_persistent()
    {
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");

        Assert.Contains("UseSystemFocusVisuals\" Value=\"True", controls, StringComparison.Ordinal);
        Assert.Contains("FocusVisualPrimaryThickness\" Value=\"2", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrast\"", palette, StringComparison.Ordinal);
    }

    [Fact]
    public void High_contrast_tokens_pair_highlight_with_highlight_text()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");

        Assert.Contains(
            "x:Key=\"PrimaryActionBackgroundBrush\" Color=\"{ThemeResource SystemColorHighlightColor}\"",
            palette,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"PrimaryActionForegroundBrush\" Color=\"{ThemeResource SystemColorHighlightTextColor}\"",
            palette,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"FocusSecondaryBrush\" Color=\"{ThemeResource SystemColorHighlightTextColor}\"",
            palette,
            StringComparison.Ordinal);
        Assert.Contains("Background\" Value=\"{ThemeResource PrimaryActionBackgroundBrush}", controls, StringComparison.Ordinal);
        Assert.Contains("FocusVisualPrimaryBrush\" Value=\"{ThemeResource LaunchActionForegroundBrush}", controls, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(xaml, "Foreground=\"{ThemeResource LaunchActionForegroundBrush}\"").Count);
        Assert.DoesNotContain("Foreground=\"#", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void High_contrast_backdrop_is_between_root_art_and_semantic_content()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var artwork = xaml.IndexOf("x:Name=\"BackgroundArtwork\"", StringComparison.Ordinal);
        var nextArtwork = xaml.IndexOf("x:Name=\"BackgroundArtworkNext\"", StringComparison.Ordinal);
        var cover = xaml.IndexOf("x:Name=\"HighContrastBackdrop\"", StringComparison.Ordinal);
        var brand = xaml.IndexOf("x:Name=\"BrandLockup\"", StringComparison.Ordinal);
        var games = xaml.IndexOf("x:Name=\"GameSelector\"", StringComparison.Ordinal);
        var content = xaml.IndexOf("x:Name=\"BannerContentRegion\"", StringComparison.Ordinal);
        var deck = xaml.IndexOf("x:Name=\"LowerActionRegion\"", StringComparison.Ordinal);

        Assert.True(artwork >= 0 && artwork < nextArtwork);
        Assert.True(nextArtwork < cover);
        Assert.True(cover < brand);
        Assert.True(cover < games);
        Assert.True(cover < content);
        Assert.True(cover < deck);
        var motion = xaml.IndexOf("x:Name=\"LauncherMotionBackground\"", StringComparison.Ordinal);
        var nextMotion = xaml.IndexOf("x:Name=\"LauncherMotionBackgroundNext\"", StringComparison.Ordinal);
        Assert.True(nextArtwork < motion && motion < nextMotion && nextMotion < cover);
    }

    [Fact]
    public void High_contrast_hides_all_decorative_art_while_dark_theme_keeps_it()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");

        Assert.Contains(
            "x:Key=\"HighContrastBackdropBrush\" Color=\"{ThemeResource SystemColorWindowColor}\"",
            palette,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DecorativeArtOpacity\">1</x:Double>", palette, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrastBackdropOpacity\">0</x:Double>", palette, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DecorativeArtOpacity\">0</x:Double>", palette, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrastBackdropOpacity\">1</x:Double>", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("backgroundnyx.png", xaml, StringComparison.Ordinal);
        foreach (var decorativeName in new[]
                 {
                     "BackgroundArtwork",
                     "BackgroundArtworkNext",
                     "LauncherMotionBackground",
                     "LauncherMotionBackgroundNext",
                     "HighContrastBackdrop",
                 })
        {
            AssertRawElement(xaml, decorativeName);
        }
        Assert.DoesNotContain("HeroStage", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroArtwork", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"{ThemeResource DecorativeArtOpacity}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"{ThemeResource HighContrastBackdropOpacity}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_launcher_background_projection_preserves_saved_and_legacy_behavior()
    {
        const string id = "custom-background";
        const string legacy = @"C:\Nyx\legacy.png";
        const string saved = @"C:\Nyx\saved.png";
        var custom = new CustomGameDefinition
        {
            Id = id,
            Name = "Custom",
            ExecutablePath = @"C:\Nyx\game.exe",
            IconPath = @"C:\Nyx\icon.png",
            BackgroundPath = legacy,
        };
        var state = new LauncherState
        {
            CustomGames = [custom],
            Appearance = new Dictionary<string, GameAppearanceState>(StringComparer.Ordinal)
            {
                [id] = new() { BackgroundPath = saved },
                ["custom-unknown"] = new() { BackgroundPath = @"C:\Nyx\orphan.png" },
                ["gi"] = new() { BackgroundPath = @"C:\Nyx\official.png" },
            },
        };
        var settings = Slice(ReadAppFile("MainPage.xaml.cs"), "public async Task ShowSettingsAsync", "private async Task ShowAddGameDialogAsync");

        Assert.Equal(saved, LauncherBackgroundSourceProjection.From(state, id));
        Assert.Null(LauncherBackgroundSourceProjection.From(state with
        {
            Appearance = new Dictionary<string, GameAppearanceState>(StringComparer.Ordinal)
            {
                [id] = new(),
            },
        }, id));
        Assert.Equal(legacy, LauncherBackgroundSourceProjection.From(state with
        {
            Appearance = new Dictionary<string, GameAppearanceState>(StringComparer.Ordinal),
        }, id));
        Assert.Null(LauncherBackgroundSourceProjection.From(state, "custom-unknown"));
        Assert.Null(LauncherBackgroundSourceProjection.From(state, "gi"));
        Assert.Contains("var savedBackground = LauncherBackgroundSourceProjection.From(before, selected.Id);", settings, StringComparison.Ordinal);
        Assert.Contains("BackgroundPath = savedBackground", settings, StringComparison.Ordinal);
        Assert.Contains("Text = savedBackground ?? string.Empty", settings, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(storedBackground, savedBackground, StringComparison.OrdinalIgnoreCase)", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("savedAppearance.BackgroundPath", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_backgrounds_preload_and_do_not_reset_during_game_switches()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var start = code.IndexOf("private void ApplySelectedAppearance", StringComparison.Ordinal);
        var end = code.IndexOf("private void StartLauncherVisualPreload", start, StringComparison.Ordinal);
        var selectionHandler = code[start..end];
        var imageLoader = Slice(code, "private void PrepareLauncherImageBackground", "private void BeginLauncherBackgroundCrossfade");
        var mediaFailure = Slice(code, "private void LauncherMotionPlayer_MediaFailed", "private void HideLauncherMotionBackgrounds");

        Assert.Contains("RefreshAllAsync", code, StringComparison.Ordinal);
        Assert.Contains("\"wuwa\"", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("if (isOfficial && launcherVisualRequestedGameId == gameId) return;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("PrepareLauncherImageBackground(fallback, generation", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherMotionBackground.Source = null", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBackgroundSource(\"ms-appx:///Assets/backgroundnyx.png\")", selectionHandler[..selectionHandler.IndexOf("HideLauncherMotionBackgrounds", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain("backgroundnyx.png", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LauncherMotionBackgroundNext\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BackgroundArtworkNext\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Random.Shared.Next(selection.Files.Count)", code, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(700)", code, StringComparison.Ordinal);
        Assert.Contains("BeginLauncherBackgroundCrossfade", code, StringComparison.Ordinal);
        Assert.Contains("selection.Files.Count > 1", code, StringComparison.Ordinal);
        Assert.Contains("PrepareLauncherImageBackground(", mediaFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBackgroundSource(selection.Files[1])", mediaFailure, StringComparison.Ordinal);
        Assert.Contains("MediaFailed += LauncherMotionPlayer_MediaFailed", code, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(imageLoader, @"requestToken != launcherImageRequestToken \|\| generation != launcherVisualGeneration").Count);
        Assert.Matches(@"bitmap\.ImageFailed[\s\S]*?requestToken != launcherImageRequestToken \|\| generation != launcherVisualGeneration\) return;\s*incoming\.Source = null;", imageLoader);
        Assert.DoesNotContain("x:Name=\"BackgroundScrim\"", xaml, StringComparison.Ordinal);
        Assert.True(Regex.Matches(xaml, "Background=\"{ThemeResource LauncherInfoSurfaceBrush}\"").Count >= 3);
    }

    [Fact]
    public void Official_switch_keeps_the_previous_layer_until_cached_or_preloaded_art_is_ready()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var selectionHandler = Slice(code, "private void ApplySelectedAppearance", "private void StartLauncherVisualPreload");
        var retire = selectionHandler.IndexOf("HideLauncherMotionBackgrounds();", StringComparison.Ordinal);
        var preload = selectionHandler.IndexOf("preloadedLauncherVisuals.TryGetValue", StringComparison.Ordinal);
        var cached = selectionHandler.IndexOf("launcherVisuals.TryLoadLastGood", StringComparison.Ordinal);

        Assert.True(retire >= 0 && preload >= 0 && cached >= 0 && preload < retire && cached < retire);
        Assert.Contains("launcherImageRequestToken++;", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundArtwork.Source = null;", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundArtworkNext.Source = null;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("PrepareLauncherImageBackground(fallback, generation", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("if (!hasVisibleBackground && selection.Files.Count > 1)", code, StringComparison.Ordinal);
        Assert.Contains("requestToken != launcherImageRequestToken || generation != launcherVisualGeneration", code, StringComparison.Ordinal);
        Assert.Contains("if (!launcherMotionPaused) player.Play();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_visual_preload_applies_only_through_the_current_page_lease()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var preload = Slice(code, "private void StartLauncherVisualPreload", "private void ApplyLauncherVisual");
        var unload = Slice(code, "private void MainPage_Unloaded", "private HoyoMaintenanceUiSnapshot");

        Assert.Contains("StartLauncherVisualPreload(SessionUiLease lease)", preload, StringComparison.Ordinal);
        Assert.Contains("lease.CancellationToken", preload, StringComparison.Ordinal);
        Assert.Contains("sessionUiLifetime.TryRun(lease", preload, StringComparison.Ordinal);
        Assert.True(
            preload.IndexOf("sessionUiLifetime.TryRun(lease", StringComparison.Ordinal)
            < preload.IndexOf("preloadedLauncherVisuals[selection.GameId] = selection", StringComparison.Ordinal));
        Assert.DoesNotContain("StartLauncherVisualPreload(lease.CancellationToken)", code, StringComparison.Ordinal);
        Assert.Contains("launcherVisualGeneration++;", unload, StringComparison.Ordinal);
        Assert.Contains("launcherBackgroundCrossfade?.Stop();", unload, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_caption_controls_are_fixed_visible_and_do_not_expose_maximize()
    {
        var code = ReadAppFile("MainWindow.xaml.cs");

        var xaml = ReadAppFile("MainWindow.xaml");
        Assert.DoesNotContain("SetBorderAndTitleBar", code, StringComparison.Ordinal);
        Assert.Contains("WindowStyleCaption = 0x00C00000", code, StringComparison.Ordinal);
        Assert.Contains("WindowStyleThickFrame = 0x00040000", code, StringComparison.Ordinal);
        Assert.Contains("SetWindowLongW", code, StringComparison.Ordinal);
        Assert.Contains("SetWindowPositionFrameChanged = 0x0020", code, StringComparison.Ordinal);
        Assert.Contains("Activated += (_, _) => RemoveSystemFrame()", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimizeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseButton\"", xaml, StringComparison.Ordinal);
        foreach (var buttonName in new[] { "SettingsButton", "MinimizeButton", "CloseButton" })
        {
            var button = SliceElement(xaml, $"x:Name=\"{buttonName}\"");
            Assert.Contains("Width=\"42\"", button, StringComparison.Ordinal);
            Assert.Contains("Height=\"36\"", button, StringComparison.Ordinal);
            Assert.Contains("MinWidth=\"42\"", button, StringComparison.Ordinal);
            Assert.Contains("MinHeight=\"36\"", button, StringComparison.Ordinal);
        }
        Assert.Contains("FontSize=\"22\"", SliceElement(xaml, "x:Name=\"SettingsIcon\""), StringComparison.Ordinal);
        Assert.Contains("FontSize=\"20\"", SliceElement(xaml, "x:Name=\"MinimizeIcon\""), StringComparison.Ordinal);
        Assert.Contains("FontSize=\"20\"", SliceElement(xaml, "x:Name=\"CloseIcon\""), StringComparison.Ordinal);
        Assert.DoesNotContain("MaximizeButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"White\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Color.FromArgb", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonForegroundColor", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonHoverBackgroundColor", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonPressedBackgroundColor", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_caption_drag_owns_the_drag_region_without_a_manual_pointer_handler()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var code = ReadAppFile("MainWindow.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");
        var app = ReadAppFile("App.xaml.cs");

        Assert.Contains("x:Name=\"DragRegion\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed=\"DragRegion_PointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DragRegion_PointerPressed", code, StringComparison.Ordinal);
        Assert.Contains("SetTitleBar(DragRegion)", code, StringComparison.Ordinal);
        Assert.Contains("ReleaseCapture();", code, StringComparison.Ordinal);
        Assert.Contains("SendMessage(", code, StringComparison.Ordinal);
        Assert.Contains("settingsTitle.PointerPressed", page, StringComparison.Ordinal);
        Assert.Contains("currentApp.BeginWindowDrag()", page, StringComparison.Ordinal);
        Assert.Contains("if (_window is MainWindow mainWindow) mainWindow.BeginDrag()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Animation_control_exposes_truthful_pause_resume_state()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var code = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("AutomationProperties.Name=\"Pause background animation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Resume background animation", code, StringComparison.Ordinal);
        Assert.Contains("Pause background animation", code, StringComparison.Ordinal);
        Assert.Contains("AnimationIcon.Glyph = paused ? \"\\uE768\" : \"\\uE769\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Animation_pause_guards_media_and_gallery_selection_paths()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        Assert.Contains("private bool launcherMotionPaused;", code, StringComparison.Ordinal);
        Assert.Contains("LauncherMotionBackground.MediaPlayer?.Pause();", code, StringComparison.Ordinal);
        Assert.Contains("LauncherMotionBackgroundNext.MediaPlayer?.Pause();", code, StringComparison.Ordinal);
        Assert.Contains("if (!launcherMotionPaused) player.Play();", code, StringComparison.Ordinal);
        Assert.Contains("if (!launcherMotionPaused && selection.Kind == \"gallery\"", code, StringComparison.Ordinal);
        Assert.Contains("if (launcherMotionPaused)", code, StringComparison.Ordinal);
        Assert.Contains("launcherGalleryTimer.Stop();", code, StringComparison.Ordinal);
        Assert.Contains("motion.MediaPlayer?.Play();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Borderless_client_size_is_dpi_aware_and_verified_after_resize()
    {
        var code = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("GetDpiForWindow", code, StringComparison.Ordinal);
        Assert.Contains("DesignDpi = 96", code, StringComparison.Ordinal);
        Assert.Contains("CalculateClientSizeForDpi", code, StringComparison.Ordinal);
        Assert.Contains("Math.Round(logicalPixels * (dpi == 0 ? DesignDpi : dpi)", code, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Resize(target)", code, StringComparison.Ordinal);
        Assert.Contains("AppWindow.ClientSize", code, StringComparison.Ordinal);
        Assert.Contains("target.Width - actual.Width", code, StringComparison.Ordinal);
        Assert.Contains("presenter.IsResizable = false", code, StringComparison.Ordinal);
        Assert.Contains("presenter.IsMaximizable = false", code, StringComparison.Ordinal);
        Assert.Contains("Width=\"1280\"", ReadAppFile("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("Height=\"720\"", ReadAppFile("MainWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void High_contrast_early_return_preserves_shared_programmatic_surface_resources()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("ApplyNyxAccentResources(content.Resources)", code, StringComparison.Ordinal);
        Assert.Contains("ApplyNyxAccentResources(dialog.Resources)", code, StringComparison.Ordinal);
        Assert.Contains("HighContrastBackdropOpacity", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_validate_manual_roots_parse_arguments_and_warn_about_saved_login()
    {
        var settings = Slice(
            ReadAppFile("MainPage.xaml.cs"),
            "public async Task ShowSettingsAsync",
            "private async Task ShowAddGameDialogAsync");

        Assert.Contains("IsValidManualInstallRoot(selected.Id, folder.Path)", settings, StringComparison.Ordinal);
        Assert.Contains("CustomArgumentParser.TryParse(officialLaunchArguments.Text", settings, StringComparison.Ordinal);
        Assert.Contains("Keeps your publisher login saved on this PC. Turning it off removes saved passwords.", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_keeps_nyx_visible_and_retains_the_normal_titlebar_minimize_button()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var app = ReadAppFile("App.xaml.cs");
        var window = ReadAppFile("MainWindow.xaml.cs");
        Assert.DoesNotContain("launchMinimize", page + app + window, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MinimizeWindowForGameLaunch", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreWindowIfMinimized", app, StringComparison.Ordinal);
        Assert.Contains("Click=\"MinimizeButton_Click\"", ReadAppFile("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("internal void Minimize()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Five_long_codes_keep_exact_copy_text_when_narrow_metadata_is_hidden()
    {
        var codes = new[]
        {
            "GENSHIN-PRIMOGEMS-2026-LONG",
            "STARRAIL_JADE_REDEMPTION_2026",
            "ZZZ-POLYCHROME-LONG-CODE-2026",
            "WUWA_ASTRITE_REDEMPTION_LONG",
            "ENDFIELD-OROBERYL-LONG-2026",
        };
        foreach (var code in codes)
        {
            Assert.True(code.Length > 16);
            Assert.DoesNotContain(' ', code);
        }

        var xaml = ReadAppFile("MainPage.xaml");
        var appCode = ReadAppFile("MainPage.xaml.cs");
        var codeText = SliceElement(xaml, "Text=\"{Binding Code}\"");
        Assert.DoesNotContain("TextTrimming", codeText, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding Code}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding CopyVisibility}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CopyVisibility => IsCopyable ? Visibility.Visible : Visibility.Collapsed", appCode, StringComparison.Ordinal);
        Assert.Contains("row.SetMetadataVisibility(!compactCodeRows)", appCode, StringComparison.Ordinal);
        Assert.Contains("CurrencyVisibility = currencyNext", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_schedule_disallows_external_navigation()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var start = xaml.IndexOf("x:Name=\"BannerCycleRegion\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("x:Name=\"LowerActionRegion\"", start, StringComparison.Ordinal);
        var strip = xaml[start..end];

        Assert.DoesNotContain("Hyperlink", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", strip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", strip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_animation_unsubscribes_and_stops_when_paused()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("CompositionTarget.Rendering -= LaunchAnimation_Rendering", code, StringComparison.Ordinal);
        Assert.Contains("StopAmbientAnimations();", Slice(code, "internal bool ToggleLauncherAnimation", "public MainPage()"), StringComparison.Ordinal);
    }

    [Fact]
    public void Brand_confirmation_targets_only_fixed_pengo_gg()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("Title = \"Open pengo.gg?\"", code, StringComparison.Ordinal);
        Assert.Contains("new Uri(\"https://pengo.gg\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Iris_assets_are_local_nonempty_and_project_packaged()
    {
        var project = ReadAppFile("Nyx.Desktop.App.csproj");
        var irisDirectory = Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "Assets",
            "Iris");
        var required = new[]
        {
            "nyx-eye-fill.png",
            "nyx-logo.png",
        };
        var obsolete = new[]
        {
            "gi-hero.png",
            "hsr-hero.png",
            "zzz-hero.png",
            "wuwa-hero.png",
            "ae-hero.png",
        };

        Assert.Contains("Assets\\Iris\\**\\*", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Iris\\**\\*\" CopyToOutputDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Catalog\\**\\*\" CopyToOutputDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Assets\\backgroundnyx.png", project, StringComparison.Ordinal);
        Assert.Contains("<Link>Assets\\Brand\\kofi-logo.png</Link>", project, StringComparison.Ordinal);
        Assert.Contains("<Link>Assets\\Brand\\eye_ball.png</Link>", project, StringComparison.Ordinal);
        Assert.Contains("<Link>Assets\\Brand\\eye_lid.png</Link>", project, StringComparison.Ordinal);
        Assert.Contains("<Link>Assets\\Brand\\eye_drips.png</Link>", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", project, StringComparison.Ordinal);
        Assert.All(required, fileName =>
        {
            var file = new FileInfo(Path.Combine(irisDirectory, fileName));
            Assert.True(file.Exists);
            Assert.True(file.Length > 1024);
        });
        Assert.All(obsolete, fileName => Assert.False(File.Exists(Path.Combine(irisDirectory, fileName))));
    }

    [Fact]
    public void Direct_distribution_is_explicitly_unpackaged_and_self_contained()
    {
        var project = ReadAppFile("Nyx.Desktop.App.csproj");

        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSdkUndockedRegFreeWinRTInitialize>true</WindowsAppSdkUndockedRegFreeWinRTInitialize>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<EnableMsixTooling>false</EnableMsixTooling>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed>False</PublishTrimmed>", project, StringComparison.Ordinal);
        Assert.Contains("Name=\"CopyApplicationPriToPublishDirectory\"", project, StringComparison.Ordinal);
        Assert.Contains("$(TargetDir)$(AssemblyName).pri", project, StringComparison.Ordinal);
        Assert.Contains("DestinationFolder=\"$(PublishDir)\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_splash_art_is_removed_without_touching_saved_state()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        foreach (var symbol in new[]
                 {
                     "HeroStage",
                     "HeroArtwork",
                     "SetHeroSource",
                     "HeroArtPlacementSolver",
                     "PinUserArt",
                     "TryResolveUserArt",
                 })
        {
            Assert.DoesNotContain(symbol, xaml + code, StringComparison.Ordinal);
        }
        Assert.Contains("var openedAppearance = savedAppearance with", code, StringComparison.Ordinal);
        Assert.Contains("Appearance = savedAppearance with", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Character splash art is no longer used.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Freeze this artwork", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TRY ANOTHER", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrent_custom_executable_conflicts_are_reported_and_add_cleanup_runs()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var controller = ReadAppFile("LauncherStateController.cs");

        Assert.Contains("out var settingsFailure", page, StringComparison.Ordinal);
        Assert.Contains("out var addFailure", page, StringComparison.Ordinal);
        Assert.True(
            page.Split("That executable is already in your game rail.", StringSplitOptions.None).Length - 1 >= 2,
            "Both locked Settings and Add Game conflicts must show the duplicate message.");
        Assert.Contains("sessions.TryRemoveCustomAdapter(game.Id);", page, StringComparison.Ordinal);
        Assert.Contains("catch (CustomGameExecutableConflictException)", controller, StringComparison.Ordinal);
        Assert.Contains("LauncherStateUpdateFailure.CustomGameExecutableConflict", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Rail_reorder_and_settings_switching_restore_selection_with_accessible_game_names()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var settings = Slice(code, "public async Task ShowSettingsAsync", "private async Task ShowAddGameDialogAsync");
        var railSwitch = Slice(settings, "settingsGameRail.SelectionChanged", "var resetOrderConfirmationArmed");

        Assert.Contains("CanReorderItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DragItemsCompleted=\"GameSelector_DragItemsCompleted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem = selected", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Save and switch\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Don't save and switch\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Stay here\"", settings, StringComparison.Ordinal);
        Assert.Contains("await SaveCurrentSettingsAsync()", railSwitch, StringComparison.Ordinal);
        Assert.Contains("LoadSettingsGame(target, tabs.SelectedIndex)", railSwitch, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedAppearance(selected.Id)", railSwitch, StringComparison.Ordinal);
        Assert.Contains("RestoreSettingsRailSelection()", railSwitch, StringComparison.Ordinal);
        Assert.Contains("\"gi\" => \"Genshin Game Icon\"", code, StringComparison.Ordinal);
        Assert.Contains("selected.Id is \"gi\" or \"hsr\" or \"zzz\" or \"wuwa\" or \"ae\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Official_settings_persist_panel_visibility_without_persisting_session_collapse()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var settings = Slice(code, "public async Task ShowSettingsAsync", "private async Task ShowAddGameDialogAsync");
        var render = Slice(code, "private void ApplySavedPanelVisibility", "private void SyncRedesignedControls");
        var refresh = Slice(code, "private async Task RefreshPublisherResourceAutomaticallyAsync", "private async Task RefreshPublisherResourcesOnStartupAsync");

        Assert.Contains("Header = \"Show Banners\"", settings, StringComparison.Ordinal);
        Assert.Contains("Header = \"Show Redemption Codes\"", settings, StringComparison.Ordinal);
        Assert.Contains("Header = \"Show Account & Export\"", settings, StringComparison.Ordinal);
        Assert.Contains("before.Preferences.VisibilityFor(selected.Id)", settings, StringComparison.Ordinal);
        Assert.Contains("OpenedPanelVisibility = selected.IsCustom ? null : openedPanelVisibility", settings, StringComparison.Ordinal);
        Assert.Contains("PanelVisibility = selected.IsCustom", settings, StringComparison.Ordinal);
        Assert.Contains("new LauncherPanelVisibility", settings, StringComparison.Ordinal);
        Assert.Contains("officialAppearanceOptions.Visibility = selected.IsCustom", settings, StringComparison.Ordinal);
        Assert.Contains("BannerCycleRegion.Visibility = !selected.IsCustom && visibility.ShowBanners", render, StringComparison.Ordinal);
        Assert.Contains("CombinedStatusPanel.Visibility = !selected.IsCustom && visibility.ShowRedemptionCodes", render, StringComparison.Ordinal);
        Assert.Contains("AccountAndToolsPanel.Visibility = !selected.IsCustom && visibility.ShowAccountAndExport", render, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleColumns.Visibility", render, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalPanel.Visibility", render, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountSectionContent.Visibility", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility", refresh, StringComparison.Ordinal);
    }

    private static void AssertRawElement(string xaml, string elementName)
    {
        var start = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {elementName}.");
        var end = xaml.IndexOf('>', start);
        Assert.True(end > start, $"Could not read {elementName}.");
        Assert.Contains(
            "AutomationProperties.AccessibilityView=\"Raw\"",
            xaml[start..end],
            StringComparison.Ordinal);
    }

    private static string SliceElement(string xaml, string marker)
    {
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {marker}.");
        var end = xaml.IndexOf('>', start);
        Assert.True(end > start, $"Could not read {marker}.");
        return xaml[start..end];
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return text[start..end];
    }

    private static string ReadAppFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine(
            [
                WorkspaceRoot,
                "Desktop",
                "src",
                "Nyx.Desktop.App",
                .. relativeSegments,
            ]));

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
