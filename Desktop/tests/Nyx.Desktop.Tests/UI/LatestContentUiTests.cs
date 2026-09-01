namespace Nyx.Desktop.Tests.UI;

public sealed class BannerCycleUiTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Selected_game_drives_accessible_current_and_upcoming_banner_groups()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderBannerCycle()", "private void RenderGenshin()");

        Assert.Contains("launcherBanners.Current.Games.TryGetValue(selected.Id", render, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(\n                BannerCycleRegion", render, StringComparison.Ordinal);
        Assert.Contains("RenderBannerRows(selected.Id, current, now)", render, StringComparison.Ordinal);
        Assert.Contains("RenderUpcomingBannerGroups(selected.Id, current, upcoming, now)", render, StringComparison.Ordinal);
        Assert.Contains("launcherGame.UpcomingForDisplayAt(now, 5)", render, StringComparison.Ordinal);
        Assert.DoesNotContain("phase.Start > now", render, StringComparison.Ordinal);
        Assert.Contains("FormatCurrentBannerTiming(current, now)", render, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBannerCard", render, StringComparison.Ordinal);
        Assert.DoesNotContain("latestContent.Current", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_refresh_repaints_selection_and_never_changes_game_session_state()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var handler = Slice(
            code,
            "private void LauncherBanners_Updated(object? sender, EventArgs e)",
            "private void GameSelector_SelectionChanged");

        Assert.Contains("RenderSelection", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLaunch", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionRefresh", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("gameSnapshot", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("updaterStatus", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_content_is_core_and_only_official_games_show_the_banner_panel()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderBannerCycle()", "private void RenderGenshin()");
        var app = ReadAppFile("App.xaml.cs");
        Assert.DoesNotContain("FeatureFlags.RemoteBannerManifest", render, StringComparison.Ordinal);
        Assert.DoesNotContain("FeatureFlags.OfficialNews", render, StringComparison.Ordinal);
        Assert.Contains("panelVisibility.ShowBanners", render, StringComparison.Ordinal);
        Assert.Contains("_launcherBanners.Start();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("if (LauncherState.Snapshot.Preferences.FeatureFlags.RemoteBannerManifest)", app, StringComparison.Ordinal);
        var refresh = Slice(app, "internal async ValueTask<bool> RefreshContentAsync", "internal async Task RefreshContentManualAsync");
        Assert.DoesNotContain("FeatureFlags.RemoteBannerManifest", refresh, StringComparison.Ordinal);
        Assert.Contains("await _launcherBanners.RefreshManualAsync(cancellationToken)", refresh, StringComparison.Ordinal);
        Assert.Contains("SetAutomaticRefreshEnabled(true)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Premium_codes_are_limited_dated_and_copyable()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderBannerCycle()", "private void RenderGenshin()");
        var click = Slice(code, "private void RedemptionCodeCopy_Click", "private void PersistCopiedRedemptionCode");

        Assert.Contains("SyncRedemptionCodeRows(selected.Id, launcherGame.Codes)", render, StringComparison.Ordinal);
        Assert.Contains(".Take(5)", code, StringComparison.Ordinal);
        Assert.Contains("code.CurrencyAmount", code, StringComparison.Ordinal);
        Assert.Contains("CurrencyIconFor(gameId)", code, StringComparison.Ordinal);
        Assert.Contains("MarkPreviouslyCopied", code, StringComparison.Ordinal);
        Assert.Contains("Clipboard.SetContent(data)", click, StringComparison.Ordinal);
        Assert.Contains("Copied {code}", click, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_banner_character_is_projected_first_and_all_rows_stay_active()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderBannerRows", "private void RenderGenshin");

        Assert.DoesNotContain("bannerRotationTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerRotationSchedule", code, StringComparison.Ordinal);
        Assert.DoesNotContain("bannerRotationIndex", code, StringComparison.Ordinal);
        Assert.Contains(".OrderBy(character => string.Equals(", render, StringComparison.Ordinal);
        Assert.Contains("current.SelectedCharacterId", render, StringComparison.Ordinal);
        Assert.Contains("? 0", render, StringComparison.Ordinal);
        Assert.Contains(": 1)", render, StringComparison.Ordinal);
        Assert.Contains(".ThenByDescending(character => character.Debut", render, StringComparison.Ordinal);
        Assert.Contains("BannerCharacterRowItem.CreateOverflow", render, StringComparison.Ordinal);
        Assert.Contains("true,", render, StringComparison.Ordinal);
        Assert.Contains("public string CharacterLinkAccessibilityName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_panel_uses_one_compact_timeline_with_intrinsic_single_line_character_names()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var columns = Slice(xaml, "x:Name=\"BannerCycleColumns\"", "x:Name=\"LowerActionRegion\"");
        var bannerRegion = Slice(xaml, "x:Name=\"BannerCycleRegion\"", "x:Name=\"BannerCycleStack\"");
        var scrollViewer = Slice(xaml, "x:Name=\"BannerCycleScrollViewer\"", "x:Name=\"BannerCycleColumns\"");

        Assert.DoesNotContain(" Width=\"704\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(" Height=\"390\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"848\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerContentRegion.Width = bannerWidth", code, StringComparison.Ordinal);
        Assert.Contains("BannerContentRegion.VerticalAlignment = VerticalAlignment.Top", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCycleRegion.Height =", code, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{ThemeResource DeckBorderBrush}\"", bannerRegion, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"330\"", scrollViewer, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Current and upcoming banner details\"", scrollViewer, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", scrollViewer, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", scrollViewer, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode=\"Auto\"", scrollViewer, StringComparison.Ordinal);
        Assert.DoesNotContain(" Height=", scrollViewer, StringComparison.Ordinal);
        Assert.Contains("Margin=\"14,0,14,10\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentBannerColumn", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("UpcomingBannerColumn", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerColumnDivider", xaml + code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCharacterList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind BannerCharacterRows, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingBannerList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind UpcomingBannerGroups, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding}\"", columns, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding CharacterRows}\"", columns, StringComparison.Ordinal);
        Assert.Contains("Orientation=\"Vertical\"", columns, StringComparison.Ordinal);
        Assert.Contains("Orientation=\"Horizontal\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"160\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"132\"", columns, StringComparison.Ordinal);
        Assert.Equal(2, columns.Split("Spacing=\"12\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, columns.Split("MaxWidth=\"400\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, columns.Split("MaxWidth=\"354\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, columns.Split("StretchDirection=\"DownOnly\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("MinHeight=\"38\"", columns, StringComparison.Ordinal);
        Assert.Contains("Width=\"38\"", columns, StringComparison.Ordinal);
        Assert.Contains("Height=\"38\"", columns, StringComparison.Ordinal);
        Assert.Contains("Width=\"34\"", columns, StringComparison.Ordinal);
        Assert.Contains("Height=\"34\"", columns, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"15\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("LineHeight=\"20\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("LineStackingStrategy=\"BlockLineHeight\"", columns, StringComparison.Ordinal);
        Assert.Equal(2, columns.Split("TextWrapping=\"NoWrap\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("MaxLines=\"1\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayFontSize", xaml + code, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemWidth", xaml + code, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumDisplayedCurrentBannerCharacters = 10", code, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumDisplayedBannerCharactersPerPhase = 10", code, StringComparison.Ordinal);
        Assert.Contains("OrderBannerCharacters(phase.Characters)", code, StringComparison.Ordinal);
        Assert.Contains("RenderUpcomingBannerGroups(selected.Id, current, upcoming, now)", code, StringComparison.Ordinal);
        Assert.Contains("launcherGame.UpcomingForDisplayAt(now, 5)", code, StringComparison.Ordinal);
        Assert.Contains("rows.Chunk(2)", code, StringComparison.Ordinal);
        Assert.Contains("CharacterRows = Characters.Chunk(2).ToArray()", code, StringComparison.Ordinal);
        Assert.Contains("CreateOverflow", code, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"UpcomingPhaseDivider\"", columns, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_supports_direct_selection_and_loaded_background_crossfade_without_hover_rotation()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.DoesNotContain("BannerPanel_Pointer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("bannerPinnedGameId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("bannerPinnedCharacterId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerCharacterRow_Click", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"{Binding CharacterId}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(380)", code, StringComparison.Ordinal);
        Assert.Contains("bitmap.ImageOpened +=", code, StringComparison.Ordinal);
        Assert.Contains("PrepareLauncherImageBackground", code, StringComparison.Ordinal);
        Assert.Contains("BeginLauncherBackgroundCrossfade", code, StringComparison.Ordinal);
        Assert.DoesNotContain("bannerRotationStartedAt", code, StringComparison.Ordinal);
        foreach (var symbol in new[]
                 {
                     "HeroStage",
                     "HeroArtwork",
                     "SetHeroSource",
                     "HeroArtPlacementSolver",
                 })
        {
            Assert.DoesNotContain(symbol, xaml + code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Code_rows_keep_redemption_and_copy_as_separate_targets()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var controls = ReadAppFile("Themes", "NyxControls.xaml");
        var row = Slice(xaml, "x:Name=\"RedemptionCodeRowLayout\"", "</DataTemplate>");
        var redeem = Slice(xaml, "x:Name=\"RedemptionCodeOpenButton\"", "x:Name=\"RedemptionCodeReward\"");
        var reward = Slice(xaml, "x:Name=\"RedemptionCodeReward\"", "x:Name=\"RedemptionCodeCopyButton\"");

        Assert.Contains("Click=\"RedemptionCode_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RedemptionCodeCopy_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding CopyAccessibilityName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"REDEMPTION CODES\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CharacterSpacing=\"0\"", xaml, StringComparison.Ordinal);
        var header = Slice(xaml, "x:Name=\"CodesHeaderGrid\"", "x:Name=\"CodesHeaderDivider\"");
        Assert.Contains("ColumnSpacing=\"4\"", header, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"12\"", header, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", header, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"4\"", header, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RedemptionCodeText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"{Binding FontSize}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RedemptionCodeTextFit\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StretchDirection=\"DownOnly\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource NyxCodeCopyButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", redeem, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", reward, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NyxCodeCopyButtonStyle\"", controls, StringComparison.Ordinal);
        Assert.Contains("SetRedemptionCodeRowHeight(30)", code, StringComparison.Ordinal);
        Assert.Contains("copiedCodeRow?.MarkCopied()", code, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"{Binding CopyGlyph}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CopyGlyph = CopiedActionGlyph", code, StringComparison.Ordinal);
        Assert.Contains("CopyGlyph = CopyActionGlyph", code, StringComparison.Ordinal);
        Assert.True(
            row.IndexOf("x:Name=\"RedemptionCodeRewardIcon\"", StringComparison.Ordinal)
            < row.IndexOf("x:Name=\"RedemptionCodeRewardValue\"", StringComparison.Ordinal));
        Assert.True(
            row.IndexOf("x:Name=\"RedemptionCodeRewardValue\"", StringComparison.Ordinal)
            < row.IndexOf("x:Name=\"RedemptionCodeCopyButton\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Redemption_header_refreshes_codes_with_visible_safe_copy_status()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var refresh = Slice(
            code,
            "private async void RefreshCodesButton_Click",
            "private async void RedemptionCode_Click");

        Assert.Contains("x:Name=\"CodesHeaderGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodesHeaderDivider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource HairlineBrush}\"", Slice(xaml, "x:Name=\"CodesHeaderDivider\"", "x:Name=\"SignalPanel\""), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodeRefreshStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"Collapsed\"",
            Slice(xaml, "x:Name=\"CodeRefreshStatusText\"", "x:Name=\"RefreshCodesButton\""),
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RefreshCodesButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RefreshCodesButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"Refresh redemption codes\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"Open the official redemption page\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshCodesManualAsync", refresh, StringComparison.Ordinal);
        Assert.Contains("CodeRefreshStatusText.Visibility = Visibility.Visible", refresh, StringComparison.Ordinal);
        Assert.Contains("UPDATING", refresh, StringComparison.Ordinal);
        Assert.Contains("UP TO DATE", refresh, StringComparison.Ordinal);
        Assert.Contains("KEPT SAFE COPY", refresh, StringComparison.Ordinal);
        Assert.Contains("pageLease", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteBannerManifest", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Achievement_help_matches_the_sources_each_game_actually_offers()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var help = Slice(
            code,
            "private async void AchievementExportHelpButton_Click",
            "private async void PullExportHelpButton_Click");

        Assert.Contains("\"gi\" => \"1. Turn on Achievements.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gi\" => \"1. Choose Game", help, StringComparison.Ordinal);
        Assert.Contains("\"zzz\" => \"Achievement export is disabled.", help, StringComparison.Ordinal);
        Assert.Contains("\"wuwa\" => \"Achievement export is not ready.", help, StringComparison.Ordinal);
        Assert.Contains("\"ae\" => \"Achievement export is deliberately not being added", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Redemption_confirmation_uses_only_the_requested_question_and_yes_no_actions()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var handler = Slice(
            code,
            "private async void RedemptionCode_Click",
            "private void RedemptionCodeCopy_Click");

        Assert.Contains("Title = \"Open Redemption Page?\"", handler, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = \"Yes\"", handler, StringComparison.Ordinal);
        Assert.Contains("CloseButtonText = \"No\"", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Content =", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("in your browser", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_window_focus_never_refreshes_remote_content()
    {
        var app = ReadAppFile("App.xaml.cs");
        var handler = Slice(app, "private async Task RefreshAfterActivationAsync", "private static async Task DisposeRefreshAsync");

        Assert.Contains("SessionRefresh.RefreshNowAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshOnReactivationAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAsync", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_subscribes_for_banner_updates_and_unsubscribes_when_unloaded()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("launcherBanners.Updated += LauncherBanners_Updated", code, StringComparison.Ordinal);
        Assert.Contains("launcherBanners.Updated -= LauncherBanners_Updated", code, StringComparison.Ordinal);
        Assert.Contains("launcherBanners = app.LauncherBanners", code, StringComparison.Ordinal);
        Assert.Contains("RenderBannerCycle();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_and_upcoming_banners_share_the_panel_without_retired_collection_ui()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        Assert.Contains("x:Name=\"CurrentBannerSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpcomingBannerList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RenderBannerRows(selected.Id, current, now)", code, StringComparison.Ordinal);
        Assert.Contains("RenderUpcomingBannerGroups(selected.Id, current, upcoming, now)", code, StringComparison.Ordinal);
        foreach (var symbol in new[]
                 {
                     "BannerCollection",
                     "RenderBannerCategories",
                     "BannerCategoryButton",
                     "selectedBannerCategories",
                     "FATE COLLAB",
                 })
        {
            Assert.DoesNotContain(symbol, xaml + code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Upcoming_banner_uses_known_patch_labels_and_only_marks_unknown_groups_as_soon()
    {
        var code = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(code, "private void RenderUpcomingBannerGroups", "private void SyncRedemptionCodeRows");
        Assert.Contains("phase.Announced", render, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(phase.Phase)", render, StringComparison.Ordinal);
        Assert.Contains("FormatBannerPhaseLabel(phase.Phase)", render, StringComparison.Ordinal);
        Assert.Contains("? \"Soon\\u2122\"", render, StringComparison.Ordinal);
        Assert.Contains("$\"Starts in {BannerTimingFormatter.FormatRemaining(phase.Start!.Value - now)}\"", render, StringComparison.Ordinal);
        Assert.Contains("\"Available on loss\"", render, StringComparison.Ordinal);
        Assert.Contains("character.Limited == false", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_header_combines_patch_phase_and_timing_without_dividing_phases()
    {
        var xaml = ReadAppFile("MainPage.xaml");
        var code = ReadAppFile("MainPage.xaml.cs");
        var header = Slice(xaml, "x:Name=\"BannerCycleHeader\"", "x:Name=\"BannerCycleColumns\"");
        var current = Slice(xaml, "x:Name=\"CurrentBannerSection\"", "x:Name=\"UpcomingBannerList\"");
        var currentTiming = Slice(xaml, "x:Name=\"BannerCycleTiming\"", "/>");
        var upcomingTiming = Slice(xaml, "x:Name=\"UpcomingBannerTiming\"", "/>");

        Assert.Contains("HorizontalAlignment=\"Stretch\"", header, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleHeading\"", header, StringComparison.Ordinal);
        Assert.Contains("Text=\"BANNERS\"", header, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCollapseButton\"", header, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"17\"", currentTiming, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource IrisBrush}\"", currentTiming, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"15\"", upcomingTiming, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource IrisBrush}\"", upcomingTiming, StringComparison.Ordinal);
        Assert.Contains("BannerCycleHeading.Text = \"BANNERS\"", code, StringComparison.Ordinal);
        Assert.Contains("$\"Ends in {BannerTimingFormatter.FormatRemaining(change - now)}\"", code, StringComparison.Ordinal);
        Assert.Contains("$\"Ends in {BannerTimingFormatter.FormatRemaining(end - now)}\"", code, StringComparison.Ordinal);
        Assert.Contains("FormatBannerTimelineLabel", code, StringComparison.Ordinal);
        Assert.Contains("$\"Patch {value[..marker].Trim()} \\u00B7 Phase", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BannerCycleHeaderDivider\"", header, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerColumnDivider", xaml + code, StringComparison.Ordinal);
        Assert.Contains("var timingVisibility = string.IsNullOrWhiteSpace(BannerCycleTiming.Text)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_reactivation_retries_remote_launcher_content_without_duplicate_handlers()
    {
        var code = ReadAppFile("MainPage.xaml.cs");

        Assert.Contains("NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged", code, StringComparison.Ordinal);
        Assert.Contains("NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged", code, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref networkContentRefreshInFlight, 1, 0)", code, StringComparison.Ordinal);
        Assert.Contains("networkRefreshGeneration", code, StringComparison.Ordinal);
        Assert.Contains("launcherBanners.RefreshOnReactivationAsync(lease.CancellationToken)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FeatureFlags.RemoteBannerManifest", Slice(code, "private async Task RefreshContentAfterNetworkReactivationAsync", "private static bool HasInternetConnection"), StringComparison.Ordinal);
        Assert.Contains(
            "System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetInternetConnectionProfile()", code, StringComparison.Ordinal);
        Assert.Contains("Refreshing banners, codes, and launcher media...", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Refreshing official news and banner art...", code, StringComparison.Ordinal);
    }

    [Fact]
    public void App_uses_only_the_banner_manifest_content_service()
    {
        var app = ReadAppFile("App.xaml.cs");
        var project = ReadAppFile("Nyx.Desktop.App.csproj");

        Assert.DoesNotContain("new LatestContentService", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_latestContent", app, StringComparison.Ordinal);
        Assert.Contains("_launcherBanners = new LauncherBannersContentService", app, StringComparison.Ordinal);
        Assert.DoesNotContain("PENGO_NYX_LAUNCHER_", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", app, StringComparison.Ordinal);
        Assert.Contains("new Uri(LauncherBannersTransport.ProductionEndpoint)", app, StringComparison.Ordinal);
        Assert.Contains("new Uri(LauncherBannersTransport.ProductionCodesEndpoint)", app, StringComparison.Ordinal);
        Assert.Contains("Assets\\Content\\**\\*", project, StringComparison.Ordinal);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_uses_bundled_manrope_for_all_text()
    {
        var typography = ReadAppFile("Themes", "NyxTypography.xaml");
        var project = ReadAppFile("Nyx.Desktop.App.csproj");
        var publisherWindow = ReadAppFile("PublisherSessionWindow.xaml");
        var fontPath = Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "Assets",
            "Fonts",
            "Manrope-Variable.ttf");
        var licensePath = Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "Assets",
            "Fonts",
            "Manrope-OFL.txt");

        Assert.True(File.Exists(fontPath));
        Assert.Equal(165_420, new FileInfo(fontPath).Length);
        Assert.True(File.Exists(licensePath));
        Assert.Contains("SIL Open Font License, Version 1.1", File.ReadAllText(licensePath), StringComparison.Ordinal);
        Assert.Contains("Assets\\Fonts\\Manrope-Variable.ttf", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Fonts\\Manrope-OFL.txt", project, StringComparison.Ordinal);
        Assert.Contains("Manrope-Variable.ttf#Manrope", typography, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ContentControlThemeFontFamily\"", typography, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NyxDisplayFont\"", typography, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NyxBodyFont\"", typography, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"NyxDataFont\"", typography, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"TextBlock\">", typography, StringComparison.Ordinal);
        Assert.DoesNotContain("Segoe UI Variable", typography, StringComparison.Ordinal);
        Assert.DoesNotContain("Segoe UI Variable", publisherWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("GI.ttf", typography, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_browser_blocks_downloads_and_site_permissions()
    {
        var code = ReadAppFile("PublisherSessionWindow.xaml.cs");

        Assert.Contains("core.DownloadStarting += Core_DownloadStarting", code, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true;", Slice(code, "private static void Core_DownloadStarting", "private static void Core_PermissionRequested"), StringComparison.Ordinal);
        Assert.Contains("args.State = CoreWebView2PermissionState.Deny", code, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker}.");
        Assert.True(end > start, $"Could not find {endMarker} after {startMarker}.");
        return source[start..end];
    }

    private static string ReadAppFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine(
            [
                WorkspaceRoot,
                "Desktop",
                "src",
                "Nyx.Desktop.App",
                .. relativeSegments,
            ])).Replace("\r\n", "\n", StringComparison.Ordinal);

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
