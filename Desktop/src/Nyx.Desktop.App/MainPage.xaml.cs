using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.Storage.Pickers;
using Windows.Media.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Nyx.Desktop.Core.Content;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Diagnostics;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Core.Recovery;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Core.Updating;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Games;
using Nyx.Desktop.Infrastructure.Content;
using Nyx.Desktop.Infrastructure.AccountStatus;
using Nyx.Desktop.Infrastructure.Hoyo;
using Nyx.Desktop.Infrastructure.Launching;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;
using Nyx.Desktop.Infrastructure.PublisherGames;
using Nyx.Desktop.Infrastructure.Sessions;
using Nyx.Desktop.Infrastructure.Updating;
using Nyx_Desktop_App.ViewModels;
using Windows.Networking.Connectivity;

namespace Nyx_Desktop_App;

public sealed partial class MainPage : Page
{
    private sealed class LaunchStarParticle
    {
        public Ellipse Shape { get; init; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; init; }
        public double Drift { get; init; }
        public double BaseOpacity { get; init; }
        public double TwinkleAmount { get; init; }
        public double TwinkleSpeed { get; init; }
        public double Phase { get; init; }
    }

    private const int WuWaLaunchObservationCount = 6;
    private const int EndfieldLaunchObservationCount = 6;
    private const int MaximumDisplayedCurrentBannerCharacters = 10;
    private const int MaximumDisplayedBannerCharactersPerPhase = 10;
    private const double LaunchStarfieldWidth = 367;
    private const double LaunchStarfieldHeight = 82;
    private static readonly TimeSpan WuWaLaunchObservationInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly Color[] LaunchStarColors =
    [
        Color.FromArgb(255, 169, 152, 237),
        Color.FromArgb(255, 151, 144, 201),
        Color.FromArgb(255, 133, 112, 204),
        Color.FromArgb(255, 244, 239, 255),
        Color.FromArgb(255, 255, 255, 255),
    ];

