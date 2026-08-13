using System.Text.RegularExpressions;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.State;
using Nyx_Desktop_App.ViewModels;

namespace Nyx.Desktop.Tests.UI;

public sealed class IrisLauncherShellTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Shipping_window_is_fixed_at_1280_by_720_and_main_content_never_scrolls()
    {
        var windowXaml = ReadAppFile("MainWindow.xaml");
        var window = ReadAppFile("MainWindow.xaml.cs");
        var page = ReadAppFile("MainPage.xaml");

        Assert.Contains("<Viewbox", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Stretch=\"Uniform\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("<Grid Background=\"#05030B\">", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FixedDesignSurface\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"1280\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"720\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("new(1280, 720)", window, StringComparison.Ordinal);
        Assert.Contains("presenter.IsResizable = false", window, StringComparison.Ordinal);
        Assert.Contains("presenter.IsMaximizable = false", window, StringComparison.Ordinal);
        Assert.Contains("ExtendsContentIntoTitleBar = true", window, StringComparison.Ordinal);
        Assert.Contains("SetTitleBar(DragRegion)", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerContentRegion\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_contract_has_one_fixed_design_profile()
    {
        var source = ReadAppFile("ViewModels", "LauncherLayoutState.cs");

        var profile = LauncherLayoutProfile.Fixed;
        Assert.Equal(1280, LauncherLayoutProfile.DesignWidth);
        Assert.Equal(720, LauncherLayoutProfile.DesignHeight);
        Assert.Equal(102, profile.RailExtent);
        Assert.Equal(82, profile.IconSize);
        Assert.Equal(620, profile.ContentWidth);
        Assert.Equal(172, profile.DeckHeight);
        Assert.Equal(405, profile.LaunchWidth);
        Assert.True(profile.ItemCrossExtent <= profile.RailExtent);
        Assert.DoesNotContain("LauncherLayoutState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherViewportGeometry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherDeckLayoutMode", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_icon_size_drives_item_image_and_focus_geometry()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var layout = ReadAppFile("ViewModels", "LauncherLayoutState.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");

        Assert.Contains("game.ApplyLayout(profile)", code, StringComparison.Ordinal);
        Assert.Contains("iconSize = profile.IconSize", code, StringComparison.Ordinal);
        Assert.Contains("itemExtent = profile.ItemExtent", code, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding IconSize}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding ItemExtent}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemCrossExtent => ItemExtent + (ItemMargin * 2)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"Width\" Value=\"112\"", controls, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Stretch", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionMarker", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionAura", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void Five_item_rail_has_bounded_cross_axis_and_scrollable_main_axis()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.DoesNotContain("-hero.png", code, StringComparison.Ordinal);
        Assert.Contains("itemsPanel.Orientation = Orientation.Vertical", code, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.SetHorizontalScrollMode(GameSelector, ScrollMode.Disabled)", code, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.SetVerticalScrollMode(GameSelector, ScrollMode.Enabled)", code, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Hidden\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RailContentRow\" Height=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RailContentRow.Height = GridLength.Auto", code, StringComparison.Ordinal);
        Assert.Contains("RailSpacerRow.Height = new GridLength(1, GridUnitType.Star)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("RailSpacerRow.Height = new GridLength(0)", code, StringComparison.Ordinal);
        Assert.Contains("GameSelector.MaxHeight = profile.ItemExtent * 5", code, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.08\"", SliceElement(xaml, "x:Name=\"AddGameButton\""), StringComparison.Ordinal);
        Assert.Contains("AddGameButton.Opacity = 0.9", code, StringComparison.Ordinal);
        Assert.Contains("AddGameButton.Opacity = 0.08", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_has_one_launch_action_and_one_anchored_lower_action_region()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var combined = xaml + code;

        Assert.Single(Regex.Matches(xaml, "Click=\"LaunchButton_Click\"").Cast<Match>());
        Assert.Single(Regex.Matches(xaml, "x:Name=\"LaunchButton\"").Cast<Match>());
        Assert.DoesNotContain("CompactLaunch", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WideLaunch", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Genshin first", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GAME 01 / 05", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"GAME {gameIndex:00} / {Games.Count:00}\"", combined, StringComparison.Ordinal);
        Assert.Contains("PullExportToggle.IsEnabled = pullsAvailable", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL LIBRARY", combined, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(xaml, "x:Name=\"LowerActionRegion\"").Cast<Match>());
        Assert.Contains("ApplyLowerActionLayout(profile)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceDeckItem", code, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_capsule_centers_the_title_and_keeps_status_and_utilities_integrated()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var layout = ReadAppFile("ViewModels", "LauncherLayoutState.cs");
        var launchTitle = SliceElement(xaml, "x:Name=\"LaunchTitle\"");
        var launchVisual = Slice(xaml, "x:Name=\"LaunchButtonVisual\"", "x:Name=\"LaunchUtilityButtons\"");
        var innerFrame = Slice(xaml, "x:Name=\"LaunchInnerFrame\"", "x:Name=\"LaunchInnerHighlight\"");
        var utilities = Slice(xaml, "x:Name=\"LaunchUtilityButtons\"", "x:Name=\"StableOpenUpdaterButton\"");

        Assert.Contains(
            "x:Name=\"LaunchTitle\"",
            launchVisual,
            StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", launchTitle, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", launchTitle, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"Normal\"", launchTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Row=", launchTitle, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchDetail\"", innerFrame, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", innerFrame, StringComparison.Ordinal);
        Assert.Contains("Height=\"18\"", innerFrame, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"2\"", SliceElement(xaml, "x:Name=\"LaunchButton\""), StringComparison.Ordinal);
        Assert.Contains("Margin=\"12,0,12,0\"", SliceElement(xaml, "x:Name=\"LaunchButton\""), StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"3\"", SliceElement(xaml, "x:Name=\"LaunchUtilityButtons\""), StringComparison.Ordinal);
        Assert.Contains("Margin=\"12,0,12,0\"", SliceElement(xaml, "x:Name=\"LaunchUtilityButtons\""), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(utilities, "<ColumnDefinition Width=\"\\*\" />").Count);
        Assert.Contains("Grid.Column=\"0\"", SliceElement(xaml, "x:Name=\"StableOpenUpdaterButton\""), StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", SliceElement(xaml, "x:Name=\"StableOpenScreenshotFolderButton\""), StringComparison.Ordinal);
        Assert.Contains("LaunchUtilityButtons.Width = LaunchButton.Width", code, StringComparison.Ordinal);
        Assert.Contains("LaunchButtonHeight = 110", layout, StringComparison.Ordinal);
        Assert.Contains("LaunchStatusStripHeight = 18", layout, StringComparison.Ordinal);

        foreach (var scale in new[] { 1d, 1.25d })
        {
            var lowerRegionDip = 378d * scale;
            var launchDip = LauncherOpenLayoutGeometry.LaunchButtonHeight * scale;
            Assert.True(launchDip <= lowerRegionDip);
        }
    }

    [Fact]
    public void Account_and_export_controls_use_the_compact_approved_hierarchy()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var sync = Slice(
            code,
            "private void SyncRedesignedControls",
            "private void RenderHoyoLabAccountIdentity");
        var onLaunchContent = Slice(xaml, "x:Name=\"OnLaunchHeading\"", "x:Name=\"StableExportHeading\"");
        var sourceOptions = SliceElement(xaml, "x:Name=\"AchievementSourceOptionsPanel\"");
        var helpStyle = Slice(controls, "x:Key=\"NyxHelpButtonStyle\"", "x:Key=\"NyxSettingsDialogPrimaryStyle\"");

        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            SliceElement(xaml, "x:Name=\"LaunchDetail\""),
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Fps120Toggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"ON LAUNCH\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowSpacing=\"3\"", SliceElement(xaml, "x:Name=\"AccountAndToolsPanel\""), StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,8,0,0\"", SliceElement(xaml, "x:Name=\"CombinedStatusPanel\""), StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,8,0,0\"", SliceElement(xaml, "x:Name=\"AccountAndToolsPanel\""), StringComparison.Ordinal);
        Assert.Contains("Margin=\"12,2,12,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Accounts\"", SliceElement(xaml, "x:Name=\"ChangePublisherAccountButton\""), StringComparison.Ordinal);
        Assert.Contains("ChangePublisherAccountButton.Content = \"Accounts\"", code, StringComparison.Ordinal);
        Assert.Contains("ChangePublisherAccountButton.Visibility = Visibility.Visible", code, StringComparison.Ordinal);
        Assert.Contains("selected.Id == \"ae\"", Slice(code, "private async void ChangePublisherAccountButton_Click", "private async Task SetPublisherConsentAsync"), StringComparison.Ordinal);
        Assert.Contains("await ConnectPublisherAccountAsync(selected.Id)", code, StringComparison.Ordinal);
        Assert.Contains("publisherAccounts.EndfieldIdentity?.DisplayText", code, StringComparison.Ordinal);
        Assert.Contains("selected.Id == \"ae\"", Slice(code, "ChangePublisherAccountButton.IsEnabled", "var resource ="), StringComparison.Ordinal);
        Assert.DoesNotContain("selected.Id is \"wuwa\" or \"ae\"", code, StringComparison.Ordinal);
        Assert.Contains("Content=\"Daily Web check-in\"", SliceElement(xaml, "x:Name=\"AutomaticDailyCheckInToggle\""), StringComparison.Ordinal);
        Assert.Contains("Content=\"Set 120 FPS\"", SliceElement(xaml, "x:Name=\"Fps120Toggle\""), StringComparison.Ordinal);
        Assert.DoesNotContain("AccountAndToolsFreshnessText", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountResourcesPrefix", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("GameToolsHeading", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GameToolsDivider", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"0\"", SliceElement(xaml, "x:Name=\"StableOpenUpdaterButton\""), StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", SliceElement(xaml, "x:Name=\"StableOpenScreenshotFolderButton\""), StringComparison.Ordinal);
        Assert.Contains("Height=\"28\"", SliceElement(xaml, "x:Name=\"StableOpenUpdaterButton\""), StringComparison.Ordinal);
        Assert.Contains("Height=\"28\"", SliceElement(xaml, "x:Name=\"StableOpenScreenshotFolderButton\""), StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", SliceElement(xaml, "x:Name=\"StableOpenUpdaterButton\""), StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", SliceElement(xaml, "x:Name=\"StableOpenScreenshotFolderButton\""), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutomaticDailyCheckInToggle\"", onLaunchContent, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Fps120Toggle\"", onLaunchContent, StringComparison.Ordinal);
        Assert.Contains("CharacterSpacing=\"100\"", SliceElement(xaml, "x:Name=\"AccountAndToolsProviderText\""), StringComparison.Ordinal);
        Assert.Contains("FontSize=\"14\"", SliceElement(xaml, "x:Name=\"AccountAndToolsProviderText\""), StringComparison.Ordinal);
        foreach (var heading in new[] { "OnLaunchHeading", "StableExportHeading" })
        {
            Assert.Contains("CharacterSpacing=\"140\"", SliceElement(xaml, $"x:Name=\"{heading}\""), StringComparison.Ordinal);
            Assert.Contains("FontSize=\"10\"", SliceElement(xaml, $"x:Name=\"{heading}\""), StringComparison.Ordinal);
        }
        foreach (var divider in new[] { "OnLaunchDivider", "ExportDivider" })
        {
            Assert.Contains("Height=\"1\"", SliceElement(xaml, $"x:Name=\"{divider}\""), StringComparison.Ordinal);
            Assert.Contains("Background=\"{ThemeResource HairlineBrush}\"", SliceElement(xaml, $"x:Name=\"{divider}\""), StringComparison.Ordinal);
        }
        var exportStatus = SliceElement(xaml, "x:Name=\"StableExportStatusText\"");
        Assert.Contains("MaxLines=\"2\"", exportStatus, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", exportStatus, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", exportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("TextTrimming", exportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("OfficialLauncherStatusText", xaml, StringComparison.Ordinal);
        Assert.Contains("SetLaunchDetail(message)", code, StringComparison.Ordinal);
        Assert.Contains("SetLaunchDetail(officialLauncherStatus)", code, StringComparison.Ordinal);
        Assert.Contains("Height=\"28\"", SliceElement(xaml, "x:Name=\"Fps120Toggle\""), StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"28\"", SliceElement(xaml, "x:Name=\"Fps120Toggle\""), StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource NyxHelpButtonStyle}\"", SliceElement(xaml, "x:Name=\"AchievementExportHelpButton\""), StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource NyxHelpButtonStyle}\"", SliceElement(xaml, "x:Name=\"PullExportHelpButton\""), StringComparison.Ordinal);
        Assert.Contains("Property=\"Width\" Value=\"28\"", helpStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"Height\" Value=\"28\"", helpStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"CornerRadius\" Value=\"14\"", helpStyle, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", SliceElement(xaml, "x:Name=\"AchievementExportHelpButton\""), StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", SliceElement(xaml, "x:Name=\"PullExportHelpButton\""), StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", sourceOptions, StringComparison.Ordinal);
        Assert.Contains("Text=\"Export from:\"", SliceElement(xaml, "x:Name=\"AchievementSourcePrefix\""), StringComparison.Ordinal);
        Assert.Contains("Width=\"28\"", SliceElement(xaml, "x:Name=\"LaunchResourceRefreshButton\""), StringComparison.Ordinal);
        Assert.Contains("Height=\"28\"", SliceElement(xaml, "x:Name=\"LaunchResourceRefreshButton\""), StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"Refresh account resources\"", SliceElement(xaml, "x:Name=\"LaunchResourceRefreshButton\""), StringComparison.Ordinal);
        Assert.Contains("Genshin Impact and Star Rail use separate saved settings", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StablePullExportToggle, pullAccessibilityName)", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StableAchievementExportToggle, achievementAccessibilityName)", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StableExportStatusText, StableExportStatusText.Text)", code, StringComparison.Ordinal);
        Assert.Contains("SetStableExportStatus(NyxToolsStatusText.Text)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveScreenshotFolder", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckGame", sync, StringComparison.Ordinal);
        Assert.Contains(
            "selected.Id is \"gi\" or \"hsr\" or \"zzz\" or \"wuwa\"",
            sync,
            StringComparison.Ordinal);
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
        var handler = Slice(code, "private void SectionCollapseButton_Click", "private void AchievementSource_Click");

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
        Assert.Contains("BannerCycleRegion.Height = expanded ? 292 : double.NaN", handler, StringComparison.Ordinal);
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
    public void Fixed_viewport_reserves_content_clearance_above_the_lower_action_region()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("x:Name=\"LowerActionRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LowerActionGrid\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DeckRow", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DeckColumn", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleStack\"", xaml, StringComparison.Ordinal);
        var bannerRegion = SliceElement(xaml, "x:Name=\"BannerCycleRegion\"");
        Assert.Contains("Width=\"560\"", bannerRegion, StringComparison.Ordinal);
        Assert.Contains("Height=\"292\"", bannerRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleRegion.MinHeight", code, StringComparison.Ordinal);
        Assert.Contains("LowerActionRegion.Height + 12", code, StringComparison.Ordinal);
        Assert.Contains("DeckHeight: 172", ReadAppFile("ViewModels", "LauncherLayoutState.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("MainPage_SizeChanged", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeChanged += MainPage_SizeChanged", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_open_layout_aligns_codes_tools_and_launch_on_the_bottom_edge()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("CombinedStatusPanel.Height = double.NaN", code, StringComparison.Ordinal);
        Assert.Contains("CombinedStatusPanel.VerticalAlignment = SignalPanel.Visibility is Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("? VerticalAlignment.Top", code, StringComparison.Ordinal);
        Assert.Contains(": VerticalAlignment.Stretch", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CombinedStatusPanel\"", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""NyxToolsPanel""\s+Grid\.Column=""1""", xaml);
        Assert.Matches(@"x:Name=""LaunchStack""\s+Grid\.Column=""2""", xaml);
        Assert.Contains("LauncherOpenLayoutGeometry.LaunchButtonHeight", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullExportToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AchievementExportToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenUpdaterButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NyxToolsPanel.VerticalAlignment = VerticalAlignment.Bottom", code, StringComparison.Ordinal);
        Assert.Contains("LaunchStack.VerticalAlignment = VerticalAlignment.Bottom", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Obsolete_official_local_deck_is_removed_and_the_launcher_action_stays_with_export_tools()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("x:Name=\"CombinedStatusPanel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"UpdaterSignalLayout\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"OfficialStatusLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LocalStatusLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyMaintenanceLayout", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PrimaryGameStatusButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrimaryGameStatusProjector.Project", code, StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("x:Name=\"PengoToolButtons\"", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"OpenUpdaterButton\"", StringComparison.Ordinal));
        Assert.Contains("Content=\"Official Launcher\"", xaml, StringComparison.Ordinal);
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
    public void Responsibility_disclaimer_focus_and_high_contrast_are_persistent()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");

        Assert.DoesNotContain("Updates and repairs: official launcher.", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PrimaryGameStatusButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Fan-made launcher", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Not affiliated with HoYoverse, Kuro Games, or GRYPHLINK", xaml, StringComparison.Ordinal);
        Assert.Contains("UseSystemFocusVisuals\" Value=\"True", controls, StringComparison.Ordinal);
        Assert.Contains("FocusVisualPrimaryThickness\" Value=\"2", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrast\"", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleAnimation", xaml, StringComparison.Ordinal);
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

        Assert.Contains("RefreshAllAsync", code, StringComparison.Ordinal);
        Assert.Contains("\"wuwa\"", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("if (isOfficial && launcherVisualRequestedGameId == gameId) return;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("SetBackgroundSource(LauncherBackgroundSourceProjection.From(launcherState.Snapshot, gameId));", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherMotionBackground.Source = null", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBackgroundSource(\"ms-appx:///Assets/backgroundnyx.png\")", selectionHandler[..selectionHandler.IndexOf("HideLauncherMotionBackgrounds", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain("backgroundnyx.png", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LauncherMotionBackgroundNext\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BackgroundArtworkNext\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Random.Shared.Next(selection.Files.Count)", code, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(700)", code, StringComparison.Ordinal);
        Assert.Contains("BeginLauncherBackgroundCrossfade", code, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(imageLoader, @"requestToken != launcherImageRequestToken \|\| generation != launcherVisualGeneration").Count);
        Assert.Matches(@"bitmap\.ImageFailed[\s\S]*?requestToken != launcherImageRequestToken \|\| generation != launcherVisualGeneration\) return;\s*incoming\.Source = null;", imageLoader);
        Assert.DoesNotContain("x:Name=\"BackgroundScrim\"", xaml, StringComparison.Ordinal);
        Assert.True(Regex.Matches(xaml, "Background=\"{ThemeResource LauncherInfoSurfaceBrush}\"").Count >= 3);
    }

    [Fact]
    public void Official_switch_retires_previous_layers_before_using_cached_or_preloaded_art()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var selectionHandler = Slice(code, "private void ApplySelectedAppearance", "private void StartLauncherVisualPreload");
        var retire = selectionHandler.IndexOf("HideLauncherMotionBackgrounds();", StringComparison.Ordinal);
        var preload = selectionHandler.IndexOf("preloadedLauncherVisuals.TryGetValue", StringComparison.Ordinal);
        var cached = selectionHandler.IndexOf("launcherVisuals.TryLoadLastGood", StringComparison.Ordinal);

        Assert.True(retire >= 0 && retire < preload && retire < cached);
        Assert.Contains("launcherImageRequestToken++;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("BackgroundArtwork.Source = null;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("BackgroundArtworkNext.Source = null;", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("SetBackgroundSource(LauncherBackgroundSourceProjection.From(launcherState.Snapshot, gameId));", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("requestToken != launcherImageRequestToken || generation != launcherVisualGeneration", code, StringComparison.Ordinal);
        Assert.Contains("if (!launcherMotionPaused) player.Play();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_caption_controls_are_fixed_visible_and_do_not_expose_maximize()
    {
        var code = ReadAppFile("MainWindow.xaml.cs");

        var xaml = ReadAppFile("MainWindow.xaml");
        Assert.Contains("presenter.SetBorderAndTitleBar(false, false)", code, StringComparison.Ordinal);
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
    public void Animation_button_is_before_settings_and_exposes_truthful_pause_resume_state()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var code = ReadAppFile("MainWindow.xaml.cs");
        var animation = xaml.IndexOf("x:Name=\"AnimationButton\"", StringComparison.Ordinal);
        var settings = xaml.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);

        Assert.True(animation >= 0 && animation < settings);
        Assert.Contains("Click=\"AnimationButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Pause background animation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToggleLauncherAnimation()", code, StringComparison.Ordinal);
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
    public void Palette_contains_no_cyan_or_signal_teal_accent()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");
        var combined = xaml + code + controls + palette;

        Assert.DoesNotContain("#70D7D1", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SignalBrush", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("TealBrush", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NyxSignalColor", combined, StringComparison.Ordinal);
        Assert.Contains("ApplyNyxAccentResources(content.Resources)", code, StringComparison.Ordinal);
        Assert.Contains("ApplyNyxAccentResources(dialog.Resources)", code, StringComparison.Ordinal);
        Assert.Contains("\"ToggleSwitchFillOn\"", code, StringComparison.Ordinal);
        Assert.Contains("\"SliderTrackValueFill\"", code, StringComparison.Ordinal);
        Assert.Contains("\"AccentButtonBackground\"", code, StringComparison.Ordinal);
        Assert.Contains("HighContrastBackdropOpacity", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Nebula_shell_uses_full_bleed_launcher_background_and_an_open_command_area()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var combined = xaml + code;

        Assert.Contains("x:Name=\"RailSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource RailSurfaceBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BackgroundArtwork\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LauncherMotionBackground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerContentRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LowerActionRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", SliceElement(xaml, "x:Name=\"LowerActionRegion\""), StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0\"", SliceElement(xaml, "x:Name=\"LowerActionRegion\""), StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GlassDeckBrush\"", palette, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height\" Value=\"110", controls, StringComparison.Ordinal);
        Assert.Contains("BeginLauncherBackgroundCrossfade", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroStage", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroArtwork", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("IrisStage", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("IrisDecorativeContent", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionAura", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_hero_surface_has_no_remaining_viewmodel_or_rendering_hooks()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        Assert.DoesNotContain("HeroStage", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroArtwork", xaml, StringComparison.Ordinal);
        foreach (var symbol in new[]
                 {
                     "HeroArtPlacementSolver",
                     "SetHeroSource",
                     "ApplyHeroTransform",
                     "ApplySolvedHeroLayout",
                     "HeroArtFitGeometry",
                     "HeroStageGeometry",
                 })
        {
            Assert.DoesNotContain(symbol, code, StringComparison.Ordinal);
        }
        Assert.Contains("BackgroundArtwork", xaml, StringComparison.Ordinal);
        Assert.Contains("LauncherMotionBackground", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Mockup_does_not_create_dead_or_unavailable_controls()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var combined = xaml + code;

        Assert.Contains("Settings", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Add Game", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ko-fi", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x:Name=\"BannerCycleHeading\"", combined, StringComparison.Ordinal);
        Assert.Contains("Text=\"BANNERS\"", combined, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCollapseButton\"", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleVersion", combined, StringComparison.Ordinal);
        Assert.Contains("RedemptionCode_Click", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"", SliceElement(xaml, "x:Name=\"GameSelector\""), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_uses_wordmarks_open_status_order_and_matching_dialog_surfaces()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var window = ReadAppFile("MainWindow.xaml");
        var project = ReadAppFile("Nyx.Desktop.App.csproj");
        var palette = ReadAppFile("Themes", "NyxPalette.xaml");

        Assert.DoesNotContain("x:Name=\"GameLogo\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem.GameLogoPath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"HeroTitle\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"OFFICIAL\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"LOCAL\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeroDescription\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"↗\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"◇\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text = $\"Settings - {selected.DisplayName}\"", code, StringComparison.Ordinal);
        Assert.Contains("currentApp.BeginWindowDrag()", code, StringComparison.Ordinal);
        Assert.Contains("SettingsSurfaceBrush", code, StringComparison.Ordinal);
        Assert.Contains("SettingsSurfaceBrush", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"NYX\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RailBrandRow\" Height=\"104\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"KofiButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"10,0,10,6\"", SliceElement(xaml, "x:Name=\"KofiButton\""), StringComparison.Ordinal);
        Assert.Contains("GameSelector.VerticalAlignment = VerticalAlignment.Top", code, StringComparison.Ordinal);
        Assert.Contains("Height=\"36\"", SliceElement(window, "x:Name=\"AppTitleBar\""), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsButton\"", window, StringComparison.Ordinal);
        Assert.Matches(@"<Grid Width=""42"" Height=""36"">\s*<Button\s+x:Name=""SettingsButton""", window);
        Assert.Equal(3, Regex.Matches(
            window,
            @"<Button\s+x:Name=""(?:Settings|Minimize|Close)Button""\s+Width=""42""\s+Height=""36""\s+MinWidth=""42""\s+MinHeight=""36""").Count);
        Assert.Contains("x:Name=\"SettingsIcon\"", window, StringComparison.Ordinal);
        Assert.Matches(@"BannerContentRegion\.Margin = new Thickness\(\s*30,\s*38,", code);
        Assert.Contains("x:Key=\"LauncherInfoSurfaceBrush\"", palette, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource LauncherInfoSurfaceBrush}\"", SliceElement(xaml, "x:Name=\"BannerCycleRegion\""), StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", SliceElement(xaml, "x:Name=\"LowerActionRegion\""), StringComparison.Ordinal);
        Assert.Contains("Color=\"#C0100C1C\"", palette, StringComparison.Ordinal);
        Assert.Contains("Color=\"#C8100C1C\"", palette, StringComparison.Ordinal);
        Assert.Contains("PengoToolButtons.HorizontalAlignment = HorizontalAlignment.Stretch", code, StringComparison.Ordinal);
        Assert.Contains("LaunchButton.Height = LauncherOpenLayoutGeometry.LaunchButtonHeight", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Assets\\GameLogos\\**\\*", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_remove_dead_toggles_and_keep_folder_options_truthful()
    {
        var settings = Slice(
            ReadAppFile("MainPage.xaml.cs"),
            "public async Task ShowSettingsAsync",
            "private async Task ShowAddGameDialogAsync");

        Assert.DoesNotContain("StayVisibleAfterLaunch", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshContentOnStartup", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeNotifications", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Safe notifications", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Allow remote banner manifest refresh", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("keeps the last safe copy if refresh fails", settings, StringComparison.Ordinal);
        Assert.Contains("Game folder", settings, StringComparison.Ordinal);
        Assert.Contains("Override the automatic detection", settings, StringComparison.Ordinal);
        Assert.Contains("IsValidManualInstallRoot(selected.Id, folder.Path)", settings, StringComparison.Ordinal);
        Assert.Contains("CustomArgumentParser.TryParse(officialLaunchArguments.Text", settings, StringComparison.Ordinal);
        Assert.Contains("Header = \"Arguments\"", settings, StringComparison.Ordinal);
        Assert.Contains("Text = \"Launch Options\"", settings, StringComparison.Ordinal);
        Assert.Contains("Width = 28", settings, StringComparison.Ordinal);
        Assert.Contains("Style = (Style)Application.Current.Resources[\"NyxHelpButtonStyle\"]", settings, StringComparison.Ordinal);
        Assert.Contains("MinWidth = 0", settings, StringComparison.Ordinal);
        Assert.True(
            settings.IndexOf("officialLaunchArgumentsEnabled,", StringComparison.Ordinal)
            < settings.IndexOf("officialLaunchArgumentsHelp,", StringComparison.Ordinal));
        Assert.Contains("https://docs.unity3d.com/6000.5/Documentation/Manual/PlayerCommandLineArguments.html", settings, StringComparison.Ordinal);
        Assert.Contains("Header = \"Locally save browser login?\"", settings, StringComparison.Ordinal);
        Assert.Contains("Keeps your publisher login saved on this PC. Turning it off removes saved passwords.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("without a command shell", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp(ActualWidth - 32", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("1180", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("650", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_lower_action_region_has_one_bottom_aligned_row()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("x:Name=\"LowerActionGrid\" ColumnSpacing=\"16\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"378\"", SliceElement(xaml, "x:Name=\"LowerActionRegion\""), StringComparison.Ordinal);
        Assert.Contains("LowerActionRegion.Height = Math.Max(profile.DeckHeight, 378)", code, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"280\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("const double toolsWidth = 415d", code, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"405\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("LaunchButton.Width = Math.Max(0, profile.LaunchWidth - 24)", code, StringComparison.Ordinal);
        var launchStack = SliceElement(xaml, "x:Name=\"LaunchStack\"");
        Assert.Contains("VerticalAlignment=\"Bottom\"", launchStack, StringComparison.Ordinal);
        Assert.Contains("RowSpacing=\"0\"", launchStack, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LaunchBacking\"", xaml, StringComparison.Ordinal);
        var metricsStart = xaml.IndexOf("x:Name=\"LaunchResourceMetricsPanel\"", StringComparison.Ordinal);
        var metricsSurfaceStart = xaml.LastIndexOf("<Border", metricsStart, StringComparison.Ordinal);
        var metricsSurface = xaml[metricsSurfaceStart..metricsStart];
        Assert.Contains("Grid.Row=\"1\"", metricsSurface, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,2\"", metricsSurface, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"50\"", metricsSurface, StringComparison.Ordinal);
        var launchResources = SliceElement(xaml, "x:Name=\"LaunchResourceMetricsPanel\"");
        Assert.DoesNotContain("Grid.Row", launchResources, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=\"50\"", launchResources, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"", launchResources, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0\"", launchResources, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.RowSpan", launchResources, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"-12,-8\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"-12,-10\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DeckRow", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("DeckColumn", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceDeckItem", code, StringComparison.Ordinal);
        Assert.Contains("LaunchButton.Height = LauncherOpenLayoutGeometry.LaunchButtonHeight", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherViewportGeometry", code, StringComparison.Ordinal);
        Assert.DoesNotContain("horizontalDeck", code, StringComparison.Ordinal);
        Assert.DoesNotContain("compactCodeRows = true", code, StringComparison.Ordinal);
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
    public void Combined_codes_panel_uses_the_full_fixed_column()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("CombinedStatusGrid.ColumnSpacing = 16", code, StringComparison.Ordinal);
        Assert.Contains("ApplyCombinedStatusLayout();", code, StringComparison.Ordinal);
        Assert.Contains("CombinedBannerColumn.Width = new GridLength(1, GridUnitType.Star)", code, StringComparison.Ordinal);
        Assert.Contains("CombinedStatusPanel.Padding = new Thickness(16, 10, 12, 10)", code, StringComparison.Ordinal);
        Assert.Contains("CombinedStatusGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) })", code, StringComparison.Ordinal);
        Assert.Contains("PlaceGridItem(CodesHeaderDivider, 1, 0, 1, 1)", code, StringComparison.Ordinal);
        Assert.Contains("PlaceGridItem(SignalPanel, 2, 0, 1, 1)", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodesHeaderDivider\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdaterSignalRow", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_portraits_do_not_rotate_and_every_current_character_stays_highlighted()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var xaml = ReadAppFile("MainPage.xaml");

        Assert.DoesNotContain("bannerRotationTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerRotationSchedule", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerPanel_Pointer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCharacterRow_Click", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentBannerPortraitButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Artwork currently displayed", code, StringComparison.Ordinal);
        Assert.Contains("const bool isActive = true", code, StringComparison.Ordinal);
        Assert.Contains("existing.Update(portrait, timing, isActive, isPinned, 100)", code, StringComparison.Ordinal);
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
    public void Banner_schedule_uses_the_approved_two_column_layout_without_category_switching()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var start = xaml.IndexOf("x:Name=\"BannerCycleRegion\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("x:Name=\"LowerActionRegion\"", start, StringComparison.Ordinal);
        var strip = xaml[start..end];

        Assert.Contains("x:Name=\"BannerCycleRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleHeader\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleHeading\"", strip, StringComparison.Ordinal);
        Assert.Contains("Text=\"BANNERS\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleVersion", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCyclePhase", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCollapseButton\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleColumns\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CurrentBannerColumn\" Width=\"*\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingBannerColumn\" Width=\"*\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CurrentBannerSection\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingBannerList\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentBannerNameBacking", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("UpcomingBannerNameBacking", strip, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"20\"", SliceElement(strip, "x:Name=\"CurrentBannerPortraitBacking\""), StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"17\"", SliceElement(strip, "x:Name=\"UpcomingBannerPortraitBacking\""), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingBannerPhaseSlot\"", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingPhaseDivider\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentBannerTimingBacking", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("UpcomingBannerHeaderBacking", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("UpcomingBannerTimingBacking", strip, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCategoryTabs\"", strip, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", SliceElement(strip, "x:Name=\"BannerCategoryTabs\""), StringComparison.Ordinal);
        Assert.Contains("<StackPanel Orientation=\"Vertical\" />", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressBar", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("<Ellipse", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("MaximumRowsOrColumns=\"6\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("BANNERS · VERSION", strip, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource LauncherInfoSurfaceBrush}\"", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("CHANGES EVERY 7S", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("Hyperlink", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", strip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", strip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NextBannerCard", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_action_uses_the_supplied_native_starfield_and_shared_motion_pause()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var start = controls.IndexOf("x:Key=\"NyxLaunchButtonStyle\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var style = controls[start..];

        Assert.Contains("x:Name=\"LaunchStarCanvas\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchNebulaLeft\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchNebulaRight\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerEntered=\"LaunchButton_PointerEntered\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LaunchWatermark\"", xaml, StringComparison.Ordinal);
        Assert.Contains("const int starCount = 66", code, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering += LaunchAnimation_Rendering", code, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering -= LaunchAnimation_Rendering", code, StringComparison.Ordinal);
        Assert.Contains("StopAmbientAnimations();", Slice(code, "internal bool ToggleLauncherAnimation", "public MainPage()"), StringComparison.Ordinal);
        Assert.Contains("Background\" Value=\"Transparent", style, StringComparison.Ordinal);
        Assert.DoesNotContain("DeckBorderBrush", style, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_feedback_uses_fixed_surface_geometry_and_safe_brand_confirmation()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var manager = Slice(code, "private async Task ShowHoyoLabAccountManagerAsync", "private void AutomaticDailyCheckInToggle_Click");
        var settings = Slice(code, "public async Task ShowSettingsAsync", "private async Task ShowAddGameDialogAsync");

        Assert.Contains("x:Name=\"RailSpacerRow\" Height=\"*\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BrandEyeBall\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BrandEyeBallTranslate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MoveBrandEye();", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CurrentBannerSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"6,0,0,0\"", SliceElement(xaml, "x:Name=\"CurrentBannerSection\""), StringComparison.Ordinal);
        Assert.Contains("Width=\"20\"", SliceElement(xaml, "x:Name=\"RedemptionCodeRewardIcon\""), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchDetail\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"18\"", SliceElement(xaml, "x:Name=\"LaunchDetail\""), StringComparison.Ordinal);
        Assert.Contains("Background = (Brush)Application.Current.Resources[\"SettingsSurfaceBrush\"]", manager, StringComparison.Ordinal);
        Assert.Contains("BorderBrush = (Brush)Application.Current.Resources[\"DeckBorderBrush\"]", manager, StringComparison.Ordinal);
        Assert.Contains("CloseButtonStyle = (Style)Application.Current.Resources[\"NyxDialogQuietStyle\"]", manager, StringComparison.Ordinal);
        Assert.Contains("FullSizeDesired = true", settings, StringComparison.Ordinal);
        Assert.Contains("LauncherLayoutProfile.DesignWidth", settings, StringComparison.Ordinal);
        Assert.Contains("LauncherLayoutProfile.DesignHeight", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualWidth - 32", settings, StringComparison.Ordinal);
        Assert.Contains("will start with the next launch", code, StringComparison.Ordinal);
        Assert.DoesNotContain("is armed for the next launch", code, StringComparison.OrdinalIgnoreCase);
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
    public void Rail_and_settings_use_the_compact_direct_manipulation_design()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var settings = Slice(code, "public async Task ShowSettingsAsync", "private async Task ShowAddGameDialogAsync");
        var railSwitch = Slice(settings, "settingsGameRail.SelectionChanged", "var resetOrderConfirmationArmed");

        Assert.Contains("CanReorderItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DragItemsCompleted=\"GameSelector_DragItemsCompleted\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding StatusGlyph}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionMarker", controls, StringComparison.Ordinal);
        Assert.Contains("SetBackgroundSource", code, StringComparison.Ordinal);
        Assert.Contains("displayedBackgroundSource", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Freeze this artwork", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Character art scale", code, StringComparison.Ordinal);
        Assert.Contains("var tabs = new ListView", code, StringComparison.Ordinal);
        Assert.Contains("var settingsGameRail = new ListView", code, StringComparison.Ordinal);
        Assert.Contains("ItemsSource = Games", code, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle = (Style)Application.Current.Resources[\"NyxGameItemStyle\"]", code, StringComparison.Ordinal);
        Assert.Contains("SelectedItem = selected", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Save and switch\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Don't save and switch\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content = \"Stay here\"", settings, StringComparison.Ordinal);
        Assert.Contains("await SaveCurrentSettingsAsync()", railSwitch, StringComparison.Ordinal);
        Assert.Contains("LoadSettingsGame(target, tabs.SelectedIndex)", railSwitch, StringComparison.Ordinal);
        Assert.Contains("ApplySelectedAppearance(selected.Id)", railSwitch, StringComparison.Ordinal);
        Assert.Contains("RestoreSettingsRailSelection()", railSwitch, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.Hide()", railSwitch, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSettingsAsync(", railSwitch, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsaved changes. Click", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("requestedSettingsGameId", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("armedSettingsGameId", settings, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(settings, "var result = await dialog.ShowAsync\\(\\)").Cast<Match>());
        Assert.Contains("Width = content.Width", code, StringComparison.Ordinal);
        Assert.Contains("Height = 36", code, StringComparison.Ordinal);
        Assert.Contains("NyxSettingsDialogPrimaryStyle", code, StringComparison.Ordinal);
        Assert.Contains("NyxSettingsDialogQuietStyle", controls, StringComparison.Ordinal);
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
