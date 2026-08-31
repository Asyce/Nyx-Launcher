using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Content;
using Nyx.Desktop.Core.Recovery;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Games;
using Nyx.Desktop.Infrastructure.Content;
using Nyx.Desktop.Infrastructure.Cache;
using Nyx.Desktop.Infrastructure.Exports;
using Nyx.Desktop.Infrastructure.Hoyo;
using Nyx.Desktop.Infrastructure.Launching;
using Nyx.Desktop.Infrastructure.Playtime;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;
using Nyx.Desktop.Infrastructure.PublisherGames;
using Nyx.Desktop.Infrastructure.Sessions;
using Nyx.Desktop.Infrastructure.Recovery;
using Nyx.Desktop.Infrastructure.State;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx_Desktop_App;

public partial class App : Application
{
    private const string MainApplicationId = "Pengo.Nyx.Desktop";
    private const string MainInstanceKey = "Pengo.Nyx.Desktop.Main";
    private const uint DeviceNotifyCallback = 2;

    private AppInstance? _currentInstance;
    private Window? _window;
    private GameSessionCoordinator? _sessions;
    private GameSessionRefreshPump? _sessionRefresh;
    private GamePlaytimeService? _gamePlaytime;
    private LauncherBannersContentService? _launcherBanners;
    private LauncherCacheService? _cache;
    private LauncherRecoveryService? _recovery;
    private ExportCoordinator? _exports;
    private BoundedAchievementExportHandoffOwner? _achievementExportHandoffs;
    private RoutedPullExportProvider? _pullExports;
    private HoyoPublisherStatusSource? _hoyoPublisherStatus;
    private WuWaAccountStatusService? _wuwaAccountStatus;
    private PublisherAccountService? _publisherAccounts;
    private Hsr120FpsSetting? _hsr120FpsSetting;
    private GameScreenshotFolderResolver? _screenshotFolders;
    private Genshin120FpsProcessStarter? _genshin120FpsProcessStarter;
    private readonly CancellationTokenSource _stableUpdateCancellation = new();
    private Task _stableUpdateTask = Task.CompletedTask;
    private int _stableUpdateStarted;
    private bool _stableUpdateHandoffCommitted;
    private volatile bool _accountShutdownStarted;
    private bool _accountShutdownComplete;
    private CancellationTokenSource? _endfieldDiscoveryCancellation;
    private Task _endfieldDiscoveryTask = Task.CompletedTask;
    private string? _diagnosticsRoot;
    private string _launchStage = "app-construction";
    private readonly object _powerCallbackSync = new();
    private DeviceNotifyCallbackRoutine? _powerCallback;
    private DispatcherQueue? _powerDispatcher;
    private nint _powerRegistrationHandle;
    private int _powerCallbackEnabled;
    private int _powerCallbacksInFlight;
    private TaskCompletionSource? _powerCallbacksDrained;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            // Do not create the canonical root from the crash path. It becomes
            // available only after legacy migration and root auditing succeed.
            var folder = _diagnosticsRoot;
            if (folder is null) return;
            Directory.CreateDirectory(folder);
            // Keep this file useful for support without copying user paths,
            // account data, or exception text that may contain them.
            File.WriteAllText(
                Path.Combine(folder, "last-crash.txt"),
                $"{DateTimeOffset.UtcNow:O}\nlaunch-stage: {_launchStage}\n{FormatSafeExceptionChain(e.Exception)}");
        }
        catch (Exception)
        {
            // Crash diagnostics must never replace the original failure.
        }
    }

    private static string FormatSafeExceptionChain(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null && lines.Count < 5; current = current.InnerException)
        {
            lines.Add($"exception-{lines.Count}: {current.GetType().Name} hresult=0x{current.HResult:X8}");
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames();
            if (frames is not null)
            {
                foreach (var frame in frames.Take(8))
                {
                    var method = frame.GetMethod();
                    if (method is null) continue;
                    lines.Add($"at: {method.DeclaringType?.FullName}.{method.Name}");
                }
            }
        }
        return string.Join('\n', lines);
    }

    internal static void SetLaunchStage(string stage)
    {
        if (Current is App app)
        {
            app._launchStage = stage;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _launchStage = "application-identity";
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(MainApplicationId));

        _launchStage = "single-instance-registration";
        var currentInstance = AppInstance.GetCurrent();
        var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            await mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
            Exit();
            return;
        }

        _currentInstance = mainInstance;
        _currentInstance.Activated += CurrentInstance_Activated;

        _launchStage = "state-initialization";
        var stateStore = new LauncherStateStore();
        LauncherState = new LauncherStateController(stateStore);
        _hsr120FpsSetting = new Hsr120FpsSetting();
        _diagnosticsRoot = Path.Combine(LauncherState.DataDirectory, "Diagnostics");
        _cache = new LauncherCacheService(LauncherState.DataDirectory);
        _recovery = new LauncherRecoveryService(
            stateStore,
            _cache,
            rediscoverInstalls: RediscoverInstallsAsync,
            retryContent: RefreshContentAsync,
            currentPlaytimeTotals: () => _gamePlaytime?.SnapshotTotals()
                ?? LauncherState.Snapshot.PlaytimeSecondsByGame);
        GenshinInspection = new GenshinInspectionAdapter(
            new WindowsAuthenticodeExecutableMetadataReader());
        GenshinDiscovery = new WindowsGenshinCandidateDiscovery(
            new WindowsMachineGenshinRegistryReader(),
            new GenshinInspectionCandidateInspector(GenshinInspection));
        var genshin120HelperPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Tools",
            Genshin120HelperPackageIdentity.FileName);
        _genshin120FpsProcessStarter = new Genshin120FpsProcessStarter(
            genshin120HelperPath,
            Genshin120HelperPackageIdentity.Sha256);
        GenshinLaunchService = new GenshinLaunchService(
            new GenshinLaunchIdentityValidator(GenshinInspection),
            new WindowsRunningProcessInspector(),
            new DotNetLaunchProcessStarter(),
            _genshin120FpsProcessStarter);
        GenshinSession = new GenshinGameSessionAdapter(
            GenshinDiscovery,
            GenshinInspection,
            GenshinLaunchService,
            () => GetManualInstallRoot("gi"),
            () => GetOfficialLaunchArguments("gi"),
            () => Genshin120FpsOnLaunch);

        var hoyoIdentity = new HoyoGameIdentityAdapter();
        var hoyoDiscovery = new HoyoCurrentUserDiscovery();
        var hoyoLaunchService = new HoyoGameLaunchService(
            new HoyoGameLaunchIdentityValidator(hoyoIdentity),
            new WindowsRunningProcessInspector(),
            new DotNetLaunchProcessStarter());
        HoyoSessions = new Dictionary<string, HoyoGameSessionAdapter>(StringComparer.Ordinal)
        {
            ["hsr"] = new(
                "hsr",
                hoyoDiscovery,
                hoyoLaunchService,
                () => GetManualInstallRoot("hsr"),
                hoyoIdentity,
                launchArguments: () => GetOfficialLaunchArguments("hsr")),
            ["zzz"] = new(
                "zzz",
                hoyoDiscovery,
                hoyoLaunchService,
                () => GetManualInstallRoot("zzz"),
                hoyoIdentity,
                () => GetHoyoRenderingMode("zzz"),
                () => GetOfficialLaunchArguments("zzz")),
        };
        HoyoPlayExecutor = new HoyoPlayHandoffExecutor();
        WuWaMaintenance = new WuWaMaintenanceService();
        PublisherGameLaunchService = PublisherGameDirectLaunchFactory.Create();
        EndfieldRootStore = new EndfieldInstallRootStore(
            read: () => LauncherState.Snapshot.Preferences.EndfieldInstallRoot,
            write: root => LauncherState.TryUpdate(state => state with
            {
                Preferences = state.Preferences with { EndfieldInstallRoot = root },
            }),
            writeIfEmpty: root =>
            {
                var stored = false;
                var updated = LauncherState.TryUpdate(state =>
                {
                    if (state.Preferences.EndfieldInstallRoot is not null) return state;
                    stored = true;
                    return state with
                    {
                        Preferences = state.Preferences with { EndfieldInstallRoot = root },
                    };
                });
                return updated && stored;
            },
            remove: () => LauncherState.TryUpdate(state => state with
            {
                Preferences = state.Preferences with { EndfieldInstallRoot = null },
            }));
        var wuwaRootLocator = new WuWaInstallRootLocator();
        _screenshotFolders = new GameScreenshotFolderResolver(gameId =>
            ResolveValidatedScreenshotRoot(gameId, hoyoDiscovery, hoyoIdentity, wuwaRootLocator));
        EndfieldMaintenance = new EndfieldOfficialMaintenanceService(EndfieldRootStore);
        PublisherGameSessions = new Dictionary<string, PublisherGameSessionAdapter>(StringComparer.Ordinal)
        {
            ["wuwa"] = new(
                "wuwa",
                () => GetManualInstallRoot("wuwa") ?? wuwaRootLocator.LocateRoot(),
                PublisherGameLaunchService,
                () => GetPublisherRenderingMode("wuwa"),
                () => GetOfficialLaunchArguments("wuwa")),
            ["ae"] = new(
                "ae",
                () => GetManualInstallRoot("ae") ?? EndfieldRootStore.Load(),
                PublisherGameLaunchService,
                launchArguments: () => GetOfficialLaunchArguments("ae")),
        };

        var officialAdapters = GameCatalog.All.Select<Nyx.Desktop.Core.Games.GameDefinition, IGameSessionAdapter>(game =>
            game.Id switch
            {
                "gi" => GenshinSession,
                "hsr" or "zzz" => HoyoSessions[game.Id],
                "wuwa" or "ae" => PublisherGameSessions[game.Id],
                _ => throw new InvalidOperationException($"No session adapter exists for '{game.Id}'."),
            });
        var customAdapters = LauncherState.Snapshot.CustomGames
            // Keep invalid or moved definitions registered as repair-only
            // sessions. Their adapter revalidates on every observation and
            // launch, so they can report NeedsReview but can never dispatch.
            .Select(static game => CustomGameSessionFactory.Create(game))
            .Cast<IGameSessionAdapter>();
        var adapters = officialAdapters.Concat(customAdapters);
        _sessions = new GameSessionCoordinator(adapters);
        _sessionRefresh = new GameSessionRefreshPump(_sessions);
        _gamePlaytime = new GamePlaytimeService(
            LauncherState.Snapshot.PlaytimeSecondsByGame,
            playtime => LauncherState.TryUpdate(state => state with { PlaytimeSecondsByGame = playtime }),
            _sessionRefresh);
        _launcherBanners = new LauncherBannersContentService(
            File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Content",
                "launcher-banners-v1.json")),
            Path.Combine(LauncherState.DataDirectory, "ContentCache"),
            new Uri(LauncherBannersTransport.ProductionEndpoint),
            codesEndpoint: new Uri(LauncherBannersTransport.ProductionCodesEndpoint),
            toolsEndpoint: new Uri(LauncherBannersTransport.ProductionToolsEndpoint));
        var accountFlags = LauncherState.Snapshot.Preferences.FeatureFlags;
        _publisherAccounts = new PublisherAccountService(
            Path.Combine(LauncherState.DataDirectory, "PublisherProfiles"),
            accountFlags.HoyoLabAccountAccess,
            accountFlags.SkportAccountAccess,
            accountFlags.HoyoLabAccountCleanupPending,
            accountFlags.SkportAccountCleanupPending,
            Path.Combine(LauncherState.DataDirectory, "Protected"),
            LauncherState.Snapshot.Preferences.PublisherPasswordSavingEnabled,
            Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Content",
                "Achievements",
                "hsr",
                "catalog.json"),
            (provider, cleanupPending, accountAccess) =>
                TryPersistPublisherCleanupPending(
                    provider,
                    cleanupPending,
                    accountAccess));
        _pullExports = new RoutedPullExportProvider(() =>
        {
            var root = GetManualInstallRoot("wuwa") ?? wuwaRootLocator.LocateRoot();
            if (root is null) return null;
            return PublisherGameLaunchService.CheckGame("wuwa", root).Status
                is PublisherGameLaunchStatus.Ready or PublisherGameLaunchStatus.Running
                    ? root
                    : null;
        });
        var achievementHelperPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Tools",
            VerifiedAchievementHelperBoundary.ExpectedHelperFileName);
        var nativeAchievementProvider = new AchievementHelperExportProvider(
            new VerifiedAchievementHelperBoundary(
                achievementHelperPath,
                AchievementHelperPackageIdentity.Sha256,
                new ProcessAchievementHelperRunner()));
        _exports = new ExportCoordinator(
            _pullExports,
            new RoutedAchievementExportProvider(
                nativeAchievementProvider,
                new PublisherAccountAchievementExportProvider(_publisherAccounts),
                gameId => LauncherState.Snapshot.Export.Games.TryGetValue(gameId, out var configured)
                    ? configured.AchievementSource
                    : null),
            achievementPrepareTimeout: TimeSpan.FromSeconds(30));
        _achievementExportHandoffs = new BoundedAchievementExportHandoffOwner(
            _exports,
            new AchievementImportBridge(
                StableUpdateBuildIdentity.PengoSiteOrigin,
                releaseChannel: StableUpdateBuildIdentity.Channel),
            new WindowsAchievementExportHandoffLauncher());
        _hoyoPublisherStatus = new HoyoPublisherStatusSource(() => new HoyoLocalVersions(
            GenshinSession.Version,
            HoyoSessions["hsr"].Version,
            HoyoSessions["zzz"].Version));
        _wuwaAccountStatus = new WuWaAccountStatusService();
        LauncherState.Changed += LauncherState_Changed;
        _ = RecoverPendingPublisherRevocationsAsync();

        _launchStage = "main-window-construction";
        _window = new MainWindow();
        _launchStage = "main-window-event-wiring";
        _window.Activated += Window_Activated;
        _window.Closed += Window_Closed;
        _window.AppWindow.Closing += AppWindow_Closing;
        _launchStage = "main-window-activation";
        _window.Activate();
        _launchStage = "background-services";
        _powerDispatcher = _window.DispatcherQueue;
        if (!TryRegisterSuspendResumeNotifications())
        {
            _gamePlaytime.DisableTracking();
        }
        _sessionRefresh.Start();
        _launcherBanners.Start();
        StartEndfieldSiblingDiscovery(wuwaRootLocator);
        _launchStage = "running";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("Powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DeviceNotifySubscribeParameters recipient,
        out nint registrationHandle);

    [DllImport("Powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(
        nint registrationHandle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(
        nint context,
        uint eventType,
        nint setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public DeviceNotifyCallbackRoutine Callback;
        public nint Context;
    }

    private bool TryRegisterSuspendResumeNotifications()
    {
        _powerCallback = SuspendResumeNotification;
        var parameters = new DeviceNotifySubscribeParameters
        {
            Callback = _powerCallback,
            Context = 0,
        };
        lock (_powerCallbackSync)
        {
            _powerCallbackEnabled = 1;
        }

        try
        {
            var result = PowerRegisterSuspendResumeNotification(
                DeviceNotifyCallback,
                ref parameters,
                out var handle);
            if (result == 0 && handle != 0)
            {
                lock (_powerCallbackSync)
                {
                    _powerRegistrationHandle = handle;
                }

                return true;
            }
        }
        catch (Exception)
        {
        }

        lock (_powerCallbackSync)
        {
            _powerCallbackEnabled = 0;
            _powerCallback = null;
        }

        return false;
    }

    private uint SuspendResumeNotification(nint context, uint eventType, nint setting)
    {
        lock (_powerCallbackSync)
        {
            if (_powerCallbackEnabled == 0)
            {
                return 0;
            }

            _powerCallbacksInFlight++;
        }

        try
        {
            if (_accountShutdownStarted || _sessionRefresh is not { } refresh)
            {
                return 0;
            }

            switch (GameSessionRefreshPump.ClassifyPowerBroadcast(eventType))
            {
                case SystemSuspendResumeEvent.Suspend:
                    _ = refresh.RequestSystemSuspend();
                    break;

                case SystemSuspendResumeEvent.AutomaticResume:
                    if (!refresh.RequestSystemResume())
                    {
                        break;
                    }

                    var dispatcher = _powerDispatcher;
                    _ = dispatcher?.TryEnqueue(() =>
                    {
                        if (!_accountShutdownStarted
                            && Volatile.Read(ref _powerCallbackEnabled) != 0)
                        {
                            _ = RefreshAfterSystemResumeAsync(refresh);
                        }
                    });
                    break;
            }
        }
        catch (Exception)
        {
            // The playtime service remains suspended until a reset publication succeeds.
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_powerCallbackSync)
            {
                _powerCallbacksInFlight--;
                if (_powerCallbacksInFlight == 0)
                {
                    drained = _powerCallbacksDrained;
                }
            }

            drained?.TrySetResult();
        }

        return 0;
    }

    private async Task RefreshAfterSystemResumeAsync(GameSessionRefreshPump refresh)
    {
        try
        {
            await refresh.RefreshNowAsync();
        }
        catch (Exception)
        {
            // The periodic refresh can apply the pending reset publication later.
        }
    }

    private Task UnregisterSuspendResumeNotifications()
    {
        nint handle;
        lock (_powerCallbackSync)
        {
            _powerCallbackEnabled = 0;
            handle = _powerRegistrationHandle;
            _powerRegistrationHandle = 0;
        }

        var unregistered = handle == 0;
        if (!unregistered)
        {
            try
            {
                unregistered = PowerUnregisterSuspendResumeNotification(handle) == 0;
            }
            catch (Exception)
            {
            }
        }

        lock (_powerCallbackSync)
        {
            if (unregistered)
            {
                _powerCallback = null;
            }
            else if (_powerRegistrationHandle == 0)
            {
                // Keep the callback rooted and retry during the next teardown path.
                _powerRegistrationHandle = handle;
            }

            if (_powerCallbacksInFlight == 0)
            {
                return Task.CompletedTask;
            }

            _powerCallbacksDrained ??= new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _powerCallbacksDrained.Task;
        }
    }

    private void CurrentInstance_Activated(object? sender, AppActivationArguments args)
    {
        if (_accountShutdownStarted) return;
        var window = _window;
        if (window is null) return;

        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_accountShutdownStarted) window.Activate();
        });
    }

    private void StartEndfieldSiblingDiscovery(WuWaInstallRootLocator wuwaRootLocator)
    {
        var cancellation = new CancellationTokenSource();
        _endfieldDiscoveryCancellation = cancellation;
        _endfieldDiscoveryTask = DiscoverEndfieldSiblingAfterActivationAsync(
            wuwaRootLocator,
            cancellation.Token);
    }

    private void CancelEndfieldSiblingDiscovery()
    {
        try { _endfieldDiscoveryCancellation?.Cancel(); }
        catch (Exception) { }
    }

    private async Task AwaitEndfieldSiblingDiscoveryAsync()
    {
        var cancellation = Interlocked.Exchange(ref _endfieldDiscoveryCancellation, null);
        var discovery = _endfieldDiscoveryTask;
        try
        {
            await discovery;
        }
        catch (Exception)
        {
            // Automatic discovery is optional and was already canceled.
        }
        finally
        {
            cancellation?.Dispose();
            _endfieldDiscoveryTask = Task.CompletedTask;
        }
    }

    private async Task DiscoverEndfieldSiblingAfterActivationAsync(
        WuWaInstallRootLocator wuwaRootLocator,
        CancellationToken cancellationToken)
    {
        EndfieldSiblingDiscoveryResult result;
        try
        {
            result = await Task.Run(
                () => TryDiscoverEndfieldSibling(wuwaRootLocator, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Automatic discovery is optional. The bounded folder picker remains.
            return;
        }

        if (result.Status is not EndfieldSiblingDiscoveryStatus.Saved
            || cancellationToken.IsCancellationRequested
            || _sessionRefresh is null)
        {
            return;
        }

        EndfieldRootAutoDiscovered?.Invoke(this, EventArgs.Empty);

        try
        {
            await _sessionRefresh.RefreshNowAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the window cancels optional discovery and its UI refresh.
        }
        catch (Exception)
        {
            // The periodic observer will retry. A saved root remains only a hint.
        }
    }

    private EndfieldSiblingDiscoveryResult TryDiscoverEndfieldSibling(
        WuWaInstallRootLocator wuwaRootLocator,
        CancellationToken cancellationToken)
    {
        var existingRoot = EndfieldRootStore.Load();
        if (existingRoot is not null)
        {
            return new(
                EndfieldSiblingDiscoveryStatus.ExistingRoot,
                existingRoot);
        }

        try
        {
            var wuwaRoot = wuwaRootLocator.LocateRoot();
            if (wuwaRoot is not null
                && PublisherGameLaunchService.CheckGame("wuwa", wuwaRoot).Status
                    is not PublisherGameLaunchStatus.Ready
                    and not PublisherGameLaunchStatus.Running)
            {
                wuwaRoot = null;
            }

            var genshinGameRoot = GenshinDiscovery.Discover().GameRoot;
            return new EndfieldSiblingDiscoveryPolicy().DiscoverAndSave(
                existingEndfieldRoot: null,
                validatedWuWaRoot: wuwaRoot,
                validatedGenshinGameRoot: genshinGameRoot,
                checkEndfield: candidate => PublisherGameLaunchService.CheckGame("ae", candidate),
                save: EndfieldRootStore.TrySaveIfEmpty,
                cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // Automatic discovery is optional. The bounded folder picker remains.
            return new(EndfieldSiblingDiscoveryStatus.Uncertain);
        }
    }

    internal GameSessionCoordinator Sessions =>
        _sessions ?? throw new InvalidOperationException("Session coordinator is not initialized.");

    internal GameSessionRefreshPump SessionRefresh =>
        _sessionRefresh ?? throw new InvalidOperationException("Session refresh is not initialized.");

    internal GamePlaytimeService GamePlaytime =>
        _gamePlaytime ?? throw new InvalidOperationException("Game playtime is not initialized.");

    internal SessionUiLifetime SessionUiLifetime { get; } = new();

    internal LauncherBannersContentService LauncherBanners =>
        _launcherBanners ?? throw new InvalidOperationException("Launcher banners are not initialized.");

    internal LauncherCacheService Cache =>
        _cache ?? throw new InvalidOperationException("Launcher cache is not initialized.");

    internal ILauncherRecoveryService Recovery =>
        _recovery ?? throw new InvalidOperationException("Launcher recovery is not initialized.");

    internal ExportCoordinator Exports =>
        _exports ?? throw new InvalidOperationException("Export coordinator is not initialized.");

    internal BoundedAchievementExportHandoffOwner AchievementExportHandoffs =>
        _achievementExportHandoffs
        ?? throw new InvalidOperationException("Achievement export handoff owner is not initialized.");

    internal HoyoPublisherStatusSource HoyoPublisherStatus =>
        _hoyoPublisherStatus ?? throw new InvalidOperationException("Publisher status is not initialized.");

    internal WuWaAccountStatusService WuWaAccountStatus =>
        _wuwaAccountStatus ?? throw new InvalidOperationException("Wuthering Waves account status is not initialized.");

    internal PublisherAccountService PublisherAccounts =>
        _publisherAccounts ?? throw new InvalidOperationException("Publisher account service is not initialized.");

    internal bool Genshin120FpsOnLaunch =>
        LauncherState.Snapshot.Preferences.Genshin120FpsOnLaunch;

    internal bool Hsr120FpsOnLaunch =>
        LauncherState.Snapshot.Preferences.Hsr120FpsOnLaunch;

    internal bool Is120FpsOnLaunch(string gameId) => gameId switch
    {
        "gi" => Genshin120FpsOnLaunch,
        "hsr" => Hsr120FpsOnLaunch,
        _ => false,
    };

    internal bool TrySet120FpsOnLaunch(string gameId, bool enabled) =>
        gameId switch
        {
            "gi" => LauncherState.TryUpdate(state => state with
            {
                Preferences = state.Preferences with { Genshin120FpsOnLaunch = enabled },
            }),
            "hsr" => LauncherState.TryUpdate(state => state with
            {
                Preferences = state.Preferences with { Hsr120FpsOnLaunch = enabled },
            }),
            _ => false,
        };

    // Keep the launch integration seam stable while the visible preference is shared.
    internal bool TrySetHsr120FpsOnLaunch(bool enabled) =>
        TrySet120FpsOnLaunch("hsr", enabled);

    internal Hsr120FpsLaunchPreparationResult PrepareHsr120FpsForLaunch() =>
        !Hsr120FpsOnLaunch
            ? new(Hsr120FpsLaunchPreparationStatus.Disabled)
            : (_hsr120FpsSetting
                ?? throw new InvalidOperationException("The Star Rail FPS setting is not initialized."))
                .Apply();

    internal GameScreenshotFolderResult ResolveScreenshotFolder(string gameId) =>
        (_screenshotFolders
            ?? throw new InvalidOperationException("Screenshot folders are not initialized."))
            .Resolve(gameId);

    internal GenshinGameSessionAdapter GenshinSession { get; private set; } = null!;

    internal LauncherStateController LauncherState { get; private set; } = null!;

    internal IReadOnlyDictionary<string, HoyoGameSessionAdapter> HoyoSessions { get; private set; } =
        null!;

    internal HoyoPlayHandoffExecutor HoyoPlayExecutor { get; private set; } = null!;

    internal WuWaMaintenanceService WuWaMaintenance { get; private set; } = null!;

    internal PublisherGameDirectLaunchService PublisherGameLaunchService { get; private set; } = null!;

    internal EndfieldInstallRootStore EndfieldRootStore { get; private set; } = null!;

    internal EndfieldOfficialMaintenanceService EndfieldMaintenance { get; private set; } = null!;

    internal IReadOnlyDictionary<string, PublisherGameSessionAdapter> PublisherGameSessions
    {
        get;
        private set;
    } = null!;

    internal nint WindowHandle => _window is null
        ? throw new InvalidOperationException("The Nyx window is not initialized.")
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    internal void BeginWindowDrag()
    {
        if (_window is MainWindow mainWindow) mainWindow.BeginDrag();
    }

    internal async ValueTask<bool> RediscoverInstallsAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionRefresh is null) return false;
        await _sessionRefresh.RefreshNowAsync(cancellationToken);
        return true;
    }

    internal async ValueTask<bool> RefreshContentAsync(CancellationToken cancellationToken = default)
    {
        if (_launcherBanners is null) return false;
        await _launcherBanners.RefreshManualAsync(cancellationToken);
        return true;
    }

    internal async Task RefreshContentManualAsync(CancellationToken cancellationToken = default)
    {
        await RefreshContentAsync(cancellationToken);
    }

    internal void ApplyContentRefreshPreferences()
    {
        if (_launcherBanners is null) return;
        _launcherBanners.SetAutomaticRefreshEnabled(true);
    }

    internal GenshinInspectionAdapter GenshinInspection { get; private set; } = null!;

    internal WindowsGenshinCandidateDiscovery GenshinDiscovery { get; private set; } = null!;

    internal GenshinLaunchService GenshinLaunchService { get; private set; } = null!;

    internal event EventHandler? WindowReactivated;

    internal event EventHandler? EndfieldRootAutoDiscovered;

    private string? GetManualInstallRoot(string gameId) =>
        LauncherState.Snapshot.Preferences.ManualInstallRoots.TryGetValue(gameId, out var root)
            ? root
            : null;

    private string? ResolveValidatedScreenshotRoot(
        string gameId,
        HoyoCurrentUserDiscovery hoyoDiscovery,
        HoyoGameIdentityAdapter hoyoIdentity,
        WuWaInstallRootLocator wuwaRootLocator)
    {
        string? candidate;
        switch (gameId)
        {
            case "gi":
                candidate = GetManualInstallRoot(gameId) ?? GenshinDiscovery.Discover().GameRoot;
                var genshin = GenshinInspection.InspectGame(candidate);
                return genshin.Status is Nyx.Desktop.Core.Genshin.GenshinInspectionStatus.Ready
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(candidate ?? string.Empty),
                        genshin.CanonicalRoot,
                        StringComparison.OrdinalIgnoreCase)
                        ? genshin.CanonicalRoot
                        : null;
            case "hsr":
            case "zzz":
                candidate = GetManualInstallRoot(gameId);
                if (candidate is null)
                {
                    var record = gameId == "hsr"
                        ? HoyoCurrentGameRecord.HsrGlobal
                        : HoyoCurrentGameRecord.ZzzGlobal;
                    var discovered = hoyoDiscovery.Discover(record);
                    candidate = discovered.Status is Nyx.Desktop.Core.Hoyo.HoyoInspectionStatus.Ready
                        ? discovered.CanonicalRoot
                        : null;
                }

                var hoyo = hoyoIdentity.Inspect(gameId, candidate);
                return hoyo.Status is Nyx.Desktop.Core.Hoyo.HoyoInspectionStatus.Ready
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(candidate ?? string.Empty),
                        hoyo.CanonicalRoot,
                        StringComparison.OrdinalIgnoreCase)
                        ? hoyo.CanonicalRoot
                        : null;
            case "wuwa":
                candidate = GetManualInstallRoot(gameId) ?? wuwaRootLocator.LocateRoot();
                var publisher = PublisherGameLaunchService.CheckGame(gameId, candidate);
                return publisher.Status is PublisherGameLaunchStatus.Ready or PublisherGameLaunchStatus.Running
                    ? Path.TrimEndingDirectorySeparator(candidate!)
                    : null;
            default:
                return null;
        }
    }

    private HoyoGameRenderingMode GetHoyoRenderingMode(string gameId) =>
        LauncherState.Snapshot.Preferences.RenderingModes.TryGetValue(gameId, out var mode)
        && string.Equals(mode, "dx12", StringComparison.Ordinal)
            ? HoyoGameRenderingMode.DirectX12
            : HoyoGameRenderingMode.PublisherDefault;

    private PublisherGameRenderingMode GetPublisherRenderingMode(string gameId) =>
        LauncherState.Snapshot.Preferences.RenderingModes.TryGetValue(gameId, out var mode)
        && string.Equals(mode, "dx11", StringComparison.Ordinal)
            ? PublisherGameRenderingMode.DirectX11
            : PublisherGameRenderingMode.PublisherDefault;

    private IReadOnlyList<string> GetOfficialLaunchArguments(string gameId)
    {
        var options = LauncherState.Snapshot.OfficialLaunchOptions;
        return options.TryGetValue(gameId, out var configured)
            && configured.Enabled
            && CustomArgumentParser.TryParse(configured.RawArguments, out var arguments)
                ? arguments
                : Array.Empty<string>();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_accountShutdownStarted
            && args.WindowActivationState is not WindowActivationState.Deactivated)
        {
            _ = RefreshAfterActivationAsync();
        }
    }

    private void LauncherState_Changed(object? sender, EventArgs e)
    {
        var preferences = LauncherState.Snapshot.Preferences;
        var flags = preferences.FeatureFlags;
        _publisherAccounts?.ApplyConsentSnapshot(
            flags.HoyoLabAccountAccess,
            flags.SkportAccountAccess,
            flags.HoyoLabAccountCleanupPending,
            flags.SkportAccountCleanupPending);
        _publisherAccounts?.ApplyPasswordSavingPreference(
            preferences.PublisherPasswordSavingEnabled);
    }

    private async Task RecoverPendingPublisherRevocationsAsync()
    {
        var accounts = _publisherAccounts;
        if (accounts is null) return;
        try
        {
            _ = await accounts.ClearSavedHoyoLabPasswordsAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        foreach (var provider in new[] { "HoYoLAB", "SKPORT" })
        {
            try
            {
                if (!accounts.HasPendingConsentRevocation(provider)) continue;
                var flags = LauncherState.Snapshot.Preferences.FeatureFlags;
                var (stateAccountAccess, stateCleanupPending) = provider switch
                {
                    "HoYoLAB" => (
                        flags.HoyoLabAccountAccess,
                        flags.HoyoLabAccountCleanupPending),
                    "SKPORT" => (
                        flags.SkportAccountAccess,
                        flags.SkportAccountCleanupPending),
                    _ => (false, false),
                };
                var disableAccess =
                    accounts.PendingConsentRevocationDisablesAccess(
                        provider,
                        stateAccountAccess,
                        stateCleanupPending);
                if (!TryPersistPublisherCleanupPending(
                    provider,
                    cleanupPending: true,
                    accountAccess: disableAccess ? false : null))
                    continue;
                var result = await accounts.RetryPendingConsentRevocationAsync(provider);
                if (result != PublisherConnectionState.NotConnected) continue;
                if (!accounts.CompleteConsentRevocation(
                    provider,
                    clearOptOutIntent: disableAccess))
                    continue;
                TryPersistPublisherCleanupPending(provider, cleanupPending: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        try
        {
            _ = await accounts.RetryHoyoLabSyncDeletionsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryPersistPublisherCleanupPending(
        string provider,
        bool cleanupPending,
        bool? accountAccess = null) =>
        LauncherState.TryUpdatePublisherCleanupPending(
            provider,
            cleanupPending,
            accountAccess);

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_accountShutdownComplete) return;
        args.Cancel = true;
        if (_accountShutdownStarted) return;
        _accountShutdownStarted = true;
        if (_currentInstance is not null)
            _currentInstance.Activated -= CurrentInstance_Activated;
        if (_window is not null)
            _window.Activated -= Window_Activated;
        _ = UnregisterSuspendResumeNotifications();
        if (!_stableUpdateHandoffCommitted) _stableUpdateCancellation.Cancel();
        sender.Hide();
        SessionUiLifetime.Terminate();
        CancelEndfieldSiblingDiscovery();
        _sessionRefresh?.Stop();
        _sessions?.Shutdown();
        _ = ShutDownAccountsAndCloseAsync();
    }

    private async Task ShutDownAccountsAndCloseAsync()
    {
        if (_window is MainWindow mainWindow)
            await DisposeMainPageAsync(mainWindow);

        var bannerShutdown = _launcherBanners is null
            ? Task.CompletedTask
            : DisposeLauncherBannersAsync(_launcherBanners);
        var publisherStatusShutdown = _hoyoPublisherStatus is null
            ? Task.CompletedTask
            : DisposePublisherStatusAsync(_hoyoPublisherStatus);
        var wuwaAccountShutdown = _wuwaAccountStatus is null
            ? Task.CompletedTask
            : DisposeWuWaAccountStatusAsync(_wuwaAccountStatus);
        var publisherAccountShutdown = _publisherAccounts is null
            ? Task.CompletedTask
            : DisposePublisherAccountsAsync(_publisherAccounts);
        var exportClose = _exports is null
            ? Task.CompletedTask
            : CloseExportsForLauncherAsync(_exports);

        await AwaitEndfieldSiblingDiscoveryAsync();
        await UnregisterSuspendResumeNotifications();
        _powerDispatcher = null;
        _gamePlaytime?.Dispose();
        if (_sessionRefresh is not null)
            await DisposeRefreshAsync(_sessionRefresh);
        if (_sessions is not null)
            await DisposeSessionsAsync(_sessions);
        _sessionRefresh = null;
        _sessions = null;
        _gamePlaytime = null;

        await Task.WhenAll(
            bannerShutdown,
            publisherStatusShutdown,
            wuwaAccountShutdown,
            publisherAccountShutdown,
            _stableUpdateTask,
            exportClose);

        if (_achievementExportHandoffs is not null)
            await DisposeAchievementHandoffsAsync(_achievementExportHandoffs);
        if (_exports is not null)
            await DisposeExportCoordinatorAsync(_exports);
        try { _pullExports?.Dispose(); }
        catch (Exception) { }
        if (_genshin120FpsProcessStarter is not null)
            await DisposeGenshin120FpsStarterAsync(_genshin120FpsProcessStarter);
        await DisposeHoyoPlayExecutorAsync(HoyoPlayExecutor);

        _launcherBanners = null;
        _hoyoPublisherStatus = null;
        _wuwaAccountStatus = null;
        _publisherAccounts = null;
        _exports = null;
        _pullExports = null;
        _achievementExportHandoffs = null;
        _genshin120FpsProcessStarter = null;
        _stableUpdateCancellation.Dispose();
        _accountShutdownComplete = true;
        try
        {
            _currentInstance?.UnregisterKey();
        }
        catch (Exception)
        {
            // Explicit unregistration is best effort; shutdown must still close the window.
        }
        _window?.Close();
    }

    internal void StartStableUpdate(Func<CancellationToken, Task> runUpdate)
    {
        ArgumentNullException.ThrowIfNull(runUpdate);
        if (_accountShutdownStarted || Interlocked.Exchange(ref _stableUpdateStarted, 1) != 0) return;
        _stableUpdateTask = runUpdate(_stableUpdateCancellation.Token);
    }

    internal void BeginStableUpdateShutdown()
    {
        _stableUpdateHandoffCommitted = true;
        _window?.Close();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_currentInstance is not null)
        {
            _currentInstance.Activated -= CurrentInstance_Activated;
        }

        LauncherState.Changed -= LauncherState_Changed;
        SessionUiLifetime.Terminate();
        CancelEndfieldSiblingDiscovery();
        UnregisterSuspendResumeNotifications().GetAwaiter().GetResult();
        _powerDispatcher = null;

        if (_window is not null)
        {
            _window.Activated -= Window_Activated;
            _window.Closed -= Window_Closed;
            _window.AppWindow.Closing -= AppWindow_Closing;
        }

        _gamePlaytime?.Dispose();
        _sessionRefresh?.Stop();
        _sessions?.Shutdown();
    }

    private async Task RefreshAfterActivationAsync()
    {
        if (_accountShutdownStarted) return;
        WindowReactivated?.Invoke(this, EventArgs.Empty);

        try
        {
            // Ordinary focus changes are not a system resume. Preserve the two
            // separated absence samples used to prove that a game really closed.
            await SessionRefresh.RefreshNowAsync();
        }
        catch (Exception)
        {
            // Session state remains fail-closed. The periodic observer can try again.
        }

    }

    private static async Task DisposeRefreshAsync(GameSessionRefreshPump refresh)
    {
        try
        {
            await refresh.DisposeAsync();
        }
        catch (Exception)
        {
            // Shutdown already blocked new coordinator work.
        }
    }

    private static async Task DisposeSessionsAsync(GameSessionCoordinator sessions)
    {
        try
        {
            await sessions.DisposeAsync();
        }
        catch (Exception)
        {
            // Admission is closed; adapter cleanup cannot reopen launch work.
        }
    }

    private static async Task DisposeLauncherBannersAsync(LauncherBannersContentService launcherBanners)
    {
        try
        {
            await launcherBanners.DisposeAsync();
        }
        catch (Exception)
        {
            // Content cleanup must never block shutdown or alter launch state.
        }
    }

    private static async Task DisposePublisherStatusAsync(HoyoPublisherStatusSource publisherStatus)
    {
        try
        {
            await publisherStatus.DisposeAsync();
        }
        catch (Exception)
        {
            // Publisher status is advisory and cannot keep the app alive.
        }
    }

    private static async Task DisposeWuWaAccountStatusAsync(WuWaAccountStatusService accountStatus)
    {
        try
        {
            await accountStatus.DisposeAsync();
        }
        catch (Exception)
        {
            // Account status is optional and never owns launch state.
        }
    }

    private static async Task DisposePublisherAccountsAsync(PublisherAccountService publisherAccounts)
    {
        try
        {
            await publisherAccounts.DisposeAsync();
        }
        catch (Exception)
        {
            // Publisher browser sessions are optional and never own launch state.
        }
    }

    private static async Task DisposeMainPageAsync(MainWindow window)
    {
        try { await window.ShutDownAsync(); }
        catch (Exception) { }
    }

    private static async Task CloseExportsForLauncherAsync(ExportCoordinator exports)
    {
        try { await exports.ShutDownForLauncherCloseAsync(); }
        catch (Exception) { }
    }

    private static async Task DisposeAchievementHandoffsAsync(
        BoundedAchievementExportHandoffOwner handoffs)
    {
        try { await handoffs.DisposeAsync(); }
        catch (Exception) { }
    }

    private static async Task DisposeExportCoordinatorAsync(ExportCoordinator exports)
    {
        try { await exports.DisposeAsync(); }
        catch (Exception) { }
    }

    private static async Task DisposeGenshin120FpsStarterAsync(
        Genshin120FpsProcessStarter starter)
    {
        try { await starter.DisposeAsync(); }
        catch (Exception) { }
    }

    private static async Task DisposeHoyoPlayExecutorAsync(HoyoPlayHandoffExecutor executor)
    {
        try { await executor.DisposeAsync(); }
        catch (Exception) { }
    }
}