    private static readonly IReadOnlyDictionary<string, string> IconPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gi"] = "ms-appx:///Assets/Catalog/giicon.png",
            ["hsr"] = "ms-appx:///Assets/Catalog/hsricon.png",
            ["zzz"] = "ms-appx:///Assets/Catalog/zzzicon.png",
            ["wuwa"] = "ms-appx:///Assets/Catalog/wuwaicon.png",
            ["ae"] = "ms-appx:///Assets/Catalog/aeicon.png",
        };

    private static readonly IReadOnlyDictionary<string, string> PrimaryResourceIconPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gi"] = "ms-appx:///Assets/Content/ResourceIcons/original-resin.webp",
            ["hsr"] = "ms-appx:///Assets/Content/ResourceIcons/trailblaze-power.webp",
            ["zzz"] = "ms-appx:///Assets/Content/ResourceIcons/battery-charge.webp",
            ["wuwa"] = "ms-appx:///Assets/Content/ResourceIcons/waveplate.webp",
        };

    private static readonly IReadOnlyDictionary<string, string> ReserveResourceIconPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hsr"] = "ms-appx:///Assets/Content/ResourceIcons/reserved-trailblaze-power.webp",
            ["zzz"] = "ms-appx:///Assets/Content/ResourceIcons/backup-energy.webp",
            ["wuwa"] = "ms-appx:///Assets/Content/ResourceIcons/waveplate-crystal.webp",
        };

    private static readonly IReadOnlyDictionary<string, (string Primary, string Reserve)> ResourceMetricNames =
        new Dictionary<string, (string Primary, string Reserve)>(StringComparer.Ordinal)
        {
            ["gi"] = ("Original Resin", "Stored Resin"),
            ["hsr"] = ("Trailblaze Power", "Reserved Trailblaze Power"),
            ["zzz"] = ("Battery Charge", "Backup Energy Charge"),
            ["wuwa"] = ("Waveplate", "Waveplate Crystal"),
        };

    private static readonly IReadOnlyDictionary<string, string> RedemptionUrlTemplates =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gi"] = "https://genshin.hoyoverse.com/en/gift?code={0}",
            ["hsr"] = "https://hsr.hoyoverse.com/gift?code={0}",
            ["zzz"] = "https://zenless.hoyoverse.com/redemption?code={0}",
        };

    private static readonly IReadOnlyDictionary<GameRailSignalKind, string> RailSignalGlyphs =
        new Dictionary<GameRailSignalKind, string>
        {
            [GameRailSignalKind.Checking] = "⋯",
            [GameRailSignalKind.Ready] = "●",
            [GameRailSignalKind.Starting] = "◐",
            [GameRailSignalKind.Running] = "▶",
            [GameRailSignalKind.UpdateAndPreDownload] = "✦",
            [GameRailSignalKind.UpdateAvailable] = "↑",
            [GameRailSignalKind.PreDownloadAvailable] = "↓",
            [GameRailSignalKind.RetryAvailable] = "!",
            [GameRailSignalKind.NeedsReview] = "!",
            [GameRailSignalKind.NotFound] = "○",
            [GameRailSignalKind.Unsupported] = "○",
        };

    private readonly GameSessionCoordinator sessions;
    private readonly GameSessionRefreshPump sessionRefresh;
    private readonly SessionUiLifetime sessionUiLifetime;
    private readonly LauncherBannersContentService launcherBanners;
    private readonly ExportCoordinator exports;
    private readonly HoyoPublisherStatusSource publisherStatus;
    private readonly WuWaAccountStatusService wuwaAccountStatus;
    private readonly PublisherAccountService publisherAccounts;
    private readonly LauncherVisualsCache launcherVisuals;
    private readonly GenshinGameSessionAdapter genshinSession;
    private readonly IReadOnlyDictionary<string, HoyoGameSessionAdapter> hoyoSessions;
    private readonly HoyoPlayHandoffExecutor hoyoPlayExecutor;
    private readonly WuWaMaintenanceService wuwaMaintenance;
    private readonly PublisherGameDirectLaunchService publisherGameLaunchService;
    private readonly EndfieldInstallRootStore endfieldRootStore;
    private readonly EndfieldOfficialMaintenanceService endfieldMaintenance;
    private readonly App app;
    private readonly LauncherStateController launcherState;
    private readonly UserAssetStore userAssets;
    private readonly WindowsGenshinCandidateDiscovery discovery;
    private readonly GenshinInspectionAdapter genshinInspection;
    private readonly HoyoGameIdentityAdapter hoyoIdentity = new();
    private string? updaterRoot;
    private GameSessionSnapshot? gameSnapshot;
    private GenshinLaunchStatus? updaterStatus;
    private GenshinLaunchFailureReason gameFailureReason;
    private string? officialLauncherStatusOverride;
    private string? preInstallNoticeKey;
    private bool updaterScanFinished;
    private bool wuwaScanFinished;
    private readonly HashSet<string> gameActionsInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> automaticDailyCheckInsInFlight = new(StringComparer.Ordinal);
    private readonly HoyoLabExportUiReservation hoyoLabExportReservation = new();
    private readonly Dictionary<string, Guid> latestExportJobs = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> hoyoLabImmediateExportJobs = [];
    private readonly Dictionary<Guid, AchievementHandoffUiState> achievementHandoffs = new();
    private readonly object exportRegistrationAdmissionSync = new();
    private TaskCompletionSource? exportRegistrationsDrained;
    private int activeExportRegistrations;
    private bool exportRegistrationAdmissionClosed;
    private readonly TaskCompletionSource shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int shutdownStarted;
    private readonly AchievementImportBridge achievementImportBridge = new();
    private string? displayedBackgroundSource;
    private bool updaterActionInFlight;
    private bool wuwaActionInFlight;
    private bool endfieldFolderActionInFlight;
    private bool screenshotFolderActionInFlight;
    private bool endfieldMaintenanceScanFinished;
    private bool endfieldMaintenanceActionInFlight;
    private bool wuwaAccountStatusActionInFlight;
    private bool wuwaAccountInitialRefreshRequested;
    private bool wuwaAccountStatusSessionDisabled;
    private bool wuwaAccountStatusSaveFailed;
    private bool publisherAccountActionInFlight;
    private readonly HashSet<string> publisherConsentSaveFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> publisherConsentCleanupFailures = new(StringComparer.Ordinal);
    private int wuwaAccountStatusUiGeneration;
    private EndfieldOfficialMaintenanceStatus? endfieldMaintenanceStatus;
    private PublisherGameInspectionReason endfieldMaintenanceReason;
    private WuWaOfficialMaintenanceStatus? wuwaMaintenanceStatus;
    private PublisherGameInspectionReason wuwaMaintenanceReason;
    private OfficialMaintenanceHandoffRequest? wuwaMaintenanceRequest;
    private bool refreshSubscribed;
    private bool launcherBannersSubscribed;
    private bool publisherStatusSubscribed;
    private bool publisherAccountsSubscribed;
    private bool selectorSubscribed;
    private bool reactivationSubscribed;
    private bool networkStatusSubscribed;
    private bool endfieldRootDiscoverySubscribed;
    private bool stableUpdateScheduled;
    private bool stableUpdateFramePending;
    private int networkAvailability = -1;
    private int networkContentRefreshInFlight;
    private int networkRefreshGeneration;
    private int hoyoRefreshGeneration;
    private readonly LatestGenerationGate wuwaRefreshGeneration = new();
    private readonly LatestGenerationGate endfieldMaintenanceGeneration = new();
    private readonly EndfieldFolderSelectionPolicy endfieldFolderSelections = new();
    private readonly EndfieldUiActionAdmission endfieldUiActions = new();
    private readonly DispatcherTimer bannerCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer codeCopyResetTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer publisherResourceRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer launcherGalleryTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly DispatcherTimer brandEyeTimer = new();
    private readonly List<LaunchStarParticle> launchStars = [];
    private readonly Random launchStarRandom = new(1416);
    private readonly Random brandEyeRandom = new(7331);
    private readonly Stopwatch launchAnimationClock = Stopwatch.StartNew();
    private readonly Dictionary<string, DateTimeOffset> publisherResourceAutomaticAttempts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapImage> imageSourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly UISettings uiSettings = new();
    internal Func<DateTimeOffset> AccountDisplayClock { get; set; } = static () => DateTimeOffset.Now;
    private double redemptionCodeRowHeight = 26;
    private bool compactCodeRows;
    private RedemptionCodeRowItem? copiedCodeRow;
    private string? copiedCodeValue;
    private SessionUiLease? pageLease;
    private PrimaryGameStatusAction primaryGameStatusAction;
    private LauncherVisualSelection? activeLauncherVisual;
    private readonly Dictionary<string, LauncherVisualSelection> preloadedLauncherVisuals = new(StringComparer.Ordinal);
    private Task? launcherVisualPreloadTask;
    private Image? visibleLauncherImageBackground;
    private MediaPlayerElement? visibleLauncherMotionBackground;
    private Storyboard? launcherBackgroundCrossfade;
    private long launcherBackgroundTransitionToken;
    private long launcherImageRequestToken;
    private string? launcherVisualRequestedGameId;
    private int launcherVisualGeneration;
    private int launcherMotionPrimaryGeneration;
    private int launcherMotionSecondaryGeneration;
    private int launcherGalleryIndex;
    private bool launcherMotionPaused;
    private bool accountSectionExpanded = true;
    private bool ambientAnimationsRunning;
    private bool launchButtonHovered;
    private double launchAnimationLastFrameSeconds;
    private Storyboard? brandEyeStoryboard;

    public ObservableCollection<GameLauncherItem> Games { get; } = new();

    public ObservableCollection<RedemptionCodeRowItem> RedemptionCodeRows { get; } = new();

    public ObservableCollection<IReadOnlyList<BannerCharacterRowItem>> BannerCharacterRows { get; } = new();

    public ObservableCollection<UpcomingBannerGroupItem> UpcomingBannerGroups { get; } = new();

    internal bool ToggleLauncherAnimation()
    {
        launcherMotionPaused = !launcherMotionPaused;
        if (launcherMotionPaused)
        {
            launcherGalleryTimer.Stop();
            LauncherMotionBackground.MediaPlayer?.Pause();
            LauncherMotionBackgroundNext.MediaPlayer?.Pause();
            StopAmbientAnimations();
            return true;
        }

        if (activeLauncherVisual is { Kind: "video" })
        {
            var motion = visibleLauncherMotionBackground
                ?? (LauncherMotionBackground.Source is not null
                    ? LauncherMotionBackground
                    : LauncherMotionBackgroundNext);
            motion.MediaPlayer?.Play();
        }
        else if (activeLauncherVisual is { Kind: "gallery", Files.Count: > 1 })
        {
            launcherGalleryTimer.Start();
        }

        StartAmbientAnimations();
        return false;
    }

    private void CreateLaunchStarfield()
    {
        LaunchStarCanvas.Children.Clear();
        launchStars.Clear();

        const int starCount = 66;
        for (var index = 0; index < starCount; index++)
        {
            var depth = launchStarRandom.NextDouble();
            var size = depth < 0.55
                ? 1.2 + (launchStarRandom.NextDouble() * 0.9)
                : depth < 0.88
                    ? 2 + launchStarRandom.NextDouble()
                    : 3 + (launchStarRandom.NextDouble() * 1.5);
            var speed = depth < 0.55
                ? 5 + (launchStarRandom.NextDouble() * 7)
                : depth < 0.88
                    ? 12 + (launchStarRandom.NextDouble() * 11)
                    : 23 + (launchStarRandom.NextDouble() * 16);
            var star = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(LaunchStarColors[launchStarRandom.Next(LaunchStarColors.Length)]),
                IsHitTestVisible = false,
            };
            var x = 8 + (launchStarRandom.NextDouble() * (LaunchStarfieldWidth - 16));
            var y = 7 + (launchStarRandom.NextDouble() * (LaunchStarfieldHeight - 14));
            Canvas.SetLeft(star, x);
            Canvas.SetTop(star, y);
            LaunchStarCanvas.Children.Add(star);
            launchStars.Add(new LaunchStarParticle
            {
                Shape = star,
                X = x,
                Y = y,
                Speed = speed,
                Drift = (launchStarRandom.NextDouble() - 0.5) * 2.2,
                BaseOpacity = 0.24 + (launchStarRandom.NextDouble() * 0.54),
                TwinkleAmount = 0.12 + (launchStarRandom.NextDouble() * 0.34),
                TwinkleSpeed = 1 + (launchStarRandom.NextDouble() * 3.5),
                Phase = launchStarRandom.NextDouble() * Math.PI * 2,
            });
        }
    }

    private void StartAmbientAnimations()
    {
        if (launcherMotionPaused || !uiSettings.AnimationsEnabled) return;
        if (!ambientAnimationsRunning)
        {
            launchAnimationLastFrameSeconds = launchAnimationClock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += LaunchAnimation_Rendering;
            ambientAnimationsRunning = true;
        }
        if (!brandEyeTimer.IsEnabled)
        {
            MoveBrandEye();
            ScheduleBrandEyeMove();
        }
    }

    private void StopAmbientAnimations()
    {
        if (ambientAnimationsRunning)
        {
            CompositionTarget.Rendering -= LaunchAnimation_Rendering;
            ambientAnimationsRunning = false;
        }
        brandEyeTimer.Stop();
        brandEyeStoryboard?.Stop();
        brandEyeStoryboard = null;
    }

    private void LaunchAnimation_Rendering(object? sender, object e)
    {
        var now = launchAnimationClock.Elapsed.TotalSeconds;
        var elapsed = now - launchAnimationLastFrameSeconds;
        if (elapsed < (1d / 30)) return;
        elapsed = Math.Min(elapsed, 0.05);
        launchAnimationLastFrameSeconds = now;
        var speedMultiplier = launchButtonHovered ? 1.38 : 1;
        var opacityBoost = launchButtonHovered ? 0.14 : 0;

        foreach (var star in launchStars)
        {
            star.X += star.Speed * elapsed * speedMultiplier;
            star.Y += star.Drift * elapsed;
            if (star.X > LaunchStarfieldWidth + 4)
            {
                star.X = -4;
                star.Y = 7 + (launchStarRandom.NextDouble() * (LaunchStarfieldHeight - 14));
            }
            if (star.Y < 3) star.Y = LaunchStarfieldHeight - 4;
            else if (star.Y > LaunchStarfieldHeight - 3) star.Y = 4;

            Canvas.SetLeft(star.Shape, star.X);
            Canvas.SetTop(star.Shape, star.Y);
            var wave = Math.Sin((now * star.TwinkleSpeed) + star.Phase);
            star.Shape.Opacity = Math.Clamp(
                star.BaseOpacity + (wave * star.TwinkleAmount) + opacityBoost,
                0.08,
                1);
        }

        LaunchNebulaLeft.Opacity = 0.27 + ((Math.Sin(now * 0.55) + 1) * 0.045);
        LaunchNebulaRight.Opacity = 0.42 + ((Math.Sin((now * 0.43) + 1.7) + 1) * 0.045);
    }

    private void BrandEyeTimer_Tick(object? sender, object e)
    {
        MoveBrandEye();
        ScheduleBrandEyeMove();
    }

    private void ScheduleBrandEyeMove()
    {
        brandEyeTimer.Interval = TimeSpan.FromMilliseconds(1500 + (brandEyeRandom.NextDouble() * 1500));
        brandEyeTimer.Start();
    }

    private void MoveBrandEye()
    {
        var angle = brandEyeRandom.NextDouble() * Math.PI * 2;
        var radius = Math.Sqrt(brandEyeRandom.NextDouble());
        var x = Math.Cos(angle) * radius * 6.25;
        var y = Math.Sin(angle) * radius * 3.25;
        var duration = new Duration(TimeSpan.FromMilliseconds(1300));
        var storyboard = new Storyboard();
        var xAnimation = new DoubleAnimation
        {
            From = BrandEyeBallTranslate.X,
            To = x,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        var yAnimation = new DoubleAnimation
        {
            From = BrandEyeBallTranslate.Y,
            To = y,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(xAnimation, BrandEyeBallTranslate);
        Storyboard.SetTargetProperty(xAnimation, "X");
        Storyboard.SetTarget(yAnimation, BrandEyeBallTranslate);
        Storyboard.SetTargetProperty(yAnimation, "Y");
        storyboard.Children.Add(xAnimation);
        storyboard.Children.Add(yAnimation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(brandEyeStoryboard, storyboard)) return;
            BrandEyeBallTranslate.X = x;
            BrandEyeBallTranslate.Y = y;
            storyboard.Stop();
            brandEyeStoryboard = null;
        };
        brandEyeStoryboard?.Stop();
        brandEyeStoryboard = storyboard;
        storyboard.Begin();
    }

    private void LaunchButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        launchButtonHovered = true;
        LaunchOuterGlow.Opacity = 0.9;
        LaunchInnerFrame.Opacity = 1;
        LaunchInnerHighlight.Opacity = 0.82;
    }

    private void LaunchButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        launchButtonHovered = false;
        LaunchOuterGlow.Opacity = 0.55;
        LaunchInnerFrame.Opacity = 0.92;
        LaunchInnerHighlight.Opacity = 0.55;
    }

    public MainPage()
    {
        InitializeComponent();
        visibleLauncherImageBackground = BackgroundArtwork;
        if (LauncherMotionBackground.MediaPlayer is { } primaryMotion)
        {
            primaryMotion.MediaOpened += LauncherMotionPlayer_MediaOpened;
            primaryMotion.MediaFailed += LauncherMotionPlayer_MediaFailed;
        }
        if (LauncherMotionBackgroundNext.MediaPlayer is { } secondaryMotion)
        {
            secondaryMotion.MediaOpened += LauncherMotionPlayer_MediaOpened;
            secondaryMotion.MediaFailed += LauncherMotionPlayer_MediaFailed;
        }
        bannerCountdownTimer.Tick += BannerCountdownTimer_Tick;
        codeCopyResetTimer.Tick += CodeCopyResetTimer_Tick;
        publisherResourceRefreshTimer.Tick += PublisherResourceRefreshTimer_Tick;
        launcherGalleryTimer.Tick += LauncherGalleryTimer_Tick;
        brandEyeTimer.Tick += BrandEyeTimer_Tick;
        CreateLaunchStarfield();

        app = (App)Application.Current;
        launcherState = app.LauncherState;
        userAssets = new UserAssetStore(launcherState.DataDirectory);
        launcherVisuals = new LauncherVisualsCache(launcherState.DataDirectory);
        RebuildGameRail(launcherState.Snapshot);
        sessions = app.Sessions;
        sessionRefresh = app.SessionRefresh;
        sessionUiLifetime = app.SessionUiLifetime;
        launcherBanners = app.LauncherBanners;
        exports = app.Exports;
        publisherStatus = app.HoyoPublisherStatus;
        wuwaAccountStatus = app.WuWaAccountStatus;
        publisherAccounts = app.PublisherAccounts;
        genshinSession = app.GenshinSession;
        hoyoSessions = app.HoyoSessions;
        hoyoPlayExecutor = app.HoyoPlayExecutor;
        wuwaMaintenance = app.WuWaMaintenance;
        publisherGameLaunchService = app.PublisherGameLaunchService;
        endfieldRootStore = app.EndfieldRootStore;
        endfieldMaintenance = app.EndfieldMaintenance;
        discovery = app.GenshinDiscovery;
        genshinInspection = app.GenshinInspection;

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        GameSelector.SelectedItem = Games.FirstOrDefault(
            game => game.Id == launcherState.Snapshot.SelectedGameId) ?? Games.FirstOrDefault();
        gameSnapshot = GameSelector.SelectedItem is GameLauncherItem selected
            && sessions.TryGetSnapshot(selected.Id, out var initialSnapshot)
                ? initialSnapshot
                : null;
        RenderSelection();
    }

    private void RebuildGameRail(Nyx.Desktop.Core.State.LauncherState state)
    {
        var official = GameCatalog.All.ToDictionary(
            static game => game.Id,
            game =>
            {
                var appearance = state.Appearance.TryGetValue(game.Id, out var saved)
                    ? saved
                    : null;
                return new GameLauncherItem(
                    game.Id,
                    game.DisplayName,
                    appearance?.IconPath ?? IconPaths[game.Id],
                    game.RailProvider,
                    "⋯",
                    "Checking local status",
                    isCustom: false);
            },
            StringComparer.Ordinal);
        var customs = state.CustomGames.ToDictionary(
            static game => game.Id,
            game =>
            {
                var appearance = state.Appearance.TryGetValue(game.Id, out var saved)
                    ? saved
                    : null;
                return new GameLauncherItem(
                    game.Id,
                    game.Name,
                    appearance?.IconPath ?? game.IconPath,
                    "CUSTOM GAME",
                    "○",
                    "Ready to check",
                    isCustom: true);
            },
            StringComparer.Ordinal);

        Games.Clear();
        foreach (var id in state.RailOrder)
        {
            if (official.TryGetValue(id, out var officialGame))
            {
                Games.Add(officialGame);
            }
            else if (customs.TryGetValue(id, out var customGame))
            {
                Games.Add(customGame);
            }
        }
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!selectorSubscribed)
        {
            GameSelector.SelectionChanged += GameSelector_SelectionChanged;
            selectorSubscribed = true;
        }

        ApplyLayout();
        StartAmbientAnimations();
        bannerCountdownTimer.Start();
        publisherResourceRefreshTimer.Start();
        var lease = sessionUiLifetime.Activate();
        pageLease = lease;
        gameActionsInFlight.Clear();
        updaterActionInFlight = false;
        wuwaActionInFlight = false;
        endfieldFolderActionInFlight = false;
        screenshotFolderActionInFlight = false;
        endfieldMaintenanceActionInFlight = false;

        if (!refreshSubscribed)
        {
            sessionRefresh.Refreshed += SessionRefresh_Refreshed;
            refreshSubscribed = true;
        }

        if (!launcherBannersSubscribed)
        {
            launcherBanners.Updated += LauncherBanners_Updated;
            launcherBannersSubscribed = true;
        }

        if (!publisherStatusSubscribed)
        {
            publisherStatus.Updated += PublisherStatus_Updated;
            publisherStatusSubscribed = true;
        }

        if (!publisherAccountsSubscribed)
        {
            publisherAccounts.Updated += PublisherAccounts_Updated;
            publisherAccountsSubscribed = true;
        }

        if (!reactivationSubscribed)
        {
            app.WindowReactivated += App_WindowReactivated;
            reactivationSubscribed = true;
        }

        if (!networkStatusSubscribed)
        {
            Interlocked.Increment(ref networkRefreshGeneration);
            NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
            networkStatusSubscribed = true;
            Volatile.Write(ref networkAvailability, HasInternetConnection() ? 1 : 0);
        }

        if (!endfieldRootDiscoverySubscribed)
        {
            app.EndfieldRootAutoDiscovered += App_EndfieldRootAutoDiscovered;
            endfieldRootDiscoverySubscribed = true;
        }

        RenderSelection();
        ScheduleStableUpdateAfterFirstFrame();
        StartLauncherVisualPreload(lease);
        _ = RefreshPublisherResourcesOnStartupAsync(lease);
        var hoyoCheck = updaterScanFinished
            ? Task.CompletedTask
            : RefreshHoyoMaintenanceAsync(lease, refreshSessions: true);
        var wuwaCheck = wuwaScanFinished
            ? Task.CompletedTask
            : RefreshWuWaMaintenanceAsync(lease, useStoredRequest: false);
        var endfieldCheck = endfieldMaintenanceScanFinished
            ? Task.CompletedTask
            : RefreshEndfieldMaintenanceAsync(lease);
        await Task.WhenAll(
            IndependentMaintenanceLaneRunner.RunAsync(
                () => hoyoCheck,
                () => wuwaCheck),
            endfieldCheck);
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (stableUpdateFramePending)
        {
            CompositionTarget.Rendering -= StableUpdate_FirstFrameRendering;
            stableUpdateFramePending = false;
        }

        launcherVisualGeneration++;
        launcherBackgroundCrossfade?.Stop();
        launcherBackgroundCrossfade = null;
        StopAmbientAnimations();
        launcherGalleryTimer.Stop();
        LauncherMotionBackground.MediaPlayer?.Pause();
        LauncherMotionBackgroundNext.MediaPlayer?.Pause();
        launcherVisualRequestedGameId = null;
        activeLauncherVisual = null;
        bannerCountdownTimer.Stop();
        publisherResourceRefreshTimer.Stop();
        codeCopyResetTimer.Stop();
        if (selectorSubscribed)
        {
            GameSelector.SelectionChanged -= GameSelector_SelectionChanged;
            selectorSubscribed = false;
        }

        if (refreshSubscribed)
        {
            sessionRefresh.Refreshed -= SessionRefresh_Refreshed;
            refreshSubscribed = false;
        }

        if (launcherBannersSubscribed)
        {
            launcherBanners.Updated -= LauncherBanners_Updated;
            launcherBannersSubscribed = false;
        }

        if (publisherStatusSubscribed)
        {
            publisherStatus.Updated -= PublisherStatus_Updated;
            publisherStatusSubscribed = false;
        }

        if (publisherAccountsSubscribed)
        {
            publisherAccounts.Updated -= PublisherAccounts_Updated;
            publisherAccountsSubscribed = false;
        }

        if (reactivationSubscribed)
        {
            app.WindowReactivated -= App_WindowReactivated;
            reactivationSubscribed = false;
        }

        if (networkStatusSubscribed)
        {
            NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
            networkStatusSubscribed = false;
            Interlocked.Increment(ref networkRefreshGeneration);
            Volatile.Write(ref networkAvailability, -1);
            Interlocked.Exchange(ref networkContentRefreshInFlight, 0);
        }

        if (endfieldRootDiscoverySubscribed)
        {
            app.EndfieldRootAutoDiscovered -= App_EndfieldRootAutoDiscovered;
            endfieldRootDiscoverySubscribed = false;
        }

        var lease = Interlocked.Exchange(ref pageLease, null);
        endfieldFolderSelections.CancelAll();
        endfieldUiActions.Reset();
        endfieldMaintenanceGeneration.Next();
        if (lease is not null)
        {
            sessionUiLifetime.Deactivate(lease);
        }
    }

    internal Task ShutDownAsync()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) == 0)
        {
            _ = ShutDownCoreAsync();
        }

        return shutdownCompletion.Task;
    }

    private async Task ShutDownCoreAsync()
    {
        try
        {
            var registrations = CloseExportRegistrationAdmission();
            sessionUiLifetime.Terminate();
            await registrations;

            try
            {
                if (launcherVisualPreloadTask is not null)
                    await launcherVisualPreloadTask;
            }
            catch (Exception)
            {
                // Visual preload is optional; disposal still owns its final drain.
            }

            await launcherVisuals.DisposeAsync();
            shutdownCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            shutdownCompletion.TrySetException(exception);
        }
    }

    private bool TryEnterExportRegistration()
    {
        lock (exportRegistrationAdmissionSync)
        {
            if (exportRegistrationAdmissionClosed) return false;
            activeExportRegistrations++;
            return true;
        }
    }

    private Task CloseExportRegistrationAdmission()
    {
        lock (exportRegistrationAdmissionSync)
        {
            exportRegistrationAdmissionClosed = true;
            return activeExportRegistrations == 0
                ? Task.CompletedTask
                : (exportRegistrationsDrained ??=
                    new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private void ReleaseExportRegistration()
    {
        TaskCompletionSource? drained = null;
        lock (exportRegistrationAdmissionSync)
        {
            activeExportRegistrations--;
            if (exportRegistrationAdmissionClosed && activeExportRegistrations == 0)
                drained = exportRegistrationsDrained;
        }

        drained?.TrySetResult();
    }

    private HoyoMaintenanceUiSnapshot DiscoverHoyoMaintenance()
    {
        var roots = discovery.Discover();
        var updaterResult = roots.UpdaterRoot is null
            ? null
            : hoyoPlayExecutor.Check("gi", roots.UpdaterRoot);

        return new(
            roots.UpdaterRoot,
            updaterResult is null ? null : MapHoyoPlayStatus(updaterResult.Status));
    }

    private async Task RefreshHoyoMaintenanceAsync(
        SessionUiLease lease,
        bool refreshSessions)
    {
        var generation = Interlocked.Increment(ref hoyoRefreshGeneration);
        try
        {
            var snapshot = await Task.Run(DiscoverHoyoMaintenance, lease.CancellationToken);
            if (refreshSessions)
            {
                await sessionRefresh.RefreshNowAsync(lease.CancellationToken);
            }

            publisherStatus.Start();
            var refreshedGame = sessions.GetSnapshot("gi");
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                if (generation != Volatile.Read(ref hoyoRefreshGeneration))
                {
                    return;
                }

                updaterRoot = snapshot.UpdaterRoot;
                updaterStatus = snapshot.UpdaterStatus;
                gameFailureReason = GenshinLaunchFailureReason.None;
                gameSnapshot = refreshedGame;
                updaterScanFinished = true;
                RenderSelection();
            });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                if (generation != Volatile.Read(ref hoyoRefreshGeneration))
                {
                    return;
                }

                updaterStatus = GenshinLaunchStatus.NeedsReview;
                gameFailureReason = GenshinLaunchFailureReason.None;
                updaterScanFinished = true;
                RenderSelection();
            });
        }
    }

    private async Task RefreshWuWaMaintenanceAsync(
        SessionUiLease lease,
        bool useStoredRequest)
    {
        var generation = wuwaRefreshGeneration.Next();
        var request = useStoredRequest ? wuwaMaintenanceRequest : null;
        try
        {
            var result = await Task.Run(
                () => request is null
                    ? wuwaMaintenance.Check()
                    : wuwaMaintenance.Check(request),
                lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = wuwaRefreshGeneration.TryApply(generation, () =>
                {
                    ApplyWuWaMaintenanceResult(result);
                    wuwaScanFinished = true;
                    RenderSelection();
                });
            });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = wuwaRefreshGeneration.TryApply(generation, () =>
                {
                    wuwaMaintenanceStatus = WuWaOfficialMaintenanceStatus.NeedsReview;
                    wuwaMaintenanceReason = PublisherGameInspectionReason.InspectionFailed;
                    wuwaMaintenanceRequest = null;
                    wuwaScanFinished = true;
                    RenderSelection();
                });
            });
        }
    }

    private async Task RefreshEndfieldMaintenanceAsync(SessionUiLease lease)
    {
        var generation = endfieldMaintenanceGeneration.Next();
        try
        {
            var result = await Task.Run(
                endfieldMaintenance.Check,
                lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = endfieldMaintenanceGeneration.TryApply(generation, () =>
                {
                    ApplyEndfieldMaintenanceResult(result);
                    endfieldMaintenanceScanFinished = true;
                    RenderSelection();
                });
            });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = endfieldMaintenanceGeneration.TryApply(generation, () =>
                {
                    endfieldMaintenanceStatus = EndfieldOfficialMaintenanceStatus.NeedsReview;
                    endfieldMaintenanceReason = PublisherGameInspectionReason.InspectionFailed;
                    endfieldMaintenanceScanFinished = true;
                    RenderSelection();
                });
            });
        }
    }

    private void App_WindowReactivated(object? sender, EventArgs e)
    {
        var lease = pageLease;
        if (lease is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StartLauncherVisualPreload(lease);
            _ = RefreshHoyoMaintenanceAsync(lease, refreshSessions: false);
            if (WuWaMaintenanceInteractionPolicy.AllowsActivationRefresh(wuwaActionInFlight))
            {
                _ = RefreshWuWaMaintenanceAsync(lease, useStoredRequest: true);
            }

            if (GameSelector?.SelectedItem is GameLauncherItem { Id: "wuwa" }
                && IsWuWaAccountStatusEnabled())
            {
                publisherResourceAutomaticAttempts["wuwa"] = AccountDisplayClock();
                _ = RefreshWuWaAccountStatusAsync(lease);
            }
            else if (GameSelector?.SelectedItem is GameLauncherItem selected)
            {
                _ = RefreshPublisherResourceAutomaticallyAsync(
                    selected.Id,
                    lease,
                    selected: true);
            }

            if (!endfieldMaintenanceActionInFlight)
            {
                _ = RefreshEndfieldMaintenanceAsync(lease);
            }
        });
    }

    private void NetworkInformation_NetworkStatusChanged(object sender)
    {
        var connected = HasInternetConnection();
        var previous = Interlocked.Exchange(ref networkAvailability, connected ? 1 : 0);
        if (!connected || previous != 0)
        {
            return;
        }

        // NetworkInformation raises on a system thread. Queue one refresh on
        // the page dispatcher; the service itself coalesces any in-flight
        // manifest/code fetch and the page lease prevents work after unload.
        if (Interlocked.CompareExchange(ref networkContentRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        var generation = Volatile.Read(ref networkRefreshGeneration);
        if (!DispatcherQueue.TryEnqueue(() =>
            _ = RefreshContentAfterNetworkReactivationAsync(generation)))
        {
            Interlocked.Exchange(ref networkContentRefreshInFlight, 0);
        }
    }

    private async Task RefreshContentAfterNetworkReactivationAsync(int generation)
    {
        try
        {
            if (generation != Volatile.Read(ref networkRefreshGeneration))
            {
                return;
            }

            var lease = pageLease;
            if (lease is null)
            {
                return;
            }

            await launcherBanners.RefreshOnReactivationAsync(lease.CancellationToken);
            launcherVisualRequestedGameId = null;
            RenderSelection();
            StartLauncherVisualPreload(lease);
        }
        catch (OperationCanceledException)
        {
            // Unload/close cancels the page lease; no retry should outlive it.
        }
        catch (Exception)
        {
            // Keep the last-known-good snapshot. The next network transition
            // or scheduled refresh can try again without affecting launch.
        }
        finally
        {
            if (generation == Volatile.Read(ref networkRefreshGeneration))
            {
                Interlocked.Exchange(ref networkContentRefreshInFlight, 0);
            }
        }
    }

    private static bool HasInternetConnection()
    {
        try
        {
            // GetInternetConnectionProfile can raise an uncatchable WinRT
            // stowed exception in unpackaged WinUI apps on some Windows 11
            // builds. Link availability is sufficient here: it only decides
            // whether a transition should retry the already fail-safe feed.
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void App_EndfieldRootAutoDiscovered(object? sender, EventArgs e)
    {
        var lease = pageLease;
        if (lease is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                endfieldMaintenanceScanFinished = false;
                endfieldMaintenanceStatus = null;
                RenderSelection();
                _ = RefreshEndfieldMaintenanceAsync(lease);
            });
        });
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var lease = pageLease;
        if (lease is null || GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        var gameId = selected.Id;
        officialLauncherStatusOverride = null;
        if (gameId == "hsr" && hoyoLabExportReservation.IsHeld)
        {
            return;
        }
        if (!gameActionsInFlight.Add(gameId))
        {
            return;
        }
        var hsr120FpsPreparationFailed = false;
        string? genshin120FpsStatus = null;
        if (!TryEnterExportRegistration())
        {
            gameActionsInFlight.Remove(gameId);
            return;
        }

        try
        {
            RenderExportTools(selected);
            ShowGameActionInProgress("Checking the game once more");
            var state = launcherState.Snapshot;
            var arm = ExportArmSnapshot.From(state.Export, gameId, state.Preferences.FeatureFlags);
            if (latestExportJobs.TryGetValue(gameId, out var activeJobId)
                && !exports.GetSnapshot(activeJobId).IsFinished)
            {
                RenderSelection();
                return;
            }
            var preflightSnapshot = sessions.GetSnapshot(gameId);
            if (preflightSnapshot.Status is LocalGameStatus.Running
                && !arm.CanStartWhileGameRunning)
            {
                RenderSelection();
                return;
            }
            var exportResult = await exports.RunForLaunchAsync(
                arm,
                async cancellationToken =>
                {
                    var admissionSnapshot = sessions.GetSnapshot(gameId);
                    if (admissionSnapshot.Status is LocalGameStatus.Running
                        && arm.CanStartWhileGameRunning)
                        return true;
                    if (gameId == "hsr")
                    {
                        var preparation = app.PrepareHsr120FpsForLaunch();
                        if (!preparation.AllowsLaunch)
                        {
                            hsr120FpsPreparationFailed = true;
                            return false;
                        }
                    }
                    var result = await sessions.RequestLaunchAsync(gameId, cancellationToken);
                    _ = sessionUiLifetime.TryRun(lease, () =>
                    {
                        if (GameSelector?.SelectedItem is GameLauncherItem current
                            && current.Id == gameId)
                        {
                            gameSnapshot = result.Snapshot;
                            if (gameId == "gi")
                            {
                                gameFailureReason = genshinSession.LastLaunchFailureReason;
                                if (result.Outcome is GameLaunchRequestOutcome.Accepted
                                    && genshinSession.LastLaunchUsed120Fps)
                                {
                                    genshin120FpsStatus = gameFailureReason switch
                                    {
                                        GenshinLaunchFailureReason.FpsAttachFailed
                                            or GenshinLaunchFailureReason.FpsAttachTimedOut =>
                                            "Genshin started, but 120 FPS could not be enabled.",
                                        GenshinLaunchFailureReason.FpsLaunchUnconfirmed =>
                                            "Genshin launch was handed off, but the final 120 FPS result was not received.",
                                        _ => "Genshin started with 120 FPS.",
                                    };
                                }
                            }
                        }
                    });
                    return result.Outcome is GameLaunchRequestOutcome.Accepted
                        or GameLaunchRequestOutcome.AlreadyRunning
                        or GameLaunchRequestOutcome.AlreadyStarting;
                },
                lease.CancellationToken);
            if (arm.RequestedKinds != ExportKind.None)
            {
                var completion = exports.WaitForCompletionAsync(exportResult.JobId).AsTask();
                ExportUiJobRetention.RememberLatest(
                    latestExportJobs,
                    hoyoLabImmediateExportJobs,
                    achievementHandoffs,
                    gameId,
                    exportResult.JobId);
                var nativeHandoff = exportResult.LaunchAdmitted
                    && arm.AchievementsArmed
                    && GetAchievementSource(gameId) == AchievementExportSources.Game
                        ? app.AchievementExportHandoffs.TrackAsync(
                            gameId,
                            exportResult.JobId)
                        : null;
                _ = TrackExportJobAsync(
                    gameId,
                    exportResult.JobId,
                    completion,
                    lease,
                    nativeHandoff);
            }
            if (exportResult.LaunchAdmitted)
            {
                _ = RunAutomaticDailyCheckInOnLaunchAsync(gameId, lease);
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                if (GameSelector?.SelectedItem is GameLauncherItem current
                    && current.Id == gameId)
                {
                    gameSnapshot = sessions.GetSnapshot(gameId);
                    if (gameId == "gi")
                    {
                        gameFailureReason = GenshinLaunchFailureReason.WindowsStartFailed;
                    }
                }
            });
        }
        finally
        {
            try
            {
                _ = sessionUiLifetime.TryRun(lease, () =>
                {
                    gameActionsInFlight.Remove(gameId);
                    RenderSelection();
                    if (hsr120FpsPreparationFailed)
                    {
                        SetOfficialLauncherStatus(
                            gameId,
                            "120 FPS safety check failed. Star Rail was not started.");
                    }
                    else if (genshin120FpsStatus is not null)
                    {
                        SetOfficialLauncherStatus(gameId, genshin120FpsStatus);
                    }
                });
            }
            finally
            {
                ReleaseExportRegistration();
            }
        }
    }

    private async void WuWaAccountStatusToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is GameLauncherItem selected && selected.Id != "wuwa")
        {
            await SetPublisherConsentAsync(
                selected.Id,
                WuWaAccountStatusToggle.IsChecked == true);
            return;
        }
        var enable = WuWaAccountStatusToggle.IsChecked == true;
        wuwaAccountStatusSaveFailed = false;
        if (!enable)
        {
            // Opt-out is a session boundary first. A read-only or failing state
            // store must never keep credential work or old totals alive.
            wuwaAccountStatusSessionDisabled = true;
            wuwaAccountInitialRefreshRequested = false;
            wuwaAccountStatusUiGeneration++;
            wuwaAccountStatusActionInFlight = false;
            wuwaAccountStatus.DisableSession();
            RenderWuWaAccountStatus();
        }

        if (enable
            && launcherState.Snapshot.Preferences.FeatureFlags.WuWaAccountStatus)
        {
            wuwaAccountStatusSessionDisabled = false;
            wuwaAccountInitialRefreshRequested = true;
            RenderWuWaAccountStatus();
            if (pageLease is { } existingLease)
                await RefreshWuWaAccountStatusAsync(existingLease);
            return;
        }

        var updated = launcherState.TryUpdate(state => state with
        {
            Preferences = state.Preferences with
            {
                FeatureFlags = state.Preferences.FeatureFlags with { WuWaAccountStatus = enable },
            },
        });
        if (!updated)
        {
            wuwaAccountStatusSaveFailed = !enable;
            RenderWuWaAccountStatus();
            return;
        }

        wuwaAccountStatusSessionDisabled = !enable;
        wuwaAccountInitialRefreshRequested = enable;
        RenderWuWaAccountStatus();
        if (enable && pageLease is { } lease)
        {
            await RefreshWuWaAccountStatusAsync(lease);
        }
    }

    private async void WuWaAccountStatusRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is GameLauncherItem selected && selected.Id != "wuwa")
        {
            if (selected.Id == "ae")
                await publisherAccounts.OpenOfficialResourcePageAsync("ae");
            else
                await RefreshPublisherResourceAsync(selected.Id);
            return;
        }
        // A manual click during the local request floor must leave the actual
        // publisher result visible instead of briefly replacing it with noise.
        if (wuwaAccountStatus.IsRefreshCoolingDown)
        {
            RenderWuWaAccountStatus();
            return;
        }

        if (pageLease is { } lease)
        {
            await RefreshWuWaAccountStatusAsync(lease);
        }
    }

    private async void PublisherAccountConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.Id == "wuwa"
            || !HasPublisherConsent(selected.Id))
            return;
        var entry = PublisherAccountCatalog.Get(selected.Id);
        var summary = publisherAccounts.Current;
        var connection = entry.Provider == "HoYoLAB" ? summary.HoyoLab : summary.Skport;
        if (connection == PublisherConnectionState.Connected)
            await RefreshPublisherResourceAsync(selected.Id);
        else
            await ConnectPublisherAccountAsync(selected.Id);
    }

    private async void DailyCheckInButton_Click(object sender, RoutedEventArgs e)
    {
        if (publisherAccountActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || !GameCatalog.TryGet(selected.Id, out var definition)
            || !definition.SupportsDailyCheckIn
            || !HasPublisherConsent(selected.Id))
            return;
        publisherAccountActionInFlight = true;
        RenderSelection();
        try
        {
            var result = await publisherAccounts.CheckInAsync(
                selected.Id,
                ChoosePublisherRoleAsync,
                pageLease?.CancellationToken ?? CancellationToken.None);
            if (result.State is DailyCheckInState.Claimed or DailyCheckInState.AlreadyClaimed
                && pageLease is { } lease)
            {
                await RefreshPublisherResourceAfterCheckInAsync(selected.Id, lease);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private async void AccountConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected) return;
        if (selected.Id == "wuwa")
        {
            WuWaAccountStatusToggle.IsChecked = !IsWuWaAccountStatusEnabled();
            WuWaAccountStatusToggle_Click(WuWaAccountStatusToggle, e);
            return;
        }

        if (!HasPublisherConsent(selected.Id))
        {
            await SetPublisherConsentAsync(selected.Id, enabled: true);
        }
        if (HasPublisherConsent(selected.Id))
        {
            var entry = PublisherAccountCatalog.Get(selected.Id);
            var hoyoAccounts = publisherAccounts.HoyoLabAccounts;
            if (entry.Provider == "HoYoLAB"
                && selected.Id is "gi" or "hsr" or "zzz"
                && hoyoAccounts.Available
                && hoyoAccounts.ActiveSlotId is null)
            {
                await ShowHoyoLabAccountManagerAsync(selected.Id);
                return;
            }
            var summary = publisherAccounts.Current;
            var connection = entry.Provider == "HoYoLAB" ? summary.HoyoLab : summary.Skport;
            if (connection != PublisherConnectionState.Connected)
                await ConnectPublisherAccountAsync(selected.Id);
        }
    }

    private async void ChangePublisherAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (publisherAccountActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.Id == "wuwa") return;
        if (!HasPublisherConsent(selected.Id))
        {
            await SetPublisherConsentAsync(selected.Id, enabled: true);
        }

        if (selected.Id == "ae")
        {
            if (HasPublisherConsent(selected.Id))
                await ConnectPublisherAccountAsync(selected.Id);
            return;
        }

        if (HasPublisherConsent(selected.Id)
            && selected.Id is "gi" or "hsr" or "zzz"
            && publisherAccounts.HoyoLabAccounts.Available)
        {
            await ShowHoyoLabAccountManagerAsync(selected.Id);
            return;
        }

        var entry = PublisherAccountCatalog.Get(selected.Id);
        var summary = publisherAccounts.Current;
        var connection = entry.Provider == "HoYoLAB" ? summary.HoyoLab : summary.Skport;
        if (connection != PublisherConnectionState.Connected)
        {
            await ConnectPublisherAccountAsync(selected.Id);
            return;
        }

        publisherAccountActionInFlight = true;
        RenderSelection();
        try
        {
            await publisherAccounts.ChangeRoleAsync(
                selected.Id,
                ChoosePublisherRoleAsync,
                pageLease?.CancellationToken ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private async Task ShowHoyoLabAccountManagerAsync(string gameId)
    {
        if (gameId is not ("gi" or "hsr" or "zzz")
            || !HasPublisherConsent(gameId))
            return;

        var accountState = publisherAccounts.HoyoLabAccounts;
        if (!accountState.Available)
            return;

        var slots = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            DisplayMemberPath = nameof(HoyoLabManagerSlotItem.DisplayText),
            MaxHeight = 220,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["QuietSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
        };
        AutomationProperties.SetName(slots, "HoYoLAB account slots");
        slots.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer is ListViewItem item
                && args.Item is HoyoLabManagerSlotItem slot)
            {
                item.Background = (Brush)Application.Current.Resources["QuietSurfaceBrush"];
                item.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
                item.BorderThickness = new Thickness(1);
                item.Padding = new Thickness(8, 6, 8, 6);
                AutomationProperties.SetName(item, slot.DisplayText);
            }
        };

        var labelBox = new TextBox
        {
            Header = "Local label",
            PlaceholderText = "Name this account",
            MaxLength = HoyoLabAccountSlotRules.MaximumLabelScalars,
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(labelBox, "Local HoYoLAB account label");

        var managerStatus = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["NyxBodyFont"],
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        };
        AutomationProperties.SetName(managerStatus, "HoYoLAB account manager status");
        AutomationProperties.SetLiveSetting(managerStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        managerStatus.Text = string.Empty;

        var useButton = CreateHoyoLabManagerButton("Use", "Use the selected HoYoLAB account");
        var addButton = CreateHoyoLabManagerButton("Add", "Add a HoYoLAB account");
        var renameButton = CreateHoyoLabManagerButton("Rename", "Rename the selected HoYoLAB account");
        var forgetButton = CreateHoyoLabManagerButton("Forget", "Forget the selected HoYoLAB account");
        var chooseCharacterButton = CreateHoyoLabManagerButton(
            "Choose Region",
            "Choose the region for this game");
        var actionButtons = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 300,
        };
        actionButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actionButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddManagerAction(useButton, 0, 0);
        AddManagerAction(addButton, 0, 1);
        AddManagerAction(renameButton, 0, 2);
        AddManagerAction(forgetButton, 1, 0);
        Grid.SetColumnSpan(chooseCharacterButton, 2);
        AddManagerAction(chooseCharacterButton, 1, 1);

        void AddManagerAction(Button button, int row, int column)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            actionButtons.Children.Add(button);
        }

        var content = new StackPanel
        {
            Spacing = 8,
            Width = Math.Clamp(ActualWidth - 96, 300, 680),
        };
        content.Children.Add(slots);
        content.Children.Add(labelBox);
        content.Children.Add(actionButtons);
        content.Children.Add(managerStatus);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "HoYoLAB accounts & region",
            Content = content,
            Background = (Brush)Application.Current.Resources["SettingsSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
            CloseButtonStyle = (Style)Application.Current.Resources["NyxDialogQuietStyle"],
            MinWidth = Math.Clamp(ActualWidth - 64, 320, 720),
            MaxWidth = Math.Clamp(ActualWidth - 64, 320, 720),
        };
        ApplyNyxAccentResources(dialog.Resources);

        string? selectedSlotId = null;
        var suppressSelectionChanged = false;
        var managerActionInFlight = false;
        string? pendingRegionSlotId = null;

        HoyoLabManagerSlotItem? SelectedItem() =>
            slots.SelectedItem as HoyoLabManagerSlotItem;

        HoyoLabAccountSlot? SelectedSlot() =>
            selectedSlotId is null
                ? null
                : publisherAccounts.HoyoLabAccounts.Slots.FirstOrDefault(slot =>
                    string.Equals(slot.Id, selectedSlotId, StringComparison.Ordinal));

        void RenderManagerSlots(string? preserveSelection)
        {
            var snapshot = publisherAccounts.HoyoLabAccounts;
            var items = snapshot.Slots
                .Select(slot => new HoyoLabManagerSlotItem(
                    slot,
                    FormatHoyoLabSlotStatus(slot, snapshot.ActiveSlotId)))
                .ToArray();
            suppressSelectionChanged = true;
            slots.ItemsSource = items;
            slots.SelectedIndex = -1;
            if (preserveSelection is not null)
            {
                var item = items.FirstOrDefault(candidate =>
                    string.Equals(candidate.Slot.Id, preserveSelection, StringComparison.Ordinal));
                if (item is not null)
                    slots.SelectedItem = item;
            }
            suppressSelectionChanged = false;
            selectedSlotId = slots.SelectedItem is HoyoLabManagerSlotItem selected
                ? selected.Slot.Id
                : null;
            if (items.Length == 0 && !managerActionInFlight)
                managerStatus.Text = string.Empty;
            UpdateManagerActionStates();
        }

        void UpdateManagerActionStates()
        {
            var selected = SelectedItem();
            var hasSelected = selected is not null;
            var enabled = !managerActionInFlight;
            slots.IsEnabled = enabled;
            labelBox.IsEnabled = enabled;
            useButton.IsEnabled = enabled && hasSelected && selected!.Slot.RemovalPending == false;
            addButton.IsEnabled = enabled;
            renameButton.IsEnabled = enabled && hasSelected && selected!.Slot.RemovalPending == false;
            forgetButton.IsEnabled = enabled && hasSelected;
            chooseCharacterButton.IsEnabled = enabled
                && hasSelected
                && string.Equals(
                    publisherAccounts.HoyoLabAccounts.ActiveSlotId,
                    selected!.Slot.Id,
                    StringComparison.Ordinal);
        }

        async Task RunManagerActionAsync(
            Func<CancellationToken, Task> action,
            string? preserveSelection,
            bool clearSelection = false)
        {
            if (managerActionInFlight || publisherAccountActionInFlight)
                return;
            managerActionInFlight = true;
            publisherAccountActionInFlight = true;
            UpdateManagerActionStates();
            RenderSelection();
            try
            {
                await action(pageLease?.CancellationToken ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                managerStatus.Text = "Canceled";
            }
            finally
            {
                managerActionInFlight = false;
                publisherAccountActionInFlight = false;
                RenderManagerSlots(clearSelection ? null : preserveSelection);
                RenderSelection();
            }
        }

        void QueueRegionChoice(string slotId)
        {
            if (managerActionInFlight
                || !string.Equals(
                    publisherAccounts.HoyoLabAccounts.ActiveSlotId,
                    slotId,
                    StringComparison.Ordinal))
            {
                return;
            }

            selectedSlotId = slotId;
            pendingRegionSlotId = slotId;
            dialog.Hide();
        }

        async Task RefreshBoundSlotAfterChangeAsync(
            PublisherConnectionState? connectionState,
            CancellationToken cancellationToken)
        {
            if (connectionState != PublisherConnectionState.Connected)
                return;

            publisherResourceAutomaticAttempts.Remove("gi");
            publisherResourceAutomaticAttempts.Remove("hsr");
            publisherResourceAutomaticAttempts.Remove("zzz");
            if (publisherAccounts.GetHoyoLabIdentity(gameId)?.IsBound != true)
                return;

            await publisherAccounts.RefreshResourceAsync(
                gameId,
                rolePicker: null,
                cancellationToken);
            publisherResourceAutomaticAttempts[gameId] = AccountDisplayClock();
        }

        slots.SelectionChanged += (_, _) =>
        {
            if (suppressSelectionChanged)
                return;
            selectedSlotId = SelectedItem()?.Slot.Id;
            UpdateManagerActionStates();
        };

        addButton.Click += async (_, _) =>
        {
            var label = labelBox.Text.Trim();
            if (label.Length == 0)
            {
                managerStatus.Text = "Enter a local label.";
                return;
            }

            PublisherConnectionState? connectionState = null;
            await RunManagerActionAsync(
                async cancellationToken =>
                {
                    connectionState = await publisherAccounts.AddHoyoLabAccountAsync(
                        label,
                        gameId,
                        cancellationToken);
                    await RefreshBoundSlotAfterChangeAsync(connectionState, cancellationToken);
                    managerStatus.Text = FormatHoyoLabManagerConnectionState(connectionState.Value);
                },
                selectedSlotId);
            var activeSlotId = publisherAccounts.HoyoLabAccounts.ActiveSlotId;
            if (connectionState == PublisherConnectionState.Connected
                && activeSlotId is not null
                && publisherAccounts.GetHoyoLabIdentity(gameId)?.IsBound != true)
            {
                QueueRegionChoice(activeSlotId);
            }
        };

        renameButton.Click += async (_, _) =>
        {
            var slot = SelectedSlot();
            var label = labelBox.Text.Trim();
            if (slot is null)
            {
                return;
            }
            if (label.Length == 0)
            {
                managerStatus.Text = "Enter a local label.";
                return;
            }

            await RunManagerActionAsync(
                async cancellationToken =>
                {
                    var renamed = await publisherAccounts.RenameHoyoLabAccountAsync(
                        slot.Id,
                        label,
                        cancellationToken);
                    managerStatus.Text = renamed ? "Renamed" : "Rename failed";
                },
                slot.Id);
        };

        useButton.Click += async (_, _) =>
        {
            var slot = SelectedSlot();
            if (slot is null)
            {
                return;
            }

            PublisherConnectionState? connectionState = null;
            await RunManagerActionAsync(
                async cancellationToken =>
                {
                    connectionState = await publisherAccounts.UseHoyoLabAccountAsync(
                        slot.Id,
                        gameId,
                        cancellationToken);
                    await RefreshBoundSlotAfterChangeAsync(connectionState, cancellationToken);
                    managerStatus.Text = FormatHoyoLabManagerConnectionState(connectionState.Value);
                },
                slot.Id);
            if (connectionState == PublisherConnectionState.Connected
                && publisherAccounts.GetHoyoLabIdentity(gameId)?.IsBound != true)
            {
                QueueRegionChoice(slot.Id);
            }
        };

        forgetButton.Click += async (_, _) =>
        {
            var slot = SelectedSlot();
            if (slot is null)
            {
                return;
            }

            selectedSlotId = null;
            await RunManagerActionAsync(
                async cancellationToken =>
                {
                    var forgotten = await publisherAccounts.ForgetHoyoLabAccountAsync(
                        slot.Id,
                        cancellationToken);
                    managerStatus.Text = forgotten ? "Forgotten" : "Removal pending";
                },
                preserveSelection: null,
                clearSelection: true);
        };

        chooseCharacterButton.Click += (_, _) =>
        {
            if (managerActionInFlight)
                return;
            var selected = SelectedSlot();
            if (selected is null
                || !string.Equals(
                    publisherAccounts.HoyoLabAccounts.ActiveSlotId,
                    selected.Id,
                    StringComparison.Ordinal))
            {
                return;
            }
            QueueRegionChoice(selected.Id);
        };

        RenderManagerSlots(preserveSelection: null);
        try
        {
            while (true)
            {
                using var cancellationRegistration = (pageLease?.CancellationToken
                    ?? CancellationToken.None).Register(dialog.Hide);
                await dialog.ShowAsync().AsTask(pageLease?.CancellationToken ?? CancellationToken.None);
                var regionSlotId = pendingRegionSlotId;
                pendingRegionSlotId = null;
                if (regionSlotId is null)
                    break;
                await RunManagerActionAsync(
                    async cancellationToken =>
                    {
                        var result = await publisherAccounts.ChangeRoleAsync(
                            gameId,
                            ChoosePublisherRoleAsync,
                            cancellationToken);
                        if (result is not null)
                            publisherResourceAutomaticAttempts[gameId] = AccountDisplayClock();
                        managerStatus.Text = result is null
                            ? "Region unchanged"
                            : "Region updated";
                    },
                    regionSlotId);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            slots.ItemsSource = null;
        }
    }

    private static Button CreateHoyoLabManagerButton(string content, string accessibleName)
    {
        var button = new Button
        {
            Content = content,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(8, 0, 8, 0),
            FontFamily = (FontFamily)Application.Current.Resources["NyxBodyFont"],
            FontSize = 9,
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static string FormatHoyoLabSlotStatus(
        HoyoLabAccountSlot slot,
        string? activeSlotId)
    {
        var statuses = new List<string>();
        if (string.Equals(slot.Id, activeSlotId, StringComparison.Ordinal))
            statuses.Add("Active");
        if (slot.IsLegacy)
            statuses.Add("Legacy");
        if (slot.RemovalPending)
            statuses.Add("Removal pending");
        return statuses.Count == 0
            ? "Available"
            : string.Join(" \u00B7 ", statuses);
    }

    private static string FormatHoyoLabManagerConnectionState(
        PublisherConnectionState state) => state switch
        {
            PublisherConnectionState.LoginRequired => "Sign in required",
            PublisherConnectionState.Connected => "Connected",
            PublisherConnectionState.Connecting => "Connecting",
            PublisherConnectionState.NeedsReview => "Try again",
            PublisherConnectionState.NotConnected => "Not connected",
            _ => state.ToString(),
        };

    private void AutomaticDailyCheckInToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected) return;
        var enabled = AutomaticDailyCheckInToggle.IsChecked == true;
        var saved = launcherState.TryUpdate(state =>
        {
            var gameIds = state.Preferences.AutomaticDailyCheckInGames.ToHashSet(StringComparer.Ordinal);
            if (enabled) gameIds.Add(selected.Id);
            else gameIds.Remove(selected.Id);
            return state with
            {
                Preferences = state.Preferences with
                {
                    AutomaticDailyCheckInGames = gameIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
                },
            };
        });
        if (!saved)
        {
            AutomaticDailyCheckInToggle.IsChecked = !enabled;
            SetStableExportStatus("Nyx could not save the daily check-in choice.");
        }
    }

    private async Task RunAutomaticDailyCheckInOnLaunchAsync(string gameId, SessionUiLease lease)
    {
        var preferences = launcherState.Snapshot.Preferences;
        if (!preferences.AutomaticDailyCheckInGames.Contains(gameId, StringComparer.Ordinal)
            || !GameCatalog.TryGet(gameId, out var definition)
            || !definition.SupportsDailyCheckIn
            || !HasPublisherConsent(gameId)
            || !automaticDailyCheckInsInFlight.Add(gameId)) return;
        _ = DispatcherQueue.TryEnqueue(RenderSelection);
        try
        {
            var result = await publisherAccounts.CheckInAsync(
                gameId,
                rolePicker: null,
                lease.CancellationToken);
            if (result.State is DailyCheckInState.Claimed or DailyCheckInState.AlreadyClaimed)
            {
                await publisherAccounts.RefreshResourceAsync(
                    gameId,
                    rolePicker: null,
                    lease.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            automaticDailyCheckInsInFlight.Remove(gameId);
            _ = DispatcherQueue.TryEnqueue(RenderSelection);
        }
    }

    private async Task SetPublisherConsentAsync(string gameId, bool enabled)
    {
        if (publisherAccountActionInFlight) return;
        var entry = PublisherAccountCatalog.Get(gameId);
        publisherAccountActionInFlight = true;
        try
        {
            var cleanupResult = PublisherConnectionState.NotConnected;
            if (!enabled)
            {
                // The in-memory service gate closes synchronously at the start
                // of this call, before cancellation or profile deletion can fail.
                try
                {
                    cleanupResult = await publisherAccounts.RevokeConsentAsync(
                        gameId,
                        pageLease?.CancellationToken ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    cleanupResult = PublisherConnectionState.NeedsReview;
                }
            }
            else
            {
                bool prepared;
                try
                {
                    prepared = await publisherAccounts.PrepareConsentEnableAsync(
                        entry.Provider,
                        pageLease?.CancellationToken ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    prepared = false;
                }
                if (!prepared)
                {
                    publisherConsentCleanupFailures.Add(entry.Provider);
                    return;
                }
            }

            var updated = launcherState.TryUpdatePublisherCleanupPending(
                entry.Provider,
                cleanupPending: !enabled,
                accountAccess: enabled);
            if (!updated)
            {
                publisherConsentSaveFailures.Add(entry.Provider);
                return;
            }

            publisherConsentSaveFailures.Remove(entry.Provider);
            if (enabled)
            {
                publisherConsentCleanupFailures.Remove(entry.Provider);
                publisherAccounts.EnableConsent(entry.Provider);
                return;
            }

            if (cleanupResult != PublisherConnectionState.NotConnected
                || !publisherAccounts.CompleteConsentRevocation(entry.Provider))
            {
                publisherConsentCleanupFailures.Add(entry.Provider);
                return;
            }

            var cleanupRecorded = launcherState.TryUpdatePublisherCleanupPending(
                entry.Provider,
                cleanupPending: false,
                accountAccess: false);
            if (cleanupRecorded)
            {
                publisherConsentCleanupFailures.Remove(entry.Provider);
            }
            else
            {
                publisherConsentSaveFailures.Add(entry.Provider);
            }
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private async Task<PublisherRoleBinding?> ChoosePublisherRoleAsync(
        IReadOnlyList<PublisherRoleChoice> choices,
        CancellationToken cancellationToken)
    {
        if (choices.Count == 0) return null;
        var list = new ListView
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(PublisherRoleChoice.DisplayText),
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 280,
        };
        AutomationProperties.SetName(list, "Available HoYoLAB regions");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Choose Region",
            Content = list,
            PrimaryButtonText = "Use this region",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = false,
        };
        list.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is PublisherRoleChoice;
        try
        {
            using var cancellationRegistration = cancellationToken.Register(() =>
                DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync().AsTask(cancellationToken);
            return result == ContentDialogResult.Primary
                && list.SelectedItem is PublisherRoleChoice selected
                    ? selected.Binding
                    : null;
        }
        finally
        {
            list.ItemsSource = null;
        }
    }

    private async Task ConnectPublisherAccountAsync(string gameId)
    {
        if (publisherAccountActionInFlight || !HasPublisherConsent(gameId)) return;
        publisherAccountActionInFlight = true;
        RenderSelection();
        try
        {
            var state = await publisherAccounts.ConnectAsync(
                gameId,
                pageLease?.CancellationToken ?? CancellationToken.None);
            if (state == PublisherConnectionState.Connected)
            {
                await publisherAccounts.RefreshResourceAsync(
                    gameId,
                    ChoosePublisherRoleAsync,
                    pageLease?.CancellationToken ?? CancellationToken.None);
                publisherResourceAutomaticAttempts[gameId] = AccountDisplayClock();
                if (pageLease is { } lease)
                    _ = RefreshPublisherResourcesOnStartupAsync(lease, gameId);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private async Task RefreshPublisherResourceAsync(string gameId)
    {
        if (publisherAccountActionInFlight || !HasPublisherConsent(gameId)) return;
        publisherAccountActionInFlight = true;
        RenderSelection();
        try
        {
            await publisherAccounts.RefreshResourceAsync(
                gameId,
                ChoosePublisherRoleAsync,
                pageLease?.CancellationToken ?? CancellationToken.None);
            publisherResourceAutomaticAttempts[gameId] = AccountDisplayClock();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private async Task RefreshPublisherResourceAutomaticallyAsync(
        string gameId,
        SessionUiLease lease,
        bool selected)
    {
        if (gameId is not ("gi" or "hsr" or "zzz")) return;
        var entry = PublisherAccountCatalog.Get(gameId);
        var now = AccountDisplayClock();
        var summary = publisherAccounts.Current;
        var resource = summary.Resources.TryGetValue(gameId, out var snapshot) ? snapshot : null;
        if (!entry.SupportsNumericResource
            || !HasPublisherConsent(gameId)
            || (resource is not null
                && PublisherResourceRefreshPolicy.IsFresh(resource.ObservedAt, now))
            || !PublisherResourceRefreshPolicy.IsDue(
                publisherResourceAutomaticAttempts.TryGetValue(gameId, out var attemptedAt)
                    ? attemptedAt
                    : null,
                now,
                selected))
        {
            return;
        }

        publisherResourceAutomaticAttempts[gameId] = now;
        try
        {
            await publisherAccounts.RefreshResourceAsync(
                gameId,
                rolePicker: null,
                lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                if (GameSelector?.SelectedItem is GameLauncherItem current
                    && string.Equals(current.Id, gameId, StringComparison.Ordinal))
                    RenderSelection();
            });
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshPublisherResourcesOnStartupAsync(
        SessionUiLease lease,
        string? skipGameId = null)
    {
        var selectedId = (GameSelector?.SelectedItem as GameLauncherItem)?.Id;
        if (selectedId == "wuwa" && IsWuWaAccountStatusEnabled())
        {
            publisherResourceAutomaticAttempts["wuwa"] = AccountDisplayClock();
            await RefreshWuWaAccountStatusAsync(lease);
        }

        if (lease.CancellationToken.IsCancellationRequested)
            return;

        foreach (var gameId in new[]
                 {
                     selectedId is "gi" or "hsr" or "zzz" ? selectedId : null,
                     "gi",
                     "hsr",
                     "zzz",
                 }
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            if (gameId == skipGameId) continue;
            await RefreshPublisherResourceAutomaticallyAsync(
                gameId,
                lease,
                selected: gameId == selectedId);
            if (lease.CancellationToken.IsCancellationRequested) return;
        }

        if (selectedId != "wuwa" && IsWuWaAccountStatusEnabled())
        {
            publisherResourceAutomaticAttempts["wuwa"] = AccountDisplayClock();
            await RefreshWuWaAccountStatusAsync(lease);
        }
    }

    private async Task RefreshPublisherResourceAfterCheckInAsync(
        string selectedGameId,
        SessionUiLease lease)
    {
        await publisherAccounts.RefreshResourceAsync(
            selectedGameId,
            ChoosePublisherRoleAsync,
            lease.CancellationToken);
        publisherResourceAutomaticAttempts[selectedGameId] = AccountDisplayClock();
    }

    private async Task DisconnectPublisherAccountAsync(string gameId)
    {
        if (publisherAccountActionInFlight || !HasPublisherConsent(gameId)) return;
        publisherAccountActionInFlight = true;
        RenderSelection();
        try
        {
            await publisherAccounts.DisconnectAsync(gameId, pageLease?.CancellationToken ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            publisherAccountActionInFlight = false;
            RenderSelection();
        }
    }

    private bool HasPublisherConsent(string gameId)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        return publisherAccounts.HasConsent(entry.Provider);
    }

    private async Task RefreshWuWaAccountStatusAsync(SessionUiLease lease)
    {
        if (wuwaAccountStatusActionInFlight
            || !IsWuWaAccountStatusEnabled())
        {
            return;
        }

        var uiGeneration = wuwaAccountStatusUiGeneration;
        wuwaAccountStatusActionInFlight = true;
        _ = sessionUiLifetime.TryRun(lease, UpdateWuWaAccountStatusIfSelected);
        try
        {
            await wuwaAccountStatus.RefreshAsync(lease.CancellationToken);
        }
        finally
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                if (uiGeneration != wuwaAccountStatusUiGeneration) return;
                wuwaAccountStatusActionInFlight = false;
                UpdateWuWaAccountStatusIfSelected();
            });
        }
    }

    private void UpdateWuWaAccountStatusIfSelected()
    {
        if (GameSelector?.SelectedItem is GameLauncherItem { Id: "wuwa" })
            RenderWuWaAccountStatus();
    }

    private async Task ChooseGameFolderAsync()
    {
        var lease = pageLease;
        if (lease is null
            || endfieldFolderActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.IsCustom)
        {
            return;
        }

        string? completionMessage = null;
        endfieldFolderActionInFlight = true;
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || lease.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            var accepted = await Task.Run(
                () => IsValidManualInstallRoot(selected.Id, folder.Path),
                lease.CancellationToken);
            if (!accepted)
            {
                completionMessage = "That is not the complete official game folder. Nothing was saved.";
                return;
            }

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            if (!launcherState.TryUpdate(state =>
            {
                var roots = new Dictionary<string, string>(state.Preferences.ManualInstallRoots, StringComparer.Ordinal)
                {
                    [selected.Id] = canonical,
                };
                return state with
                {
                    Preferences = state.Preferences with
                    {
                        ManualInstallRoots = new ReadOnlyDictionary<string, string>(roots),
                        EndfieldInstallRoot = selected.Id == "ae"
                            ? canonical
                            : state.Preferences.EndfieldInstallRoot,
                    },
                };
            }))
            {
                completionMessage = "Nyx could not save that folder. Nothing was changed.";
                return;
            }
            await sessionRefresh.RefreshNowAsync(lease.CancellationToken);
            if (selected.Id == "ae") await RefreshEndfieldMaintenanceAsync(lease);
            if (selected.Id == "wuwa") await RefreshWuWaMaintenanceAsync(lease, useStoredRequest: false);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            completionMessage = "Nyx could not check that folder.";
        }
        finally
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                endfieldFolderActionInFlight = false;
                RenderSelection();
                if (completionMessage is not null)
                {
                    HeroDescription.Text = completionMessage;
                    SetLaunchDetail(completionMessage);
                }
            });
        }
    }

    private bool IsValidManualInstallRoot(string gameId, string root) => gameId switch
    {
        "gi" => genshinInspection.InspectGame(root, GenshinPathOrigin.PreviouslySaved).Status
            is GenshinInspectionStatus.Ready,
        "hsr" or "zzz" => hoyoIdentity.Inspect(gameId, root).Status is HoyoInspectionStatus.Ready,
        "wuwa" or "ae" => publisherGameLaunchService.CheckGame(gameId, root).Status
            is PublisherGameLaunchStatus.Ready or PublisherGameLaunchStatus.Running,
        _ => false,
    };

    private async void OpenScreenshotFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var lease = pageLease;
        if (lease is null
            || screenshotFolderActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || !GameCatalog.TryGet(selected.Id, out var definition)
            || !definition.SupportsScreenshots)
        {
            return;
        }

        var gameId = selected.Id;
        screenshotFolderActionInFlight = true;
        StableOpenScreenshotFolderButton.IsEnabled = false;
        SetOfficialLauncherStatus(gameId, "Checking the screenshot folder...");
        try
        {
            var result = await Task.Run(
                () => app.ResolveScreenshotFolder(gameId),
                lease.CancellationToken);
            if (lease.CancellationToken.IsCancellationRequested
                || GameSelector?.SelectedItem is not GameLauncherItem current
                || current.Id != gameId)
            {
                return;
            }

            if (result.Status is not GameScreenshotFolderStatus.Ready
                || string.IsNullOrWhiteSpace(result.FolderPath))
            {
                SetOfficialLauncherStatus(gameId, result.Status switch
                {
                    GameScreenshotFolderStatus.Ready =>
                        "Windows could not open that screenshot folder.",
                    GameScreenshotFolderStatus.Unavailable =>
                        "Screenshot folder is unavailable for this game.",
                    _ => "Screenshot folders are not supported for this game.",
                });
                return;
            }

            SetOfficialLauncherStatus(
                gameId,
                await Windows.System.Launcher.LaunchFolderPathAsync(result.FolderPath)
                    ? "Screenshot folder opened."
                    : "Windows could not open that screenshot folder.");
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetOfficialLauncherStatus(gameId, "Windows could not open that screenshot folder.");
        }
        finally
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                screenshotFolderActionInFlight = false;
                if (GameSelector?.SelectedItem is GameLauncherItem current)
                    SyncRedesignedControls(current);
            });
        }
    }

    private void Fps120Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected
            || !GameCatalog.TryGet(selected.Id, out var definition)
            || !definition.Supports120Fps)
        {
            return;
        }

        var enabled = Fps120Toggle.IsChecked == true;
        if (!app.TrySet120FpsOnLaunch(selected.Id, enabled))
        {
            Fps120Toggle.IsChecked = app.Is120FpsOnLaunch(selected.Id);
            SetOfficialLauncherStatus(selected.Id, "Nyx could not save the 120 FPS setting.");
            return;
        }

        Fps120Toggle.IsChecked = app.Is120FpsOnLaunch(selected.Id);
    }

    private void SetOfficialLauncherStatus(string gameId, string message)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.IsCustom
            || selected.Id != gameId)
        {
            return;
        }

        officialLauncherStatusOverride = message;
        SetLaunchDetail(message);
    }

    private void SetLaunchDetail(string message)
    {
        LaunchDetail.Text = message ?? string.Empty;
        AutomationProperties.SetName(
            LaunchDetail,
            string.IsNullOrWhiteSpace(message) ? "Launch status" : message);
        ToolTipService.SetToolTip(
            LaunchDetail,
            string.IsNullOrWhiteSpace(message) ? null : message);
        ToolTipService.SetToolTip(
            LaunchButton,
            string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private void SetStableExportStatus(string? message)
    {
        StableExportStatusText.Text = message ?? string.Empty;
        StableExportStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomationProperties.SetName(StableExportStatusText, StableExportStatusText.Text);
        AutomationProperties.SetHelpText(StableExportStatusText, StableExportStatusText.Text);
        ToolTipService.SetToolTip(
            StableExportStatusText,
            string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private async void OpenUpdaterButton_Click(object sender, RoutedEventArgs e)
    {
        var lease = pageLease;
        if (lease is null || GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        officialLauncherStatusOverride = null;
        if (selected.Id == "wuwa")
        {
            await OpenWuWaMaintenanceAsync(lease);
            return;
        }

        if (selected.Id == "ae")
        {
            await OpenEndfieldMaintenanceAsync(lease);
            return;
        }

        if (updaterActionInFlight
            || updaterRoot is null
            || selected.Id is not ("gi" or "hsr" or "zzz"))
        {
            return;
        }

        PreInstallNoticeButton.IsEnabled = false;
        updaterActionInFlight = true;
        OpenUpdaterButton.IsEnabled = false;
        OpenUpdaterButton.Content = "Opening…";

        try
        {
            var selectedGameId = selected.Id;
            var result = await hoyoPlayExecutor.OpenOrObserveCurrentAsync(
                selectedGameId,
                updaterRoot,
                lease.CancellationToken);
            var status = MapHoyoPlayStatus(result.Status);
            _ = sessionUiLifetime.TryRun(lease, () => updaterStatus = status);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(
                lease,
                () => updaterStatus = GenshinLaunchStatus.LaunchFailed);
        }
        finally
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                updaterActionInFlight = false;
                RenderSelection();
            });
        }
    }

    private async Task OpenEndfieldMaintenanceAsync(SessionUiLease lease)
    {
        if (endfieldMaintenanceActionInFlight
            || endfieldFolderActionInFlight
            || GameSelector?.SelectedItem is not GameLauncherItem { Id: "ae" })
        {
            return;
        }

        var actionAdmission = endfieldUiActions.TryEnter(EndfieldUiActionKind.OpenMaintenance);
        if (actionAdmission is null)
        {
            return;
        }

        var generation = endfieldMaintenanceGeneration.Next();
        endfieldMaintenanceActionInFlight = true;
        OpenUpdaterButton.IsEnabled = false;
        OpenUpdaterButton.Content = "Opening…";

        try
        {
            var result = await endfieldMaintenance.OpenOrObserveCurrentAsync(
                lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = endfieldMaintenanceGeneration.TryApply(generation, () =>
                {
                    ApplyEndfieldMaintenanceResult(result);
                    RenderSelection();
                });
            });

            if (result.Status is EndfieldOfficialMaintenanceStatus.Opened
                && endfieldMaintenanceGeneration.IsCurrent(generation))
            {
                var observed = await BoundedMaintenanceObservation.ObserveAsync(
                    token => Task.Run(endfieldMaintenance.Check, token),
                    observation => observation.Status is not EndfieldOfficialMaintenanceStatus.Ready,
                    EndfieldLaunchObservationCount,
                    WuWaLaunchObservationInterval,
                    lease.CancellationToken);
                _ = sessionUiLifetime.TryRun(lease, () =>
                {
                    _ = endfieldMaintenanceGeneration.TryApply(generation, () =>
                    {
                        ApplyEndfieldMaintenanceResult(observed);
                        RenderSelection();
                    });
                });
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = endfieldMaintenanceGeneration.TryApply(generation, () =>
                {
                    endfieldMaintenanceStatus = EndfieldOfficialMaintenanceStatus.Failed;
                    endfieldMaintenanceReason = PublisherGameInspectionReason.InspectionFailed;
                });
            });
        }
        finally
        {
            actionAdmission.Dispose();
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                endfieldMaintenanceActionInFlight = false;
                RenderSelection();
            });
        }
    }

    private async Task OpenWuWaMaintenanceAsync(SessionUiLease lease)
    {
        if (wuwaActionInFlight
            || wuwaMaintenanceRequest is null
            || GameSelector?.SelectedItem is not GameLauncherItem { Id: "wuwa" })
        {
            return;
        }

        var request = wuwaMaintenanceRequest;
        var generation = wuwaRefreshGeneration.Next();
        PreInstallNoticeButton.IsEnabled = false;
        wuwaActionInFlight = true;
        OpenUpdaterButton.IsEnabled = false;
        OpenUpdaterButton.Content = "Opening…";

        try
        {
            var result = await wuwaMaintenance.OpenOrObserveCurrentAsync(
                request,
                lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = wuwaRefreshGeneration.TryApply(generation, () =>
                {
                    ApplyWuWaMaintenanceResult(result);
                    RenderSelection();
                });
            });

            if (result.Status is WuWaOfficialMaintenanceStatus.Opened
                && result.Request is not null
                && wuwaRefreshGeneration.IsCurrent(generation))
            {
                var observed = await BoundedMaintenanceObservation.ObserveAsync(
                    token => Task.Run(() => wuwaMaintenance.Check(result.Request), token),
                    observation => observation.Status is not WuWaOfficialMaintenanceStatus.Ready,
                    WuWaLaunchObservationCount,
                    WuWaLaunchObservationInterval,
                    lease.CancellationToken);
                _ = sessionUiLifetime.TryRun(lease, () =>
                {
                    _ = wuwaRefreshGeneration.TryApply(generation, () =>
                    {
                        ApplyWuWaMaintenanceResult(observed);
                        RenderSelection();
                    });
                });
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                _ = wuwaRefreshGeneration.TryApply(generation, () =>
                {
                    wuwaMaintenanceStatus = WuWaOfficialMaintenanceStatus.Failed;
                    wuwaMaintenanceReason = PublisherGameInspectionReason.InspectionFailed;
                    wuwaMaintenanceRequest = null;
                });
            });
        }
        finally
        {
            _ = sessionUiLifetime.TryRun(lease, () =>
            {
                wuwaActionInFlight = false;
                RenderSelection();
            });
        }
    }

    private async void BrandLockup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Open pengo.gg?",
            Content = "Open the Nyx website in your browser?",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            Background = (Brush)Application.Current.Resources["SettingsSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            PrimaryButtonStyle = (Style)Application.Current.Resources["NyxDialogPrimaryStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["NyxDialogQuietStyle"],
        };
        ApplyNyxAccentResources(dialog.Resources);
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            await OpenFixedDestinationAsync(new Uri("https://pengo.gg"), "the Nyx website");
        }
    }

    private void RailSurface_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        AddGameButton.Opacity = 0.9;

    private void RailSurface_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), AddGameButton))
        {
            AddGameButton.Opacity = 0.08;
        }
    }

    private void AddGameButton_GotFocus(object sender, RoutedEventArgs e) =>
        AddGameButton.Opacity = 0.9;

    private void AddGameButton_LostFocus(object sender, RoutedEventArgs e) =>
        AddGameButton.Opacity = 0.08;

    private async void RefreshCodesButton_Click(object sender, RoutedEventArgs e)
    {
        var lease = pageLease;
        if (lease is null || !RefreshCodesButton.IsEnabled)
        {
            return;
        }

        RefreshCodesButton.IsEnabled = false;
        CodeRefreshStatusText.Visibility = Visibility.Visible;
        CodeRefreshStatusText.Text = "UPDATING";
        try
        {
            var succeeded = await launcherBanners.RefreshCodesManualAsync(lease.CancellationToken);
            if (lease.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            CodeRefreshStatusText.Text = succeeded ? "UP TO DATE" : "KEPT SAFE COPY";
            if (succeeded)
            {
                RenderSelection();
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!lease.CancellationToken.IsCancellationRequested)
            {
                CodeRefreshStatusText.Text = "KEPT SAFE COPY";
            }
        }
        finally
        {
            if (!lease.CancellationToken.IsCancellationRequested && ReferenceEquals(pageLease, lease))
            {
                RefreshCodesButton.IsEnabled = true;
            }
        }
    }

    private async void RedemptionCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string code }
            || string.IsNullOrWhiteSpace(code)
            || GameSelector?.SelectedItem is not GameLauncherItem { IsCustom: false } selected
            || !TryBuildRedemptionUri(selected.Id, code, out var destination))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Open Redemption Page?",
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.None,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            await OpenFixedDestinationAsync(destination, $"{selected.DisplayName} redemption");
        }
    }

    private void RedemptionCodeCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string code }
            || string.IsNullOrWhiteSpace(code)
            || GameSelector?.SelectedItem is not GameLauncherItem { IsCustom: false } selected) return;
        var data = new DataPackage();
        data.SetText(code);
        Clipboard.SetContent(data);
        copiedCodeRow?.ResetCopyState();
        copiedCodeRow = RedemptionCodeRows.FirstOrDefault(row => string.Equals(row.Code, code, StringComparison.Ordinal));
        copiedCodeRow?.MarkPreviouslyCopied();
        copiedCodeRow?.MarkCopied();
        PersistCopiedRedemptionCode(selected.Id, code);
        codeCopyResetTimer.Stop();
        copiedCodeValue = code;
        codeCopyResetTimer.Start();
        NyxToolsStatusText.Text = $"Copied {code}.";
    }

    private static bool TryBuildRedemptionUri(string gameId, string code, out Uri destination)
    {
        destination = null!;
        if (!RedemptionUrlTemplates.TryGetValue(gameId, out var template)) return false;
        var escapedCode = Uri.EscapeDataString(code);
        if (!Uri.TryCreate(
                string.Format(CultureInfo.InvariantCulture, template, escapedCode),
                UriKind.Absolute,
                out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !parsed.IsDefaultPort
            || parsed.Host is not ("genshin.hoyoverse.com"
                or "hsr.hoyoverse.com"
                or "zenless.hoyoverse.com"))
        {
            return false;
        }

        destination = parsed;
        return true;
    }

    private void PersistCopiedRedemptionCode(string gameId, string code)
    {
        _ = launcherState.TryUpdate(state =>
        {
            var values = state.Preferences.CopiedRedemptionCodes.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.Ordinal);
            var gameCodes = values.TryGetValue(gameId, out var existing)
                ? existing.ToList()
                : [];
            gameCodes.RemoveAll(value => string.Equals(value, code, StringComparison.Ordinal));
            gameCodes.Insert(0, code);
            if (gameCodes.Count > 100) gameCodes.RemoveRange(100, gameCodes.Count - 100);
            values[gameId] = gameCodes.AsReadOnly();
            return state with
            {
                Preferences = state.Preferences with { CopiedRedemptionCodes = values },
            };
        });
    }

    private void CodeCopyResetTimer_Tick(object? sender, object e)
    {
        codeCopyResetTimer.Stop();
        copiedCodeValue = null;
        copiedCodeRow?.ResetCopyState();
        copiedCodeRow = null;
    }

    private async void KofiButton_Click(object sender, RoutedEventArgs e) =>
        await OpenFixedDestinationAsync(new Uri("https://ko-fi.com/asyce"), "Ko-fi");

    private async Task OpenFixedDestinationAsync(Uri destination, string label)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(destination))
            {
                HeroDescription.Text = $"Windows could not open {label}.";
            }
        }
        catch (Exception)
        {
            HeroDescription.Text = $"Windows could not open {label}.";
        }
    }

    private async void AddGameButton_Click(object sender, RoutedEventArgs e) =>
        await ShowAddGameDialogAsync();

    private async Task OpenFolderAsync(LauncherRecoveryAction action, TextBlock message)
    {
        try
        {
            string? folder;
            if (action is LauncherRecoveryAction.OpenOutputFolder)
            {
                folder = Path.Combine(WindowsDocumentsDirectory.Get(), "Pengo Exports");
                Directory.CreateDirectory(folder);
            }
            else
            {
                var result = await app.Recovery.OpenDataFolderAsync();
                folder = result.Succeeded ? result.SafeLocation : null;
            }
            if (string.IsNullOrWhiteSpace(folder))
            {
                message.Text = "Nyx could not open that folder.";
                return;
            }

            if (!await Windows.System.Launcher.LaunchFolderPathAsync(folder))
            {
                message.Text = "Windows could not open that folder.";
            }
        }
        catch (Exception)
        {
            message.Text = "Windows could not open that folder.";
        }
    }

    private void RebuildAfterStateRecovery()
    {
        if (!launcherState.TryReload()) return;
        app.ApplyContentRefreshPreferences();
        SynchronizeCustomSessions(launcherState.Snapshot);
        RebuildGameRail(launcherState.Snapshot);
        GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == launcherState.Snapshot.SelectedGameId)
            ?? Games.FirstOrDefault();
        RenderSelection();
    }

    private void SynchronizeCustomSessions(Nyx.Desktop.Core.State.LauncherState state)
    {
        var savedIds = state.CustomGames.Select(static game => game.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var existingId in sessions.GetAllSnapshots().Keys
                     .Where(static id => id.StartsWith("custom-", StringComparison.Ordinal))
                     .Where(id => !savedIds.Contains(id)))
        {
            sessions.TryRemoveCustomAdapter(existingId);
        }

        foreach (var game in state.CustomGames)
        {
            sessions.TryRemoveCustomAdapter(game.Id);
            sessions.TryRegisterCustomAdapter(CustomGameSessionFactory.Create(game));
        }
    }

    private static void ApplyNyxAccentResources(ResourceDictionary resources)
    {
        if (Application.Current.Resources["HighContrastBackdropOpacity"] is double opacity && opacity > 0)
        {
            return;
        }

        static SolidColorBrush CloneBrush(string key)
        {
            var source = (SolidColorBrush)Application.Current.Resources[key];
            return new SolidColorBrush(source.Color);
        }

        foreach (var key in new[]
                 {
                     "AccentFillColorDefaultBrush",
                     "AccentButtonBackground",
                     "ToggleSwitchFillOn",
                     "ToggleSwitchStrokeOn",
                     "SliderThumbBackground",
                     "SliderTrackValueFill",
                 })
        {
            resources[key] = CloneBrush("IrisBrush");
        }
        foreach (var key in new[]
                 {
                     "AccentFillColorSecondaryBrush",
                     "AccentButtonBackgroundPointerOver",
                     "ToggleSwitchFillOnPointerOver",
                     "SliderThumbBackgroundPointerOver",
                     "SliderTrackValueFillPointerOver",
                 })
        {
            resources[key] = CloneBrush("AccentFillColorSecondaryBrush");
        }
        foreach (var key in new[]
                 {
                     "AccentFillColorTertiaryBrush",
                     "AccentButtonBackgroundPressed",
                     "ToggleSwitchFillOnPressed",
                     "SliderThumbBackgroundPressed",
                     "SliderTrackValueFillPressed",
                 })
        {
            resources[key] = CloneBrush("AccentFillColorTertiaryBrush");
        }
        resources["AccentButtonForeground"] = CloneBrush("PrimaryActionForegroundBrush");
        resources["AccentButtonForegroundPointerOver"] = CloneBrush("PrimaryActionForegroundBrush");
        resources["AccentButtonForegroundPressed"] = CloneBrush("PrimaryActionForegroundBrush");
    }

    public async Task ShowSettingsAsync(int initialTabIndex = 0)
    {
        if (XamlRoot is null)
        {
            return;
        }

        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        var before = launcherState.Snapshot;
        var savedAppearance = before.Appearance.TryGetValue(selected.Id, out var existingAppearance)
            ? existingAppearance
            : new Nyx.Desktop.Core.State.GameAppearanceState();
        var savedBackground = LauncherBackgroundSourceProjection.From(before, selected.Id);
        var openedAppearance = savedAppearance with
        {
            IconPath = savedAppearance.IconPath ?? selected.IconPath,
            BackgroundPath = savedBackground,
        };
        var iconPath = new TextBox
        {
            Header = selected.Id switch
            {
                "gi" => "Genshin Game Icon",
                "hsr" => "Star Rail Game Icon",
                "zzz" => "Zenless Game Icon",
                "wuwa" => "Wuthering Waves Game Icon",
                "ae" => "Endfield Game Icon",
                _ => $"{selected.DisplayName} Game Icon",
            },
            Text = savedAppearance.IconPath ?? selected.IconPath,
        };
        var backgroundPath = new TextBox
        {
            Header = "Launcher background",
            Text = savedBackground ?? string.Empty,
            PlaceholderText = "Nyx background (default)",
        };
        var browseIcon = new Button { Content = "CHANGE ICON", Style = (Style)Application.Current.Resources["NyxQuietActionStyle"] };
        var browseBackground = new Button { Content = "CHANGE BACKGROUND", Style = (Style)Application.Current.Resources["NyxQuietActionStyle"] };
        var openedManualInstallRoot = selected.IsCustom
            ? null
            : before.Preferences.ManualInstallRoots.TryGetValue(selected.Id, out var savedManualRoot)
                ? savedManualRoot
                : selected.Id == "ae" ? before.Preferences.EndfieldInstallRoot : null;
        string? editedManualInstallRoot = openedManualInstallRoot;
        var gameFolder = new TextBox
        {
            Header = "Game folder",
            Text = openedManualInstallRoot ?? "Override the automatic detection",
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(gameFolder, "Saved official game folder");
        var browseGameFolder = new Button
        {
            Content = "BROWSE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var gameFolderStatus = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        var savedOfficialLaunchOptions = !selected.IsCustom
            && before.OfficialLaunchOptions.TryGetValue(selected.Id, out var launchOptions)
                ? launchOptions
                : new OfficialGameLaunchOptions();
        var officialLaunchArguments = new TextBox
        {
            Header = "Arguments",
            Text = savedOfficialLaunchOptions.RawArguments,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };
        var officialLaunchArgumentsEnabled = new ToggleSwitch
        {
            IsOn = savedOfficialLaunchOptions.Enabled,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            OnContent = "On",
            OffContent = "Off",
        };
        AutomationProperties.SetName(officialLaunchArgumentsEnabled, "Toggle launch options");
        var officialLaunchArgumentsHelp = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Content = "?",
            Style = (Style)Application.Current.Resources["NyxHelpButtonStyle"],
        };
        AutomationProperties.SetName(
            officialLaunchArgumentsHelp,
            "Open Unity player command-line argument documentation");
        officialLaunchArgumentsHelp.Click += async (_, _) =>
            await OpenFixedDestinationAsync(
                new Uri("https://docs.unity3d.com/6000.5/Documentation/Manual/PlayerCommandLineArguments.html"),
                "Unity player command-line argument documentation");
        var openedPanelVisibility = selected.IsCustom
            ? null
            : before.Preferences.VisibilityFor(selected.Id);
        var showBanners = new ToggleSwitch
        {
            Header = "Show Banners",
            IsOn = openedPanelVisibility?.ShowBanners ?? true,
        };
        var showRedemptionCodes = new ToggleSwitch
        {
            Header = "Show Redemption Codes",
            IsOn = openedPanelVisibility?.ShowRedemptionCodes ?? true,
        };
        var showAccountAndExport = new ToggleSwitch
        {
            Header = "Show Account & Export",
            IsOn = openedPanelVisibility?.ShowAccountAndExport ?? true,
        };
        var publisherPasswordSaving = new ToggleSwitch
        {
            Header = "Locally save browser login?",
            IsOn = before.Preferences.PublisherPasswordSavingEnabled,
            OnContent = "Save and autofill",
            OffContent = "Never save",
        };
        var resetOrder = new Button
        {
            Content = "RESET GAME ORDER",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var resetLauncherState = new Button
        {
            Content = "RESET LAUNCHER STATE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var cacheSummary = new TextBlock
        {
            Text = $"Generated content: {FormatBytes(app.Cache.GetTotals().GeneratedBytes)}",
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var refreshContent = new Button
        {
            Content = "REFRESH CONTENT NOW",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var clearCache = new Button
        {
            Content = "CLEAR GENERATED CACHE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var openData = new Button
        {
            Content = "OPEN DATA FOLDER",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var openExports = new Button
        {
            Content = "OPEN EXPORT FOLDER",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var copyDiagnostics = new Button
        {
            Content = "COPY SAFE DIAGNOSTICS",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var rediscover = new Button
        {
            Content = "REDISCOVER GAME INSTALLS",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var resetSavedAppearance = new Button
        {
            Content = "RESET SAVED APPEARANCE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var restoreSettings = new Button
        {
            Content = "RESTORE LAST-KNOWN-GOOD SETTINGS",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
        };
        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
        };

        var custom = before.CustomGames.FirstOrDefault(game => game.Id == selected.Id);
        var customName = new TextBox { Header = "Custom game name", Text = custom?.Name ?? selected.DisplayName };
        var customExecutable = new TextBox { Header = "Exact executable", Text = custom?.ExecutablePath ?? string.Empty };
        var customRuntime = new TextBox { Header = "Runtime executable (optional)", Text = custom?.RuntimePath ?? string.Empty };
        var customArguments = new TextBox
        {
            Header = "Arguments (optional)",
            PlaceholderText = "Arguments",
            Text = custom?.RawArguments ?? string.Empty,
        };
        var customAdmin = new ToggleSwitch { Header = "Ask Windows for administrator approval", IsOn = custom?.RequestAdministrator ?? false };
        var browseExecutable = new Button { Content = "REPAIR / CHANGE EXE", Style = (Style)Application.Current.Resources["NyxQuietActionStyle"] };
        var browseRuntime = new Button { Content = "CHOOSE RUNTIME", Style = (Style)Application.Current.Resources["NyxQuietActionStyle"] };

        async Task<string?> PickFileAsync(params string[] extensions)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle);
            return (await picker.PickSingleFileAsync())?.Path;
        }

        browseIcon.Click += async (_, _) =>
        {
            var path = await PickFileAsync(".png", ".jpg", ".jpeg", ".webp", ".ico");
            if (path is not null) iconPath.Text = path;
        };
        browseBackground.Click += async (_, _) =>
        {
            var path = await PickFileAsync(".png", ".jpg", ".jpeg", ".webp");
            if (path is not null)
            {
                backgroundPath.Text = path;
                SetBackgroundSource(path);
            }
        };
        browseGameFolder.Click += async (_, _) =>
        {
            var lease = pageLease;
            if (lease is null || selected.IsCustom)
            {
                return;
            }

            browseGameFolder.IsEnabled = false;
            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null)
                {
                    gameFolderStatus.Text = "Folder selection canceled. Nothing was saved.";
                    return;
                }

                var accepted = await Task.Run(
                    () => IsValidManualInstallRoot(selected.Id, folder.Path),
                    lease.CancellationToken);
                if (!accepted)
                {
                    gameFolderStatus.Text = "That is not a valid official game folder. Nothing was saved.";
                    return;
                }

                editedManualInstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
                gameFolder.Text = editedManualInstallRoot;
                gameFolderStatus.Text = "Folder checked. Press Save to keep this override.";
            }
            catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                gameFolderStatus.Text = "Nyx could not check that folder. Nothing was saved.";
            }
            finally
            {
                browseGameFolder.IsEnabled = true;
            }
        };
        browseExecutable.Click += async (_, _) =>
        {
            var path = await PickFileAsync(".exe");
            if (path is not null) customExecutable.Text = path;
        };
        browseRuntime.Click += async (_, _) =>
        {
            var path = await PickFileAsync(".exe");
            if (path is not null) customRuntime.Text = path;
        };

        refreshContent.Click += async (_, _) =>
        {
            refreshContent.IsEnabled = false;
            message.Text = "Refreshing banners, codes, and launcher media...";
            try
            {
                await app.RefreshContentManualAsync();
                message.Text = "Launcher content refreshed."
                    + (launcherBanners.Current.Health.Status is "ok" ? string.Empty : " Nyx kept the last safe copy.");
                RenderSelection();
            }
            catch (Exception)
            {
                message.Text = "Nyx could not refresh content. The last safe copy is still in use.";
            }
            finally
            {
                refreshContent.IsEnabled = true;
            }
        };
        clearCache.Click += async (_, _) =>
        {
            var result = await app.Recovery.ClearGeneratedCacheAsync();
            cacheSummary.Text = $"Generated content: {FormatBytes(app.Cache.GetTotals().GeneratedBytes)}";
            message.Text = result.Succeeded
                ? "Generated content cache cleared. Nyx will rebuild it when needed."
                : "Nyx could not clear the generated content cache.";
        };
        openData.Click += async (_, _) => await OpenFolderAsync(LauncherRecoveryAction.OpenDataFolder, message);
        openExports.Click += async (_, _) => await OpenFolderAsync(LauncherRecoveryAction.OpenOutputFolder, message);
        copyDiagnostics.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(LauncherDiagnosticsText.FormatForCopy(BuildDiagnosticsSnapshot()));
                Clipboard.SetContent(package);
                message.Text = "Safe diagnostics copied. They contain no user paths or account data.";
            }
            catch (Exception)
            {
                message.Text = "Nyx could not copy diagnostics to the clipboard.";
            }
        };
        rediscover.Click += async (_, _) =>
        {
            rediscover.IsEnabled = false;
            message.Text = "Rediscovering installed games...";
            try
            {
                var result = await app.Recovery.RediscoverInstallsAsync();
                await sessionRefresh.RefreshNowAsync();
                RenderSelection();
                message.Text = result.Succeeded
                    ? "Game install checks refreshed."
                    : "Nyx could not finish rediscovering installs.";
            }
            catch (Exception)
            {
                message.Text = "Nyx could not finish rediscovering installs.";
            }
            finally
            {
                rediscover.IsEnabled = true;
            }
        };
        resetSavedAppearance.Click += async (_, _) =>
        {
            var result = await app.Recovery.ResetSelectedAppearanceAsync(selected.Id);
            if (result.Succeeded)
            {
                RebuildAfterStateRecovery();
                iconPath.Text = selected.IsCustom ? custom?.IconPath ?? selected.IconPath : IconPaths[selected.Id];
                backgroundPath.Text = string.Empty;
                message.Text = "Saved appearance reset. Choose Save to keep other changes in this dialog.";
            }
            else
            {
                message.Text = "Nyx could not reset the saved appearance.";
            }
        };
        restoreSettings.Click += async (_, _) =>
        {
            var result = await app.Recovery.RestoreLastKnownGoodSettingsAsync();
            if (result.Succeeded)
            {
                RebuildAfterStateRecovery();
                message.Text = "Last-known-good settings restored. Close and reopen Settings to review them.";
            }
            else
            {
                message.Text = "No usable last-known-good settings backup was found.";
            }
        };

        var appearancePanel = new StackPanel { Spacing = 10 };
        var customAppearanceOptions = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Choose the icon and background used by this custom game.",
                    Foreground = (Brush)Application.Current.Resources["MistBrush"],
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        var officialAppearanceOptions = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                gameFolder,
                browseGameFolder,
                gameFolderStatus,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Launch Options",
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = (Brush)Application.Current.Resources["MoonBrush"],
                            FontSize = 12,
                        },
                        officialLaunchArgumentsEnabled,
                        officialLaunchArgumentsHelp,
                    },
                },
                officialLaunchArguments,
                showBanners,
                showRedemptionCodes,
                showAccountAndExport,
            },
        };
        appearancePanel.Children.Add(customAppearanceOptions);
        appearancePanel.Children.Add(officialAppearanceOptions);
        appearancePanel.Children.Add(iconPath);
        appearancePanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { browseIcon },
        });
        var customBackgroundOptions = new StackPanel
        {
            Spacing = 10,
            Children = { backgroundPath, browseBackground },
        };
        appearancePanel.Children.Add(customBackgroundOptions);
        officialAppearanceOptions.Visibility = selected.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        customAppearanceOptions.Visibility = selected.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        customBackgroundOptions.Visibility = selected.IsCustom ? Visibility.Visible : Visibility.Collapsed;

        var launcherPanel = new StackPanel { Spacing = 10 };
        launcherPanel.Children.Add(publisherPasswordSaving);
        launcherPanel.Children.Add(new TextBlock
        {
            Text = "Keeps your publisher login saved on this PC. Turning it off removes saved passwords.",
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        launcherPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { resetOrder, resetLauncherState },
        });
        launcherPanel.Children.Add(new TextBlock
        {
            Text = "To reorder games, close Settings and drag their icons directly on the launcher rail.",
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        launcherPanel.Children.Add(new TextBlock
        {
            Text = "Reset launcher state resets the selected game, order, custom games, saved appearance, export switches, manual folder overrides, launch options, rendering, and preferences. Protected publisher accounts, generated cache, downloads/exports, and game files stay untouched. Restore Last-Known-Good can undo it until a later successful settings save replaces that backup.",
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var recoveryPanel = new StackPanel { Spacing = 12 };
        recoveryPanel.Children.Add(cacheSummary);
        recoveryPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refreshContent, clearCache } });
        recoveryPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { openData, openExports } });
        recoveryPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyDiagnostics, rediscover } });
        recoveryPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { resetSavedAppearance, restoreSettings } });

        var customPanel = new StackPanel { Spacing = 10 };
        customPanel.Children.Add(customName);
        customPanel.Children.Add(customExecutable);
        customPanel.Children.Add(browseExecutable);
        customPanel.Children.Add(customRuntime);
        customPanel.Children.Add(browseRuntime);
        customPanel.Children.Add(customArguments);
        customPanel.Children.Add(customAdmin);

        var panels = new List<FrameworkElement> { appearancePanel, launcherPanel, recoveryPanel, customPanel };
        var tabNames = new List<string> { "Game", "Launcher", "Recovery" };
        if (selected.IsCustom) tabNames.Add("Custom game");
        var panelHost = new Grid();
        foreach (var panel in panels)
        {
            panelHost.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
                Visibility = Visibility.Collapsed,
            });
        }
        initialTabIndex = Math.Clamp(initialTabIndex, 0, tabNames.Count - 1);
        ((ScrollViewer)panelHost.Children[initialTabIndex]).Visibility = Visibility.Visible;
        var tabs = new ListView
        {
            Width = 150,
            ItemsSource = tabNames,
            SelectedIndex = initialTabIndex,
            SelectionMode = ListViewSelectionMode.Single,
        };
        tabs.SelectionChanged += (_, _) =>
        {
            for (var index = 0; index < panelHost.Children.Count; index++)
                panelHost.Children[index].Visibility = index == tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        };

        var settingsGameRail = new ListView
        {
            Width = 112,
            ItemsSource = Games,
            ItemTemplate = GameSelector.ItemTemplate,
            ItemContainerStyle = (Style)Application.Current.Resources["NyxGameItemStyle"],
            SelectedItem = selected,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
        };
        AutomationProperties.SetName(settingsGameRail, "Settings games");
        ScrollViewer.SetHorizontalScrollBarVisibility(settingsGameRail, ScrollBarVisibility.Hidden);
        var settingsSwitchPromptText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["MoonBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var saveAndSwitch = new Button
        {
            Content = "Save and switch",
            Style = (Style)Application.Current.Resources["NyxSettingsDialogPrimaryStyle"],
        };
        var discardAndSwitch = new Button
        {
            Content = "Don't save and switch",
            Style = (Style)Application.Current.Resources["NyxSettingsDialogQuietStyle"],
        };
        var stayHere = new Button
        {
            Content = "Stay here",
            Style = (Style)Application.Current.Resources["NyxSettingsDialogQuietStyle"],
        };
        var settingsSwitchPrompt = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            Children =
            {
                settingsSwitchPromptText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { saveAndSwitch, discardAndSwitch, stayHere },
                },
            },
        };

        // The launcher uses one fixed 1280x720 design surface. FullSizeDesired
        // lets the dialog use that same surface instead of a screen-relative size.
        var settingsWidth = LauncherLayoutProfile.DesignWidth;
        var settingsHeight = LauncherLayoutProfile.DesignHeight;
        var settingsInset = settingsWidth < 720 ? 40 : 64;
        var settingsTabWidth = settingsWidth < 720 ? 112 : 160;
        var settingsColumnGap = settingsWidth < 720 ? 12 : 24;
        var content = new Grid
        {
            Width = Math.Max(248, settingsWidth - settingsInset),
            Height = settingsHeight,
            ColumnSpacing = settingsColumnGap,
        };
        ApplyNyxAccentResources(content.Resources);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(settingsTabWidth) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(settingsGameRail, 0);
        content.Children.Add(settingsGameRail);
        Grid.SetRow(tabs, 0);
        Grid.SetColumn(tabs, 1);
        content.Children.Add(tabs);
        Grid.SetRow(panelHost, 0);
        Grid.SetColumn(panelHost, 2);
        content.Children.Add(panelHost);
        Grid.SetRow(settingsSwitchPrompt, 1);
        Grid.SetColumn(settingsSwitchPrompt, 2);
        content.Children.Add(settingsSwitchPrompt);
        Grid.SetRow(message, 2);
        Grid.SetColumn(message, 2);
        message.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(message);

        var settingsTitle = new Grid
        {
            Width = content.Width,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var settingsTitleText = new TextBlock
        {
            Text = $"Settings - {selected.DisplayName}",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.Resources["NyxBodyFont"],
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["MoonBrush"],
        };
        settingsTitle.Children.Add(settingsTitleText);
        AutomationProperties.SetName(settingsTitle, "Drag the Settings window");
        settingsTitle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(settingsTitle).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (Application.Current is App currentApp)
            {
                currentApp.BeginWindowDrag();
                args.Handled = true;
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = settingsTitle,
            FullSizeDesired = true,
            Width = settingsWidth,
            Height = settingsHeight,
            Background = (Brush)Application.Current.Resources["SettingsSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            MinWidth = settingsWidth,
            MaxWidth = settingsWidth,
            MinHeight = settingsHeight,
            MaxHeight = settingsHeight,
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = selected.IsCustom ? "Delete Game" : string.Empty,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["NyxSettingsDialogPrimaryStyle"],
            SecondaryButtonStyle = (Style)Application.Current.Resources["NyxSettingsDialogQuietStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["NyxSettingsDialogQuietStyle"],
        };
        ApplyNyxAccentResources(dialog.Resources);
        dialog.Resources["ContentDialogMinWidth"] = settingsWidth;
        dialog.Resources["ContentDialogMaxWidth"] = settingsWidth;
        dialog.Resources["ContentDialogMinHeight"] = settingsHeight;
        dialog.Resources["ContentDialogMaxHeight"] = settingsHeight;
        bool HasUnsavedSettings()
        {
            if (!string.Equals(
                    iconPath.Text,
                    savedAppearance.IconPath ?? selected.IconPath,
                    StringComparison.OrdinalIgnoreCase)
                || (selected.IsCustom
                    && !string.Equals(backgroundPath.Text, savedBackground ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                || (!selected.IsCustom
                    && (!string.Equals(editedManualInstallRoot, openedManualInstallRoot, StringComparison.Ordinal)
                        || !string.Equals(
                            officialLaunchArguments.Text,
                            savedOfficialLaunchOptions.RawArguments,
                            StringComparison.Ordinal)
                        || officialLaunchArgumentsEnabled.IsOn != savedOfficialLaunchOptions.Enabled
                        || openedPanelVisibility is { } openedVisibility
                            && (showBanners.IsOn != openedVisibility.ShowBanners
                                || showRedemptionCodes.IsOn != openedVisibility.ShowRedemptionCodes
                                || showAccountAndExport.IsOn != openedVisibility.ShowAccountAndExport)))
                || publisherPasswordSaving.IsOn != before.Preferences.PublisherPasswordSavingEnabled)
            {
                return true;
            }

            if (custom is null)
            {
                return false;
            }

            return !string.Equals(customName.Text, custom.Name, StringComparison.Ordinal)
                || !string.Equals(customExecutable.Text, custom.ExecutablePath, StringComparison.Ordinal)
                || !string.Equals(customRuntime.Text, custom.RuntimePath ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(customArguments.Text, custom.RawArguments ?? string.Empty, StringComparison.Ordinal)
                || customAdmin.IsOn != custom.RequestAdministrator;
        }

        var saveSucceeded = false;
        GameLauncherItem? pendingSettingsGame = null;
        var restoringSettingsRailSelection = false;

        void LoadSettingsGame(GameLauncherItem target, int tabIndex)
        {
            selected = target;
            before = launcherState.Snapshot;
            savedAppearance = before.Appearance.TryGetValue(selected.Id, out var currentAppearance)
                ? currentAppearance
                : new Nyx.Desktop.Core.State.GameAppearanceState();
            savedBackground = LauncherBackgroundSourceProjection.From(before, selected.Id);
            openedAppearance = savedAppearance with
            {
                IconPath = savedAppearance.IconPath ?? selected.IconPath,
                BackgroundPath = savedBackground,
            };
            iconPath.Header = selected.Id switch
            {
                "gi" => "Genshin Game Icon",
                "hsr" => "Star Rail Game Icon",
                "zzz" => "Zenless Game Icon",
                "wuwa" => "Wuthering Waves Game Icon",
                "ae" => "Endfield Game Icon",
                _ => $"{selected.DisplayName} Game Icon",
            };
            iconPath.Text = savedAppearance.IconPath ?? selected.IconPath;
            backgroundPath.Text = savedBackground ?? string.Empty;
            openedManualInstallRoot = selected.IsCustom
                ? null
                : before.Preferences.ManualInstallRoots.TryGetValue(selected.Id, out var savedManualRoot)
                    ? savedManualRoot
                    : selected.Id == "ae" ? before.Preferences.EndfieldInstallRoot : null;
            editedManualInstallRoot = openedManualInstallRoot;
            gameFolder.Text = openedManualInstallRoot ?? "Override the automatic detection";
            gameFolderStatus.Text = string.Empty;
            savedOfficialLaunchOptions = !selected.IsCustom
                && before.OfficialLaunchOptions.TryGetValue(selected.Id, out var launchOptions)
                    ? launchOptions
                    : new OfficialGameLaunchOptions();
            officialLaunchArguments.Text = savedOfficialLaunchOptions.RawArguments;
            officialLaunchArgumentsEnabled.IsOn = savedOfficialLaunchOptions.Enabled;
            openedPanelVisibility = selected.IsCustom
                ? null
                : before.Preferences.VisibilityFor(selected.Id);
            showBanners.IsOn = openedPanelVisibility?.ShowBanners ?? true;
            showRedemptionCodes.IsOn = openedPanelVisibility?.ShowRedemptionCodes ?? true;
            showAccountAndExport.IsOn = openedPanelVisibility?.ShowAccountAndExport ?? true;
            publisherPasswordSaving.IsOn = before.Preferences.PublisherPasswordSavingEnabled;
            custom = before.CustomGames.FirstOrDefault(game => game.Id == selected.Id);
            customName.Text = custom?.Name ?? selected.DisplayName;
            customExecutable.Text = custom?.ExecutablePath ?? string.Empty;
            customRuntime.Text = custom?.RuntimePath ?? string.Empty;
            customArguments.Text = custom?.RawArguments ?? string.Empty;
            customAdmin.IsOn = custom?.RequestAdministrator ?? false;
            officialAppearanceOptions.Visibility = selected.IsCustom ? Visibility.Collapsed : Visibility.Visible;
            customAppearanceOptions.Visibility = selected.IsCustom ? Visibility.Visible : Visibility.Collapsed;
            customBackgroundOptions.Visibility = selected.IsCustom ? Visibility.Visible : Visibility.Collapsed;
            tabNames = new List<string> { "Game", "Launcher", "Recovery" };
            if (selected.IsCustom) tabNames.Add("Custom game");
            tabs.ItemsSource = tabNames;
            tabs.SelectedIndex = Math.Clamp(tabIndex, 0, tabNames.Count - 1);
            for (var index = 0; index < panelHost.Children.Count; index++)
                panelHost.Children[index].Visibility = index == tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            settingsTitleText.Text = $"Settings - {selected.DisplayName}";
            dialog.SecondaryButtonText = selected.IsCustom ? "Delete Game" : string.Empty;
            if (!ReferenceEquals(settingsGameRail.SelectedItem, selected))
            {
                restoringSettingsRailSelection = true;
                settingsGameRail.SelectedItem = selected;
            }
            else
            {
                restoringSettingsRailSelection = false;
            }
            GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == selected.Id) ?? selected;
            pendingSettingsGame = null;
            settingsSwitchPrompt.Visibility = Visibility.Collapsed;
            message.Text = string.Empty;
        }

        void RestoreSettingsRailSelection()
        {
            restoringSettingsRailSelection = true;
            settingsGameRail.SelectedItem = selected;
        }

        settingsGameRail.SelectionChanged += (_, _) =>
        {
            if (restoringSettingsRailSelection
                || settingsGameRail.SelectedItem is not GameLauncherItem target
                || string.Equals(target.Id, selected.Id, StringComparison.Ordinal))
            {
                restoringSettingsRailSelection = false;
                return;
            }

            if (HasUnsavedSettings())
            {
                pendingSettingsGame = target;
                RestoreSettingsRailSelection();
                settingsSwitchPromptText.Text = $"Save changes for {selected.DisplayName} before switching to {target.DisplayName}?";
                settingsSwitchPrompt.Visibility = Visibility.Visible;
                return;
            }

            LoadSettingsGame(target, tabs.SelectedIndex);
        };
        saveAndSwitch.Click += async (_, _) =>
        {
            var target = pendingSettingsGame;
            if (target is not null && await SaveCurrentSettingsAsync())
                LoadSettingsGame(target, tabs.SelectedIndex);
        };
        discardAndSwitch.Click += (_, _) =>
        {
            var target = pendingSettingsGame;
            if (target is null) return;
            ApplySelectedAppearance(selected.Id);
            RenderSelection();
            LoadSettingsGame(target, tabs.SelectedIndex);
        };
        stayHere.Click += (_, _) =>
        {
            pendingSettingsGame = null;
            settingsSwitchPrompt.Visibility = Visibility.Collapsed;
            RestoreSettingsRailSelection();
        };
        var resetOrderConfirmationArmed = false;
        var resetLauncherConfirmationArmed = false;
        resetOrder.Click += (_, _) =>
        {
            if (!resetOrderConfirmationArmed)
            {
                resetOrderConfirmationArmed = true;
                resetOrder.Content = "CONFIRM RESET ORDER";
                message.Text = "Press Confirm reset order to restore GI, HSR, ZZZ, WuWa, Endfield, then custom games. No game is deleted.";
                return;
            }

            if (launcherState.TryUpdate(LauncherSettingsStateMerge.ResetRailOrder))
            {
                resetOrderConfirmationArmed = false;
                resetOrder.Content = "RESET GAME ORDER";
                RebuildAfterStateRecovery();
                message.Text = "Game order reset. Games and settings were kept.";
            }
            else
            {
                message.Text = "Nyx could not save the new order. Your previous order is still safe.";
            }
        };
        resetLauncherState.Click += (_, _) =>
        {
            if (!resetLauncherConfirmationArmed)
            {
                resetLauncherConfirmationArmed = true;
                resetLauncherState.Content = "CONFIRM RESET STATE";
                message.Text = "Press Confirm reset state to restore launcher settings only. Accounts, cache, downloads, exports, and files stay untouched.";
                return;
            }

            if (launcherState.TryReset())
            {
                dialog.Hide();
                RebuildAfterStateRecovery();
            }
            else
            {
                message.Text = "Nyx could not reset launcher settings. Your previous settings are still safe.";
            }
        };
        var manualInstallRootChanged = false;
        async Task<bool> SaveCurrentSettingsAsync()
        {
            try
            {
                if (!selected.IsCustom
                    && !CustomArgumentParser.TryParse(officialLaunchArguments.Text, out _))
                {
                    message.Text = "Launch options are not valid. Close quotes and keep each option within the safe limits.";
                    return false;
                }

                var storedIcon = iconPath.Text;
                if (!string.Equals(storedIcon, savedAppearance.IconPath ?? selected.IconPath, StringComparison.OrdinalIgnoreCase))
                {
                    storedIcon = userAssets.CopyImage(selected.Id, "icon", storedIcon);
                }
                var storedBackground = string.IsNullOrWhiteSpace(backgroundPath.Text)
                    ? null
                    : backgroundPath.Text;
                if (storedBackground is not null
                    && !string.Equals(storedBackground, savedBackground, StringComparison.OrdinalIgnoreCase))
                {
                    storedBackground = userAssets.CopyImage(selected.Id, "background", storedBackground);
                }

                CustomGameDefinition? updatedCustom = custom;
                if (custom is not null)
                {
                    var validation = CustomGameValidator.Validate(
                        new CustomGameDraft(
                            customName.Text,
                            customExecutable.Text,
                            storedIcon,
                            storedBackground,
                            string.IsNullOrWhiteSpace(customRuntime.Text) ? null : customRuntime.Text,
                            string.IsNullOrWhiteSpace(customArguments.Text) ? null : customArguments.Text,
                            customAdmin.IsOn,
                            custom.Id,
                            custom.CreationOrder),
                        before.CustomGames.Where(game => game.Id != custom.Id));
                    if (!validation.IsValid || validation.Game is null)
                    {
                        message.Text = $"Custom game settings need review: {validation.Error}.";
                        return false;
                    }
                    updatedCustom = validation.Game;
                }

                var settingsEdit = new LauncherSettingsEdit
                {
                    GameId = selected.Id,
                    OpenedAppearance = openedAppearance,
                    Appearance = savedAppearance with
                    {
                        IconPath = storedIcon,
                        BackgroundPath = storedBackground,
                    },
                    CustomGame = updatedCustom,
                    RailOrder = launcherState.Snapshot.RailOrder,
                    OpenedManualInstallRoot = openedManualInstallRoot,
                    ManualInstallRoot = selected.IsCustom ? null : editedManualInstallRoot,
                    OpenedOfficialLaunchOptions = selected.IsCustom ? null : savedOfficialLaunchOptions,
                    OfficialLaunchOptions = selected.IsCustom
                        ? null
                        : new OfficialGameLaunchOptions
                        {
                            RawArguments = officialLaunchArguments.Text,
                            Enabled = officialLaunchArgumentsEnabled.IsOn,
                        },
                    PublisherPasswordSavingEnabled = publisherPasswordSaving.IsOn,
                    OpenedPanelVisibility = selected.IsCustom ? null : openedPanelVisibility,
                    PanelVisibility = selected.IsCustom
                        ? null
                        : new LauncherPanelVisibility
                        {
                            ShowBanners = showBanners.IsOn,
                            ShowRedemptionCodes = showRedemptionCodes.IsOn,
                            ShowAccountAndExport = showAccountAndExport.IsOn,
                        },
                };
                saveSucceeded = launcherState.TryUpdate(
                    state => LauncherSettingsStateMerge.Apply(state, before, settingsEdit),
                    out var settingsFailure);
                if (!saveSucceeded)
                {
                    message.Text = settingsFailure is LauncherStateUpdateFailure.CustomGameExecutableConflict
                        ? "That executable is already in your game rail. Your previous settings are still safe."
                        : "Nyx could not save Settings. Your previous settings are still safe.";
                    return false;
                }

                manualInstallRootChanged = !string.Equals(
                    openedManualInstallRoot,
                    editedManualInstallRoot,
                    StringComparison.Ordinal);

                if (updatedCustom is not null)
                {
                    sessions.TryRemoveCustomAdapter(updatedCustom.Id);
                    var savedCustom = launcherState.Snapshot.CustomGames.FirstOrDefault(
                        game => string.Equals(game.Id, updatedCustom.Id, StringComparison.Ordinal));
                    if (savedCustom is not null)
                    {
                        sessions.TryRegisterCustomAdapter(CustomGameSessionFactory.Create(savedCustom));
                    }
                }

                app.ApplyContentRefreshPreferences();
                if (before.Preferences.PublisherPasswordSavingEnabled
                    && !publisherPasswordSaving.IsOn
                    && !await app.PublisherAccounts.ClearSavedPasswordsAsync())
                {
                    message.Text = "Password saving is off, but Nyx could not remove old saved passwords. Disconnecting the publisher account also deletes its private profile.";
                }
                if (manualInstallRootChanged && !selected.IsCustom)
                {
                    try
                    {
                        var lease = pageLease;
                        if (lease is not null)
                        {
                            await sessionRefresh.RefreshNowAsync(lease.CancellationToken);
                            if (selected.Id == "ae")
                            {
                                await RefreshEndfieldMaintenanceAsync(lease);
                            }
                            else if (selected.Id == "wuwa")
                            {
                                await RefreshWuWaMaintenanceAsync(lease, useStoredRequest: false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (pageLease?.CancellationToken.IsCancellationRequested == true)
                    {
                    }
                }
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                message.Text = "Nyx could not safely copy one of those images.";
                return false;
            }
        }

        dialog.PrimaryButtonClick += async (_sender, args) =>
            args.Cancel = !await SaveCurrentSettingsAsync();

        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Secondary && custom is not null)
        {
            var confirmDelete = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete {custom.Name}?",
                Content = "This removes the custom game from Nyx. The game files on disk will not be touched.",
                PrimaryButtonText = "Delete game",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmDelete.ShowAsync() is not ContentDialogResult.Primary)
            {
                return;
            }

            var deleted = launcherState.TryUpdate(state => state with
            {
                CustomGames = state.CustomGames.Where(game => game.Id != custom.Id).ToArray(),
                RailOrder = state.RailOrder.Where(id => id != custom.Id).ToArray(),
                Appearance = state.Appearance.Where(pair => pair.Key != custom.Id).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                SelectedGameId = "gi",
            });
            if (!deleted)
            {
                return;
            }
            sessions.TryRemoveCustomAdapter(custom.Id);
            RebuildGameRail(launcherState.Snapshot);
            GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == "gi");
        }
        else if (saveSucceeded)
        {
            RebuildGameRail(launcherState.Snapshot);
            GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == selected.Id) ?? Games.FirstOrDefault();
        }
        else
        {
            ApplySelectedAppearance(selected.Id);
            RenderSelection();
        }

    }

    private async Task ShowAddGameDialogAsync()
    {
        if (XamlRoot is null)
        {
            return;
        }

        var name = new TextBox { Header = "Game name", PlaceholderText = "My game" };
        var executable = new TextBox { Header = "Game executable", PlaceholderText = "Choose the exact .exe file" };
        var icon = new TextBox { Header = "Game icon", PlaceholderText = "Choose a PNG, JPG, WebP, or ICO file" };
        var chooseExecutable = new Button
        {
            Content = "BROWSE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var chooseIcon = new Button
        {
            Content = "BROWSE",
            Style = (Style)Application.Current.Resources["NyxQuietActionStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        chooseExecutable.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) executable.Text = file.Path;
        };
        chooseIcon.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
            {
                picker.FileTypeFilter.Add(extension);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, app.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) icon.Text = file.Path;
        };
        var message = new TextBlock
        {
            Text = "Nyx starts this exact file directly. You can add a separate runtime file and administrator approval in Settings after saving.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MistBrush"],
        };
        static Grid PickerRow(TextBox field, Button button)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(field);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            return row;
        }

        var addGameWidth = Math.Clamp(ActualWidth - 32, 320, 744);
        var content = new StackPanel
        {
            Width = Math.Max(248, addGameWidth - 48),
            Spacing = 12,
            Children = { message, name, PickerRow(executable, chooseExecutable), PickerRow(icon, chooseIcon) },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add Game",
            Background = (Brush)Application.Current.Resources["SettingsSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DeckBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            MinWidth = addGameWidth,
            MaxWidth = addGameWidth,
            Content = content,
            PrimaryButtonText = "Add Game",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["NyxDialogPrimaryStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["NyxDialogQuietStyle"],
        };
        ApplyNyxAccentResources(dialog.Resources);
        dialog.Resources["ContentDialogMinWidth"] = addGameWidth;
        dialog.Resources["ContentDialogMaxWidth"] = addGameWidth;
        CustomGameDefinition? addedGame = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var id = CustomGameValidator.GenerateId();
            var validation = CustomGameValidator.Validate(
                new CustomGameDraft(
                    name.Text,
                    executable.Text,
                    icon.Text,
                    Id: id,
                    CreationOrder: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                launcherState.Snapshot.CustomGames);
            if (!validation.IsValid || validation.Game is null)
            {
                args.Cancel = true;
                message.Text = validation.Error switch
                {
                    CustomGameValidationError.NameRequired => "Enter a game name.",
                    CustomGameValidationError.ExecutableMissing => "The selected game executable no longer exists.",
                    CustomGameValidationError.IconMissing => "The selected icon no longer exists.",
                    CustomGameValidationError.DuplicateExecutable => "That executable is already in your game rail.",
                    CustomGameValidationError.UnsafeArguments => "The saved arguments are not safe to start directly.",
                    _ => "Choose an exact local .exe and a local icon image.",
                };
                message.Foreground = (Brush)Application.Current.Resources["LavenderBrush"];
                return;
            }

            try
            {
                var copiedIcon = userAssets.CopyImage(id, "icon", validation.Game.IconPath);
                var game = validation.Game with { IconPath = copiedIcon };
                if (!sessions.TryRegisterCustomAdapter(CustomGameSessionFactory.Create(game)))
                {
                    args.Cancel = true;
                    message.Text = "Nyx could not prepare this game. Nothing was launched.";
                    message.Foreground = (Brush)Application.Current.Resources["LavenderBrush"];
                    return;
                }

                var saved = launcherState.TryUpdate(
                    state => LauncherCustomGameStateMerge.Add(state, game),
                    out var addFailure);
                if (!saved)
                {
                    sessions.TryRemoveCustomAdapter(game.Id);
                    args.Cancel = true;
                    message.Text = addFailure is LauncherStateUpdateFailure.CustomGameExecutableConflict
                        ? "That executable is already in your game rail. Nothing was launched."
                        : "Nyx could not save this game. Nothing was launched.";
                    message.Foreground = (Brush)Application.Current.Resources["LavenderBrush"];
                    return;
                }

                addedGame = game;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                args.Cancel = true;
                message.Text = "Nyx could not safely copy that icon into its data folder.";
                message.Foreground = (Brush)Application.Current.Resources["LavenderBrush"];
            }
        };
        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary && addedGame is not null)
        {
            RebuildGameRail(launcherState.Snapshot);
            GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == addedGame.Id);
            var lease = pageLease;
            if (lease is not null)
            {
                await sessionRefresh.RefreshNowAsync(lease.CancellationToken);
            }
        }
    }

    private void ExportToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }
        if (gameActionsInFlight.Contains(selected.Id))
        {
            return;
        }
        if (selected.Id == "hsr" && hoyoLabExportReservation.IsHeld)
        {
            return;
        }
        var achievementSource = selected.Id == "hsr"
            && (ReferenceEquals(sender, PullExportToggle) && PullExportToggle.IsChecked == true
                || ReferenceEquals(sender, AchievementExportToggle) && AchievementExportToggle.IsChecked == true)
                ? AchievementExportSources.Game
                : GetAchievementSource(selected.Id);
        var capability = ExportProviderCatalog.GetEnabled(
            selected.Id,
            launcherState.Snapshot.Preferences.FeatureFlags,
            achievementSource);
        if (ReferenceEquals(sender, PullExportToggle) && !capability.Supports(ExportKind.Pulls)) return;
        if (ReferenceEquals(sender, AchievementExportToggle) && !capability.Supports(ExportKind.Achievements)) return;
        var gameId = selected.Id;
        var pullsArmed = capability.Supports(ExportKind.Pulls) && PullExportToggle.IsChecked == true;
        var achievementsArmed = capability.Supports(ExportKind.Achievements) && AchievementExportToggle.IsChecked == true;
        var saved = launcherState.TryUpdate(state =>
        {
            var games = state.Export.Games.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            games[gameId] = new Nyx.Desktop.Core.State.ExportGameArming
            {
                PullsArmed = pullsArmed,
                AchievementsArmed = achievementsArmed,
                AchievementSource = achievementSource,
            };
            return state with { Export = state.Export with { Games = games } };
        });
        if (!saved) NyxToolsStatusText.Text = "Nyx could not save that choice. Try again.";
        else RenderSelection();
    }

    private void StableAchievementExportToggle_Click(object sender, RoutedEventArgs e)
    {
        AchievementExportToggle.IsChecked = StableAchievementExportToggle.IsChecked;
        ExportToggle_Click(AchievementExportToggle, e);
    }

    private void StablePullExportToggle_Click(object sender, RoutedEventArgs e)
    {
        PullExportToggle.IsChecked = StablePullExportToggle.IsChecked;
        ExportToggle_Click(PullExportToggle, e);
    }

    private async void AchievementExportHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GameSelector?.SelectedItem as GameLauncherItem;
        var source = selected is null
            ? AchievementExportSources.Game
            : GetAchievementSource(selected.Id);
        var instructions = selected?.Id switch
        {
            "gi" => "1. Turn on Achievements.\n2. Launch Genshin Impact through Nyx.\n3. Enter the game normally and follow the small capture window.",
            "zzz" => "Achievement export is disabled. Nyx does not yet have a complete exact-role individual state, and the catalog still needs icon and ID-total reconciliation. Counts and showcases are not enough to build a safe export.",
            "wuwa" => "Achievement export is not ready. The candidate list, release boundary, and two required IDs remain unresolved, and Nyx has no complete account-state source.",
            "ae" => "Achievement export is deliberately not being added for Arknights: Endfield right now.",
            _ when source == AchievementExportSources.HoyoLab => "1. Connect HoYoLAB above.\n2. Choose HoYoLAB as the source.\n3. Turn on Achievements.\n4. Nyx exports immediately; the game can stay closed.",
            _ => "1. Choose Game as the source.\n2. Turn on Achievements.\n3. Launch the game through Nyx.\n4. Enter the game normally and follow the small capture window.",
        };
        await ShowExportHelpAsync("Achievement export", instructions);
    }

    private async void PullExportHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var instructions = GameSelector?.SelectedItem is GameLauncherItem { Id: "ae" }
            ? "Pull history export is not supported for Arknights: Endfield. The old local-log method stopped in version 1.1, while newer token and cache methods are unstable and account-sensitive."
            : "1. Turn on Pull History.\n2. Launch the game through Nyx.\n3. Nyx reads the game-owned pull-history cache.\n4. The result is saved in Pengo Exports. HoYoLAB cannot provide this export.";
        await ShowExportHelpAsync("Pull history export", instructions);
    }

    private async Task ShowExportHelpAsync(string title, string instructions)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = instructions,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 430,
            },
            CloseButtonText = "Done",
        };
        await dialog.ShowAsync();
    }

    private void SectionCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section } button) return;

        bool expanded;
        string label;
        switch (section)
        {
            case "banners":
                expanded = BannerCycleColumns.Visibility is Visibility.Collapsed;
                BannerCycleColumns.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                label = "Banners";
                break;
            case "codes":
                expanded = SignalPanel.Visibility is Visibility.Collapsed;
                SignalPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                CodesHeaderDivider.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                CombinedStatusPanel.VerticalAlignment = VerticalAlignment.Bottom;
                label = "Codes";
                break;
            case "account":
                expanded = AccountSectionContent.Visibility is Visibility.Collapsed;
                accountSectionExpanded = expanded;
                AccountSectionContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                if (!expanded)
                {
                    AccountAndToolsIdentityText.Visibility = Visibility.Collapsed;
                }
                else if (GameSelector.SelectedItem is GameLauncherItem selected)
                {
                    if (selected.Id == "wuwa")
                        RenderWuWaAccountIdentity();
                    else
                        RenderHoyoLabAccountIdentity(selected);
                }
                AccountAndToolsPanel.VerticalAlignment = VerticalAlignment.Bottom;
                label = "Account";
                break;
            default:
                return;
        }

        button.Content = expanded ? "\uE70D" : "\uE70E";
        var action = expanded ? "Collapse" : "Expand";
        AutomationProperties.SetName(button, $"{action} {label}");
        ToolTipService.SetToolTip(button, $"{action} {label}");
    }

    private async void AchievementSource_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem { Id: "hsr" }
            || sender is not FrameworkElement { Tag: string requested })
        {
            return;
        }
        if (gameActionsInFlight.Contains("hsr")
            || hoyoLabExportReservation.IsHeld
            || HasUnfinishedExport("hsr"))
        {
            return;
        }

        var source = AchievementExportSources.Normalize("hsr", requested);
        var existing = launcherState.Snapshot.Export.Games.TryGetValue("hsr", out var configuredArming)
            ? configuredArming
            : new Nyx.Desktop.Core.State.ExportGameArming();
        if (existing.PullsArmed)
        {
            RenderSelection();
            return;
        }
        var saved = launcherState.TryUpdate(state =>
        {
            var games = state.Export.Games.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            var current = games.TryGetValue("hsr", out var configured)
                ? configured
                : new Nyx.Desktop.Core.State.ExportGameArming();
            games["hsr"] = current with
            {
                AchievementSource = source,
                AchievementsArmed = source == AchievementExportSources.HoyoLab
                    ? false
                    : current.AchievementsArmed,
            };
            return state with { Export = state.Export with { Games = games } };
        });
        if (!saved)
        {
            NyxToolsStatusText.Text = "Nyx could not save that achievement source. Try again.";
        }
        else if (source == AchievementExportSources.HoyoLab && existing.AchievementsArmed)
        {
            await StartHoyoLabAchievementExportAsync();
        }
        else
        {
            RenderSelection();
        }
    }

    private void StableAchievementSourceRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string source })
            AchievementSource_Click(sender, e);
    }

    private async Task StartHoyoLabAchievementExportAsync()
    {
        var lease = pageLease;
        var state = launcherState.Snapshot;
        var achievementSource = GetAchievementSource("hsr");
        var capability = ExportProviderCatalog.GetEnabled(
            "hsr",
            state.Preferences.FeatureFlags,
            achievementSource);
        if (lease is null
            || GameSelector?.SelectedItem is not GameLauncherItem { Id: "hsr" }
            || gameActionsInFlight.Contains("hsr")
            || hoyoLabExportReservation.IsHeld
            || achievementSource != AchievementExportSources.HoyoLab
            || !capability.Supports(ExportKind.Achievements))
        {
            return;
        }

        if (latestExportJobs.TryGetValue("hsr", out var activeJobId)
            && !exports.GetSnapshot(activeJobId).IsFinished)
        {
            RenderSelection();
            return;
        }

        var reservation = hoyoLabExportReservation.TryAcquire();
        if (reservation is null)
        {
            return;
        }
        if (!TryEnterExportRegistration())
        {
            reservation.Dispose();
            return;
        }
        try
        {
            RenderSelection();
            AchievementExportToggle.IsEnabled = false;
            AchievementExportLabel.Text = "Working";
            NyxToolsStatusText.Text = "HoYoLAB is exporting achievements. Star Rail can stay closed.";
            var result = await exports.RunForLaunchAsync(
                new ExportArmSnapshot("hsr", PullsArmed: false, AchievementsArmed: true),
                static _ => ValueTask.FromResult(true),
                lease.CancellationToken);
            var completion = exports.WaitForCompletionAsync(result.JobId).AsTask();
            ExportUiJobRetention.RememberLatest(
                latestExportJobs,
                hoyoLabImmediateExportJobs,
                achievementHandoffs,
                "hsr",
                result.JobId);
            hoyoLabImmediateExportJobs.Add(result.JobId);
            _ = TrackExportJobAsync("hsr", result.JobId, completion, lease);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            NyxToolsStatusText.Text = "HoYoLAB achievement export could not start. Try Connect, then export again.";
        }
        finally
        {
            reservation.Dispose();
            try
            {
                _ = sessionUiLifetime.TryRun(lease, RenderSelection);
            }
            finally
            {
                ReleaseExportRegistration();
            }
        }
    }

    private string GetAchievementSource(string gameId)
    {
        var saved = launcherState.Snapshot.Export.Games.TryGetValue(gameId, out var configured)
            ? configured.AchievementSource
            : null;
        return AchievementExportSources.Normalize(gameId, saved);
    }

    private bool HasUnfinishedExport(string gameId) =>
        latestExportJobs.TryGetValue(gameId, out var jobId)
        && !exports.GetSnapshot(jobId).IsFinished;

    private void RenderingModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.Id is not ("zzz" or "wuwa"))
        {
            return;
        }

        var current = GetRenderingMode(selected.Id);
        var next = selected.Id switch
        {
            "zzz" when current == "default" => "dx12",
            "wuwa" when current == "default" => "dx11",
            _ => "default",
        };
        SaveRenderingMode(selected.Id, next);
    }

    private void RenderingModeResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is GameLauncherItem { Id: "zzz" or "wuwa" } selected)
        {
            SaveRenderingMode(selected.Id, "default");
        }
    }

    private string GetRenderingMode(string gameId) =>
        launcherState.Snapshot.Preferences.RenderingModes.TryGetValue(gameId, out var mode)
            ? mode
            : "default";

    private void SaveRenderingMode(string gameId, string mode)
    {
        var saved = launcherState.TryUpdate(state =>
        {
            var values = state.Preferences.RenderingModes.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            if (mode == "default")
            {
                values.Remove(gameId);
            }
            else
            {
                values[gameId] = mode;
            }
            return state with
            {
                Preferences = state.Preferences with { RenderingModes = values },
            };
        });
        NyxToolsStatusText.Text = saved
            ? mode == "default"
                ? "Graphics reset to the publisher default."
                : $"{(mode == "dx12" ? "DirectX 12" : "DirectX 11")} will be used on the next launch."
            : "Nyx could not save that graphics choice.";
        RenderSelection();
    }

    private async void OpenExportsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryOpenExportsFolderAsync())
            NyxToolsStatusText.Text = "Windows could not open the export folder.";
    }

    private static async Task<bool> TryOpenExportsFolderAsync()
    {
        try
        {
            var folder = Path.Combine(WindowsDocumentsDirectory.Get(), "Pengo Exports");
            Directory.CreateDirectory(folder);
            return await Windows.System.Launcher.LaunchFolderPathAsync(folder);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameSelector?.SelectedItem is GameLauncherItem selected
            && latestExportJobs.TryGetValue(selected.Id, out var jobId)
            && exports.Cancel(jobId))
            NyxToolsStatusText.Text = "Canceling this export safely…";
    }

    private async Task TrackExportJobAsync(
        string gameId,
        Guid jobId,
        Task<ExportJobSnapshot> completion,
        SessionUiLease lease,
        Task<AchievementExportHandoffOutcome>? nativeHandoff = null)
    {
        while (!lease.CancellationToken.IsCancellationRequested && !completion.IsCompleted)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (GameSelector?.SelectedItem is GameLauncherItem { Id: var selectedId } && selectedId == gameId)
                {
                    RenderExportTools((GameLauncherItem)GameSelector.SelectedItem);
                }
            });
            await Task.WhenAny(completion, Task.Delay(400, lease.CancellationToken));
        }

        ExportJobSnapshot final;
        try { final = await completion.WaitAsync(lease.CancellationToken); }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { return; }
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (GameSelector?.SelectedItem is GameLauncherItem { Id: var selectedId } && selectedId == gameId)
                RenderSelection();
        });
        SanitizedExportDiagnosticWriter.TryWrite(
            launcherState.DataDirectory,
            final);
        if (final.Pulls.State is ExportTaskState.Succeeded)
            await TryOpenExportsFolderAsync();
        if (nativeHandoff is not null
            && final.Achievements.State is ExportTaskState.Succeeded)
        {
            await ObserveNativeAchievementHandoffAsync(
                gameId,
                jobId,
                nativeHandoff,
                lease);
        }
        else if (final.Achievements.State is ExportTaskState.Succeeded
            && final.Achievements.Artifact is
            {
                IsHandoffCurrent: true,
                OutputPath: { Length: > 0 } outputPath,
            })
        {
            await DeliverAchievementExportAsync(
                gameId,
                jobId,
                outputPath,
                lease);
        }
    }

    private async Task ObserveNativeAchievementHandoffAsync(
        string gameId,
        Guid jobId,
        Task<AchievementExportHandoffOutcome> handoff,
        SessionUiLease lease)
    {
        _ = sessionUiLifetime.TryRun(
            lease,
            () => SetAchievementHandoffIfLatest(gameId, jobId, AchievementHandoffUiState.Opening));
        AchievementExportHandoffOutcome outcome;
        try
        {
            outcome = await handoff.WaitAsync(lease.CancellationToken);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            outcome = AchievementExportHandoffOutcome.Fallback;
        }
        _ = sessionUiLifetime.TryRun(
            lease,
            () => SetAchievementHandoffIfLatest(
                gameId,
                jobId,
                outcome == AchievementExportHandoffOutcome.Delivered
                    ? AchievementHandoffUiState.Delivered
                    : AchievementHandoffUiState.Fallback));
    }

    private async Task DeliverAchievementExportAsync(
        string gameId,
        Guid jobId,
        string outputPath,
        SessionUiLease lease)
    {
        _ = sessionUiLifetime.TryRun(
            lease,
            () => SetAchievementHandoffIfLatest(gameId, jobId, AchievementHandoffUiState.Opening));
        try
        {
            await using var bridge = await achievementImportBridge.StartAsync(
                gameId,
                outputPath,
                lease.CancellationToken);
            var opened = await Windows.System.Launcher.LaunchUriAsync(bridge.BrowserUri);
            if (!opened)
            {
                _ = sessionUiLifetime.TryRun(
                    lease,
                    () => SetAchievementHandoffIfLatest(gameId, jobId, AchievementHandoffUiState.Fallback));
                return;
            }
            _ = sessionUiLifetime.TryRun(
                lease,
                () => SetAchievementHandoffIfLatest(gameId, jobId, AchievementHandoffUiState.Waiting));
            var result = await bridge.Completion.WaitAsync(lease.CancellationToken);
            _ = sessionUiLifetime.TryRun(
                lease,
                () => SetAchievementHandoffIfLatest(
                    gameId,
                    jobId,
                    result is AchievementImportDeliveryState.Delivered
                        ? AchievementHandoffUiState.Delivered
                        : AchievementHandoffUiState.Fallback));
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _ = sessionUiLifetime.TryRun(
                lease,
                () => SetAchievementHandoffIfLatest(gameId, jobId, AchievementHandoffUiState.Fallback));
        }
    }

    private void SetAchievementHandoffIfLatest(
        string gameId,
        Guid jobId,
        AchievementHandoffUiState state)
    {
        if (ExportUiJobRetention.TrySetHandoff(
            latestExportJobs,
            achievementHandoffs,
            gameId,
            jobId,
            state)) RenderSelection();
    }

    private void SessionRefresh_Refreshed(object? sender, GameSessionsRefreshedEventArgs e)
    {
        var lease = pageLease;
        if (lease is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
            sessionUiLifetime.TryRun(lease, () =>
            {
                if (GameSelector?.SelectedItem is GameLauncherItem selected
                    && e.Snapshots.TryGetValue(selected.Id, out var snapshot))
                {
                    gameSnapshot = snapshot;
                }

                RenderSelection();
            }));
    }

    private void LauncherBanners_Updated(object? sender, EventArgs e)
    {
        var lease = pageLease;
        if (lease is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
            sessionUiLifetime.TryRun(lease, () =>
            {
                RenderSelection();
            }));
    }

    private void PublisherStatus_Updated(object? sender, EventArgs e)
    {
        var lease = pageLease;
        if (lease is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
            sessionUiLifetime.TryRun(lease, RenderSelection));
    }

    private void PublisherAccounts_Updated(object? sender, EventArgs e)
    {
        var lease = pageLease;
        if (lease is null) return;
        _ = DispatcherQueue.TryEnqueue(() => sessionUiLifetime.TryRun(lease, RenderSelection));
    }

    private void GameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        officialLauncherStatusOverride = null;
        if (GameSelector?.SelectedItem is GameLauncherItem selected
            && selected.Id == "wuwa"
            && IsWuWaAccountStatusEnabled())
        {
            wuwaAccountInitialRefreshRequested = false;
        }
        RenderSelection();
        if (pageLease is { } lease
            && GameSelector?.SelectedItem is GameLauncherItem { IsCustom: false } selectedForResource)
        {
            if (selectedForResource.Id == "wuwa" && IsWuWaAccountStatusEnabled())
            {
                if (PublisherResourceRefreshPolicy.IsDue(
                        publisherResourceAutomaticAttempts.TryGetValue("wuwa", out var wuwaAttempt)
                            ? wuwaAttempt
                            : null,
                        AccountDisplayClock(),
                        selected: true))
                {
                    publisherResourceAutomaticAttempts["wuwa"] = AccountDisplayClock();
                    _ = RefreshWuWaAccountStatusAsync(lease);
                }
            }
            else
            {
                _ = RefreshPublisherResourceAutomaticallyAsync(
                    selectedForResource.Id,
                    lease,
                    selected: true);
            }
        }
    }

    private void GameSelector_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var selectedId = (GameSelector.SelectedItem as GameLauncherItem)?.Id;
        var reordered = Games.Select(static game => game.Id).ToArray();
        if (!launcherState.TryUpdate(state => state with { RailOrder = reordered }))
        {
            RebuildGameRail(launcherState.Snapshot);
        }
        GameSelector.SelectedItem = Games.FirstOrDefault(game => game.Id == selectedId) ?? Games.FirstOrDefault();
    }

    private void GameSelector_Loaded(object sender, RoutedEventArgs e) =>
        ApplyLayout();

    private void ApplyLayout()
    {
        if (GameSelector is null)
        {
            return;
        }

        var profile = LauncherLayoutProfile.Fixed;
        foreach (var game in Games)
        {
            game.ApplyLayout(profile);
        }

        const double bannerContentMaxWidth = 848d;
        ContentPanel.MaxWidth = bannerContentMaxWidth;
        BannerContentRegion.MaxWidth = bannerContentMaxWidth;
        ApplyLowerActionLayout(profile);

        if (GameSelector.ItemsPanelRoot is ItemsStackPanel itemsPanel)
        {
            itemsPanel.Orientation = Orientation.Vertical;
        }

        RailRow.Height = new GridLength(0);
        RailColumn.Width = new GridLength(profile.RailExtent);
        ContentColumn.Width = new GridLength(profile.ContentWidth + 76);

        Grid.SetRow(RailSurface, 0);
        Grid.SetRowSpan(RailSurface, 2);
        Grid.SetColumn(RailSurface, 0);
        Grid.SetColumnSpan(RailSurface, 1);
        RailSurface.Width = profile.RailExtent;
        RailSurface.Height = double.NaN;
        RailSurface.HorizontalAlignment = HorizontalAlignment.Left;
        RailSurface.VerticalAlignment = VerticalAlignment.Stretch;
        RailSurface.BorderThickness = new Thickness(0, 0, 1, 0);

        RailBrandRow.Height = new GridLength(90);
        RailContentRow.Height = GridLength.Auto;
        RailAddRow.Height = GridLength.Auto;
        RailSpacerRow.Height = new GridLength(1, GridUnitType.Star);
        RailFooterRow.Height = new GridLength(54);
        Grid.SetRow(BrandLockup, 0);
        Grid.SetRowSpan(BrandLockup, 1);
        BrandLockup.Width = profile.RailExtent - 4;
        BrandLockup.Height = 90;
        BrandLockup.Margin = new Thickness(2, 0, 2, 0);
        BrandLockup.HorizontalAlignment = HorizontalAlignment.Center;
        BrandLockup.VerticalAlignment = VerticalAlignment.Top;
        BrandLogo.Width = profile.RailExtent - 10;
        BrandLogo.Height = 80;
        BrandLogo.Margin = new Thickness(0, 7, 0, 0);
        AddGameButton.Visibility = Visibility.Visible;
        AddGameButton.Width = profile.IconSize;
        AddGameButton.Height = profile.IconSize;
        AddGameButton.Margin = new Thickness(0);
        AddGameButton.HorizontalAlignment = HorizontalAlignment.Center;
        AddGameButton.VerticalAlignment = VerticalAlignment.Center;
        KofiButton.Visibility = Visibility.Visible;
        KofiButton.Width = Math.Max(78, profile.RailExtent - 10);
        Grid.SetRow(KofiButton, 4);

        Grid.SetRow(GameSelector, 1);
        Grid.SetRowSpan(GameSelector, 1);
        Grid.SetColumn(GameSelector, 0);
        Grid.SetColumnSpan(GameSelector, 1);
        GameSelector.Width = profile.RailExtent;
        GameSelector.Height = double.NaN;
        GameSelector.MaxHeight = profile.ItemExtent * 5;
        GameSelector.Margin = new Thickness(0);
        GameSelector.HorizontalAlignment = HorizontalAlignment.Left;
        GameSelector.VerticalAlignment = VerticalAlignment.Top;
        ScrollViewer.SetHorizontalScrollMode(GameSelector, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(GameSelector, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollMode(GameSelector, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(GameSelector, ScrollBarVisibility.Auto);

        Grid.SetRow(BannerContentRegion, 0);
        Grid.SetRowSpan(BannerContentRegion, 2);
        Grid.SetColumn(BannerContentRegion, 1);
        Grid.SetColumnSpan(BannerContentRegion, 2);
        BannerContentRegion.HorizontalAlignment = HorizontalAlignment.Left;
        BannerContentRegion.VerticalAlignment = VerticalAlignment.Top;
        BannerContentRegion.Margin = new Thickness(
            26,
            38,
            18,
            LowerActionRegion.Height + 12);

        Grid.SetRow(LowerActionRegion, 0);
        Grid.SetRowSpan(LowerActionRegion, 2);
        Grid.SetColumn(LowerActionRegion, 1);
        Grid.SetColumnSpan(LowerActionRegion, 2);
        LowerActionRegion.Margin = new Thickness(0);
    }

    private void ApplyLowerActionLayout(LauncherLayoutProfile profile)
    {
        compactCodeRows = false;
        LowerActionRegion.Height = Math.Max(profile.DeckHeight, 280);
        LowerActionRegion.Padding = new Thickness(26, 8, 26, 12);
        LowerActionGrid.ColumnSpacing = 16;

        SignalPanel.MinWidth = 0;
        ApplyCombinedStatusLayout();
        CombinedStatusGrid.ColumnSpacing = 16;
        CombinedStatusGrid.RowSpacing = 8;
        PengoToolsLabel.Visibility = Visibility.Collapsed;
        PengoToolButtons.Margin = new Thickness(0);
        NyxToolsPanel.Margin = new Thickness(0);
        PullExportToggle.Width = 104;
        AchievementExportPanel.Width = 142;
        AchievementExportToggle.Width = 142;
        OpenUpdaterButton.Width = 142;
        NyxToolsStatusText.Visibility = Visibility.Visible;
        SetRedemptionCodeMetadataVisibility();
        LaunchButton.Margin = new Thickness(12, 0, 12, 0);

        const double toolsWidth = 415d;
        ApplyToolButtonLayout(toolsWidth, forceStacked: false);

        CombinedStatusPanel.Height = double.NaN;
        CombinedStatusPanel.Margin = new Thickness(0, 8, 0, 0);
        CombinedStatusPanel.Padding = new Thickness(16, 10, 12, 10);
        CombinedStatusPanel.VerticalAlignment = VerticalAlignment.Bottom;
        CombinedStatusPanel.CornerRadius = new CornerRadius(10);
        CombinedBannerColumn.Width = new GridLength(1, GridUnitType.Star);

        LaunchStack.Width = profile.LaunchWidth;
        LaunchStack.Height = double.NaN;
        LaunchStack.HorizontalAlignment = HorizontalAlignment.Stretch;
        LaunchStack.VerticalAlignment = VerticalAlignment.Bottom;
        LaunchStack.Margin = new Thickness(0);
        LaunchButton.Width = Math.Max(0, profile.LaunchWidth - 24);
        LaunchButton.Height = LauncherOpenLayoutGeometry.LaunchButtonHeight;
        LaunchUtilityButtons.Width = LaunchButton.Width;
        LaunchDetail.Height = LauncherOpenLayoutGeometry.LaunchStatusStripHeight;

        NyxToolsPanel.Width = toolsWidth;
        NyxToolsPanel.Margin = new Thickness(0);
        NyxToolsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        NyxToolsPanel.VerticalAlignment = VerticalAlignment.Bottom;
        NyxToolsStatusText.Visibility = Visibility.Collapsed;
        PengoToolsLabel.Visibility = Visibility.Collapsed;
        PengoToolButtons.Margin = new Thickness(0, 6, 0, 0);
        SetRedemptionCodeRowHeight(30);
    }

    private void ApplyCombinedStatusLayout()
    {
        CombinedStatusGrid.RowDefinitions.Clear();
        CombinedStatusGrid.ColumnDefinitions.Clear();
        CombinedStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        CombinedStatusGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        CombinedStatusGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        CombinedStatusGrid.ColumnDefinitions.Add(CombinedBannerColumn);
        CombinedBannerColumn.Width = new GridLength(1, GridUnitType.Star);
        PlaceGridItem(CodesHeaderGrid, 0, 0, 1, 1);
        PlaceGridItem(CodesHeaderDivider, 1, 0, 1, 1);
        PlaceGridItem(SignalPanel, 2, 0, 1, 1);
    }

    private double ApplyToolButtonLayout(double availableWidth, bool forceStacked)
    {
        var visibleButtons = new FrameworkElement[]
        {
            PullExportToggle,
            AchievementExportPanel,
            OpenUpdaterButton,
            CancelExportButton,
            OpenExportsButton,
        }.Where(button => button.Visibility is Visibility.Visible).ToArray();
        var visibleButtonCount = visibleButtons.Length;
        var requiredWidth = visibleButtons.Sum(button => button switch
        {
            _ when ReferenceEquals(button, PullExportToggle) => 104,
            _ when ReferenceEquals(button, AchievementExportPanel) => 142,
            _ when ReferenceEquals(button, OpenUpdaterButton) => 142,
            _ when ReferenceEquals(button, CancelExportButton) => 86,
            _ when ReferenceEquals(button, OpenExportsButton) => 98,
            _ => 0,
        }) + Math.Max(0, visibleButtonCount - 1) * 6;
        var stacked = forceStacked || requiredWidth > availableWidth;
        PengoToolButtons.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
        PengoToolButtons.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var button in new FrameworkElement[]
        {
            PullExportToggle,
            AchievementExportToggle,
            OpenUpdaterButton,
            CancelExportButton,
            OpenExportsButton,
        })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Width = stacked ? double.NaN : button switch
            {
                _ when ReferenceEquals(button, PullExportToggle) => 104,
                _ when ReferenceEquals(button, AchievementExportToggle) => 142,
                _ when ReferenceEquals(button, OpenUpdaterButton) => 142,
                _ => double.NaN,
            };
        }
        return stacked ? (visibleButtonCount * 48) + (Math.Max(0, visibleButtonCount - 1) * 6) : 48;
    }

    private void SetRedemptionCodeRowHeight(double height)
    {
        redemptionCodeRowHeight = height;
        foreach (var row in RedemptionCodeRows)
        {
            row.SetRowHeight(height);
        }
    }

    private void SetRedemptionCodeMetadataVisibility()
    {
        foreach (var row in RedemptionCodeRows)
        {
            row.SetMetadataVisibility(!compactCodeRows);
        }
    }

    private static void PlaceGridItem(
        FrameworkElement element,
        int row,
        int column,
        int rowSpan,
        int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void RenderSelection()
    {
        RefreshGameRailSignals();
        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        ApplySelectedAppearance(selected.Id);
        gameSnapshot = sessions.TryGetSnapshot(selected.Id, out var selectedSnapshot)
            ? selectedSnapshot
            : null;
        ApplySavedPanelVisibility(selected);
        WuWaAccountStatusStrip.Visibility = !selected.IsCustom
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!selected.IsCustom)
        {
            if (selected.Id == "wuwa") RenderWuWaAccountStatus();
            else RenderPublisherAccountStatus(selected.Id);
        }
        RedemptionCodeList.Visibility = Visibility.Visible;
        ApplyLayout();
        if (launcherState.Snapshot.SelectedGameId != selected.Id)
        {
            _ = launcherState.TryUpdate(state => state with { SelectedGameId = selected.Id });
        }
        RenderBannerCycle();
        RenderExportTools(selected);
        RenderRenderingMode(selected);

        if (selected.IsCustom)
        {
            RenderCustomGame(selected);
        }
        else if (selected.Id == "gi")
        {
            RenderGenshin();
        }
        else if (selected.Id is "hsr" or "zzz")
        {
            RenderHoyo(selected);
        }
        else if (selected.Id == "wuwa")
        {
            RenderWuWa(selected);
        }
        else
        {
            RenderEndfield(selected);
        }

        ApplyPrimaryGameStatus(selected);
        SyncRedesignedControls(selected);
        ApplySavedPanelVisibility(selected);
    }

    private void ApplySavedPanelVisibility(GameLauncherItem selected)
    {
        var visibility = launcherState.Snapshot.Preferences.VisibilityFor(selected.Id);
        BannerCycleRegion.Visibility = !selected.IsCustom && visibility.ShowBanners
            ? Visibility.Visible
            : Visibility.Collapsed;
        CombinedStatusPanel.Visibility = !selected.IsCustom && visibility.ShowRedemptionCodes
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountAndToolsPanel.Visibility = !selected.IsCustom && visibility.ShowAccountAndExport
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SyncRedesignedControls(GameLauncherItem selected)
    {
        if (selected.IsCustom)
        {
            RenderPreInstallNotice(selected);
            LaunchResourceMetricsPanel.Visibility = Visibility.Collapsed;
            AccountAndToolsIdentityText.Visibility = Visibility.Collapsed;
            OnLaunchPanel.Visibility = Visibility.Collapsed;
            Fps120Toggle.Visibility = Visibility.Collapsed;
            StableOpenScreenshotFolderButton.IsEnabled = false;
            return;
        }

        var definition = GameCatalog.GetRequired(selected.Id);
        AccountAndToolsProviderText.Text = "ACCOUNT";
        ChangePublisherAccountButton.Content = "Accounts";
        SetStableExportStatus(NyxToolsStatusText.Text);
        StableOpenUpdaterButton.Content = OpenUpdaterButton.Content;
        StableOpenUpdaterButton.IsEnabled = OpenUpdaterButton.IsEnabled;
        RenderPreInstallNotice(selected);
        AutomationProperties.SetName(
            StableOpenUpdaterButton,
            $"Open {selected.DisplayName}'s official launcher");
        var officialLauncherStatus = officialLauncherStatusOverride
            ?? (OpenUpdaterButton.Content?.ToString() switch
            {
                "Try Again" => "Official launcher failed to open. Try again.",
                _ when !updaterScanFinished => "Checking the official launcher...",
                _ when updaterStatus == GenshinLaunchStatus.Running => "Official launcher is open.",
                _ => string.Empty,
            });
        if (!string.IsNullOrWhiteSpace(officialLauncherStatus))
        {
            SetLaunchDetail(officialLauncherStatus);
        }

        Fps120Toggle.Visibility = definition.Supports120Fps
            ? Visibility.Visible
            : Visibility.Collapsed;
        Fps120Toggle.IsChecked = definition.Supports120Fps
            && app.Is120FpsOnLaunch(selected.Id);
        Fps120Toggle.IsEnabled = definition.Supports120Fps;
        AutomationProperties.SetName(
            Fps120Toggle,
            selected.Id switch
            {
                "gi" => "Set Genshin Impact to 120 FPS on launch",
                "hsr" => "Set Star Rail to 120 FPS on launch",
                _ => "Set 120 FPS on launch",
            });
        ToolTipService.SetToolTip(
            Fps120Toggle,
            selected.Id switch
            {
                "gi" => "Set Genshin Impact to 120 FPS before launch.",
                "hsr" => "Set Star Rail to 120 FPS before launch.",
                _ => "Set the selected game to 120 FPS before launch.",
            });

        StableOpenScreenshotFolderButton.IsEnabled = !screenshotFolderActionInFlight
            && definition.SupportsScreenshots;
        AutomationProperties.SetName(
            StableOpenScreenshotFolderButton,
            $"Open {selected.DisplayName} screenshot folder");

        var dailySupported = definition.SupportsDailyCheckIn;
        AutomaticDailyCheckInToggle.Visibility = dailySupported
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomaticDailyCheckInToggle.IsChecked = dailySupported
            && launcherState.Snapshot.Preferences.AutomaticDailyCheckInGames.Contains(
                selected.Id,
                StringComparer.Ordinal);
        OnLaunchPanel.Visibility = dailySupported || definition.Supports120Fps
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (selected.Id == "wuwa")
        {
            RenderWuWaAccountIdentity();
            var enabled = IsWuWaAccountStatusEnabled();
            AccountConnectionButton.Content = enabled ? "Stop" : "Start";
            AccountConnectionButton.IsEnabled = !wuwaAccountStatusActionInFlight;
            AutomationProperties.SetName(
                AccountConnectionButton,
                enabled
                    ? "Stop Wuthering Waves resource status"
                    : "Start Wuthering Waves resource status");
            ChangePublisherAccountButton.Visibility = Visibility.Collapsed;
            var wuwaSnapshot = wuwaAccountStatus.Current?.Snapshot;
            RenderLaunchResourceMetrics(
                selected.Id,
                wuwaSnapshot is null
                    ? null
                    : LauncherResourceMetricsProjection.FromWuWa(wuwaSnapshot));
            LaunchResourceRefreshButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            LaunchResourceRefreshButton.IsEnabled = !wuwaAccountStatusActionInFlight;
            return;
        }

        RenderHoyoLabAccountIdentity(selected);

        var entry = PublisherAccountCatalog.Get(selected.Id);
        var summary = publisherAccounts.Current;
        var connection = entry.Provider == "HoYoLAB" ? summary.HoyoLab : summary.Skport;
        var consentEnabled = publisherAccounts.HasConsent(entry.Provider);
        AccountConnectionButton.Content = publisherAccountActionInFlight
            ? "Wait"
            : !consentEnabled
                ? "Disconnected"
                : connection switch
                {
                    PublisherConnectionState.Connected => "Connected",
                    PublisherConnectionState.Connecting => "Wait",
                    PublisherConnectionState.NeedsReview => "Try Again",
                    PublisherConnectionState.LoginRequired => "Sign In",
                    _ => "Sign In",
                };
        AccountConnectionButton.IsEnabled = !publisherAccountActionInFlight
            && connection is not (PublisherConnectionState.Connecting or PublisherConnectionState.Connected);
        AutomationProperties.SetName(
            AccountConnectionButton,
            connection == PublisherConnectionState.Connected
                ? $"{entry.Provider} connected"
                : $"{AccountConnectionButton.Content} to {entry.Provider}");
        ChangePublisherAccountButton.Visibility = Visibility.Visible;
        ChangePublisherAccountButton.IsEnabled = !publisherAccountActionInFlight
            && (selected.Id == "ae"
                || consentEnabled && publisherAccounts.HoyoLabAccounts.Available);

        var resource = summary.Resources.TryGetValue(selected.Id, out var capturedResource)
            ? capturedResource
            : null;
        RenderLaunchResourceMetrics(
            selected.Id,
            consentEnabled
                && resource is not null
                    ? LauncherResourceMetricsProjection.FromPublisher(resource, AccountDisplayClock())
                    : null);
        LaunchResourceRefreshButton.Visibility = entry.SupportsNumericResource
            && consentEnabled
            && connection == PublisherConnectionState.Connected
                ? Visibility.Visible
                : Visibility.Collapsed;
        LaunchResourceRefreshButton.IsEnabled = !publisherAccountActionInFlight;
    }

    private void ScheduleStableUpdateAfterFirstFrame()
    {
        if (stableUpdateScheduled) return;
        stableUpdateScheduled = true;
        stableUpdateFramePending = true;
        CompositionTarget.Rendering += StableUpdate_FirstFrameRendering;
    }

    private void StableUpdate_FirstFrameRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= StableUpdate_FirstFrameRendering;
        stableUpdateFramePending = false;
        RecordInitialRenderDuration();
        _ = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => app.StartStableUpdate(RunStableUpdateAsync));
    }

    private async Task RunStableUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installation = StableUpdatePolicy.FindInstalled(
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                StableUpdateBuildIdentity.Channel,
                StableUpdateBuildIdentity.Version);
            if (installation is null) return;

            if (!await StableUpdateHandoffClient.ConfirmCurrentAsync(
                installation.ControlUpdaterPath,
                Environment.ProcessId,
                cancellationToken)) return;
            using var transport = new StableUpdateTransport();
            var update = await transport.CheckAsync(
                installation.CurrentVersion,
                cancellationToken);
            if (update is null) return;

            var download = await transport.DownloadIfAcceptedAsync(
                update,
                installation.StagingRoot,
                () => ConfirmStableUpdateAsync(update.Manifest, cancellationToken),
                cancellationToken);
            if (download is null) return;

            _ = await StableUpdateHandoffClient.HandoffAsync(
                installation.ControlUpdaterPath,
                download,
                Environment.ProcessId,
                app.BeginStableUpdateShutdown,
                cancellationToken);
        }
        catch (Exception)
        {
            // Installed update checks are optional and intentionally silent.
        }
    }

    private async Task<bool> ConfirmStableUpdateAsync(
        UpdateReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        var mebibytes = manifest.PackageSize / (1024d * 1024d);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Nyx update available",
            Content = $"Version {manifest.Version} is ready ({mebibytes:0.#} MB). Download and install it now?",
            PrimaryButtonText = "Update now",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["NyxDialogPrimaryStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["NyxDialogQuietStyle"],
        };
        return await dialog.ShowAsync().AsTask(cancellationToken) is ContentDialogResult.Primary;
    }

    private void RenderPreInstallNotice(GameLauncherItem selected)
    {
        var message = selected.Id == "wuwa"
            && wuwaMaintenanceStatus is (
                WuWaOfficialMaintenanceStatus.Ready
                or WuWaOfficialMaintenanceStatus.Running
                or WuWaOfficialMaintenanceStatus.Opened
                or WuWaOfficialMaintenanceStatus.Failed)
            && wuwaMaintenanceRequest?.PreInstallAvailable == true
                ? "Pre-install available — open Official Launcher"
                : selected.Id is "gi" or "hsr" or "zzz"
                    ? GameRailSignalProjector.ProjectPublisher(selected.Id, publisherStatus.Current)?.Kind switch
                    {
                        GameRailSignalKind.UpdateAndPreDownload =>
                            "Update and pre-install available — open Official Launcher",
                        GameRailSignalKind.PreDownloadAvailable =>
                            "Pre-install available — open Official Launcher",
                        _ => null,
                    }
                    : null;
        var available = message is not null;
        PreInstallNoticeButton.IsEnabled = available && StableOpenUpdaterButton.IsEnabled;
        var key = message is null ? null : $"{selected.Id}:{message}";
        if (string.Equals(preInstallNoticeKey, key, StringComparison.Ordinal))
        {
            return;
        }

        preInstallNoticeKey = key;
        PreInstallNoticeButton.Content = message ?? string.Empty;
        PreInstallNoticeButton.Visibility = available
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationProperties.SetName(
            PreInstallNoticeButton,
            message ?? "No pre-install available");
        AutomationProperties.SetHelpText(PreInstallNoticeButton, message);
        StableOpenUpdaterButton.BorderBrush = (Brush)Application.Current.Resources[
            available ? "PreInstallNoticeBrush" : "DeckBorderBrush"];
        StableOpenUpdaterButton.Background = (Brush)Application.Current.Resources[
            available ? "PreInstallSurfaceBrush" : "QuietSurfaceBrush"];
        StableOpenUpdaterButton.BorderThickness = new Thickness(available ? 2 : 1);
    }

    private void RenderHoyoLabAccountIdentity(GameLauncherItem selected)
    {
        if (selected.Id == "ae")
        {
            var endfieldIdentityText = publisherAccounts.EndfieldIdentity?.DisplayText ?? string.Empty;
            AccountAndToolsIdentityText.Text = endfieldIdentityText;
            AccountAndToolsIdentityText.Visibility = !accountSectionExpanded || string.IsNullOrEmpty(endfieldIdentityText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            AutomationProperties.SetName(
                AccountAndToolsIdentityText,
                string.IsNullOrEmpty(endfieldIdentityText)
                    ? "No Endfield account selected"
                    : $"Endfield account: {endfieldIdentityText}");
            return;
        }

        if (selected.Id is not ("gi" or "hsr" or "zzz"))
        {
            AccountAndToolsIdentityText.Text = string.Empty;
            AccountAndToolsIdentityText.Visibility = Visibility.Collapsed;
            return;
        }

        var identity = publisherAccounts.GetHoyoLabIdentity(selected.Id);
        var identityText = identity is { IsBound: true }
            ? identity.CharacterSummary
            : string.Empty;
        AccountAndToolsIdentityText.Text = identityText;
        AccountAndToolsIdentityText.Visibility = accountSectionExpanded && identity is { IsBound: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        var needsRegion = identity is not { IsBound: true };
        ChangePublisherAccountButton.Content = "Accounts";
        AutomationProperties.SetName(
            ChangePublisherAccountButton,
            needsRegion
                ? "Choose the HoYoLAB region for this game"
                : "Change HoYoLAB account or region");
        AutomationProperties.SetName(
            AccountAndToolsIdentityText,
            needsRegion
                ? "No HoYoLAB character region selected"
                : $"HoYoLAB character identity: {identityText}");
        AutomationProperties.SetHelpText(
            AccountAndToolsIdentityText,
            "HoYoLAB character identity and region; connection state is shown separately.");
    }

    private ImageSource? ResolveImageSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var key = uri.AbsoluteUri;
        if (imageSourceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = new BitmapImage(uri);
        imageSourceCache.Add(key, image);
        return image;
    }

    private void RenderLaunchResourceMetrics(
        string gameId,
        LauncherResourceMetrics? metrics)
    {
        if (metrics is null
            || !PrimaryResourceIconPaths.TryGetValue(gameId, out var primaryIconPath)
            || !ResourceMetricNames.TryGetValue(gameId, out var names))
        {
            LaunchResourceMetricsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LaunchResourceMetricsPanel.Visibility = Visibility.Visible;

        var primaryIcon = ResolveImageSource(primaryIconPath);
        if (!ReferenceEquals(LaunchPrimaryResourceIcon.Source, primaryIcon))
            LaunchPrimaryResourceIcon.Source = primaryIcon;
        LaunchPrimaryResourceValueText.Text = metrics.Primary;
        LaunchPrimaryResourceItem.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(LaunchPrimaryResourceItem, names.Primary);

        var showReserve = metrics.Reserve is not null;
        if (showReserve && ReserveResourceIconPaths.TryGetValue(gameId, out var reserveIconPath))
        {
            LaunchReserveResourceItem.Visibility = Visibility.Visible;
            var reserveIcon = ResolveImageSource(reserveIconPath);
            if (!ReferenceEquals(LaunchReserveResourceIcon.Source, reserveIcon))
                LaunchReserveResourceIcon.Source = reserveIcon;
            LaunchReserveResourceValueText.Text = metrics.Reserve;
            ToolTipService.SetToolTip(LaunchReserveResourceItem, names.Reserve);
        }
        else
        {
            LaunchReserveResourceItem.Visibility = Visibility.Collapsed;
        }

        LaunchRecoveryResourceItem.Visibility = metrics.Recovery is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (metrics.Recovery is not null)
            LaunchRecoveryResourceValueText.Text = metrics.Recovery;

        LaunchDailyResourceItem.Visibility = metrics.Daily is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (metrics.Daily is not null)
            LaunchDailyResourceValueText.Text = metrics.Daily;

        AutomationProperties.SetName(LaunchResourceMetricsPanel, metrics.AutomationText);
    }

    private void RenderRenderingMode(GameLauncherItem selected)
    {
        var supported = selected.Id is "zzz" or "wuwa";
        RenderingModePanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;

        var mode = GetRenderingMode(selected.Id);
        RenderingModeButton.Content = mode switch
        {
            "dx12" => "DX12 ON",
            "dx11" => "DX11 ON",
            _ when selected.Id == "zzz" => "DX12 OFF",
            _ => "DX11 OFF",
        };
        RenderingModeResetButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(
            RenderingModeButton,
            selected.Id == "zzz"
                ? $"ZZZ graphics mode: {(mode == "dx12" ? "DirectX 12" : "publisher default")}. Activate to change."
                : $"Wuthering Waves graphics mode: {(mode == "dx11" ? "DirectX 11" : "publisher default")}. Activate to change.");
    }

    private void ApplyPrimaryGameStatus(GameLauncherItem selected)
    {
        var snapshot = gameSnapshot ?? sessions.GetSnapshot(selected.Id);
        var signal = GameRailSignalProjector.Project(
            selected.Id,
            snapshot,
            publisherStatus.Current,
            directLaunchSupported: true);
        var projection = PrimaryGameStatusProjector.Project(
            signal.Kind,
            supportsFolderPicker: !selected.IsCustom);
        primaryGameStatusAction = projection.Action;
        HeroDescription.Text = projection.Text;
        PrimaryGameStatusButton.IsHitTestVisible = projection.Action is not PrimaryGameStatusAction.None;
        PrimaryGameStatusButton.IsTabStop = projection.Action is not PrimaryGameStatusAction.None;
        AutomationProperties.SetName(PrimaryGameStatusButton, projection.Text);
    }

    private async void PrimaryGameStatusButton_Click(object sender, RoutedEventArgs e)
    {
        switch (primaryGameStatusAction)
        {
            case PrimaryGameStatusAction.OpenOfficialLauncher:
                OpenUpdaterButton_Click(sender, e);
                break;
            case PrimaryGameStatusAction.ChooseGameFolder:
                await ChooseGameFolderAsync();
                break;
            case PrimaryGameStatusAction.OpenRecovery:
                await ShowSettingsAsync(initialTabIndex: 2);
                break;
            case PrimaryGameStatusAction.RetryLaunch:
                LaunchButton_Click(sender, e);
                break;
        }
    }

    private void ApplySelectedAppearance(string gameId)
    {
        var isOfficial = gameId is "gi" or "hsr" or "zzz" or "wuwa" or "ae";
        if (isOfficial && launcherVisualRequestedGameId == gameId) return;
        launcherVisualRequestedGameId = gameId;
        var generation = ++launcherVisualGeneration;
        launcherGalleryTimer.Stop();
        activeLauncherVisual = null;
        if (isOfficial)
        {
            launcherImageRequestToken++;
            if (preloadedLauncherVisuals.TryGetValue(gameId, out var preloaded))
            {
                ApplyLauncherVisual(preloaded, generation);
            }
            else if (launcherVisuals.TryLoadLastGood(gameId) is { } cached)
            {
                preloadedLauncherVisuals[gameId] = cached;
                ApplyLauncherVisual(cached, generation);
            }
            else if (LauncherBackgroundSourceProjection.From(launcherState.Snapshot, gameId) is { } fallback)
            {
                PrepareLauncherImageBackground(fallback, generation, TimeSpan.FromMilliseconds(380));
            }
            if (pageLease is { } lease) StartLauncherVisualPreload(lease);
            return;
        }

        HideLauncherMotionBackgrounds();
        SetBackgroundSource(LauncherBackgroundSourceProjection.From(launcherState.Snapshot, gameId));
        BackgroundArtwork.Opacity = 1;
    }

    private void StartLauncherVisualPreload(SessionUiLease lease)
    {
        if (launcherVisualPreloadTask is { IsCompleted: false }) return;
        launcherVisualPreloadTask = launcherVisuals.RefreshAllAsync(selection =>
        {
            _ = DispatcherQueue.TryEnqueue(() =>
                sessionUiLifetime.TryRun(lease, () =>
                {
                    preloadedLauncherVisuals[selection.GameId] = selection;
                    if (GameSelector?.SelectedItem is GameLauncherItem selected
                        && selected.Id == selection.GameId)
                        ApplyLauncherVisual(selection, launcherVisualGeneration);
                }));
        }, lease.CancellationToken);
    }

    private void ApplyLauncherVisual(LauncherVisualSelection selection, int generation)
    {
        if (generation != launcherVisualGeneration
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.Id != selection.GameId) return;
        if (activeLauncherVisual?.GameId == selection.GameId
            && activeLauncherVisual.Revision == selection.Revision
            && activeLauncherVisual.Files.SequenceEqual(selection.Files)
            && (selection.Kind != "video" || visibleLauncherMotionBackground is not null)) return;
        activeLauncherVisual = selection;
        launcherGalleryIndex = selection.Kind == "gallery" && selection.Files.Count > 1
            ? Random.Shared.Next(selection.Files.Count)
            : 0;
        if (selection.Kind == "video")
        {
            var hasVisibleBackground = visibleLauncherMotionBackground?.Source is not null
                || visibleLauncherImageBackground?.Source is not null;
            if (!hasVisibleBackground && selection.Files.Count > 1)
            {
                SetBackgroundSource(selection.Files[1]);
                BackgroundArtwork.Opacity = 1;
                BackgroundArtworkNext.Opacity = 0;
                visibleLauncherImageBackground = BackgroundArtwork;
            }
            PrepareLauncherMotionBackground(selection.Files[0], generation);
            return;
        }
        ApplyLauncherGalleryFrame();
        if (!launcherMotionPaused && selection.Kind == "gallery" && selection.Files.Count > 1)
            launcherGalleryTimer.Start();
    }

    private void PrepareLauncherMotionBackground(string file, int generation)
    {
        var incoming = ReferenceEquals(visibleLauncherMotionBackground, LauncherMotionBackground)
            ? LauncherMotionBackgroundNext
            : LauncherMotionBackground;
        incoming.Opacity = 0;
        if (ReferenceEquals(incoming, LauncherMotionBackground)) launcherMotionPrimaryGeneration = generation;
        else launcherMotionSecondaryGeneration = generation;
        if (incoming.MediaPlayer is not { } player) return;
        player.MediaOpened -= LauncherMotionPlayer_MediaOpened;
        player.MediaOpened += LauncherMotionPlayer_MediaOpened;
        player.MediaFailed -= LauncherMotionPlayer_MediaFailed;
        player.MediaFailed += LauncherMotionPlayer_MediaFailed;
        player.IsMuted = true;
        player.IsLoopingEnabled = true;
        incoming.Source = MediaSource.CreateFromUri(new Uri(file));
        if (!launcherMotionPaused) player.Play();
    }

    private void LauncherMotionPlayer_MediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var incoming = ReferenceEquals(LauncherMotionBackground.MediaPlayer, sender)
                ? LauncherMotionBackground
                : ReferenceEquals(LauncherMotionBackgroundNext.MediaPlayer, sender)
                    ? LauncherMotionBackgroundNext
                    : null;
            if (incoming is null) return;
            var generation = ReferenceEquals(incoming, LauncherMotionBackground)
                ? launcherMotionPrimaryGeneration
                : launcherMotionSecondaryGeneration;
            if (generation != launcherVisualGeneration)
            {
                incoming.MediaPlayer?.Pause();
                incoming.Source = null;
                return;
            }
            if (launcherMotionPaused) incoming.MediaPlayer?.Pause();
            BeginLauncherBackgroundCrossfade(
                incoming,
                generation,
                TimeSpan.FromMilliseconds(380));
        });
    }

    private void LauncherMotionPlayer_MediaFailed(
        Windows.Media.Playback.MediaPlayer sender,
        Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var incoming = ReferenceEquals(LauncherMotionBackground.MediaPlayer, sender)
                ? LauncherMotionBackground
                : ReferenceEquals(LauncherMotionBackgroundNext.MediaPlayer, sender)
                    ? LauncherMotionBackgroundNext
                    : null;
            if (incoming is null) return;
            var generation = ReferenceEquals(incoming, LauncherMotionBackground)
                ? launcherMotionPrimaryGeneration
                : launcherMotionSecondaryGeneration;
            if (generation != launcherVisualGeneration
                || activeLauncherVisual is not { Kind: "video" } selection) return;
            HideLauncherMotionBackgrounds();
            if (selection.Files.Count > 1)
                PrepareLauncherImageBackground(
                    selection.Files[1],
                    generation,
                    TimeSpan.FromMilliseconds(380));
        });
    }

    private void HideLauncherMotionBackgrounds()
    {
        launcherBackgroundCrossfade?.Stop();
        launcherBackgroundCrossfade = null;
        launcherBackgroundTransitionToken++;
        visibleLauncherMotionBackground = null;
        foreach (var motion in new[] { LauncherMotionBackground, LauncherMotionBackgroundNext })
        {
            motion.Opacity = 0;
            motion.MediaPlayer?.Pause();
            motion.Source = null;
        }
        BackgroundArtwork.Opacity = 1;
        BackgroundArtworkNext.Opacity = 0;
        visibleLauncherImageBackground = BackgroundArtwork;
    }

    private void LauncherGalleryTimer_Tick(object? sender, object e)
    {
        if (launcherMotionPaused)
        {
            launcherGalleryTimer.Stop();
            return;
        }
        if (activeLauncherVisual is not { Kind: "gallery", Files.Count: > 1 } selection)
        {
            launcherGalleryTimer.Stop();
            return;
        }
        launcherGalleryIndex = (launcherGalleryIndex + 1) % selection.Files.Count;
        ApplyLauncherGalleryFrame(rotating: true);
    }

    private void ApplyLauncherGalleryFrame(bool rotating = false)
    {
        if (activeLauncherVisual is not { Files.Count: > 0 } selection
            || selection.Kind is not ("image" or "gallery")) return;
        PrepareLauncherImageBackground(
            selection.Files[launcherGalleryIndex % selection.Files.Count],
            launcherVisualGeneration,
            rotating ? TimeSpan.FromMilliseconds(700) : TimeSpan.FromMilliseconds(380));
    }

    private void PrepareLauncherImageBackground(string file, int generation, TimeSpan duration)
    {
        var incoming = ReferenceEquals(visibleLauncherImageBackground, BackgroundArtwork)
            ? BackgroundArtworkNext
            : BackgroundArtwork;
        var requestToken = ++launcherImageRequestToken;
        incoming.Opacity = 0;
        var bitmap = new BitmapImage();
        bitmap.ImageOpened += (_, _) =>
        {
            if (requestToken != launcherImageRequestToken || generation != launcherVisualGeneration) return;
            BeginLauncherBackgroundCrossfade(incoming, generation, duration);
        };
        bitmap.ImageFailed += (_, _) =>
        {
            if (requestToken != launcherImageRequestToken || generation != launcherVisualGeneration) return;
            incoming.Source = null;
            incoming.Opacity = 0;
        };
        incoming.Source = bitmap;
        bitmap.UriSource = new Uri(file);
    }

    private void BeginLauncherBackgroundCrossfade(UIElement incoming, int generation, TimeSpan duration)
    {
        if (generation != launcherVisualGeneration) return;
        launcherBackgroundCrossfade?.Stop();
        var transitionToken = ++launcherBackgroundTransitionToken;
        var layers = new UIElement[]
        {
            BackgroundArtwork,
            BackgroundArtworkNext,
            LauncherMotionBackground,
            LauncherMotionBackgroundNext,
        };
        if (launcherMotionPaused)
        {
            CompleteLauncherBackgroundCrossfade(incoming, generation, transitionToken);
            return;
        }
        if (!uiSettings.AnimationsEnabled)
        {
            CompleteLauncherBackgroundCrossfade(incoming, generation, transitionToken);
            return;
        }

        var fade = new Storyboard();
        foreach (var layer in layers)
        {
            var animation = new DoubleAnimation
            {
                From = layer.Opacity,
                To = ReferenceEquals(layer, incoming) ? 1 : 0,
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, layer);
            Storyboard.SetTargetProperty(animation, "Opacity");
            fade.Children.Add(animation);
        }
        fade.Completed += (_, _) => CompleteLauncherBackgroundCrossfade(incoming, generation, transitionToken);
        launcherBackgroundCrossfade = fade;
        fade.Begin();
    }

    private void CompleteLauncherBackgroundCrossfade(UIElement incoming, int generation, long transitionToken)
    {
        if (generation != launcherVisualGeneration || transitionToken != launcherBackgroundTransitionToken) return;
        launcherBackgroundCrossfade = null;
        foreach (var layer in new UIElement[]
                 {
                     BackgroundArtwork,
                     BackgroundArtworkNext,
                     LauncherMotionBackground,
                     LauncherMotionBackgroundNext,
                 })
            layer.Opacity = ReferenceEquals(layer, incoming) ? 1 : 0;

        if (incoming is MediaPlayerElement motion)
        {
            visibleLauncherMotionBackground = motion;
            visibleLauncherImageBackground = null;
        }
        else if (incoming is Image image)
        {
            visibleLauncherImageBackground = image;
            visibleLauncherMotionBackground = null;
        }

        foreach (var motionLayer in new[] { LauncherMotionBackground, LauncherMotionBackgroundNext })
        {
            if (ReferenceEquals(motionLayer, incoming)) continue;
            motionLayer.MediaPlayer?.Pause();
            motionLayer.Source = null;
        }
    }

    private void SetBackgroundSource(string? source)
    {
        if (string.Equals(displayedBackgroundSource, source, StringComparison.OrdinalIgnoreCase)) return;
        BackgroundArtwork.Source = string.IsNullOrWhiteSpace(source)
            ? null
            : new BitmapImage(new Uri(source));
        displayedBackgroundSource = source;
    }

    private void RenderCustomGame(GameLauncherItem selected)
    {
        BannerCycleRegion.Visibility = Visibility.Collapsed;
        NyxToolsPanel.Visibility = Visibility.Collapsed;
        RedemptionCodeList.Visibility = Visibility.Collapsed;

        var snapshot = gameSnapshot;
        if (snapshot is null || snapshot.Readiness is LocalReadinessEvidence.Unknown)
        {
            HeroDescription.Text = "Checking the exact custom executable.";
            SetLaunchControls(false, "CHECKING", "Verifying the saved file", $"Checking {selected.DisplayName}");
            return;
        }

        switch (snapshot.Status)
        {
            case LocalGameStatus.Ready:
                HeroDescription.Text = "Custom executable verified.";
                SetLaunchControls(true, "LAUNCH", string.Empty, $"Launch {selected.DisplayName}");
                break;
            case LocalGameStatus.Starting:
                HeroDescription.Text = "Waiting for the exact game process.";
                SetLaunchControls(false, "STARTING", "Waiting for the game", $"Starting {selected.DisplayName}");
                break;
            case LocalGameStatus.Running:
                HeroDescription.Text = $"{selected.DisplayName} is running.";
                SetLaunchControls(false, "RUNNING", "Detected", $"{selected.DisplayName} is running");
                break;
            case LocalGameStatus.LaunchFailed:
                HeroDescription.Text = "The custom game did not start. Check its saved path in Settings.";
                SetLaunchControls(true, "TRY AGAIN", string.Empty, $"Try launching {selected.DisplayName} again");
                break;
            case LocalGameStatus.NotFound:
                HeroDescription.Text = "The saved executable moved or is missing. Repair it in Settings.";
                SetLaunchControls(false, "NOT FOUND", "Repair the saved path", $"{selected.DisplayName} was not found");
                break;
            default:
                HeroDescription.Text = "Nyx could not prove the exact custom process.";
                SetLaunchControls(false, "LOCKED", "Review the saved path", $"{selected.DisplayName} needs review");
                break;
        }
    }

    private void RenderExportTools(GameLauncherItem selected)
    {
        NyxToolsPanel.Visibility = Visibility.Collapsed;
        ApplySavedPanelVisibility(selected);
        if (selected.IsCustom) return;
        var definition = GameCatalog.GetRequired(selected.Id);
        var armed = launcherState.Snapshot.Export.Games.TryGetValue(selected.Id, out var saved)
            ? saved
            : new Nyx.Desktop.Core.State.ExportGameArming
            {
                AchievementSource = AchievementExportSources.Normalize(selected.Id, null),
            };
        var achievementSource = AchievementExportSources.Normalize(
            selected.Id,
            armed.AchievementSource);
        if (selected.Id == "hsr" && armed.PullsArmed)
        {
            achievementSource = AchievementExportSources.Game;
        }
        var capability = ExportProviderCatalog.GetEnabled(
            selected.Id,
            launcherState.Snapshot.Preferences.FeatureFlags,
            achievementSource);
        var gameAchievementAvailable = ExportProviderCatalog.GetEnabled(
            selected.Id,
            launcherState.Snapshot.Preferences.FeatureFlags,
            AchievementExportSources.Game).Supports(ExportKind.Achievements);
        var hoyoLabAchievementAvailable = selected.Id == "hsr"
            && ExportProviderCatalog.GetEnabled(
                selected.Id,
                launcherState.Snapshot.Preferences.FeatureFlags,
                AchievementExportSources.HoyoLab).Supports(ExportKind.Achievements);
        var pullsOffered = definition.SupportsPulls;
        var achievementsOffered = definition.SupportsAchievements;
        var pullsAvailable = capability.Supports(ExportKind.Pulls);
        var achievementsAvailable = selected.Id == "hsr"
            ? gameAchievementAvailable || hoyoLabAchievementAvailable
            : capability.Supports(ExportKind.Achievements);
        var usesHoyoLab = selected.Id == "hsr"
            && achievementSource == AchievementExportSources.HoyoLab;
        var hasActiveJob = latestExportJobs.TryGetValue(selected.Id, out var activeJobId)
            && !exports.GetSnapshot(activeJobId).IsFinished;
        var hasHoyoLabExportPreparation = selected.Id == "hsr"
            && hoyoLabExportReservation.IsHeld;
        var sourceLocked = armed.PullsArmed;
        var showAchievementSource = selected.Id == "hsr"
            && armed.AchievementsArmed
            && achievementsAvailable;
        PullExportToggle.IsChecked = pullsAvailable && armed.PullsArmed;
        AchievementExportToggle.IsChecked = !usesHoyoLab
            && achievementsAvailable
            && armed.AchievementsArmed;
        StablePullExportToggle.IsChecked = pullsAvailable && armed.PullsArmed;
        StableAchievementExportToggle.IsChecked = !usesHoyoLab
            && achievementsAvailable
            && armed.AchievementsArmed;
        PullExportToggle.Visibility = pullsOffered
            ? Visibility.Visible
            : Visibility.Collapsed;
        AchievementExportPanel.Visibility = achievementsOffered
            ? Visibility.Visible
            : Visibility.Collapsed;
        AchievementSourceButton.Visibility = showAchievementSource
            ? Visibility.Visible
            : Visibility.Collapsed;
        AchievementSourceButton.IsEnabled = selected.Id == "hsr"
            && !sourceLocked
            && !gameActionsInFlight.Contains(selected.Id)
            && !hasHoyoLabExportPreparation
            && !hasActiveJob;
        AchievementSourceButton.Content = achievementSource == AchievementExportSources.HoyoLab
            ? "HOYOLAB \u25BE"
            : "GAME \u25BE";
        AchievementSourceOptionsPanel.Visibility = showAchievementSource
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameAchievementSourceRadio.IsChecked = selected.Id == "hsr"
            && achievementSource == AchievementExportSources.Game;
        HoyoLabAchievementSourceRadio.IsChecked = selected.Id == "hsr"
            && achievementSource == AchievementExportSources.HoyoLab;
        Grid.SetColumnSpan(GameAchievementSourceRadio, 1);
        GameAchievementSourceRadio.Visibility = showAchievementSource
            ? Visibility.Visible
            : Visibility.Collapsed;
        Grid.SetColumn(GameAchievementSourceRadio, 1);
        Grid.SetColumn(HoyoLabAchievementSourceRadio, 2);
        Grid.SetColumnSpan(HoyoLabAchievementSourceRadio, 1);
        HoyoLabAchievementSourceRadio.Visibility = showAchievementSource && !armed.PullsArmed
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameAchievementSourceRadio.IsEnabled = gameAchievementAvailable
            && !sourceLocked
            && !gameActionsInFlight.Contains(selected.Id)
            && !hasHoyoLabExportPreparation
            && !hasActiveJob;
        HoyoLabAchievementSourceRadio.IsEnabled = hoyoLabAchievementAvailable
            && !sourceLocked
            && AchievementSourceButton.IsEnabled;
        AchievementExportToggle.Height = selected.Id == "hsr" ? 28 : 34;
        AchievementExportToggle.MinHeight = selected.Id == "hsr" ? 28 : 34;
        AchievementExportLabel.Text = "Achievements";
        PullExportToggle.IsEnabled = pullsAvailable
            && !hasActiveJob
            && !gameActionsInFlight.Contains(selected.Id)
            && !hasHoyoLabExportPreparation;
        AchievementExportToggle.IsEnabled = achievementsAvailable
            && !hasActiveJob
            && !gameActionsInFlight.Contains(selected.Id)
            && !hasHoyoLabExportPreparation;
        StablePullExportToggle.IsEnabled = PullExportToggle.IsEnabled;
        StablePullExportToggle.Visibility = pullsOffered ? Visibility.Visible : Visibility.Collapsed;
        StableAchievementExportToggle.IsEnabled = AchievementExportToggle.IsEnabled;
        StableAchievementExportToggle.Visibility = achievementsOffered ? Visibility.Visible : Visibility.Collapsed;
        PullExportCard.Visibility = pullsOffered ? Visibility.Visible : Visibility.Collapsed;
        AchievementExportCard.Visibility = achievementsOffered ? Visibility.Visible : Visibility.Collapsed;
        var pullAccessibilityName = pullsAvailable
            ? $"Export pulls on the next {selected.DisplayName} launch"
            : $"Pull export for {selected.DisplayName} is coming later";
        var achievementAccessibilityName = achievementsAvailable
            ? selected.Id == "hsr"
                ? "Turn on Star Rail achievement export, then choose Game or HoYoLAB"
                : $"Export achievements on the next {selected.DisplayName} launch"
            : $"Achievement export for {selected.DisplayName} is coming later";
        AutomationProperties.SetName(PullExportToggle, pullAccessibilityName);
        AutomationProperties.SetName(StablePullExportToggle, pullAccessibilityName);
        ToolTipService.SetToolTip(StablePullExportToggle, pullAccessibilityName);
        AutomationProperties.SetName(AchievementExportToggle, achievementAccessibilityName);
        AutomationProperties.SetName(StableAchievementExportToggle, achievementAccessibilityName);
        ToolTipService.SetToolTip(StableAchievementExportToggle, achievementAccessibilityName);

        CancelExportButton.Visibility = Visibility.Collapsed;
        OpenExportsButton.Visibility = Visibility.Collapsed;
        if (latestExportJobs.TryGetValue(selected.Id, out var jobId))
        {
            var job = exports.GetSnapshot(jobId);
            CancelExportButton.Visibility = job.IsFinished ? Visibility.Collapsed : Visibility.Visible;
            var handoff = achievementHandoffs.TryGetValue(jobId, out var savedHandoff)
                ? savedHandoff
                : AchievementHandoffUiState.None;
            OpenExportsButton.Visibility = job.IsFinished
                && handoff is not AchievementHandoffUiState.Delivered
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            NyxToolsStatusText.Text = FormatExportStatus(
                job,
                handoff,
                hoyoLabImmediateExportJobs.Contains(jobId));
        }
        else
        {
            var kinds = !pullsAvailable && !achievementsAvailable && (pullsOffered || achievementsOffered)
                ? "Export tools for this game are not ready yet."
                : !pullsOffered && !achievementsOffered
                ? "No supported export tools for this game."
                : (armed.PullsArmed, armed.AchievementsArmed) switch
                {
                    (true, true) => "Pull and achievement exports will start with the next launch.",
                    (true, false) => "Pull export will start with the next launch.",
                    (false, true) => "Achievement export will start with the next launch.",
                    _ => string.Empty,
                };
            NyxToolsStatusText.Text = kinds;
        }
        SetStableExportStatus(NyxToolsStatusText.Text);
        AutomationProperties.SetName(NyxToolsPanel, $"Nyx exports for {selected.DisplayName}");
        AutomationProperties.SetName(AccountAndToolsPanel, $"Account and exports for {selected.DisplayName}");
        ApplyLayout();
    }

    private static string FormatExportStatus(
        ExportJobSnapshot job,
        AchievementHandoffUiState handoff = AchievementHandoffUiState.None,
        bool hoyoLabImmediate = false)
    {
        if (!job.IsFinished)
        {
            if (job.Achievements.State is ExportTaskState.Preparing)
                return hoyoLabImmediate
                    ? "HoYoLAB is preparing the achievement export. Star Rail can stay closed."
                    : "Achievements: preparing capture before launch...";
            if (job.Pulls.State is ExportTaskState.Preparing)
                return "Pulls: safely checking the pre-launch cache...";
            if (job.Pulls.State is ExportTaskState.Running
                && job.Achievements.State is ExportTaskState.Running)
                return "Enter the world and open Wish or Warp History. Nyx continues automatically.";
            if (job.Pulls.State is ExportTaskState.Running)
                return "Open Wish or Warp History. Nyx continues automatically.";
            if (job.Achievements.State is ExportTaskState.Running)
                return hoyoLabImmediate
                    ? "HoYoLAB is exporting achievements. Star Rail can stay closed."
                    : "Return to the title, then enter the world. Nyx continues automatically.";
            return "Export is running. Keep the game open.";
        }
        if (job.State == ExportJobState.Completed)
        {
            return handoff switch
            {
                AchievementHandoffUiState.Opening => "Export complete. Opening the Pengo preview...",
                AchievementHandoffUiState.Waiting => "Export complete. Waiting for the Pengo preview...",
                AchievementHandoffUiState.Delivered => "Export complete. Review it in the Pengo preview.",
                AchievementHandoffUiState.Fallback =>
                    "Export complete. The browser could not receive it automatically. Use Open Export Folder to view the file.",
                _ => "Export complete. The files are in Pengo Exports.",
            };
        }
        if (job.State == ExportJobState.Canceled) return "Export canceled. No unfinished file was kept.";
        if (job.State == ExportJobState.Unsupported) return "This game’s export provider is coming later.";
        var failures = new List<string>(2);
        if (job.Pulls.State is ExportTaskState.Failed)
            failures.Add(FormatPullFailure(job.Pulls.ErrorCode));
        if (job.Achievements.State is ExportTaskState.Failed)
            failures.Add(FormatAchievementFailure(job.Achievements.ErrorCode));
        return failures.Count switch
        {
            0 => "The export did not finish, but the game launch was not blocked.",
            2 => "Pulls and achievements failed. Open their ? help, then retry from a fresh game launch.",
            _ => failures[0],
        };
    }

    private static string FormatPullFailure(string? code) => code switch
    {
        PullExportErrorCodes.HistoryNotUpdated or PullExportErrorCodes.HistoryNotFound =>
            "Pulls: no fresh History update. Open Wish or Warp History, then try Export again.",
        PullExportErrorCodes.OutputFailed => "Pulls: Nyx could not create the export file.",
        _ => "Pulls: export failed without blocking the game.",
    };

    private static string FormatAchievementFailure(string? code) => code switch
    {
        "capture_timeout_no_frames" =>
            "Achievements: no game traffic reached the capture helper. Close the game and retry from a fresh start.",
        "capture_timeout_unrecognized_frames" =>
            "Achievements: HSR's current network format was not recognized. Launch remains available.",
        "capture_timeout_no_commands" =>
            "Achievements: game traffic arrived, but its login data could not be decoded. Close the game and retry from a fresh start.",
        "capture_timeout" or "capture_closed" or "timed-out" =>
            "Achievements: return to the title, enter the world, then try Export again.",
        "approval-canceled" => "Achievements: administrator approval was canceled.",
        "administrator_required" => "Achievements: Star Rail needs administrator approval.",
        "normal_user_required" =>
            "Achievements: close Nyx and reopen it normally, not as administrator.",
        "output-missing" or "output_write_failed" => "Achievements: Nyx could not create the export file.",
        _ => "Achievements: export failed without blocking the game.",
    };

    private void RefreshGameRailSignals()
    {
        foreach (var game in Games)
        {
            var signal = GameRailSignalProjector.Project(
                game.Id,
                sessions.GetSnapshot(game.Id),
                publisherStatus.Current,
                directLaunchSupported: true);
            game.UpdateStatus(RailSignalGlyphs[signal.Kind], signal.Description);

            if (GameSelector?.ContainerFromItem(game) is ListViewItem container)
            {
                AutomationProperties.SetName(container, game.AccessibleName);
            }
        }
    }

    private void RenderBannerCycle()
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        var panelVisibility = launcherState.Snapshot.Preferences.VisibilityFor(selected.Id);
        BannerCycleRegion.Visibility = !selected.IsCustom && panelVisibility.ShowBanners
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!selected.IsCustom
            && launcherBanners.Current.Games.TryGetValue(selected.Id, out var launcherGame))
        {
            var health = launcherBanners.Current.Health.Games.TryGetValue(selected.Id, out var gameHealth)
                ? gameHealth.Status
                : launcherBanners.Current.Health.Status;
            AutomationProperties.SetName(
                BannerCycleRegion,
                $"Current and next banners for {selected.DisplayName}. Nyx feed status: {health}.");

            SyncRedemptionCodeRows(selected.Id, launcherGame.Codes);

            var now = DateTimeOffset.UtcNow;
            var current = launcherGame.Current is { } live
                && live.Start <= now
                && (live.End is null || now < live.End)
                ? live
                : null;
            var upcoming = launcherGame.UpcomingForDisplayAt(now, 5);
            RenderBannerRows(selected.Id, current, now);
            RenderUpcomingBannerGroups(selected.Id, current, upcoming, now);
            BannerCycleHeading.Text = "BANNERS";
            BannerCycleTiming.Text = FormatBannerTimelineLabel(
                current?.Phase,
                FormatCurrentBannerTiming(current, now));
            var timingVisibility = string.IsNullOrWhiteSpace(BannerCycleTiming.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            BannerCycleTiming.Visibility = timingVisibility;
            return;
        }

        BannerCharacterRows.Clear();
        UpcomingBannerGroups.Clear();
        SyncRedemptionCodeRows(selected.Id, []);
    }

    private static string FormatCurrentBannerTiming(
        LauncherBannersCurrentPhase? current,
        DateTimeOffset now)
    {
        if (current is null) return string.Empty;
        if (current.NextChangeAt is { } change && change > now)
        {
            return $"Ends in {BannerTimingFormatter.FormatRemaining(change - now)}";
        }
        if (current.End is { } end && end > now)
        {
            return $"Ends in {BannerTimingFormatter.FormatRemaining(end - now)}";
        }
        return string.Empty;
    }

    private static string FormatBannerTimelineLabel(string? phase, string timing)
    {
        var label = FormatBannerPhaseLabel(phase);
        if (string.IsNullOrEmpty(label)) return timing;
        return string.IsNullOrEmpty(timing) ? label : $"{label} \u00B7 {timing}";
    }

    private static string FormatBannerPhaseLabel(string? phase)
    {
        var value = phase?.Trim();
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith("Version ", StringComparison.OrdinalIgnoreCase)) value = value[8..].Trim();
        else if (value.StartsWith("Patch ", StringComparison.OrdinalIgnoreCase)) value = value[6..].Trim();
        var marker = value.LastIndexOf(" Phase ", StringComparison.OrdinalIgnoreCase);
        return marker > 0
            ? $"Patch {value[..marker].Trim()} \u00B7 Phase {value[(marker + 7)..].Trim()}"
            : $"Patch {value}";
    }

    private void RenderUpcomingBannerGroups(
        string gameId,
        LauncherBannersCurrentPhase? current,
        IReadOnlyList<LauncherBannersUpcomingPhase> upcoming,
        DateTimeOffset now)
    {
        var projected = new List<UpcomingBannerGroupItem>();
        if (gameId == "ae" && current is not null)
        {
            var lossCharacters = OrderBannerCharacters(current.Characters)
                .Where(static character => character.Limited == false)
                .Select(CreateUpcomingBannerCharacter)
                .ToArray();
            if (lossCharacters.Length > 0)
            {
                projected.Add(new UpcomingBannerGroupItem(
                    $"loss:{current.Start.ToUniversalTime():O}",
                    "Available on loss",
                    lossCharacters));
            }
        }

        projected.AddRange(upcoming
            .Where(static phase => phase.Characters.Count > 0)
            .Take(5)
            .Select((phase, index) =>
            {
                var orderedCharacters = OrderBannerCharacters(phase.Characters).ToArray();
                var displayedCharacters = orderedCharacters.Length <= MaximumDisplayedBannerCharactersPerPhase
                    ? orderedCharacters.Select(CreateUpcomingBannerCharacter).ToArray()
                    :
                    [
                        .. orderedCharacters
                            .Take(MaximumDisplayedBannerCharactersPerPhase - 1)
                            .Select(CreateUpcomingBannerCharacter),
                        UpcomingBannerCharacterItem.CreateOverflow(
                            orderedCharacters
                                .Skip(MaximumDisplayedBannerCharactersPerPhase - 1)
                                .Select(CreateBannerPortrait)
                                .ToArray()),
                    ];
                return new UpcomingBannerGroupItem(
                    phase.Announced ? $"announced:{index}" : phase.Start!.Value.ToUniversalTime().ToString("O"),
                    phase.Announced
                        ? string.IsNullOrWhiteSpace(phase.Phase)
                            ? "Soon\u2122"
                            : FormatBannerPhaseLabel(phase.Phase)
                        : FormatBannerTimelineLabel(
                            phase.Phase,
                            $"Starts in {BannerTimingFormatter.FormatRemaining(phase.Start!.Value - now)}"),
                    displayedCharacters);
            })
            .ToArray());

        for (var index = 0; index < projected.Count; index++)
        {
            var next = projected[index];
            if (index < UpcomingBannerGroups.Count
                && UpcomingBannerGroups[index].Matches(next))
            {
                UpcomingBannerGroups[index].UpdateTiming(next.Timing);
            }
            else if (index < UpcomingBannerGroups.Count)
            {
                UpcomingBannerGroups[index] = next;
            }
            else
            {
                UpcomingBannerGroups.Add(next);
            }
        }

        while (UpcomingBannerGroups.Count > projected.Count)
        {
            UpcomingBannerGroups.RemoveAt(UpcomingBannerGroups.Count - 1);
        }
    }

    private void SyncRedemptionCodeRows(string gameId, IReadOnlyList<LauncherRedemptionCode> codes)
    {
        var visible = codes
            .OrderByDescending(static code => code.Added)
            .ThenBy(static code => code.Code, StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        if (visible.Length == 0)
        {
            if (RedemptionCodeRows.Count != 1 || RedemptionCodeRows[0].IsCopyable)
            {
                RedemptionCodeRows.Clear();
                RedemptionCodeRows.Add(RedemptionCodeRowItem.Empty);
            }

            RedemptionCodeRows[0].SetRowHeight(redemptionCodeRowHeight);
            SetRedemptionCodeMetadataVisibility();
            return;
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var code = visible[index];
            var existing = index < RedemptionCodeRows.Count ? RedemptionCodeRows[index] : null;
            if (existing is not null
                && existing.IsCopyable
                && string.Equals(existing.Code, code.Code, StringComparison.Ordinal))
            {
                continue;
            }

            var row = new RedemptionCodeRowItem(
                code.Code,
                code.Added,
                code.CurrencyAmount,
                code.CurrencyName,
                CurrencyIconFor(gameId),
                true,
                RedemptionUrlTemplates.ContainsKey(gameId),
                redemptionCodeRowHeight);
            if (launcherState.Snapshot.Preferences.CopiedRedemptionCodes.TryGetValue(gameId, out var copied)
                && copied.Contains(row.Code, StringComparer.Ordinal))
            {
                row.MarkPreviouslyCopied();
            }
            if (string.Equals(copiedCodeValue, row.Code, StringComparison.Ordinal))
            {
                row.MarkCopied();
                copiedCodeRow = row;
            }

            if (index < RedemptionCodeRows.Count)
            {
                RedemptionCodeRows[index] = row;
            }
            else
            {
                RedemptionCodeRows.Add(row);
            }
        }

        while (RedemptionCodeRows.Count > visible.Length)
        {
            RedemptionCodeRows.RemoveAt(RedemptionCodeRows.Count - 1);
        }
        SetRedemptionCodeMetadataVisibility();
    }

    private static string CurrencyIconFor(string gameId) => gameId switch
    {
        "ae" => "ms-appx:///Assets/Currency/ae.png",
        "gi" or "hsr" or "zzz" or "wuwa" => $"ms-appx:///Assets/Currency/{gameId}.webp",
        _ => string.Empty,
    };

    private void BannerCountdownTimer_Tick(object? sender, object e)
    {
        if (GameSelector?.SelectedItem is GameLauncherItem { IsCustom: false })
        {
            RenderBannerCycle();
        }

        RenderLocalAccountTimeTick();
    }

    private async void PublisherResourceRefreshTimer_Tick(object? sender, object e)
    {
        var lease = pageLease;
        if (lease is null) return;

        var selectedId = (GameSelector?.SelectedItem as GameLauncherItem)?.Id;
        foreach (var gameId in new[]
                 {
                     selectedId is "gi" or "hsr" or "zzz" ? selectedId : null,
                     "gi",
                     "hsr",
                     "zzz",
                 }
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            if (lease.CancellationToken.IsCancellationRequested) return;
            await RefreshPublisherResourceAutomaticallyAsync(
                gameId,
                lease,
                selected: string.Equals(selectedId, gameId, StringComparison.Ordinal));
        }

        if (!lease.CancellationToken.IsCancellationRequested
            && IsWuWaAccountStatusEnabled()
            && PublisherResourceRefreshPolicy.IsDue(
                publisherResourceAutomaticAttempts.TryGetValue("wuwa", out var wuwaAttempt)
                    ? wuwaAttempt
                    : null,
                AccountDisplayClock(),
                selected: string.Equals(selectedId, "wuwa", StringComparison.Ordinal)))
        {
            publisherResourceAutomaticAttempts["wuwa"] = AccountDisplayClock();
            await RefreshWuWaAccountStatusAsync(lease);
        }
    }

    private void RenderLocalAccountTimeTick()
    {
        if (WuWaAccountStatusStrip.Visibility is not Visibility.Visible
            || GameSelector?.SelectedItem is not GameLauncherItem selected
            || selected.IsCustom
            || selected.Id == "wuwa")
            return;

        // This is a local projection of the last snapshot. It never refreshes,
        // connects, checks in, or performs any account/network operation.
        RenderPublisherAccountStatus(selected.Id);
    }

    private async void CharacterLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                CommandParameter: Uri destination,
                Tag: string characterName,
            }
            || destination.Scheme != Uri.UriSchemeHttps
            || !destination.IsDefaultPort
            || !string.IsNullOrEmpty(destination.UserInfo)
            || !destination.Host.Equals("pengo.gg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Open {characterName}?",
            Content = $"Open {characterName}'s Pengo page in your browser?",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
        };
        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
        {
            await OpenFixedDestinationAsync(destination, $"{characterName}'s Pengo page");
        }
    }

    private void RenderBannerRows(string gameId, LauncherBannersCurrentPhase? current, DateTimeOffset now)
    {
        if (current is null)
        {
            BannerCharacterRows.Clear();
            return;
        }

        var characters = OrderBannerCharacters(current.Characters, current.SelectedCharacterId)
            .Where(character => gameId != "ae" || character.Limited != false)
            .ToArray();
        var timing = FormatCurrentBannerTiming(current, now);
        var phaseStableKey = current.Start.ToUniversalTime().ToString("O");
        BannerCharacterRows.Clear();
        var namedCount = characters.Length <= MaximumDisplayedCurrentBannerCharacters
            ? characters.Length
            : MaximumDisplayedCurrentBannerCharacters - 1;
        var rows = characters.Take(namedCount)
            .Select(character => new BannerCharacterRowItem(
                character,
                phaseStableKey,
                ResolveBannerPortrait(character),
                timing,
                true,
                false,
                100))
            .ToList();

        if (characters.Length > MaximumDisplayedCurrentBannerCharacters)
        {
            rows.Add(BannerCharacterRowItem.CreateOverflow(
                phaseStableKey,
                timing,
                characters.Skip(namedCount).Select(CreateBannerPortrait).ToArray()));
        }

        foreach (var row in rows.Chunk(5))
        {
            BannerCharacterRows.Add(row);
        }
    }

    private static IOrderedEnumerable<LauncherBannersCharacter> OrderBannerCharacters(
        IEnumerable<LauncherBannersCharacter> characters,
        string? selectedCharacterId = null) =>
        characters
            .OrderBy(character => string.Equals(character.Id, selectedCharacterId, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(character => character.Debut ?? DateTimeOffset.MaxValue);

    private ImageSource? ResolveBannerPortrait(LauncherBannersCharacter character)
    {
        var path = character.Icon is null ? null : launcherBanners.TryResolveManagedAsset(character.Icon);
        path ??= character.Variants
            .Select(launcherBanners.TryResolveManagedAsset)
            .FirstOrDefault(static candidate => candidate is not null);
        return ResolveImageSource(path);
    }

    private BannerPortraitItem CreateBannerPortrait(LauncherBannersCharacter character) =>
        new(character.Name, ResolveBannerPortrait(character));

    private UpcomingBannerCharacterItem CreateUpcomingBannerCharacter(LauncherBannersCharacter character) =>
        new(character.Id, character.Name, ResolveBannerPortrait(character), character.CharacterUrl);

    private void RenderGenshin()
    {
        OpenUpdaterButton.Visibility = Visibility.Visible;

        if (gameSnapshot is null || gameSnapshot.Readiness is LocalReadinessEvidence.Unknown)
        {
            HeroDescription.Text = "Looking for the official Genshin Impact install.";
            SetLaunchControls(false, "CHECKING", "Verifying local files", "Checking Genshin Impact");
            RenderUpdater();
            return;
        }

        var gameVersion = genshinSession.Version;
        switch (gameSnapshot.Status)
        {
            case LocalGameStatus.Ready:
                HeroDescription.Text = "Official files verified.";
                SetLaunchControls(true, "LAUNCH", VerifiedFilesDetail(gameVersion), "Launch Genshin Impact");
                break;
            case LocalGameStatus.Starting:
                HeroDescription.Text = "Nyx is waiting for the exact Genshin Impact process.";
                SetLaunchControls(false, "STARTING", "Waiting for the game", "Starting Genshin Impact");
                break;
            case LocalGameStatus.Running:
                HeroDescription.Text = "Genshin Impact is already running.";
                SetRunningExportControls("Genshin Impact", gameVersion);
                break;
            case LocalGameStatus.LaunchFailed:
                RenderLaunchFailure(gameVersion);
                break;
            case LocalGameStatus.NeedsReview:
                HeroDescription.Text = "Nyx found something unexpected. Launching stays locked.";
                SetLaunchControls(false, "LOCKED", "Check with HoYoPlay", "Genshin Impact needs review");
                break;
            default:
                HeroDescription.Text = "Genshin Impact was not found in HoYoPlay.";
                SetLaunchControls(false, "NOT FOUND", "Install with HoYoPlay", "Genshin Impact was not found");
                break;
        }

        RenderUpdater();
    }

    private void RenderHoyo(GameLauncherItem selected)
    {
        OpenUpdaterButton.Visibility = Visibility.Visible;

        var snapshot = gameSnapshot;
        var version = hoyoSessions[selected.Id].Version;
        if (snapshot is null || snapshot.Readiness is LocalReadinessEvidence.Unknown)
        {
            HeroDescription.Text = $"Looking for the official {selected.DisplayName} install.";
            SetLaunchControls(false, "CHECKING", "Verifying local files", $"Checking {selected.DisplayName}");
            RenderUpdater();
            return;
        }

        switch (snapshot.Status)
        {
            case LocalGameStatus.Ready:
                HeroDescription.Text = "Official files verified.";
                SetLaunchControls(true, "LAUNCH", VerifiedFilesDetail(version), $"Launch {selected.DisplayName}");
                break;
            case LocalGameStatus.Starting:
                HeroDescription.Text = $"Nyx is waiting for the exact {selected.DisplayName} process.";
                SetLaunchControls(false, "STARTING", "Waiting for the game", $"Starting {selected.DisplayName}");
                break;
            case LocalGameStatus.Running:
                HeroDescription.Text = $"{selected.DisplayName} is already running.";
                SetRunningExportControls(selected.DisplayName, version);
                break;
            case LocalGameStatus.LaunchFailed:
                HeroDescription.Text = $"{selected.DisplayName} did not start. Check the install in HoYoPlay.";
                SetLaunchControls(true, "TRY AGAIN", VersionOnly(version), $"Try launching {selected.DisplayName} again");
                break;
            case LocalGameStatus.NeedsReview:
                HeroDescription.Text = "Nyx found something unexpected. Launching stays locked.";
                SetLaunchControls(false, "LOCKED", "Check with HoYoPlay", $"{selected.DisplayName} needs review");
                break;
            default:
                HeroDescription.Text = $"{selected.DisplayName} was not found in HoYoPlay.";
                SetLaunchControls(false, "NOT FOUND", "Install with HoYoPlay", $"{selected.DisplayName} was not found");
                break;
        }

        RenderUpdater();
    }

    private void RenderPublisherSession(GameLauncherItem selected)
    {
        var snapshot = gameSnapshot;
        if (snapshot is null || snapshot.Readiness is LocalReadinessEvidence.Unknown)
        {
            HeroDescription.Text = $"Looking for the official {selected.DisplayName} install.";
            SetLaunchControls(false, "CHECKING", "Verifying local files", $"Checking {selected.DisplayName}");
            return;
        }

        if (snapshot.Readiness is LocalReadinessEvidence.NotFound)
        {
            HeroDescription.Text = selected.Id == "ae"
                ? "Choose the GRYPHLINK folder that contains Arknights: Endfield."
                : "The official Wuthering Waves install was not found.";
            SetLaunchControls(
                false,
                "NOT FOUND",
                selected.Id == "ae" ? "Choose the game folder" : "Check the official launcher",
                $"{selected.DisplayName} was not found");
            return;
        }

        switch (snapshot.Status)
        {
            case LocalGameStatus.Ready:
                HeroDescription.Text = "Official files verified.";
                SetLaunchControls(true, "LAUNCH", "Official files verified.", $"Launch {selected.DisplayName}");
                break;
            case LocalGameStatus.Starting:
                HeroDescription.Text = $"Nyx is waiting for the exact {selected.DisplayName} process.";
                SetLaunchControls(false, "STARTING", "Waiting for the game", $"Starting {selected.DisplayName}");
                break;
            case LocalGameStatus.Running:
                HeroDescription.Text = $"{selected.DisplayName} is already running.";
                if (selected.Id == "wuwa") SetRunningExportControls(selected.DisplayName, version: null);
                else SetLaunchControls(false, "RUNNING", "Detected", $"{selected.DisplayName} is running");
                break;
            case LocalGameStatus.LaunchFailed:
                HeroDescription.Text = selected.Id == "wuwa"
                    ? "Wuthering Waves did not start. Check its files with the official launcher."
                    : "Arknights: Endfield did not start. Choose Change Folder if GRYPHLINK moved the game.";
                SetLaunchControls(true, "TRY AGAIN", string.Empty, $"Try launching {selected.DisplayName} again");
                break;
            default:
                HeroDescription.Text = "Nyx found unexpected local files or could not prove the exact game process. Launching stays locked.";
                SetLaunchControls(false, "LOCKED", "Official files need review", $"{selected.DisplayName} needs review");
                break;
        }
    }

    private void RenderEndfield(GameLauncherItem selected)
    {
        OpenUpdaterButton.Visibility = Visibility.Visible;
        OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
            maintenanceReady: false,
            wuwaActionInFlight,
            hasRequest: wuwaMaintenanceRequest is not null);
        OpenUpdaterButton.Content = "Official Launcher";
        RenderEndfieldMaintenance();

        RenderPublisherSession(selected);
    }

    private void RenderEndfieldMaintenance()
    {
        if (!endfieldMaintenanceScanFinished || endfieldMaintenanceStatus is null)
        {
            OpenUpdaterButton.IsEnabled = false;
            AutomationProperties.SetName(OpenUpdaterButton, "Checking the official GRYPHLINK launcher");
            return;
        }

        switch (endfieldMaintenanceStatus)
        {
            case EndfieldOfficialMaintenanceStatus.Ready:
                OpenUpdaterButton.IsEnabled = !endfieldMaintenanceActionInFlight
                    && !endfieldFolderActionInFlight;
                AutomationProperties.SetName(
                    OpenUpdaterButton,
                    "Open GRYPHLINK for Endfield updates, pre-downloads, verification and repairs");
                break;
            case EndfieldOfficialMaintenanceStatus.Running:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "GRYPHLINK is running");
                break;
            case EndfieldOfficialMaintenanceStatus.Opened:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "GRYPHLINK start requested");
                break;
            case EndfieldOfficialMaintenanceStatus.Failed:
                OpenUpdaterButton.Content = "Try Again";
                OpenUpdaterButton.IsEnabled = !endfieldMaintenanceActionInFlight
                    && !endfieldFolderActionInFlight;
                AutomationProperties.SetName(OpenUpdaterButton, "Try opening GRYPHLINK again");
                break;
            case EndfieldOfficialMaintenanceStatus.NotFound:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "GRYPHLINK folder is not configured");
                break;
            default:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "GRYPHLINK maintenance needs review");
                break;
        }
    }

    private void ApplyEndfieldMaintenanceResult(EndfieldOfficialMaintenanceResult result)
    {
        endfieldMaintenanceStatus = result.Status;
        endfieldMaintenanceReason = result.InspectionReason;
    }

    private void RenderWuWa(GameLauncherItem selected)
    {
        OpenUpdaterButton.Visibility = Visibility.Visible;
        OpenUpdaterButton.IsEnabled = false;
        OpenUpdaterButton.Content = "Official Launcher";
        RenderPublisherSession(selected);

        if (IsWuWaAccountStatusEnabled()
            && !wuwaAccountInitialRefreshRequested
            && pageLease is { } lease)
        {
            wuwaAccountInitialRefreshRequested = true;
            _ = RefreshWuWaAccountStatusAsync(lease);
        }

        if (!wuwaScanFinished || wuwaMaintenanceStatus is null)
        {
            AutomationProperties.SetName(OpenUpdaterButton, "Checking the Wuthering Waves launcher");
            return;
        }

        switch (wuwaMaintenanceStatus)
        {
            case WuWaOfficialMaintenanceStatus.Ready:
                OpenUpdaterButton.Visibility = Visibility.Visible;
                OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
                    maintenanceReady: true,
                    wuwaActionInFlight,
                    hasRequest: wuwaMaintenanceRequest is not null);
                AutomationProperties.SetName(
                    OpenUpdaterButton,
                    "Open the official Wuthering Waves launcher for maintenance");
                break;
            case WuWaOfficialMaintenanceStatus.Running:
                OpenUpdaterButton.Visibility = Visibility.Visible;
                OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
                    maintenanceReady: false,
                    wuwaActionInFlight,
                    hasRequest: wuwaMaintenanceRequest is not null);
                AutomationProperties.SetName(OpenUpdaterButton, "Wuthering Waves launcher is running");
                break;
            case WuWaOfficialMaintenanceStatus.Opened:
                OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
                    maintenanceReady: false,
                    wuwaActionInFlight,
                    hasRequest: wuwaMaintenanceRequest is not null);
                AutomationProperties.SetName(OpenUpdaterButton, "Wuthering Waves launcher start requested");
                break;
            case WuWaOfficialMaintenanceStatus.Failed:
                OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
                    maintenanceReady: false,
                    wuwaActionInFlight,
                    hasRequest: wuwaMaintenanceRequest is not null);
                AutomationProperties.SetName(OpenUpdaterButton, "Wuthering Waves launcher failed to open");
                break;
            case WuWaOfficialMaintenanceStatus.NotFound:
                AutomationProperties.SetName(OpenUpdaterButton, "Wuthering Waves launcher was not found");
                break;
            default:
                OpenUpdaterButton.IsEnabled = WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
                    maintenanceReady: false,
                    wuwaActionInFlight,
                    hasRequest: wuwaMaintenanceRequest is not null);
                AutomationProperties.SetName(OpenUpdaterButton, "Wuthering Waves maintenance needs review");
                break;
        }
    }

    private void RenderWuWaAccountStatus()
    {
        RenderWuWaAccountIdentity();
        WuWaAccountResourceValueText.Text = string.Empty;
        AccountProviderText.Text = "ROVER";
        AccountConnectionWarningText.Text = "Unofficial local connection · may stop working.";
        AutomationProperties.SetHelpText(
            WuWaAccountStatusStrip,
            AccountConnectionWarningText.Text);
        PublisherAccountConnectButton.Visibility = Visibility.Collapsed;
        DailyCheckInButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(WuWaAccountStatusStrip, "Wuthering Waves Rover status");
        AutomationProperties.SetName(WuWaAccountStatusToggle, "Enable or disable local Wuthering Waves Rover status");
        var enabled = IsWuWaAccountStatusEnabled();
        WuWaAccountStatusToggle.IsChecked = enabled;
        WuWaAccountStatusToggle.Content = enabled ? "ON" : "START";
        WuWaAccountStatusToggle.IsEnabled = !wuwaAccountStatusActionInFlight;
        WuWaAccountStatusRefreshButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        WuWaAccountStatusRefreshButton.IsEnabled = !wuwaAccountStatusActionInFlight;

        if (!enabled)
        {
            WuWaAccountMetricsText.Text = "ENERGY + DAILIES";
            WuWaAccountFreshnessText.Text = wuwaAccountStatusSaveFailed
                ? "OFF · SETTING NOT SAVED"
                : "OPT IN";
            return;
        }

        if (wuwaAccountStatusActionInFlight)
        {
            // Do not show an earlier account while the cache identity is being
            // re-established for this request.
            WuWaAccountMetricsText.Text = "Checking official account status";
            WuWaAccountFreshnessText.Text = "CHECKING";
            return;
        }

        var result = wuwaAccountStatus.Current;
        if (result?.Snapshot is { } snapshot)
        {
            WuWaAccountMetricsText.Text =
                $"WP {snapshot.Energy}/{snapshot.MaxEnergy}  ·  RES {snapshot.StoreEnergy}  ·  DAILY {snapshot.Liveness}/{snapshot.LivenessMaxCount}";
        }
        else
        {
            WuWaAccountMetricsText.Text = "Waiting for official account status";
        }

        if (wuwaAccountStatusActionInFlight || result is null) return;
        var age = result.SuccessfulAt is { } successfulAt
            ? FormatAccountStatusAge(DateTimeOffset.UtcNow - successfulAt)
            : null;
        if (result.Failure is WuWaAccountStatusFailure.None)
        {
            WuWaAccountFreshnessText.Text = $"UPDATED {age ?? "NOW"}";
            return;
        }

        var failure = result.Failure switch
        {
            WuWaAccountStatusFailure.CacheNotFound => "OPEN KURO LAUNCHER",
            WuWaAccountStatusFailure.CacheMalformed => "CACHE UNREADABLE",
            WuWaAccountStatusFailure.MultipleAccounts => "CHOOSE ACCOUNT IN KURO",
            WuWaAccountStatusFailure.PlayerInfoRejected or WuWaAccountStatusFailure.RoleRejected => "SIGN IN AGAIN",
            WuWaAccountStatusFailure.Timeout => "TIMED OUT",
            WuWaAccountStatusFailure.RateLimited => age is null ? "TRY AGAIN SOON" : $"UPDATED {age}",
            WuWaAccountStatusFailure.Canceled => "CANCELED",
            _ => "STATUS UNAVAILABLE",
        };
        WuWaAccountFreshnessText.Text = result.IsStale && age is not null
            ? $"STALE {age} · {failure}"
            : failure;
    }

    private void RenderWuWaAccountIdentity()
    {
        var identity = IsWuWaAccountStatusEnabled() && !wuwaAccountStatusActionInFlight
            ? wuwaAccountStatus.Current?.Identity
            : null;
        var identityText = identity?.DisplayText ?? string.Empty;
        AccountAndToolsIdentityText.Text = identityText;
        AccountAndToolsIdentityText.Visibility = accountSectionExpanded && !string.IsNullOrEmpty(identityText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationProperties.SetName(
            AccountAndToolsIdentityText,
            string.IsNullOrEmpty(identityText)
                ? "No Wuthering Waves account selected"
                : $"Wuthering Waves account: {identityText}");
        AutomationProperties.SetHelpText(
            AccountAndToolsIdentityText,
            "Wuthering Waves account UID and region; account name is not available.");
    }

    private void RenderPublisherAccountStatus(string gameId)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        var summary = publisherAccounts.Current;
        var connection = entry.Provider == "HoYoLAB" ? summary.HoyoLab : summary.Skport;
        var consentEnabled = publisherAccounts.HasConsent(entry.Provider);
        AccountProviderText.Text = entry.Provider == "HoYoLAB" ? "HOYOLAB" : "SKPORT";
        if (gameId == "ae") AccountProviderText.Text = "GRYPHLINE";
        AccountConnectionWarningText.Text = consentEnabled
            ? "Nyx-only private browser · disconnect deletes its profile."
            : "Off by default · allow before Nyx opens publisher account pages.";
        AutomationProperties.SetHelpText(
            WuWaAccountStatusStrip,
            AccountConnectionWarningText.Text);
        AutomationProperties.SetName(WuWaAccountStatusStrip, $"{entry.Provider} account tools for {gameId}");
        AutomationProperties.SetName(
            WuWaAccountStatusToggle,
            consentEnabled
                ? $"Turn off {entry.Provider} account access and delete its Nyx profile"
                : $"Allow {entry.Provider} account access");

        var now = AccountDisplayClock();
        var resource = consentEnabled && summary.Resources.TryGetValue(gameId, out var value)
            ? value
            : null;
        var resourceState = summary.ResourceStates.TryGetValue(gameId, out var recordedResourceState)
            ? recordedResourceState
            : PublisherResourceState.NotStarted;
        var resourceDiagnostic = summary.ResourceDiagnostics.TryGetValue(
            gameId,
            out var recordedResourceDiagnostic)
                ? recordedResourceDiagnostic
                : PublisherResourceCaptureDiagnostic.NotAvailable;
        var resourceGuidance =
            PublisherAccountPresentation.ResourceCaptureGuidance(resourceDiagnostic);
        var resourceLabel = resourceGuidance ?? (resourceState switch
        {
            PublisherResourceState.Checking => "CHECKING",
            PublisherResourceState.Fresh when resource is not null =>
                $"UPDATED {FormatAccountStatusAge(now - resource.ObservedAt)}",
            PublisherResourceState.Stale when resource is not null =>
                $"STALE {FormatAccountStatusAge(now - resource.ObservedAt)}",
            PublisherResourceState.SelectionRequired => "CHOOSE REGION",
            PublisherResourceState.LoginRequired => "SIGN IN AGAIN",
            PublisherResourceState.NeedsReview => "TRY AGAIN",
            PublisherResourceState.Unavailable => "UNAVAILABLE",
            _ => "NOT CHECKED",
        });
        WuWaAccountResourceValueText.Text = string.Empty;
        WuWaAccountMetricsText.Text = resource is not null
            ? FormatPublisherResource(resource, now)
            : gameId == "ae"
                ? "OFFICIAL PROTOCOL TERMINAL"
                : $"{entry.ResourceName.ToUpperInvariant()}  —";

        if (resource is not null)
        {
            var compact = PublisherAccountDisplayProjection.FormatCompactResource(resource, now);
            WuWaAccountMetricsText.Text = compact.Label;
            WuWaAccountResourceValueText.Text = compact.Value;
            AutomationProperties.SetName(
                PublisherResourceMetricGrid,
                compact.AutomationText);
        }
        else
        {
            AutomationProperties.SetName(
                PublisherResourceMetricGrid,
                WuWaAccountMetricsText.Text);
        }

        var checkIn = summary.CheckIns.TryGetValue(gameId, out var result) ? result : null;
        var currentCheckIn = checkIn is not null
            && PublisherAccountPresentation.IsCurrentDayCheckIn(checkIn, now)
                ? checkIn
                : null;
        WuWaAccountFreshnessText.Text = publisherAccountActionInFlight
            ? "WORKING"
            : !consentEnabled
                ? publisherConsentSaveFailures.Contains(entry.Provider)
                    ? "OFF · SETTING NOT SAVED"
                    : publisherConsentCleanupFailures.Contains(entry.Provider)
                        || publisherAccounts.HasPendingConsentRevocation(entry.Provider)
                        ? "OFF · CLEANUP PENDING"
                        : "ACCESS OFF"
            : currentCheckIn is not null
                ? currentCheckIn.State switch
                {
                    DailyCheckInState.Claimed => "CLAIMED TODAY",
                    DailyCheckInState.AlreadyClaimed => "DONE TODAY",
                    DailyCheckInState.LoginNeeded => "LOGIN NEEDED",
                    DailyCheckInState.SelectionRequired => "CHOOSE REGION",
                    DailyCheckInState.CouldNotCheck => currentCheckIn.Message.ToUpperInvariant(),
                    _ => connection.ToString().ToUpperInvariant(),
                }
                : checkIn is not null
                    ? $"DAY EXPIRED · {connection.ToString().ToUpperInvariant()}"
                    : connection switch
                    {
                        PublisherConnectionState.Connected => "CONNECTED",
                        PublisherConnectionState.Connecting => "CONNECTING",
                        PublisherConnectionState.LoginRequired => "LOGIN NEEDED",
                        PublisherConnectionState.NeedsReview => "TRY AGAIN",
                        _ => "PRIVATE SESSION",
                    };

        if (gameId != "ae" && resource is null)
        {
            WuWaAccountMetricsText.Text = resourceGuidance is not null
                ? $"{entry.ResourceName.ToUpperInvariant()} · {resourceGuidance}"
                : resourceState switch
                {
                    PublisherResourceState.Checking => $"CHECKING {entry.ResourceName.ToUpperInvariant()}",
                    PublisherResourceState.SelectionRequired => "CHOOSE REGION",
                    PublisherResourceState.LoginRequired => $"{entry.ResourceName.ToUpperInvariant()}  —",
                    PublisherResourceState.NeedsReview => $"{entry.ResourceName.ToUpperInvariant()} CHECK NEEDS REVIEW",
                    PublisherResourceState.Unavailable => $"{entry.ResourceName.ToUpperInvariant()} UNAVAILABLE",
                    _ => WuWaAccountMetricsText.Text,
                };
            AutomationProperties.SetName(
                PublisherResourceMetricGrid,
                WuWaAccountMetricsText.Text);
        }

        if (consentEnabled && !publisherAccountActionInFlight)
        {
            var dailyLabel = currentCheckIn?.State switch
            {
                DailyCheckInState.Claimed => "CLAIMED TODAY",
                DailyCheckInState.AlreadyClaimed => "DONE TODAY",
                DailyCheckInState.LoginNeeded => "SIGN IN",
                DailyCheckInState.SelectionRequired => "CHOOSE REGION",
                DailyCheckInState.CouldNotCheck => "TRY AGAIN",
                _ => null,
            };
            WuWaAccountFreshnessText.Text = dailyLabel ?? resourceLabel;
        }

        var accessibleFreshness = currentCheckIn?.State == DailyCheckInState.CouldNotCheck
            ? $"{WuWaAccountFreshnessText.Text}. {currentCheckIn.Message}"
            : WuWaAccountFreshnessText.Text;
        AutomationProperties.SetName(WuWaAccountFreshnessText, accessibleFreshness);
        AutomationProperties.SetHelpText(WuWaAccountFreshnessText, accessibleFreshness);

        WuWaAccountStatusToggle.IsChecked = consentEnabled;
        WuWaAccountStatusToggle.Content = consentEnabled ? "ON" : "ALLOW";
        WuWaAccountStatusToggle.IsEnabled = !publisherAccountActionInFlight;
        PublisherAccountConnectButton.Visibility = consentEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        PublisherAccountConnectButton.Content = connection switch
        {
            PublisherConnectionState.Connected => "CONNECTED",
            PublisherConnectionState.Connecting => "WAIT",
            PublisherConnectionState.LoginRequired => "SIGN IN",
            PublisherConnectionState.NeedsReview => "TRY AGAIN",
            _ => "CONNECT",
        };
        AutomationProperties.SetName(
            PublisherAccountConnectButton,
            connection == PublisherConnectionState.Connected
                ? $"Refresh {entry.Provider} account resources"
                : $"Connect {entry.Provider} in a Nyx-only private browser");
        PublisherAccountConnectButton.IsEnabled = consentEnabled
            && !publisherAccountActionInFlight
            && connection != PublisherConnectionState.Connecting;
        WuWaAccountStatusRefreshButton.Visibility = consentEnabled
            && (gameId == "ae"
                || (entry.SupportsNumericResource && connection == PublisherConnectionState.Connected))
                ? Visibility.Visible
                : Visibility.Collapsed;
        AutomationProperties.SetName(
            WuWaAccountStatusRefreshButton,
            gameId == "ae"
                ? "Open the official Arknights Endfield Protocol Terminal"
                : $"Refresh {entry.ResourceName}");
        WuWaAccountStatusRefreshButton.IsEnabled = consentEnabled && !publisherAccountActionInFlight;
        DailyCheckInButton.Visibility = consentEnabled
            && entry.SupportsDailyCheckIn
            && connection == PublisherConnectionState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;
        DailyCheckInButton.IsEnabled = consentEnabled
            && entry.SupportsDailyCheckIn
            && !publisherAccountActionInFlight;
    }

    public static string FormatPublisherResource(PublisherResourceSnapshot resource, DateTimeOffset now)
        => PublisherAccountDisplayProjection.FormatResource(resource, now);

    public static int RemainingRecoverySeconds(PublisherResourceSnapshot resource, DateTimeOffset now)
        => PublisherAccountDisplayProjection.RemainingRecoverySeconds(resource, now);

    private static string FormatRecoveryDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}H {duration.Minutes}M"
            : $"{Math.Max(1, duration.Minutes)}M";
    }

    private bool IsWuWaAccountStatusEnabled() =>
        launcherState.Snapshot.Preferences.FeatureFlags.WuWaAccountStatus
        && !wuwaAccountStatusSessionDisabled;

    private static string FormatAccountStatusAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "NOW";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)}M AGO";
        return $"{Math.Max(1, (int)age.TotalHours)}H AGO";
    }

    private void ApplyWuWaMaintenanceResult(WuWaOfficialMaintenanceResult result)
    {
        wuwaMaintenanceStatus = result.Status;
        wuwaMaintenanceReason = result.InspectionReason;
        wuwaMaintenanceRequest = result.Request;
    }

    private void RenderLaunchFailure(string? gameVersion)
    {
        switch (gameFailureReason)
        {
            case GenshinLaunchFailureReason.ElevationRequired:
                HeroDescription.Text = "Windows requires administrator approval. HoYoPlay is the safe available action.";
                SetLaunchControls(false, "ADMIN REQUIRED", "Open HoYoPlay", "Administrator approval is required; open HoYoPlay");
                break;
            case GenshinLaunchFailureReason.ElevationCancelled:
                HeroDescription.Text = "Nothing started. Choose Try again when you are ready to approve Windows.";
                SetLaunchControls(true, "TRY AGAIN", "Approval cancelled", "Try launching Genshin Impact again");
                break;
            case GenshinLaunchFailureReason.ElevatedStartFailed:
                HeroDescription.Text = "Windows approved the request, but Genshin Impact did not start. Nothing else was opened.";
                SetLaunchControls(true, "TRY AGAIN", "Admin start failed", "Try the administrator start again");
                break;
            case GenshinLaunchFailureReason.FpsHelperUnavailable:
                HeroDescription.Text = "The verified 120 FPS helper is unavailable. Genshin Impact was not started.";
                SetLaunchControls(true, "TRY AGAIN", "120 FPS helper unavailable", "Try launching Genshin Impact again");
                break;
            case GenshinLaunchFailureReason.FpsHelperTimedOut:
                HeroDescription.Text = "The 120 FPS helper did not answer in time. Genshin Impact was not confirmed as started.";
                SetLaunchControls(true, "TRY AGAIN", "120 FPS helper timed out", "Try launching Genshin Impact again");
                break;
            case GenshinLaunchFailureReason.FpsHelperFailed:
                HeroDescription.Text = "The 120 FPS helper could not safely start Genshin Impact.";
                SetLaunchControls(true, "TRY AGAIN", "120 FPS start failed", "Try launching Genshin Impact again");
                break;
            default:
                HeroDescription.Text = "Genshin Impact did not start. Check the install in HoYoPlay.";
                SetLaunchControls(true, "TRY AGAIN", VersionOnly(gameVersion), "Try launching Genshin Impact again");
                break;
        }
    }

    private void RenderUpdater()
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            return;
        }

        OpenUpdaterButton.Content = "Official Launcher";

        if (!updaterScanFinished)
        {
            OpenUpdaterButton.IsEnabled = false;
            AutomationProperties.SetName(OpenUpdaterButton, $"Checking HoYoPlay for {selected.DisplayName}");
            return;
        }

        switch (updaterStatus)
        {
            case GenshinLaunchStatus.Ready:
                OpenUpdaterButton.IsEnabled = !updaterActionInFlight;
                AutomationProperties.SetName(OpenUpdaterButton, $"Open HoYoPlay for {selected.DisplayName}");
                break;
            case GenshinLaunchStatus.Running:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "HoYoPlay is running");
                break;
            case GenshinLaunchStatus.LaunchFailed:
                OpenUpdaterButton.Content = "Try Again";
                OpenUpdaterButton.IsEnabled = !updaterActionInFlight;
                AutomationProperties.SetName(OpenUpdaterButton, "Try opening HoYoPlay again");
                break;
            case GenshinLaunchStatus.NeedsReview:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "HoYoPlay needs review");
                break;
            default:
                OpenUpdaterButton.IsEnabled = false;
                AutomationProperties.SetName(OpenUpdaterButton, "HoYoPlay was not found");
                break;
        }
    }

    private static GenshinLaunchStatus MapHoyoPlayStatus(HoyoPlayOpenStatus status) => status switch
    {
        HoyoPlayOpenStatus.Ready => GenshinLaunchStatus.Ready,
        HoyoPlayOpenStatus.Running or HoyoPlayOpenStatus.Opened =>
            GenshinLaunchStatus.Running,
        HoyoPlayOpenStatus.Failed => GenshinLaunchStatus.LaunchFailed,
        _ => GenshinLaunchStatus.NeedsReview,
    };

    private void ShowGameActionInProgress(string detail)
    {
        HeroDescription.Text = "Nyx is checking the official files before it starts the game.";
        var gameName = (GameSelector?.SelectedItem as GameLauncherItem)?.DisplayName ?? "game";
        SetLaunchControls(false, "STARTING", detail, $"Starting {gameName}");
    }

    private void SetRunningExportControls(string gameName, string? version)
    {
        if (GameSelector?.SelectedItem is not GameLauncherItem selected)
        {
            SetLaunchControls(false, "RUNNING", VersionOnly(version), $"{gameName} is running");
            return;
        }

        var state = launcherState.Snapshot;
        var arm = ExportArmSnapshot.From(state.Export, selected.Id, state.Preferences.FeatureFlags);
        var hasActiveJob = latestExportJobs.TryGetValue(selected.Id, out var jobId)
            && !exports.GetSnapshot(jobId).IsFinished;
        if (arm.CanStartWhileGameRunning && !hasActiveJob)
        {
            SetLaunchControls(true, "EXPORT", "Game already running", $"Export selected {gameName} data now");
            return;
        }

        SetLaunchControls(false, "RUNNING", VersionOnly(version), $"{gameName} is running");
    }

    private void SetLaunchControls(
        bool enabled,
        string title,
        string detail,
        string accessibleName)
    {
        var selectedGameId = (GameSelector?.SelectedItem as GameLauncherItem)?.Id;
        LaunchButton.IsEnabled = enabled
            && (selectedGameId is null || !gameActionsInFlight.Contains(selectedGameId))
            && !(selectedGameId == "hsr" && hoyoLabExportReservation.IsHeld);
        LaunchTitle.Text = title;
        SetLaunchDetail(detail);
        AutomationProperties.SetName(LaunchButton, accessibleName);
        ToolTipService.SetToolTip(
            LaunchButton,
            string.IsNullOrWhiteSpace(detail) ? accessibleName : detail);
    }

    private static string WithVersion(string state, string? version) =>
        string.IsNullOrWhiteSpace(version) ? state : $"{state} · {version}";

    private static string VersionOnly(string? version) =>
        string.IsNullOrWhiteSpace(version) ? string.Empty : version;

    private static string VerifiedFilesDetail(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? "Official files verified."
            : $"Official files verified \u00B7 {version}";

    private void GameSelector_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem item && args.Item is GameLauncherItem game)
        {
            AutomationProperties.SetName(item, game.AccessibleName);
        }
    }

    private sealed record HoyoMaintenanceUiSnapshot(
        string? UpdaterRoot,
        GenshinLaunchStatus? UpdaterStatus);

    private sealed record HoyoLabManagerSlotItem(
        HoyoLabAccountSlot Slot,
        string Status)
    {
        public string DisplayText => $"{Slot.Label} \u00B7 {Status}";
    }

    private enum AchievementHandoffUiState
    {
        None,
        Opening,
        Waiting,
        Delivered,
        Fallback,
    }
}

public sealed record BannerPortraitItem(string Name, ImageSource? PortraitSource);

public sealed record UpcomingBannerCharacterItem(
    string SourceCharacterId,
    string Name,
    ImageSource? PortraitSource,
    Uri? CharacterUrl,
    IReadOnlyList<BannerPortraitItem>? OverflowPortraits = null)
{
    public UpcomingBannerCharacterItem(string sourceCharacterId, string name, ImageSource? portraitSource)
        : this(sourceCharacterId, name, portraitSource, null)
    {
    }

    public static UpcomingBannerCharacterItem CreateOverflow(IReadOnlyList<BannerPortraitItem> portraits) =>
        new(
            $"overflow:{string.Join('|', portraits.Select(static portrait => portrait.Name))}",
            string.Join(", ", portraits.Select(static portrait => portrait.Name)),
            null,
            null,
            portraits);

    public bool IsOverflow => OverflowPortraits is { Count: > 0 };
    public Visibility PrimaryVisibility => IsOverflow ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OverflowVisibility => IsOverflow ? Visibility.Visible : Visibility.Collapsed;
    public bool CanOpen => !IsOverflow && CharacterUrl is not null;
    public string AccessibilityName => CanOpen
        ? $"Open Pengo page for {Name}"
        : Name;
}

public sealed class UpcomingBannerGroupItem : INotifyPropertyChanged
{
    public UpcomingBannerGroupItem(
        string stableKey,
        string timing,
        IReadOnlyList<UpcomingBannerCharacterItem> characters)
    {
        StableKey = stableKey;
        Timing = timing;
        Characters = characters.ToArray();
        CharacterRows = Characters.Chunk(5).ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StableKey { get; }
    public string Timing { get; private set; }
    public IReadOnlyList<UpcomingBannerCharacterItem> Characters { get; }
    public IReadOnlyList<IReadOnlyList<UpcomingBannerCharacterItem>> CharacterRows { get; }

    public bool Matches(UpcomingBannerGroupItem other) =>
        string.Equals(StableKey, other.StableKey, StringComparison.Ordinal)
        && Characters.Count == other.Characters.Count
        && Characters.Zip(other.Characters).All(pair =>
            string.Equals(pair.First.SourceCharacterId, pair.Second.SourceCharacterId, StringComparison.Ordinal)
            && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && Equals(pair.First.PortraitSource, pair.Second.PortraitSource)
            && Equals(pair.First.CharacterUrl, pair.Second.CharacterUrl)
            && (pair.First.OverflowPortraits ?? []).SequenceEqual(pair.Second.OverflowPortraits ?? []));

    public void UpdateTiming(string timing)
    {
        if (string.Equals(Timing, timing, StringComparison.Ordinal)) return;
        Timing = timing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Timing)));
    }
}

public sealed class BannerCharacterRowItem : INotifyPropertyChanged
{
    public BannerCharacterRowItem(
        LauncherBannersCharacter character,
        string phaseStableKey,
        ImageSource? portraitSource,
        string timing,
        bool isActive,
        bool isPinned,
        double progress)
    {
        CharacterId = character.Id;
        PhaseStableKey = phaseStableKey;
        Name = character.Name;
        CharacterUrl = character.CharacterUrl;
        Detail = timing;
        OverflowPortraits = [];
        Update(portraitSource, timing, isActive, isPinned, progress);
    }

    private BannerCharacterRowItem(
        string phaseStableKey,
        string timing,
        IReadOnlyList<BannerPortraitItem> overflowPortraits)
    {
        CharacterId = $"overflow:{string.Join('|', overflowPortraits.Select(static portrait => portrait.Name))}";
        PhaseStableKey = phaseStableKey;
        Name = string.Join(", ", overflowPortraits.Select(static portrait => portrait.Name));
        Detail = timing;
        IsActive = true;
        Progress = 100;
        OverflowPortraits = overflowPortraits;
    }

    public static BannerCharacterRowItem CreateOverflow(
        string phaseStableKey,
        string timing,
        IReadOnlyList<BannerPortraitItem> portraits) =>
        new(phaseStableKey, timing, portraits);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CharacterId { get; }

    public string PhaseStableKey { get; }

    public string Name { get; }

    public IReadOnlyList<BannerPortraitItem> OverflowPortraits { get; }

    public bool IsOverflow => OverflowPortraits.Count > 0;

    public Visibility PrimaryVisibility => IsOverflow ? Visibility.Collapsed : Visibility.Visible;

    public Visibility OverflowVisibility => IsOverflow ? Visibility.Visible : Visibility.Collapsed;

    public Uri? CharacterUrl { get; }

    public bool CanOpen => CharacterUrl is not null;

    public string CharacterLinkAccessibilityName => CanOpen
        ? $"Open Pengo page for {Name}"
        : Name;

    public string Detail { get; private set; }

    public ImageSource? PortraitSource { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsPinned { get; private set; }

    public double Progress { get; private set; }

    public double RowOpacity => IsActive ? 1 : 0.72;

    public Thickness ActiveBorderThickness => IsActive ? new Thickness(2) : new Thickness(1);

    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SeparatorVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;

    public string ActiveLabel => IsPinned ? "PINNED" : IsActive ? "ACTIVE" : string.Empty;

    public string ProgressLabel => IsActive ? $"{Math.Round(Progress):0}%" : string.Empty;

    public string PinLabel => IsPinned ? "UNPIN" : "PIN";

    public void Update(ImageSource? portraitSource, string timing, bool isActive, bool isPinned, double progress)
    {
        var nextProgress = Math.Clamp(progress, 0, 100);
        var portraitChanged = !ReferenceEquals(PortraitSource, portraitSource);
        var timingChanged = !string.Equals(Detail, timing, StringComparison.Ordinal);
        var activeChanged = IsActive != isActive;
        var pinnedChanged = IsPinned != isPinned;
        var progressChanged = Math.Abs(Progress - nextProgress) >= 0.01;

        PortraitSource = portraitSource;
        Detail = timing;
        IsActive = isActive;
        IsPinned = isPinned;
        Progress = nextProgress;

        if (portraitChanged) Notify(nameof(PortraitSource));
        if (timingChanged) Notify(nameof(Detail));
        if (progressChanged)
        {
            Notify(nameof(Progress));
            Notify(nameof(ProgressLabel));
        }
        if (activeChanged)
        {
            Notify(nameof(RowOpacity));
            Notify(nameof(ActiveBorderThickness));
            Notify(nameof(ActiveVisibility));
            Notify(nameof(SeparatorVisibility));
            Notify(nameof(ActiveLabel));
            Notify(nameof(ProgressLabel));
            Notify(nameof(PinLabel));
        }
        if (pinnedChanged)
        {
            Notify(nameof(ActiveLabel));
            Notify(nameof(PinLabel));
        }
    }

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}

public static class BannerTimingFormatter
{
    public static string FormatRemaining(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{Math.Max(0, duration.Minutes)}m";
    }
}

public sealed class RedemptionCodeRowItem : INotifyPropertyChanged
{
    private const string CopyActionGlyph = "\uE8C8";
    private const string CopiedActionGlyph = "\uE73E";
    private bool wasCopied;

    public RedemptionCodeRowItem(
        string code,
        DateOnly added,
        int currencyAmount,
        string currencyName,
        string currencyIconSource,
        bool isCopyable,
        bool canRedeem,
        double rowHeight = 26)
    {
        Code = code;
        AddedLabel = isCopyable
            ? added.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant()
            : string.Empty;
        IsCopyable = isCopyable;
        CanRedeem = canRedeem;
        CurrencyAmount = currencyAmount;
        CurrencyName = currencyName;
        CurrencyIconSource = currencyIconSource;
        CurrencyAmountLabel = currencyAmount > 0 ? currencyAmount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        CurrencyVisibility = currencyAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        FontSize = code.Length > 18 ? 9.5 : code.Length > 14 ? 10.5 : 12.5;
        AccessibilityName = isCopyable
            ? $"Redemption code {code}, {currencyAmount} {currencyName}, added {added:yyyy-MM-dd}"
            : code;
        RedemptionAccessibilityName = canRedeem
            ? $"Open official redemption page for code {code}"
            : $"Redemption code {code}";
        CopyGlyph = isCopyable ? CopyActionGlyph : string.Empty;
        RowHeight = rowHeight;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static RedemptionCodeRowItem Empty { get; } = new(
        "No premium codes available", default, 0, string.Empty, string.Empty, false, false);

    public string Code { get; }

    public string AddedLabel { get; }

    public bool IsCopyable { get; }

    public Visibility CopyVisibility => IsCopyable ? Visibility.Visible : Visibility.Collapsed;

    public bool CanRedeem { get; }

    public int CodeColumnSpan => IsCopyable ? 1 : 3;

    public int CurrencyAmount { get; }

    public string CurrencyName { get; }

    public string CurrencyIconSource { get; }

    public string CurrencyAmountLabel { get; }

    public Visibility CurrencyVisibility { get; private set; }

    public double FontSize { get; }

    public string AccessibilityName { get; }

    public string RedemptionAccessibilityName { get; }

    public string CopyAccessibilityName => IsCopyable
        ? wasCopied
            ? $"Copy redemption code {Code}; copied previously"
            : $"Copy redemption code {Code}"
        : Code;

    public string CopyGlyph { get; private set; }

    public double RowHeight { get; private set; }

    public Visibility MetadataVisibility { get; private set; } = Visibility.Visible;

    public double CodeOpacity { get; private set; } = 1;

    public void MarkPreviouslyCopied()
    {
        if (wasCopied) return;
        wasCopied = true;
        CodeOpacity = 0.58;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CodeOpacity)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyAccessibilityName)));
    }

    public void SetRowHeight(double height)
    {
        if (Math.Abs(RowHeight - height) < 0.01) return;
        RowHeight = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowHeight)));
    }

    public void SetMetadataVisibility(bool isVisible)
    {
        var next = isVisible ? Visibility.Visible : Visibility.Collapsed;
        var currencyNext = isVisible && CurrencyAmount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (MetadataVisibility == next && CurrencyVisibility == currencyNext) return;
        MetadataVisibility = next;
        CurrencyVisibility = currencyNext;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrencyVisibility)));
    }

    public void MarkCopied()
    {
        wasCopied = true;
        CodeOpacity = 0.72;
        CopyGlyph = CopiedActionGlyph;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CodeOpacity)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyAccessibilityName)));
    }

    public void ResetCopyState()
    {
        if (CopyGlyph == CopyActionGlyph) return;
        CopyGlyph = CopyActionGlyph;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyGlyph)));
    }
}

public sealed class GameLauncherItem : INotifyPropertyChanged
{
    private double iconSize = 104;
    private double itemExtent = 112;
    private string statusGlyph;
    private string statusDescription;

    public GameLauncherItem(
        string id,
        string displayName,
        string iconPath,
        string maintenanceProvider,
        string statusGlyph,
        string statusDescription,
        bool isCustom = false)
    {
        Id = id;
        DisplayName = displayName;
        IconPath = iconPath;
        MaintenanceProvider = maintenanceProvider;
        IsCustom = isCustom;
        this.statusGlyph = statusGlyph;
        this.statusDescription = statusDescription;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string DisplayName { get; }

    public string IconPath { get; }

    public string MaintenanceProvider { get; }

    public bool IsCustom { get; }

    public string StatusGlyph => statusGlyph;

    public string StatusDescription => statusDescription;

    public double IconSize => iconSize;

    public double ItemExtent => itemExtent;

    public string AccessibleName => $"{DisplayName}. {StatusDescription}. Select game.";

    public void UpdateStatus(string glyph, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glyph);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (statusGlyph == glyph && statusDescription == description)
        {
            return;
        }

        statusGlyph = glyph;
        statusDescription = description;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDescription)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public void ApplyLayout(LauncherLayoutProfile profile)
    {
        if (iconSize == profile.IconSize && itemExtent == profile.ItemExtent)
        {
            return;
        }

        iconSize = profile.IconSize;
        itemExtent = profile.ItemExtent;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemExtent)));
    }
}
