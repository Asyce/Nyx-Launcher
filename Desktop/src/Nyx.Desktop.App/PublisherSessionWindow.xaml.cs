using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Web.WebView2.Core;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Exports;
using Windows.Foundation;
using Windows.Graphics;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow : Window, IAsyncDisposable
{
    private const int ResourceCaptureTimeoutSeconds = 12;
    private static readonly Uri WebView2DownloadUri =
        new("https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/");
    private static readonly TimeSpan BrowserProcessExitTimeout = TimeSpan.FromSeconds(5);
    private readonly string profileDirectory;
    private readonly string provider;
    private readonly TimeProvider timeProvider;
    private readonly bool passwordSavingEnabled;
    private readonly Action? passwordCleanupCompleted;
    private readonly PublisherPasswordNavigationGate passwordNavigationGate;
    private readonly PublisherClaimWriteAuthority claimWriteAuthority = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource browserProcessExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PublisherVisibleConnectCompletion> connectCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object browserProcessExitHandlerGate = new();
    private Uri? approvedTopLevelUri;
    private Uri? visibleConnectUri;
    private SessionProbeCapture? pendingSessionProbe;
    private EndfieldIdentityCapture? pendingEndfieldIdentityCapture;
    private CheckInCapture? pendingCheckInCapture;
    private PendingResourceCapture? pendingResourceCapture;
    private PublisherRoleBinding? expectedHsrAchievementRole;
    private PublisherCheckInCaptureDiagnostic lastCheckInDiagnostic;
    private PublisherProfileMutationJournal? profileMutationJournal;
    private PublisherEndfieldAccountIdentity? reviewedEndfieldIdentity;
    private long sessionProbeGeneration;
    private long endfieldIdentityGeneration;
    private long checkInGeneration;
    private long resourceGeneration;
    private PublisherSessionPurpose purpose;
    private string? authorizedGameId;
    private int initialized;
    private int browserCloseStarted;
    private int browserProcessExitBarrierArmed;
    private int visibleConnectOperationInFlight;
    private int hsrAchievementListNetworkState;
    private bool webView2RuntimeUnavailable;
    private bool windowClosed;
    private bool disposed;
    private Exception? browserCloseFailure;
    private CoreWebView2Environment? browserProcessExitEnvironment;
    private TypedEventHandler<CoreWebView2Environment, CoreWebView2BrowserProcessExitedEventArgs>?
        browserProcessExitedHandler;
    private Window? socialLoginWindow;
    private Microsoft.UI.Xaml.Controls.WebView2? socialLoginBrowser;

    public PublisherSessionWindow(
        string profileDirectory,
        string provider,
        TimeProvider? timeProvider = null,
        bool passwordSavingEnabled = false,
        Action? passwordCleanupCompleted = null)
    {
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        this.provider = provider;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.passwordSavingEnabled = passwordSavingEnabled;
        this.passwordCleanupCompleted = passwordCleanupCompleted;
        passwordNavigationGate = new(passwordSavingEnabled);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        Closed += (_, _) =>
        {
            windowClosed = true;
            lifetime.Cancel();
            CloseSocialLoginWindow();
            CloseBrowserOnce();
            connectCompletion.TrySetResult(PublisherVisibleConnectCompletion.Canceled);
            closed.TrySetResult();
        };
    }

    public async Task InitializeAsync(
        Uri initialUri,
        bool visible,
        PublisherSessionPurpose purpose,
        string gameId,
        string heading,
        CancellationToken cancellationToken,
        PublisherProfileMutationJournal? profileMutationJournal = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(initialUri);
        var entry = PublisherAccountCatalog.Get(gameId);
        if (!string.Equals(entry.Provider, provider, StringComparison.Ordinal)
            || !PublisherVisibleConnectNavigationPolicy.IsAllowedInitial(
                provider,
                purpose,
                gameId,
                initialUri))
            throw new InvalidOperationException("The publisher session purpose does not authorize this page.");
        if (purpose == PublisherSessionPurpose.Connect && profileMutationJournal is null)
            throw new InvalidOperationException("Connect sessions require profile mutation tracking.");
        if (Interlocked.Exchange(ref initialized, 1) != 0)
            throw new InvalidOperationException("The publisher session purpose is already fixed.");
        approvedTopLevelUri = initialUri;
        this.purpose = purpose;
        this.profileMutationJournal = purpose == PublisherSessionPurpose.Connect
            ? profileMutationJournal
            : null;
        authorizedGameId = gameId;
        WindowHeading.Text = heading;
        DoneButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        AppWindow.IsShownInSwitchers = visible;
        // Keep the reviewed desktop markup even while the window is hidden.
        AppWindow.Resize(new SizeInt32(1280, 720));
        if (!visible) AppWindow.Move(new PointInt32(-20000, -20000));
        Activate();
        if (!visible) AppWindow.Hide();

        if (visible
            && purpose == PublisherSessionPurpose.Connect
            && !IsWebView2RuntimeAvailable())
        {
            ShowWebView2RuntimeRequired();
            return;
        }

        // WebView pages can persist cookies or script storage from any response,
        // not only from a reviewed login POST. Crossing into a Connect profile is
        // therefore the conservative mutation boundary for cancellation.
        if (purpose == PublisherSessionPurpose.Connect)
            profileMutationJournal!.MarkMayHaveChanged();
        var core = await InitializeBrowserProfileAsync(visible, cancellationToken);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = visible;
        core.Settings.AreHostObjectsAllowed = false;
        if (visible
            && purpose == PublisherSessionPurpose.Connect
            && provider == "SKPORT"
            && gameId == "ae")
        {
            _ = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                """
                document.addEventListener('DOMContentLoaded', () => {
                    const style = document.createElement('style');
                    style.textContent = 'div:has(> div > img.mobile-logo) { display: none !important; }';
                    document.head.appendChild(style);
                }, { once: true });
                """);
        }
        core.NavigationStarting += Core_NavigationStarting;
        // Visible sign-in and daily check-in otherwise behave like the
        // publisher's own page in a normal browser. The daily page keeps one
        // narrow interception: the exact current claim endpoint is filtered so
        // one explicit Nyx click can authorize one claim write, while reviewed
        // retired endpoints are filtered only so the request policy can reject
        // them. Both sessions remain confined to the isolated profile, fixed
        // top-level page, and existing popup/download/permission boundaries.
        if (purpose == PublisherSessionPurpose.CheckIn)
        {
            foreach (var pattern in
                PublisherAccountCatalog.GetCheckInWebResourceFilterPatterns(gameId))
            {
                core.AddWebResourceRequestedFilter(
                    pattern,
                    CoreWebView2WebResourceContext.All);
            }
            core.WebResourceRequested += Core_WebResourceRequested;
        }
        else if (purpose == PublisherSessionPurpose.Achievements)
        {
            foreach (var pattern in
                PublisherAccountCatalog.GetAchievementWebResourceFilterPatterns(gameId))
            {
                core.AddWebResourceRequestedFilter(
                    pattern,
                    CoreWebView2WebResourceContext.All);
            }
            core.WebResourceRequested += Core_WebResourceRequested;
        }
        else if (purpose != PublisherSessionPurpose.Connect)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;
        }
        core.WebResourceResponseReceived += Core_WebResourceResponseReceived;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.DownloadStarting += Core_DownloadStarting;
        core.PermissionRequested += Core_PermissionRequested;
        // Resource capture owns its first and only navigation so requests from
        // initialization cannot be mistaken for the measured operation.
        if (purpose != PublisherSessionPurpose.Resource)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token);
            try
            {
                if (visible && purpose == PublisherSessionPurpose.Connect)
                {
                    visibleConnectUri = initialUri;
                    if (Interlocked.Exchange(ref visibleConnectOperationInFlight, 1) != 0)
                        throw new InvalidOperationException("A publisher connect operation is already running.");
                    try
                    {
                        await AttemptVisibleConnectPageAsync(
                            initialUri,
                            operation.Token);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref visibleConnectOperationInFlight, 0);
                        if (!windowClosed)
                        {
                            DoneButton.IsEnabled = true;
                            RetryButton.IsEnabled = true;
                        }
                    }
                }
                else
                {
                    await NavigateAsync(initialUri, operation.Token);
                }
            }
            catch (OperationCanceledException) when (
                lifetime.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // Closing the publisher window ends initialization immediately.
            }
        }

        if (visible && purpose == PublisherSessionPurpose.Connect && !windowClosed)
            _ = MonitorVisibleConnectAsync();
    }

    public async Task ClearSavedPasswordsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (passwordSavingEnabled)
            throw new InvalidOperationException("Password removal requires password saving to be disabled.");
        if (Interlocked.Exchange(ref initialized, 1) != 0)
            throw new InvalidOperationException("The publisher browser profile is already initialized.");

        cancellationToken.ThrowIfCancellationRequested();
        _ = await InitializeBrowserProfileAsync(visible: false, cancellationToken);
        await passwordNavigationGate.ClearSavedPasswordsAsync(
            ClearPublisherBrowsingDataAsync,
            cancellationToken);
    }

    private async Task<CoreWebView2> InitializeBrowserProfileAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(profileDirectory);
        // Each publisher receives its own app-owned WebView2 directory. This
        // never reads or attaches to Chrome, Edge, or another browser profile.
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            profileDirectory,
            new CoreWebView2EnvironmentOptions());
        AttachBrowserProcessExitHandler(environment);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2
                ?? throw new InvalidOperationException("Publisher browser did not initialize.");
            Volatile.Write(ref browserProcessExitBarrierArmed, 1);
            cancellationToken.ThrowIfCancellationRequested();

            // General form autofill is deliberately always disabled. Password
            // storage is a separate WebView2 feature and is enabled only by the
            // user's saved opt-in. Nyx never logs credentials or fills fields itself.
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = passwordSavingEnabled;

            return core;
        }
        catch
        {
            if (Volatile.Read(ref browserProcessExitBarrierArmed) == 0)
            {
                try
                {
                    DetachBrowserProcessExitHandler();
                }
                catch (Exception teardownFailure)
                {
                    throw new PublisherSessionTeardownException(teardownFailure);
                }
            }

            throw;
        }
    }

    private void AttachBrowserProcessExitHandler(CoreWebView2Environment environment)
    {
        TypedEventHandler<CoreWebView2Environment, CoreWebView2BrowserProcessExitedEventArgs> handler =
            (_, _) => browserProcessExited.TrySetResult();
        lock (browserProcessExitHandlerGate)
        {
            if (browserProcessExitEnvironment is not null || browserProcessExitedHandler is not null)
                throw new InvalidOperationException("Publisher browser exit monitoring is already active.");

            environment.BrowserProcessExited += handler;
            browserProcessExitEnvironment = environment;
            browserProcessExitedHandler = handler;
        }
    }

    private void DetachBrowserProcessExitHandler()
    {
        lock (browserProcessExitHandlerGate)
        {
            var environment = browserProcessExitEnvironment;
            var handler = browserProcessExitedHandler;
            browserProcessExitEnvironment = null;
            browserProcessExitedHandler = null;
            if (environment is not null && handler is not null)
                environment.BrowserProcessExited -= handler;
        }
    }

    private async Task ClearPublisherBrowsingDataAsync(
        PublisherBrowsingDataKind dataKind,
        CancellationToken cancellationToken)
    {
        if (dataKind != PublisherBrowsingDataKind.PasswordAutosave)
            throw new InvalidOperationException("Only saved-password cleanup is authorized before navigation.");
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException("Publisher browser did not initialize.");

        // Turning autosave off alone leaves old entries eligible to autofill.
        // Remove only WebView2's saved-password data; cookies and the signed-in
        // session stay. No credential value is inspected by Nyx.
        await core.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.PasswordAutosave);
        passwordCleanupCompleted?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task AttemptVisibleConnectPageAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var presentation = await PublisherVisibleConnectFlow.AttemptPageAsync(
            operationCancellation => NavigateWithOutcomeAsync(uri, operationCancellation),
            cancellationToken);
        if (windowClosed || lifetime.IsCancellationRequested)
            return;

        RetryButton.Visibility = presentation.ShowRetry
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = presentation.Ready
            ? presentation.Guidance ?? string.Empty
            : presentation.Guidance
                ?? "The official sign-in page needs review. Choose Retry or close this window.";
    }

    private async Task MonitorVisibleConnectAsync()
    {
        try
        {
            var baselineEstablished = false;
            var wasAuthenticated = false;
            while (!windowClosed && !connectCompletion.Task.IsCompleted)
            {
                PublisherEndfieldAccountIdentity? endfieldIdentity = null;
                var authenticated = provider == "HoYoLAB"
                    ? await GetHoyoSessionProofOnceAsync(lifetime.Token)
                        == PublisherSessionProof.Authenticated
                    : provider == "SKPORT"
                        && (endfieldIdentity = await TryReadEndfieldRegionAsync(lifetime.Token)) is not null;
                if (baselineEstablished && !wasAuthenticated && authenticated)
                {
                    await TryCompleteVisibleConnectAsync(
                        reportFailure: false,
                        endfieldIdentity: endfieldIdentity,
                        cancellationToken: lifetime.Token);
                    return;
                }

                wasAuthenticated = authenticated;
                baselineEstablished = true;
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Done remains available when automatic detection cannot complete.
        }
    }

    public Task<PublisherVisibleConnectCompletion> WaitForConnectCompletionAsync(
        CancellationToken cancellationToken) =>
        connectCompletion.Task.WaitAsync(cancellationToken);

    public PublisherEndfieldAccountIdentity? ReviewedEndfieldIdentity =>
        Volatile.Read(ref reviewedEndfieldIdentity);

    private async Task<PublisherEndfieldAccountIdentity?> ReviewEndfieldAccountIdentityAsync(
        CancellationToken cancellationToken)
    {
        if (purpose != PublisherSessionPurpose.Connect
            || provider != "SKPORT"
            || authorizedGameId != "ae")
            throw new InvalidOperationException("This publisher session cannot review an Endfield account.");

        var observedIdentity = ReviewedEndfieldIdentity;
        if (observedIdentity is not null)
            return observedIdentity;

        var generation = Interlocked.Increment(ref endfieldIdentityGeneration);
        var capture = new EndfieldIdentityCapture(generation, cancellationToken);
        if (Interlocked.CompareExchange(ref pendingEndfieldIdentityCapture, capture, null) is not null)
            throw new InvalidOperationException("An Endfield account review is already running.");
        try
        {
            Browser.CoreWebView2!.Reload();
            return await capture.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref pendingEndfieldIdentityCapture, null, capture) == capture)
                capture.Cancel();
        }
    }

    private async Task<PublisherEndfieldAccountIdentity?> TryReadEndfieldRegionAsync(
        CancellationToken cancellationToken)
    {
        if (purpose != PublisherSessionPurpose.Connect
            || provider != "SKPORT"
            || authorizedGameId != "ae"
            || Browser.Source is not Uri currentPage
            || !PublisherAccountCatalog.IsExactCheckInUri("ae", currentPage))
            return null;
        try
        {
            var raw = await Browser.CoreWebView2!
                .ExecuteScriptAsync(
                    """
                    (() => {
                      const allowed = new Set(['Asia', 'Americas / Europe']);
                      const matches = new Set();
                      for (const element of document.querySelectorAll('body *')) {
                        const style = getComputedStyle(element);
                        const bounds = element.getBoundingClientRect();
                        if (style.display === 'none'
                            || style.visibility === 'hidden'
                            || Number.parseFloat(style.opacity || '1') === 0
                            || bounds.width <= 0
                            || bounds.height <= 0) continue;
                        const text = (element.textContent || '').trim().replace(/\s+/g, ' ');
                        if (allowed.has(text)) matches.add(text);
                      }
                      return matches.size === 1 ? [...matches][0] : null;
                    })()
                    """)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return PublisherEndfieldAccountIdentityParser.TryCreateRegionOnly(
                ReadScriptString(raw),
                out var identity)
                    ? identity
                    : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<PublisherSessionProof> GetSessionProofAsync(CancellationToken cancellationToken)
    {
        var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("Publisher browser is not initialized.");
        if (provider == "HoYoLAB")
        {
            return await PublisherSessionProofRetryPolicy.RunAsync(
                GetHoyoSessionProofOnceAsync,
                static (delay, operationCancellation) =>
                    Task.Delay(delay, operationCancellation),
                cancellationToken);
        }
        if (provider != "SKPORT") return PublisherSessionProof.NeedsReview;

        var capture = BeginSessionProbe(cancellationToken);
        try
        {
            core.Reload();
            return await capture.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return PublisherSessionProof.NeedsReview;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref pendingSessionProbe, null, capture) == capture)
                capture.Cancel();
        }
    }

    private async Task<PublisherSessionProof> GetHoyoSessionProofOnceAsync(
        CancellationToken cancellationToken)
    {
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException("Publisher browser is not initialized.");
        var cookies = await core.CookieManager
            .GetCookiesAsync("https://sg-public-api.hoyolab.com/")
            .AsTask(cancellationToken);
        var names = cookies
            .Select(static cookie => cookie.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("ltoken_v2")
            && (names.Contains("ltuid_v2") || names.Contains("account_id_v2"))
            ? PublisherSessionProof.Authenticated
            : PublisherSessionProof.LoginRequired;
    }

    public async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsAsync(
        PublisherRoleBinding? role,
        IReadOnlySet<long> currentCatalogIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentCatalogIds);
        if (purpose != PublisherSessionPurpose.Achievements
            || !string.Equals(authorizedGameId, "hsr", StringComparison.Ordinal))
            throw new InvalidOperationException("This publisher session cannot export those achievements.");
        if (role is not null
            && !PublisherAccountCatalog.IsValidRoleBinding("hsr", role))
            throw new ExportProviderException("hoyolab-role-selection-required");

        var apiCookies = await Browser.CoreWebView2!.CookieManager
            .GetCookiesAsync("https://sg-public-api.hoyolab.com/")
            .AsTask(cancellationToken);
        var apiCookieNames = apiCookies
            .Select(static cookie => cookie.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!apiCookieNames.Contains("ltoken_v2")
            || (!apiCookieNames.Contains("ltuid_v2")
                && !apiCookieNames.Contains("account_id_v2")))
            throw new ExportProviderException("hoyolab-api-cookie-missing");

        var accountIds = apiCookies
            .Where(static cookie =>
                string.Equals(cookie.Name, "account_id_v2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cookie.Name, "ltuid_v2", StringComparison.OrdinalIgnoreCase))
            .Select(static cookie => cookie.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (accountIds.Length != 1
            || accountIds[0].Length is < 1 or > 32
            || accountIds[0][0] == '0'
            || !accountIds[0].All(char.IsAsciiDigit))
            throw new ExportProviderException("hoyolab-api-account-mismatch");
        var resultKey = "__pengoNyxHsrAchievements_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(resultKey);
        Volatile.Write(ref expectedHsrAchievementRole, role);
        try
        {
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(BuildHsrAchievementExportScript(resultKey, role))
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (!string.Equals(
                started,
                JsonSerializer.Serialize("started"),
                StringComparison.Ordinal))
                throw new ExportProviderException("hoyolab-script-start-failed");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = await Browser.CoreWebView2
                    .ExecuteScriptAsync($"window[{serializedKey}] ?? null")
                    .AsTask(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (!string.Equals(raw, "null", StringComparison.Ordinal))
                {
                    try
                    {
                        return HoyoLabHsrAchievementResultParser.Parse(
                            raw,
                            currentCatalogIds,
                            role);
                    }
                    catch (ExportProviderException exception)
                        when (exception.Code == "hoyolab-list-request-failed")
                    {
                        throw new ExportProviderException(
                            (HsrAchievementListNetworkState)Volatile.Read(
                                ref hsrAchievementListNetworkState) switch
                            {
                                HsrAchievementListNetworkState.None =>
                                    "hoyolab-list-client-no-request",
                                HsrAchievementListNetworkState.PreflightAllowed =>
                                    "hoyolab-list-client-no-get",
                                HsrAchievementListNetworkState.RequestBlocked =>
                                    "hoyolab-list-policy-blocked",
                                HsrAchievementListNetworkState.BlockedWrongMethod =>
                                    "hoyolab-list-policy-method",
                                HsrAchievementListNetworkState.BlockedMissingQuery =>
                                    "hoyolab-list-policy-missing-query",
                                HsrAchievementListNetworkState.BlockedExtraQuery =>
                                    "hoyolab-list-policy-extra-query",
                                HsrAchievementListNetworkState.BlockedQueryValue =>
                                    "hoyolab-list-policy-query-value",
                                HsrAchievementListNetworkState.RequestAllowed =>
                                    "hoyolab-list-no-response",
                                HsrAchievementListNetworkState.ResponseAccepted =>
                                    "hoyolab-list-response-rejected",
                                HsrAchievementListNetworkState.ResponseFailed =>
                                    "hoyolab-list-http-failed",
                                _ => "hoyolab-list-request-failed",
                            });
                    }
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new ExportProviderException("timed-out");
        }
        catch (TimeoutException)
        {
            throw new ExportProviderException("timed-out");
        }
        finally
        {
            Volatile.Write(ref expectedHsrAchievementRole, null);
            try
            {
                await Browser.CoreWebView2!
                    .ExecuteScriptAsync($"delete window[{serializedKey}]")
                    .AsTask(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The hidden result slot is per-page, random, and process-local.
                // A failed cleanup must not replace the real export outcome.
            }
        }
    }

    public async Task<DailyCheckInResult> RunCheckInAsync(
        PublisherAccountCatalogEntry entry,
        PublisherRoleBinding? expectedBinding,
        bool allowAccountWideStatus,
        CancellationToken cancellationToken)
    {
        if (purpose != PublisherSessionPurpose.CheckIn
            || !string.Equals(authorizedGameId, entry.GameId, StringComparison.Ordinal))
            throw new InvalidOperationException("This publisher session cannot perform that check-in.");
        if (entry.CheckInUri is null || !entry.SupportsDailyCheckIn)
            return new(entry.GameId, DailyCheckInState.Unavailable, "No official daily check-in is available.", DateTimeOffset.UtcNow);
        if ((entry.GameId == "ae" && (expectedBinding is not null || !allowAccountWideStatus))
            || (entry.GameId != "ae"
                && (expectedBinding is null
                    || !PublisherAccountCatalog.IsValidRoleBinding(entry.GameId, expectedBinding))))
            return new(entry.GameId, DailyCheckInState.CouldNotCheck, "The selected character could not be proven.", DateTimeOffset.UtcNow);

        var operationTime = timeProvider.GetLocalNow();
        var expectedDate = DateOnly.FromDateTime(operationTime.DateTime);
        var before = await CaptureCheckInProofAsync(
            entry,
            "GET",
            expectedDate,
            operationTime,
            expectedBinding,
            allowAccountWideStatus,
            navigate: true,
            cancellationToken);
        if (before is PublisherCheckInProof.LoginNeeded)
            return new(entry.GameId, DailyCheckInState.LoginNeeded, $"Connect {entry.Provider} first.", DateTimeOffset.UtcNow);
        if (before is PublisherCheckInProof.Claimed)
            return new(entry.GameId, DailyCheckInState.AlreadyClaimed, "Already checked in today.", DateTimeOffset.UtcNow);
        if (before is not PublisherCheckInProof.Ready)
            return new(
                entry.GameId,
                DailyCheckInState.CouldNotCheck,
                $"The official page needs review ({DescribeCheckInDiagnostic(lastCheckInDiagnostic)}).",
                DateTimeOffset.UtcNow);
        if (Browser.Source is not Uri currentPage
            || !PublisherAccountCatalog.IsExactCheckInUri(entry.GameId, currentPage))
        {
            return new(
                entry.GameId,
                DailyCheckInState.CouldNotCheck,
                "The official page no longer matches the selected game.",
                DateTimeOffset.UtcNow);
        }

        var claimCapture = BeginCheckInCapture(
            entry.GameId,
            "POST",
            expectedDate,
            operationTime,
            expectedBinding,
            allowAccountWideStatus,
            cancellationToken);
        try
        {
            using var claimWrite = claimWriteAuthority.Arm(entry.GameId);
            var clickResult = await Browser.CoreWebView2!
                .ExecuteScriptAsync(BuildExactClaimScript(entry.GameId))
                .AsTask(cancellationToken);
            if (!string.Equals(ReadScriptString(clickResult), "clicked", StringComparison.Ordinal))
                return new(entry.GameId, DailyCheckInState.CouldNotCheck, "The official claim control was not available.", DateTimeOffset.UtcNow);

            var accepted = await claimCapture.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds),
                cancellationToken);
            if (accepted is PublisherCheckInProof.LoginNeeded)
                return new(entry.GameId, DailyCheckInState.LoginNeeded, $"Connect {entry.Provider} first.", DateTimeOffset.UtcNow);
            if (accepted is not PublisherCheckInProof.ClaimAccepted)
                return new(entry.GameId, DailyCheckInState.CouldNotCheck, "The official page did not accept the claim.", DateTimeOffset.UtcNow);
        }
        catch (TimeoutException)
        {
            return new(entry.GameId, DailyCheckInState.CouldNotCheck, "The official claim control was not available.", DateTimeOffset.UtcNow);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref pendingCheckInCapture, null, claimCapture) == claimCapture)
                claimCapture.Cancel();
        }

        var after = await CaptureCheckInProofAsync(
            entry,
            "GET",
            expectedDate,
            operationTime,
            expectedBinding,
            allowAccountWideStatus,
            navigate: true,
            cancellationToken);
        return after switch
        {
            PublisherCheckInProof.Claimed =>
                new(entry.GameId, DailyCheckInState.Claimed, "Daily reward claimed.", DateTimeOffset.UtcNow),
            PublisherCheckInProof.LoginNeeded =>
                new(entry.GameId, DailyCheckInState.LoginNeeded, $"Connect {entry.Provider} first.", DateTimeOffset.UtcNow),
            _ =>
                new(entry.GameId, DailyCheckInState.CouldNotCheck, "The official page did not confirm the claim.", DateTimeOffset.UtcNow),
        };
    }

    public async Task<PublisherResourceReadResult> ReadResourceAsync(
        PublisherAccountCatalogEntry entry,
        PublisherRoleBinding? expectedBinding,
        CancellationToken cancellationToken)
    {
        if (purpose != PublisherSessionPurpose.Resource
            || !string.Equals(authorizedGameId, entry.GameId, StringComparison.Ordinal))
            throw new InvalidOperationException("This publisher session cannot read that resource.");
        if (entry.ResourceUri is null
            || !entry.SupportsNumericResource
            || !PublisherAccountCatalog.IsExactResourcePageUri(entry.GameId, entry.ResourceUri))
            return new(null, PublisherResourceReadOutcome.NeedsReview);

        var generation = Interlocked.Increment(ref resourceGeneration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        if (expectedBinding is not null
            && !PublisherAccountCatalog.IsValidRoleBinding(entry.GameId, expectedBinding))
            return new(null, PublisherResourceReadOutcome.NeedsReview);
        var controllerKey = "__pengoNyxResource_" + Guid.NewGuid().ToString("N");
        var authority = new PublisherResourceCaptureAuthority(
            entry.GameId,
            generation,
            expectedBinding);
        var capture = new PendingResourceCapture(authority, controllerKey, linked.Token);
        var previous = Interlocked.Exchange(ref pendingResourceCapture, capture);
        if (previous is not null)
        {
            previous.Cancel();
            await AbortResourceFetchAsync(previous.ControllerKey);
        }
        try
        {
            await NavigateAsync(entry.ResourceUri, linked.Token);
            if (!ReferenceEquals(Volatile.Read(ref pendingResourceCapture), capture)
                || generation != Interlocked.Read(ref resourceGeneration))
                return new(null, PublisherResourceReadOutcome.NeedsReview);
            if (!authority.Open(generation))
                return new(null, PublisherResourceReadOutcome.NeedsReview);
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(BuildResourceFetchScript(
                    controllerKey,
                    entry.GameId,
                    expectedBinding))
                .AsTask(linked.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (!string.Equals(
                started,
                JsonSerializer.Serialize("started"),
                StringComparison.Ordinal))
                return new(null, PublisherResourceReadOutcome.NeedsReview);

            await Task.Delay(
                TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds),
                linked.Token);
            var trigger = await ReadResourceFetchStateAsync(
                controllerKey,
                entry.GameId,
                linked.Token);
            return PublisherResourceTriggerPolicy.Seal(
                authority,
                generation,
                trigger);
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
            if (Interlocked.CompareExchange(ref pendingResourceCapture, null, capture) == capture)
                capture.Cancel();
        }
    }

    private async Task<PublisherCheckInProof> CaptureCheckInProofAsync(
        PublisherAccountCatalogEntry entry,
        string method,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant,
        PublisherRoleBinding? expectedBinding,
        bool allowAccountWideStatus,
        bool navigate,
        CancellationToken cancellationToken)
    {
        var capture = BeginCheckInCapture(
            entry.GameId,
            method,
            expectedDate,
            expectedInstant,
            expectedBinding,
            allowAccountWideStatus,
            cancellationToken);
        lastCheckInDiagnostic = PublisherCheckInCaptureDiagnostic.None;
        try
        {
            if (navigate) await NavigateAsync(entry.CheckInUri!, cancellationToken);
            var proof = await capture.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds),
                cancellationToken);
            lastCheckInDiagnostic = capture.Diagnostic;
            // Official pages can issue a short-lived bootstrap request before
            // their final, role-bound status request. Once an exact response
            // has completed, an earlier rejected candidate must not poison it.
            return proof;
        }
        catch (TimeoutException)
        {
            lastCheckInDiagnostic = capture.Diagnostic == PublisherCheckInCaptureDiagnostic.None
                ? PublisherCheckInCaptureDiagnostic.TimedOutWithoutEndpoint
                : capture.Diagnostic;
            return PublisherCheckInProof.Invalid;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref pendingCheckInCapture, null, capture) == capture)
                capture.Cancel();
        }
    }

    private CheckInCapture BeginCheckInCapture(
        string gameId,
        string method,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant,
        PublisherRoleBinding? expectedBinding,
        bool allowAccountWideStatus,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref checkInGeneration);
        var capture = new CheckInCapture(
            gameId,
            method,
            expectedDate,
            expectedInstant,
            expectedBinding,
            allowAccountWideStatus,
            generation,
            cancellationToken);
        Interlocked.Exchange(ref pendingCheckInCapture, capture)?.Cancel();
        return capture;
    }

    private static string DescribeCheckInDiagnostic(PublisherCheckInCaptureDiagnostic diagnostic) =>
        diagnostic switch
        {
            PublisherCheckInCaptureDiagnostic.TimedOutWithoutEndpoint => "the official status request was not seen",
            PublisherCheckInCaptureDiagnostic.EndpointQueryRejected => "the official status request changed",
            PublisherCheckInCaptureDiagnostic.InvalidStatusOrType => "the official status response changed",
            PublisherCheckInCaptureDiagnostic.InvalidBody => "the official status data changed",
            _ => "the official status could not be confirmed",
        };

    private SessionProbeCapture BeginSessionProbe(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref sessionProbeGeneration);
        var capture = new SessionProbeCapture(generation, cancellationToken);
        Interlocked.Exchange(ref pendingSessionProbe, capture)?.Cancel();
        return capture;
    }

    private static string BuildExactClaimScript(string gameId)
    {
        if (gameId == "ae")
        {
            return """
                (() => {
                  const selectors = [
                    'img[src$="PCCalendarTodayBg.510de0.png"]',
                    'img[src$="MobileCalendarTodayBg.5f4677.png"]'
                  ];
                  const current = selectors.flatMap(selector => Array.from(document.querySelectorAll(selector)));
                  if (current.length !== 1) return 'missing';
                  current[0].click();
                  return 'clicked';
                })()
                """;
        }

        var contract = PublisherAccountCatalog.GetCheckInDomContract(gameId);
        var selector = contract.ReadySelector;
        if (!string.IsNullOrEmpty(contract.ReceivedSelector))
        {
            return $$"""
                (() => {
                  const items = Array.from(document.querySelectorAll({{JsonSerializer.Serialize(selector)}}));
                  if (items.length === 0 || items.length > 62) return 'missing';
                  const receivedSelector = {{JsonSerializer.Serialize(contract.ReceivedSelector)}};
                  const current = items.find(item => !item.querySelector(receivedSelector));
                  if (!current) return 'missing';
                  current.click();
                  return 'clicked';
                })()
                """;
        }
        return $$"""
            (() => {
              const current = document.querySelectorAll({{JsonSerializer.Serialize(selector)}});
              if (current.length !== 1) return 'missing';
              current[0].click();
              return 'clicked';
            })()
            """;
    }

    private async Task<PublisherResourceTriggerResult?> ReadResourceFetchStateAsync(
        string controllerKey,
        string gameId,
        CancellationToken cancellationToken)
    {
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        try
        {
            var raw = await Browser.CoreWebView2!
                .ExecuteScriptAsync(
                    $$"""
                    (() => {
                      const state = window[{{serializedKey}}];
                      if (!state || typeof state.status !== 'string')
                        return { state: 'missing', roles: [] };
                      const status = state.status === 'running'
                        || state.status === 'done'
                        || state.status === 'login'
                        || state.status === 'invalid'
                        || state.status === 'no-roles'
                        || state.status === 'canceled'
                        || state.status === 'signature-rejected'
                        || state.status === 'request-blocked'
                        || state.status === 'timed-out'
                          ? state.status
                          : 'missing';
                      let roles = [];
                      try {
                        if (status === 'done' && Array.isArray(state.roles)) {
                          roles = state.roles.splice(0, 9).map(role => ({
                            region: typeof role?.region === 'string'
                              ? role.region.slice(0, 65)
                              : null,
                            uid: typeof role?.uid === 'string'
                              ? role.uid.slice(0, 21)
                              : null,
                            nickname: typeof role?.nickname === 'string'
                              ? role.nickname.slice(0, 65)
                              : null,
                          }));
                        }
                      } finally {
                        if (Array.isArray(state.roles)) state.roles.length = 0;
                      }
                      return { state: status, roles: roles };
                    })()
                    """)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return PublisherResourceTriggerResultParser.TryParse(
                gameId,
                raw,
                out var result)
                    ? result
                    : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task AbortResourceFetchAsync(string controllerKey)
    {
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        try
        {
            await Browser.CoreWebView2!
                .ExecuteScriptAsync(
                    $$"""
                    (() => {
                      const key = {{serializedKey}};
                      const state = window[key];
                      try {
                        if (state && typeof state.abort === 'function') state.abort();
                      } catch {
                      }
                      try {
                        delete window[key];
                      } catch {
                      }
                      return 'cleared';
                    })()
                    """)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Closing or navigating the isolated WebView already cancels its fetches.
        }
    }

    private static string BuildResourceFetchScript(
        string controllerKey,
        string gameId,
        PublisherRoleBinding? savedRole)
    {
        var contract = PublisherAccountCatalog.GetResourceFetchContract(gameId);
        var hsrSignerScript = gameId == "hsr"
            ? """
          // HSR_DS_SIGNER_START
          const HSR_DS_SALT = '6s25p5ox5y14umn1p61aqyyvbvvl3lrt';
          const HSR_DS_RANDOM_LENGTH = 6;
          const HSR_DS_ALPHABET = 'abcdefghijklmnopqrstuvwxyz';
          const HSR_MD5_SHIFTS = Object.freeze([
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
          ]);
          const HSR_MD5_CONSTANTS = Object.freeze([
            0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
            0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
            0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
            0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
            0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
            0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
            0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
            0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
            0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
            0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
            0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
            0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
            0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
            0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
            0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
            0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
          ]);

          function hsrMd5Ascii(value) {
            if (typeof value !== 'string'
              || value.length === 0
              || value.length > 128
              || /[^\x20-\x7e]/.test(value))
              throw INVALID;

            const paddedLength = Math.ceil((value.length + 9) / 64) * 64;
            const bytes = new Uint8Array(paddedLength);
            try {
              for (let index = 0; index < value.length; index += 1) {
                bytes[index] = value.charCodeAt(index);
              }
              bytes[value.length] = 0x80;
              const view = new DataView(bytes.buffer);
              view.setUint32(paddedLength - 8, value.length * 8, true);
              view.setUint32(paddedLength - 4, 0, true);

              let stateA = 0x67452301;
              let stateB = 0xefcdab89;
              let stateC = 0x98badcfe;
              let stateD = 0x10325476;
              for (let offset = 0; offset < paddedLength; offset += 64) {
                let a = stateA;
                let b = stateB;
                let c = stateC;
                let d = stateD;
                for (let round = 0; round < 64; round += 1) {
                  let mixed;
                  let word;
                  if (round < 16) {
                    mixed = (b & c) | (~b & d);
                    word = round;
                  } else if (round < 32) {
                    mixed = (d & b) | (~d & c);
                    word = (5 * round + 1) % 16;
                  } else if (round < 48) {
                    mixed = b ^ c ^ d;
                    word = (3 * round + 5) % 16;
                  } else {
                    mixed = c ^ (b | ~d);
                    word = (7 * round) % 16;
                  }
                  const sum = (
                    a
                    + mixed
                    + HSR_MD5_CONSTANTS[round]
                    + view.getUint32(offset + word * 4, true)) >>> 0;
                  const shift = HSR_MD5_SHIFTS[round];
                  const rotated = ((sum << shift) | (sum >>> (32 - shift))) >>> 0;
                  const previousD = d;
                  d = c;
                  c = b;
                  b = (b + rotated) >>> 0;
                  a = previousD;
                }
                stateA = (stateA + a) >>> 0;
                stateB = (stateB + b) >>> 0;
                stateC = (stateC + c) >>> 0;
                stateD = (stateD + d) >>> 0;
              }

              let digest = '';
              for (const word of [stateA, stateB, stateC, stateD]) {
                for (let byte = 0; byte < 4; byte += 1) {
                  digest += ((word >>> (byte * 8)) & 0xff)
                    .toString(16)
                    .padStart(2, '0');
                }
              }
              return digest;
            } finally {
              bytes.fill(0);
            }
          }

          function hsrRandom() {
            if (!globalThis.crypto
              || typeof globalThis.crypto.getRandomValues !== 'function')
              throw INVALID;
            const sample = new Uint8Array(1);
            const characters = [];
            try {
              while (characters.length < HSR_DS_RANDOM_LENGTH) {
                globalThis.crypto.getRandomValues(sample);
                if (sample[0] >= 234) continue;
                characters.push(HSR_DS_ALPHABET[sample[0] % HSR_DS_ALPHABET.length]);
              }
              return characters.join('');
            } finally {
              sample.fill(0);
              characters.length = 0;
            }
          }

          function hsrNoteHeaders() {
            if (hsrMd5Ascii(
              'salt=6s25p5ox5y14umn1p61aqyyvbvvl3lrt&t=1700000000&r=abcdef')
              !== '52ac4768378434146675f980be7d092a')
              throw INVALID;
            const timestamp = Math.floor(Date.now() / 1000);
            if (!Number.isSafeInteger(timestamp)
              || timestamp < 1600000000
              || timestamp > 4102444800)
              throw INVALID;
            const random = hsrRandom();
            if (random.length !== HSR_DS_RANDOM_LENGTH
              || !/^[a-z]{6}$/.test(random))
              throw INVALID;
            const material = 'salt=' + HSR_DS_SALT + '&t=' + timestamp + '&r=' + random;
            const signature = hsrMd5Ascii(material);
            if (!/^[a-f0-9]{32}$/.test(signature)) throw INVALID;
            const ds = timestamp + ',' + random + ',' + signature;
            if (!/^[0-9]{10},[a-z]{6},[a-f0-9]{32}$/.test(ds)) throw INVALID;
            return Object.freeze({
              'x-rpc-client_type': '5',
              'x-rpc-app_version': '1.5.0',
              'x-rpc-language': 'en-us',
              DS: timestamp + ',' + random + ',' + signature,
            });
          }
          // HSR_DS_SIGNER_END
          """
            : string.Empty;
        var noteRequestScript = gameId == "hsr"
            ? """
            let noteHeaders;
            try {
              noteHeaders = hsrNoteHeaders();
            } catch {
              throw SIGNATURE_REJECTED;
            }
            await request(noteUrl, false, noteHeaders);
            """
            : "await request(noteUrl);";
        return $$"""
        (() => {
          const CONTROLLER_KEY = {{JsonSerializer.Serialize(controllerKey)}};
          const ROLE_ENDPOINT = {{JsonSerializer.Serialize(contract.RoleDiscoveryEndpoint.AbsoluteUri)}};
          const NOTE_ENDPOINT = {{JsonSerializer.Serialize(contract.NoteEndpoint.AbsoluteUri)}};
          const GAME_BIZ = {{JsonSerializer.Serialize(contract.GameBusiness)}};
          const REGIONS = Object.freeze({{JsonSerializer.Serialize(contract.Regions)}});
          const SAVED_ROLE_REGION = {{JsonSerializer.Serialize(savedRole?.Server ?? string.Empty)}};
          const SAVED_ROLE_UID = {{JsonSerializer.Serialize(savedRole?.RoleId ?? string.Empty)}};
          const HAS_SAVED_ROLE = SAVED_ROLE_REGION !== '' && SAVED_ROLE_UID !== '';
          const MAX_ROLE_RESPONSE_BYTES = 65536;
          const MAX_ROLE_COUNT = 8;
          const MAX_NICKNAME_UTF8_BYTES = 64;
          const MAX_NICKNAME_SCALARS = 32;
          const REQUEST_TIMEOUT = 6000;
          const OPERATION_TIMEOUT = 10000;
          const INVALID = Symbol('invalid');
          const LOGIN = Symbol('login');
          const SIGNATURE_REJECTED = Symbol('signature-rejected');
          const BROWSER_REQUEST_BLOCKED = Symbol('request-blocked');
          const OPERATION_TIMED_OUT = Symbol('timed-out');
        {{hsrSignerScript}}
          if (Object.hasOwn(window, CONTROLLER_KEY)) return 'busy';

          const operationController = new AbortController();
          const operationState = {
            status: 'running',
            roles: [],
            abort: () => operationController.abort(),
          };
          Object.defineProperty(window, CONTROLLER_KEY, {
            configurable: true,
            enumerable: false,
            writable: false,
            value: operationState,
          });

          function plain(value) {
            if (value === null || typeof value !== 'object' || Array.isArray(value)) return false;
            const prototype = Object.getPrototypeOf(value);
            return prototype === Object.prototype || prototype === null;
          }

          function validUid(value) {
            return typeof value === 'string' && /^[1-9][0-9]{0,19}$/.test(value);
          }

          function validRegion(value) {
            if (typeof value !== 'string') return false;
            for (const region of REGIONS) {
              if (region === value) return true;
            }
            return false;
          }

          function nickname(value) {
            if (typeof value !== 'string'
              || value.length === 0
              || Array.from(value).length > MAX_NICKNAME_SCALARS
              || new TextEncoder().encode(value).byteLength > MAX_NICKNAME_UTF8_BYTES
              || /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(value))
              return null;
            return value;
          }

          async function boundedJson(response) {
            if (!response.body || typeof response.body.getReader !== 'function') throw INVALID;
            const reader = response.body.getReader();
            const decoder = new TextDecoder('utf-8', { fatal: true });
            let bytes = 0;
            let text = '';
            try {
              while (true) {
                const part = await reader.read();
                if (!plain(part) || typeof part.done !== 'boolean') throw INVALID;
                if (part.done) break;
                if (!(part.value instanceof Uint8Array)) throw INVALID;
                bytes += part.value.byteLength;
                if (bytes > MAX_ROLE_RESPONSE_BYTES) {
                  part.value.fill(0);
                  await reader.cancel();
                  throw INVALID;
                }
                try {
                  text += decoder.decode(part.value, { stream: true });
                } finally {
                  part.value.fill(0);
                }
              }
              text += decoder.decode();
              try {
                return JSON.parse(text);
              } catch {
                throw INVALID;
              }
            } finally {
              text = '';
              if (typeof reader.releaseLock === 'function') reader.releaseLock();
            }
          }

          async function request(url, roleDiscovery = false, noteHeaders = null) {
            const expected = url.toString();
            const requestController = new AbortController();
            const abort = () => requestController.abort();
            operationController.signal.addEventListener('abort', abort, { once: true });
            try {
              if (operationController.signal.aborted) {
                requestController.abort();
                throw INVALID;
              }
              let requestTimedOut = false;
              const timeout = setTimeout(
                () => {
                  requestTimedOut = true;
                  requestController.abort();
                },
                REQUEST_TIMEOUT);
              try {
                let response;
                try {
                  response = await fetch(expected, {
                    method: 'GET',
                    credentials: 'include',
                    redirect: 'error',
                    cache: 'no-store',
                    referrerPolicy: 'no-referrer',
                    ...(roleDiscovery
                      ? { headers: { 'x-rpc-language': 'en' } }
                      : noteHeaders === null
                        ? {}
                        : { headers: noteHeaders }),
                    signal: requestController.signal,
                  });
                } catch {
                  if (operationController.signal.aborted) throw INVALID;
                  if (requestTimedOut) throw OPERATION_TIMED_OUT;
                  if (requestController.signal.aborted) throw INVALID;
                  throw BROWSER_REQUEST_BLOCKED;
                }
                if (!response || response.url !== expected) throw INVALID;
                return response;
              } finally {
                clearTimeout(timeout);
              }
            } finally {
              operationController.signal.removeEventListener('abort', abort);
            }
          }

          async function discover(requestedRegion) {
            const roleUrl = new URL(ROLE_ENDPOINT);
            roleUrl.searchParams.set('game_biz', GAME_BIZ);
            roleUrl.searchParams.set('region', requestedRegion);
            const response = await request(roleUrl, true);
            if (response.status === 401 || response.status === 403) throw LOGIN;
            const contentType = response.headers && response.headers.get('content-type');
            if (response.status !== 200
              || typeof contentType !== 'string'
              || contentType.split(';', 1)[0].trim().toLowerCase() !== 'application/json')
              throw INVALID;

            let roleResult = await boundedJson(response);
            try {
              if (!plain(roleResult)
                || !Object.hasOwn(roleResult, 'retcode')
                || !Number.isSafeInteger(roleResult.retcode))
                throw INVALID;
              if (roleResult.retcode === -100) throw LOGIN;
              if (roleResult.retcode !== 0
                || !Object.hasOwn(roleResult, 'data')
                || !plain(roleResult.data)
                || !Object.hasOwn(roleResult.data, 'list')
                || !Array.isArray(roleResult.data.list)
                || roleResult.data.list.length > MAX_ROLE_COUNT)
                throw INVALID;
              const found = [];
              for (const row of roleResult.data.list) {
                if (!plain(row)
                  || row.game_biz !== GAME_BIZ
                  || row.region !== requestedRegion
                  || !validUid(row.game_uid))
                  throw INVALID;
                found.push({
                  region: row.region,
                  uid: row.game_uid,
                  nickname: Object.hasOwn(row, 'nickname')
                    ? nickname(row.nickname)
                    : null,
                });
              }
              return found;
            } finally {
              roleResult = null;
            }
          }

          async function requestNote(region, uid) {
            if (!validRegion(region) || !validUid(uid)) throw INVALID;
            const noteUrl = new URL(NOTE_ENDPOINT);
            noteUrl.searchParams.set('role_id', uid);
            noteUrl.searchParams.set('server', region);
            {{noteRequestScript}}
          }

          let operationTimedOut = false;
          const operationTimeout = setTimeout(
            () => {
              operationTimedOut = true;
              operationController.abort();
            },
            OPERATION_TIMEOUT);
          void (async () => {
            let discovered = [];
            const roles = [];
            const seen = new Set();
            try {
              if (HAS_SAVED_ROLE) {
                await requestNote(SAVED_ROLE_REGION, SAVED_ROLE_UID);
                operationState.status = 'done';
                return;
              }

              discovered = await Promise.all(
                REGIONS.map(requestedRegion => discover(requestedRegion)));
              for (const group of discovered) {
                for (const role of group) {
                  const key = role.region + ':' + role.uid;
                  if (seen.has(key)) throw INVALID;
                  seen.add(key);
                  roles.push(role);
                  if (roles.length > MAX_ROLE_COUNT) throw INVALID;
                }
              }
              if (roles.length === 0) return;
              for (const role of roles) {
                await requestNote(role.region, role.uid);
              }
              operationState.roles = roles.map(role => Object.freeze({
                region: role.region,
                uid: role.uid,
                nickname: role.nickname,
              }));
              operationState.status = 'done';
            } catch (reason) {
              operationState.roles.length = 0;
              operationState.status = reason === LOGIN
                ? 'login'
                : reason === SIGNATURE_REJECTED
                  ? 'signature-rejected'
                  : reason === BROWSER_REQUEST_BLOCKED
                    ? 'request-blocked'
                    : reason === OPERATION_TIMED_OUT
                      ? 'timed-out'
                      : operationTimedOut
                      ? 'timed-out'
                        : operationController.signal.aborted
                          ? 'canceled'
                          : 'invalid';
            } finally {
              if (operationState.status === 'running') operationState.status = 'no-roles';
              for (const group of discovered) {
                if (Array.isArray(group)) group.length = 0;
              }
              discovered.length = 0;
              roles.length = 0;
              seen.clear();
              clearTimeout(operationTimeout);
            }
          })();
          return 'started';
        })()
        """;
    }

    private static string BuildHsrAchievementExportScript(
        string resultKey,
        PublisherRoleBinding? role) =>
        $$"""
        (() => {
          const RESULT_KEY = {{JsonSerializer.Serialize(resultKey)}};
          const SAVED_ROLE_REGION = {{JsonSerializer.Serialize(role?.Server ?? string.Empty)}};
          const SAVED_ROLE_UID = {{JsonSerializer.Serialize(role?.RoleId ?? string.Empty)}};
          Object.defineProperty(window, RESULT_KEY, {
            configurable: true,
            enumerable: false,
            writable: true,
            value: null,
          });
          (async () => {
          const ROLES = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
          const FALLBACK_LOGIN = 'https://sg-public-api.hoyolab.com/common/badge/v1/login/info';
          const FALLBACK_LIST = 'https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list';
          const REGIONS = [
            'prod_official_usa',
            'prod_official_eur',
            'prod_official_asia',
            'prod_official_cht',
          ];
          const MAX_ROLES = 65536;
          const MAX_ROWS = 10000;
          const TIMEOUT = 12000;

          function plain(value) {
            if (value === null || typeof value !== 'object' || Array.isArray(value)) return false;
            const prototype = Object.getPrototypeOf(value);
            return prototype === null || Object.getPrototypeOf(prototype) === null;
          }

          function data(envelope, stage) {
            if (!plain(envelope)
              || !Object.hasOwn(envelope, 'retcode')
              || typeof envelope.retcode !== 'number')
              throw new Error(stage + '-envelope');
            if (envelope.retcode !== 0) {
              if (stage === 'login'
                && Number.isSafeInteger(envelope.retcode)
                && envelope.retcode >= -9999999
                && envelope.retcode <= 9999999)
                throw new Error('login-retcode:' + String(envelope.retcode));
              if (stage === 'list'
                && Number.isSafeInteger(envelope.retcode)
                && envelope.retcode >= -9999999
                && envelope.retcode <= 9999999)
                throw new Error('list-retcode:' + String(envelope.retcode));
              throw new Error(stage + '-retcode');
            }
            if (!Object.hasOwn(envelope, 'data') || !plain(envelope.data))
              throw new Error(stage + '-data');
            return envelope.data;
          }

          async function boundedJson(response, maximum, stage) {
            if (!response.body || typeof response.body.getReader !== 'function') throw new Error(stage + '-response');
            const reader = response.body.getReader();
            const decoder = new TextDecoder('utf-8', { fatal: true });
            let bytes = 0;
            let text = '';
            try {
              while (true) {
                const part = await reader.read();
                if (!plain(part) || typeof part.done !== 'boolean') throw new Error(stage + '-response');
                if (part.done) break;
                if (!(part.value instanceof Uint8Array)) throw new Error(stage + '-response');
                bytes += part.value.byteLength;
                if (bytes > maximum) {
                  await reader.cancel();
                  throw new Error(stage + '-response');
                }
                text += decoder.decode(part.value, { stream: true });
              }
              text += decoder.decode();
              try {
                return JSON.parse(text);
              } catch {
                throw new Error(stage + '-response');
              }
            } finally {
              if (typeof reader.releaseLock === 'function') reader.releaseLock();
            }
          }

          async function request(url, maximum, stage, headers = undefined) {
            const expected = url.toString();
            const controller = new AbortController();
            const timeout = setTimeout(() => controller.abort(), TIMEOUT);
            try {
              const response = await fetch(expected, {
                method: 'GET',
                credentials: 'include',
                redirect: 'error',
                cache: 'no-store',
                referrerPolicy: 'no-referrer',
                headers: headers,
                signal: controller.signal,
              });
              const contentType = response && response.headers && response.headers.get('content-type');
              if (!response
                || response.status !== 200
                || response.url !== expected
                || typeof contentType !== 'string'
                || contentType.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
                throw new Error(response && (response.status === 401 || response.status === 403)
                  ? 'login-required'
                  : stage + '-response');
              }
              return data(await boundedJson(response, maximum, stage), stage);
            } finally {
              clearTimeout(timeout);
            }
          }

          let stage = 'role';
          try {
            const roles = [];
            const seenRoles = new Set();
            for (const requestedRegion of REGIONS) {
              const roleUrl = new URL(ROLES);
              roleUrl.searchParams.set('game_biz', 'hkrpg_global');
              roleUrl.searchParams.set('region', requestedRegion);
              let roleResult;
              try {
                roleResult = await request(
                  roleUrl,
                  MAX_ROLES,
                  'role',
                  { 'x-rpc-language': 'en' });
              } catch (error) {
                if (error && (error.name === 'AbortError'
                  || error.message === 'login-required'
                  || error.message === 'role-response'
                  || error.message === 'role-envelope'
                  || error.message === 'role-retcode'
                  || error.message === 'role-data')) throw error;
                throw new Error('role-request');
              }
              const roleRows = Object.hasOwn(roleResult, 'list') ? roleResult.list : null;
              if (!Array.isArray(roleRows) || roleRows.length > 8) throw new Error('role-shape');
              for (const row of roleRows) {
                if (!plain(row)
                  || row.game_biz !== 'hkrpg_global'
                  || row.region !== requestedRegion
                  || typeof row.game_uid !== 'string'
                  || !/^[1-9][0-9]{0,19}$/.test(row.game_uid)) throw new Error('role-row');
                const key = row.region + ':' + row.game_uid;
                if (seenRoles.has(key)) throw new Error('role-duplicate');
                seenRoles.add(key);
                roles.push({ region: row.region, uid: row.game_uid });
              }
            }
            if (roles.length === 0) throw new Error('role-none');

            const hasSavedRole = SAVED_ROLE_REGION !== '' || SAVED_ROLE_UID !== '';
            const selected = hasSavedRole
              ? roles.filter(item =>
                  item.region === SAVED_ROLE_REGION
                  && item.uid === SAVED_ROLE_UID)
              : roles;
            if (selected.length !== 1)
              throw new Error(hasSavedRole ? 'role-changed' : 'role-multiple');
            const region = selected[0].region;
            const uid = selected[0].uid;
            const provenRole = Object.freeze({
              game_uid: uid,
              region: region,
            });

            stage = 'session';
            const chunks = window.webpackChunkcultivation_tool;
            if (!Array.isArray(chunks)) throw new Error('session-chunks');
            let webpackRequire;
            const probeChunk = 1000000000 + Math.floor(Math.random() * 100000000);
            chunks.push([[probeChunk], {}, loader => { webpackRequire = loader; }]);
            if (!webpackRequire || typeof webpackRequire.e !== 'function')
              throw new Error('session-require');
            let Vue;
            let publisherSession;
            let roleUtil;
            const sessionDeadline = Date.now() + 8000;
            while (Date.now() < sessionDeadline) {
              try {
                Vue = typeof window.Vue === 'function'
                  ? window.Vue
                  : null;
                if (!Vue) {
                  const vueModule = webpackRequire(74061);
                  Vue = typeof vueModule === 'function'
                    ? vueModule
                    : vueModule && typeof vueModule.default === 'function'
                      ? vueModule.default
                      : null;
                  if (!Vue && typeof webpackRequire.n === 'function') {
                    const vueGetter = webpackRequire.n(vueModule);
                    const normalizedVue = typeof vueGetter === 'function'
                      ? vueGetter()
                      : null;
                    Vue = typeof normalizedVue === 'function'
                      ? normalizedVue
                      : null;
                  }
                }
              } catch {
                throw new Error('session-vue');
              }
              if (!Vue || !Vue.prototype) throw new Error('session-vue');
              publisherSession = Vue
                && Vue.prototype
                && Vue.prototype.$session;
              roleUtil = Vue
                && Vue.prototype
                && Vue.prototype.$accountRoleUtil;
              if (publisherSession
                && typeof publisherSession.init === 'function'
                && typeof publisherSession.recheck === 'function'
                && typeof publisherSession.initGameRole === 'function'
                && roleUtil
                && typeof roleUtil.setInitOptions === 'function'
                && typeof roleUtil.initGameRole === 'function')
                break;
              await new Promise(resolve => setTimeout(resolve, 100));
            }
            if (!publisherSession
              || typeof publisherSession.init !== 'function'
              || typeof publisherSession.recheck !== 'function'
              || typeof publisherSession.initGameRole !== 'function')
              throw new Error('session-missing');
            if (!roleUtil
              || typeof roleUtil.setInitOptions !== 'function'
              || typeof roleUtil.initGameRole !== 'function')
              throw new Error('session-role-setter');
            try {
              // The current official cultivation page defaults its account
              // helper to the retired cookie-token flow. Nyx's connected
              // profile is an official LToken session, which the same current
              // helper explicitly supports. Select that fixed official mode;
              // no token value is read, copied, synthesized, or converted.
              roleUtil.setInitOptions({ tokenType: 'ltoken' });
              await publisherSession.recheck();
              await publisherSession.init();
            } catch (error) {
              if (error && error.name === 'AbortError') throw error;
              throw new Error('session-account');
            }
            const publisherState = publisherSession.state;
            if (!publisherState
              || typeof publisherState !== 'object'
              || Array.isArray(publisherState))
              throw new Error('session-account');
            let publisherRole = publisherState.role;
            if (!publisherRole
              || String(publisherRole.game_uid || '') !== uid
              || publisherRole.region !== region) {
              try {
                await publisherSession.initGameRole();
              } catch (error) {
                if (error && error.name === 'AbortError') throw error;
                // Some valid LToken sessions fail the official helper's later
                // event-login detail request. Continue to the exact bounded
                // role-list fallback below instead of rejecting the session.
              }
              publisherRole = publisherSession.state
                && publisherSession.state.role;
            }
            if (!publisherRole
              || String(publisherRole.game_uid || '') !== uid
              || publisherRole.region !== region) {
              let explicitlySelectedRole;
              try {
                const selectedRole = await roleUtil.initGameRole({
                  chooseRoleExplicitly: list => {
                    if (!Array.isArray(list) || list.length > 8)
                      throw new Error('session-role');
                    const matches = list.filter(item =>
                      item
                      && typeof item === 'object'
                      && !Array.isArray(item)
                      && String(item.game_uid || '') === uid
                      && item.region === region);
                    if (matches.length !== 1)
                      throw new Error('session-role');
                    explicitlySelectedRole = matches[0];
                    return explicitlySelectedRole;
                  },
                });
                publisherRole = selectedRole
                  || (publisherSession.state && publisherSession.state.role);
              } catch (error) {
                if (error && error.name === 'AbortError') throw error;
                // The current official helper can prove the exact role and then
                // reject its separate event-login detail call for an otherwise
                // valid LToken session. The exact role-list result remains the
                // only value retained; the list request below is still bounded
                // to that role and its response is independently validated.
              }
              // The exact role was already proven above by the separately
              // bounded LToken role response. The official helper is advisory
              // here; it is not allowed to replace or broaden that role.
              publisherRole = publisherRole || explicitlySelectedRole || provenRole;
            }
            if (publisherRole && publisherRole.region !== region)
              throw new Error('session-role-region');
            if (publisherRole
              && String(publisherRole.game_uid || '') !== uid)
              throw new Error('session-role-uid');

            stage = 'list';
            let result;
            try {
              await webpackRequire.e(564);
              const api = webpackRequire(99362);
              if (!plain(api) || typeof api.J !== 'function')
                throw new Error('list-client');
              result = await api.J('/achievement/list', {
                params: {
                  game_biz: 'hkrpg_global',
                  badge_region: region,
                  badge_uid: String(uid),
                  show_hide: false,
                  need_all: true,
                },
              });
              if (!plain(result)) throw new Error('list-data');
            } catch (error) {
              const retcode = error
                && error.data
                && error.data.retcode;
              if (retcode === -100) {
                // This exact first-party endpoint is the compatibility route
                // used by Nyx's previously accepted HoYoLAB exports. The
                // current cultivation client is still tried first. Only its
                // explicit login rejection enables this bounded retry. The
                // prior accepted flow first initializes the exact badge
                // session, then binds that response back to the proven role.
                const fallbackLoginUrl = new URL(FALLBACK_LOGIN);
                fallbackLoginUrl.searchParams.set('game_biz', 'hkrpg_global');
                fallbackLoginUrl.searchParams.set('lang', 'en-us');
                fallbackLoginUrl.searchParams.set('ts', String(Date.now()));
                let fallbackLogin;
                try {
                  fallbackLogin = await request(fallbackLoginUrl, 16384, 'login');
                } catch (fallbackLoginError) {
                  if (fallbackLoginError && (fallbackLoginError.name === 'AbortError'
                    || fallbackLoginError.message === 'login-required'
                    || fallbackLoginError.message === 'login-response'
                    || fallbackLoginError.message === 'login-envelope'
                    || fallbackLoginError.message === 'login-retcode'
                    || fallbackLoginError.message === 'login-data'
                    || /^login-retcode:-?[0-9]{1,7}$/.test(fallbackLoginError.message || '')))
                    throw fallbackLoginError;
                  throw new Error('login-request');
                }
                const fallbackRegion = Object.hasOwn(fallbackLogin, 'region')
                  ? fallbackLogin.region
                  : null;
                const fallbackUid = Object.hasOwn(fallbackLogin, 'game_uid')
                  ? fallbackLogin.game_uid
                  : null;
                if (fallbackRegion !== region
                  || String(fallbackUid || '') !== uid)
                  throw new Error('login-binding');
                const fallbackUrl = new URL(FALLBACK_LIST);
                fallbackUrl.searchParams.set('game', 'hkrpg');
                fallbackUrl.searchParams.set('game_biz', 'hkrpg_global');
                fallbackUrl.searchParams.set('badge_region', region);
                fallbackUrl.searchParams.set('badge_uid', String(uid));
                fallbackUrl.searchParams.set('show_hide', 'false');
                fallbackUrl.searchParams.set('need_all', 'true');
                try {
                  result = await request(fallbackUrl, 2097152, 'list');
                } catch (fallbackError) {
                  if (fallbackError && (fallbackError.name === 'AbortError'
                    || fallbackError.message === 'login-required'
                    || fallbackError.message === 'list-response'
                    || fallbackError.message === 'list-envelope'
                    || fallbackError.message === 'list-retcode'
                    || fallbackError.message === 'list-data'
                    || /^list-retcode:-?[0-9]{1,7}$/.test(fallbackError.message || '')))
                    throw fallbackError;
                  throw new Error('list-request');
                }
              } else if (Number.isSafeInteger(retcode)
                && retcode >= -9999999
                && retcode <= 9999999) {
                throw new Error('list-retcode:' + String(retcode));
              } else {
                if (error && (error.name === 'AbortError'
                  || error.message === 'login-required'
                  || error.message === 'list-client'
                  || error.message === 'list-response'
                  || error.message === 'list-envelope'
                  || error.message === 'list-retcode'
                  || error.message === 'list-data')) throw error;
                throw new Error('list-request');
              }
            }
            if (!plain(result)) throw new Error('list-data');
            const rows = Object.hasOwn(result, 'achievement_list')
              ? result.achievement_list
              : null;
            if (!Array.isArray(rows) || rows.length > MAX_ROWS) throw new Error('list-shape');

            const seen = new Set();
            const completed = [];
            let rowCount = 0;
            const visit = row => {
              rowCount += 1;
              if (rowCount > MAX_ROWS) throw new Error('list-shape');
              if (!plain(row)
                || !Object.hasOwn(row, 'id')
                || !Object.hasOwn(row, 'finished')
                || typeof row.finished !== 'boolean')
                throw new Error('list-row');
              const rawId = row.id;
              const id = typeof rawId === 'number'
                ? rawId
                : (typeof rawId === 'string' && /^[1-9][0-9]{0,15}$/.test(rawId)
                  ? Number(rawId)
                  : NaN);
              if (!Number.isSafeInteger(id) || id <= 0) throw new Error('list-row');
              if (seen.has(id)) throw new Error('list-duplicate');
              seen.add(id);
              if (row.finished) completed.push(id);
              if (Object.hasOwn(row, 'sub_achievements')) {
                if (!Array.isArray(row.sub_achievements))
                  throw new Error('list-row');
                for (const child of row.sub_achievements) visit(child);
              }
            };
            for (const row of rows) {
              visit(row);
            }
            completed.sort((left, right) => left - right);
            return JSON.stringify({
              state: 'ok',
              ids: completed,
              region: region,
              uid: String(uid),
            });
          } catch (error) {
            const reviewed = new Set([
              'login-required',
              'login-request',
              'login-response',
              'login-envelope',
              'login-retcode',
              'login-data',
              'login-binding',
              'role-request',
              'role-response',
              'role-envelope',
              'role-retcode',
              'role-data',
              'role-shape',
              'role-row',
              'role-duplicate',
              'role-none',
              'role-multiple',
              'role-changed',
              'session-chunks',
              'session-require',
              'session-vue',
              'session-missing',
              'session-account',
              'session-role',
              'session-role-setter',
              'session-role-bind',
              'session-role-region',
              'session-role-uid',
              'list-request',
              'list-client',
              'list-response',
              'list-envelope',
              'list-retcode',
              'list-data',
              'list-shape',
              'list-row',
              'list-duplicate',
            ]);
            const message = error && typeof error.message === 'string' ? error.message : '';
            const state = error && error.name === 'AbortError'
              ? 'timed-out'
              : (reviewed.has(message)
                  || /^login-retcode:-?[0-9]{1,7}$/.test(message)
                  || /^list-retcode:-?[0-9]{1,7}$/.test(message)
                ? message
                : stage + '-processing');
            return JSON.stringify({ state: state, ids: [], region: '', uid: '' });
          }
          })().then(
            result => { window[RESULT_KEY] = result; },
            () => {
              window[RESULT_KEY] = JSON.stringify({
                state: 'invalid',
                ids: [],
                region: '',
                uid: '',
              });
            });
          return 'started';
        })()
        """;

    private async Task NavigateAsync(
        Uri uri,
        CancellationToken cancellationToken,
        Action? navigationRequested = null)
    {
        var outcome = await NavigateWithOutcomeAsync(
            uri,
            cancellationToken,
            navigationRequested);
        if (outcome is not PublisherVisibleConnectNavigationOutcome.Succeeded)
            throw new InvalidOperationException("The official page did not load.");
    }

    private void ShowWebView2RuntimeRequired()
    {
        webView2RuntimeUnavailable = true;
        Browser.Visibility = Visibility.Collapsed;
        DoneButton.Visibility = Visibility.Collapsed;
        RetryButton.Content = "INSTALL";
        AutomationProperties.SetName(RetryButton, "Install Microsoft WebView2");
        RetryButton.Visibility = Visibility.Visible;
        RetryButton.IsEnabled = true;
        StatusText.Text = "HoYoLAB needs Microsoft WebView2. Choose Install, then reopen this window.";
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private async Task<PublisherVisibleConnectNavigationOutcome> NavigateWithOutcomeAsync(
        Uri uri,
        CancellationToken cancellationToken,
        Action? navigationRequested = null)
    {
        var outcome = PublisherVisibleConnectNavigationOutcome.BrowserFailure;
        await passwordNavigationGate.NavigateAsync(
            ClearPublisherBrowsingDataAsync,
            async operationCancellation =>
                outcome = await NavigateCoreWithOutcomeAsync(
                    uri,
                    operationCancellation,
                    navigationRequested),
            cancellationToken);
        return outcome;
    }

    private async Task<PublisherVisibleConnectNavigationOutcome> NavigateCoreWithOutcomeAsync(
        Uri uri,
        CancellationToken cancellationToken,
        Action? navigationRequested = null)
    {
        approvedTopLevelUri = uri;
        var core = Browser.CoreWebView2;
        if (core is null)
        {
            return PublisherVisibleConnectNavigationOutcome.BrowserFailure;
        }
        var correlation = new PublisherNavigationCompletionCorrelation(uri);
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
            correlation.TryObserveStarting(
                args.Uri,
                args.NavigationId,
                args.IsRedirected,
                args.Cancel);
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) =>
            correlation.TryObserveCompleted(
                args.NavigationId,
                ClassifyNavigationCompletion(args.IsSuccess, args.WebErrorStatus));
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            try { core.Stop(); } catch (Exception) { }
            try
            {
                core.Navigate(uri.AbsoluteUri);
                navigationRequested?.Invoke();
            }
            catch
            {
                return PublisherVisibleConnectNavigationOutcome.BrowserFailure;
            }

            try
            {
                return await correlation.WaitAsync(
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                return PublisherVisibleConnectNavigationOutcome.TimedOut;
            }
        }
        finally
        {
            PublisherNavigationHandlerCleanup.Remove(
                () => core.NavigationStarting -= Starting,
                () => core.NavigationCompleted -= Completed,
                () => Volatile.Read(ref browserCloseStarted) != 0);
        }
    }

    private static PublisherVisibleConnectNavigationOutcome ClassifyNavigationCompletion(
        bool isSuccess,
        CoreWebView2WebErrorStatus webErrorStatus)
    {
        if (isSuccess)
            return PublisherVisibleConnectNavigationOutcome.Succeeded;
        return webErrorStatus switch
        {
            CoreWebView2WebErrorStatus.OperationCanceled =>
                PublisherVisibleConnectNavigationOutcome.WebViewCanceled,
            CoreWebView2WebErrorStatus.Timeout =>
                PublisherVisibleConnectNavigationOutcome.TimedOut,
            CoreWebView2WebErrorStatus.UnexpectedError =>
                PublisherVisibleConnectNavigationOutcome.BrowserFailure,
            _ => PublisherVisibleConnectNavigationOutcome.NetworkFailure,
        };
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps
            || !target.IsDefaultPort
            || !string.IsNullOrEmpty(target.UserInfo))
        {
            args.Cancel = true;
            return;
        }

        if (purpose == PublisherSessionPurpose.Connect)
        {
            if (!IsAllowedConnectTopLevel(target)) args.Cancel = true;
            return;
        }

        if (approvedTopLevelUri is null
            || !string.Equals(
                PublisherAccountCatalog.NormalizeTopLevelUri(target),
                PublisherAccountCatalog.NormalizeTopLevelUri(approvedTopLevelUri),
                StringComparison.Ordinal))
            args.Cancel = true;
    }

    private void Core_WebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var authorized = TryAuthorizeWebResourceRequest(args);
        if (purpose == PublisherSessionPurpose.Achievements
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requestUri)
            && IsHsrAchievementListCandidate(requestUri))
        {
            var next = authorized
                ? string.Equals(args.Request.Method, "GET", StringComparison.Ordinal)
                    ? HsrAchievementListNetworkState.RequestAllowed
                    : HsrAchievementListNetworkState.PreflightAllowed
                : ClassifyBlockedHsrAchievementListRequest(
                    requestUri,
                    args.Request.Method);
            Interlocked.Exchange(ref hsrAchievementListNetworkState, (int)next);
        }
        if (!authorized)
            TryBlockWebResourceRequest(sender, args);
    }

    private bool TryAuthorizeWebResourceRequest(
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var context = MapResourceContext(args.ResourceContext);
        if (purpose == PublisherSessionPurpose.Resource
            && string.Equals(authorizedGameId, "hsr", StringComparison.Ordinal)
            && string.Equals(args.Request.Method, "OPTIONS", StringComparison.Ordinal)
            && args.Request.Content is not null)
            return false;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
            || authorizedGameId is null
            || !PublisherAccountCatalog.IsAllowedWebResourceRequest(
                provider,
                purpose,
                authorizedGameId,
                uri,
                args.Request.Method,
                context,
                claimWriteAuthority,
                requestBody: null,
                contentType: null))
            return false;

        var expectedAchievementRole = Volatile.Read(ref expectedHsrAchievementRole);
        if (purpose == PublisherSessionPurpose.Achievements
            && string.Equals(authorizedGameId, "hsr", StringComparison.Ordinal)
            && expectedAchievementRole is not null
            && IsHsrAchievementListCandidate(uri)
            && !PublisherAccountCatalog.IsExactHsrAchievementListRequestForRole(
                uri,
                args.Request.Method,
                expectedAchievementRole))
            return false;

        var capture = Volatile.Read(ref pendingResourceCapture);
        if (purpose == PublisherSessionPurpose.Resource
            && context is (PublisherWebResourceContext.XmlHttpRequest or PublisherWebResourceContext.Fetch)
            && string.Equals(args.Request.Method, "GET", StringComparison.Ordinal)
            && capture is not null
            && capture.Authority.Generation == Interlocked.Read(ref resourceGeneration)
            && PublisherAccountCatalog.TryGetResourceBinding(
                capture.Authority.GameId,
                uri,
                out var binding)
            && binding is not null)
            return capture.Authority.TryReserve(
                capture.Authority.Generation,
                capture.Authority.GameId,
                binding);
        return true;
    }

    private void TryBlockWebResourceRequest(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            BlockWebResourceRequest(sender, args);
        }
        catch
        {
            // The WebView can be disposed while a deferred request is in flight.
        }
    }

    private static void BlockWebResourceRequest(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args) =>
        args.Response = sender.Environment.CreateWebResourceResponse(
            null,
            403,
            "Blocked by publisher session policy",
            "Content-Type: text/plain; charset=utf-8");

    private static PublisherWebResourceContext MapResourceContext(
        CoreWebView2WebResourceContext context) => context switch
        {
            CoreWebView2WebResourceContext.Document => PublisherWebResourceContext.Document,
            CoreWebView2WebResourceContext.Stylesheet => PublisherWebResourceContext.Stylesheet,
            CoreWebView2WebResourceContext.Image => PublisherWebResourceContext.Image,
            CoreWebView2WebResourceContext.Media => PublisherWebResourceContext.Media,
            CoreWebView2WebResourceContext.Font => PublisherWebResourceContext.Font,
            CoreWebView2WebResourceContext.Script => PublisherWebResourceContext.Script,
            CoreWebView2WebResourceContext.XmlHttpRequest => PublisherWebResourceContext.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch => PublisherWebResourceContext.Fetch,
            _ => PublisherWebResourceContext.Other,
        };

    private static bool IsHsrAchievementListCandidate(Uri uri) =>
        (string.Equals(
                uri.Host,
                "sg-act-public-api.hoyolab.com",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                uri.Host,
                "sg-public-api.hoyolab.com",
                StringComparison.OrdinalIgnoreCase))
        && string.Equals(
            uri.AbsolutePath,
            "/event/rpgcultivate/achievement/list",
            StringComparison.Ordinal);

    private static HsrAchievementListNetworkState ClassifyBlockedHsrAchievementListRequest(
        Uri uri,
        string method)
    {
        if (method is not ("GET" or "OPTIONS"))
            return HsrAchievementListNetworkState.BlockedWrongMethod;

        var keys = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair =>
            {
                var separator = pair.IndexOf('=');
                return Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            })
            .ToArray();
        var expected = new HashSet<string>(
            ["game_biz", "badge_region", "badge_uid"],
            StringComparer.Ordinal);
        if (keys.Length < expected.Count
            || expected.Except(keys).Any())
            return HsrAchievementListNetworkState.BlockedMissingQuery;
        if (keys.Length > expected.Count
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            return HsrAchievementListNetworkState.BlockedExtraQuery;
        return HsrAchievementListNetworkState.BlockedQueryValue;
    }

    private void Core_WebResourceResponseReceived(
        CoreWebView2 sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (purpose == PublisherSessionPurpose.Achievements
            && string.Equals(args.Request.Method, "GET", StringComparison.Ordinal)
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var listUri)
            && IsHsrAchievementListCandidate(listUri))
        {
            var responseState = args.Response.StatusCode == 200
                && HasJsonContentType(args.Response.Headers.GetHeader("Content-Type"))
                ? HsrAchievementListNetworkState.ResponseAccepted
                : HsrAchievementListNetworkState.ResponseFailed;
            Interlocked.Exchange(
                ref hsrAchievementListNetworkState,
                (int)responseState);
        }

        var sessionProbe = Volatile.Read(ref pendingSessionProbe);
        if (sessionProbe is not null
            && sessionProbe.Generation == Interlocked.Read(ref sessionProbeGeneration)
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var sessionProbeUri)
            && PublisherAccountCatalog.IsExactSkportSessionProbeUri(sessionProbeUri, args.Request.Method)
            && sessionProbe.TryBegin())
        {
            _ = CompleteSessionProbeAsync(args, sessionProbe);
            return;
        }

        if (purpose == PublisherSessionPurpose.Connect
            && provider == "SKPORT"
            && authorizedGameId == "ae"
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var identityUri)
            && PublisherAccountCatalog.IsExactEndfieldAccountIdentityRequest(
                identityUri,
                args.Request.Method))
        {
            var endfieldIdentity = Volatile.Read(ref pendingEndfieldIdentityCapture);
            if (endfieldIdentity is null
                || endfieldIdentity.Generation != Interlocked.Read(ref endfieldIdentityGeneration))
            {
                _ = CompleteEndfieldIdentityCaptureAsync(args, null);
            }
            else if (endfieldIdentity.TryBegin())
            {
                _ = CompleteEndfieldIdentityCaptureAsync(args, endfieldIdentity);
            }
            return;
        }

        var checkInCapture = Volatile.Read(ref pendingCheckInCapture);
        if (checkInCapture is not null
            && checkInCapture.Generation == Interlocked.Read(ref checkInGeneration)
            && string.Equals(args.Request.Method, checkInCapture.Method, StringComparison.Ordinal)
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var checkInUri))
        {
            if (PublisherAccountCatalog.IsCheckInResponseEndpoint(
                    checkInCapture.GameId,
                    checkInUri,
                    checkInCapture.Method))
            {
                if (PublisherAccountCatalog.IsExactCheckInResponseUri(
                        checkInCapture.GameId,
                        checkInUri,
                        checkInCapture.Method,
                        checkInCapture.ExpectedBinding,
                        checkInCapture.AllowAccountWideStatus))
                {
                    if (checkInCapture.TryBegin())
                    {
                        _ = CompleteCheckInCaptureAsync(args, checkInCapture);
                        return;
                    }
                }
                else
                {
                    checkInCapture.MarkCandidateDiagnostic(
                        PublisherCheckInCaptureDiagnostic.EndpointQueryRejected);
                }
            }
        }

        var capture = Volatile.Read(ref pendingResourceCapture);
        if (capture is null
            || capture.Authority.Generation != Interlocked.Read(ref resourceGeneration)
            || !string.Equals(args.Request.Method, "GET", StringComparison.Ordinal)
            || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var responseUri)
            || !PublisherAccountCatalog.TryGetResourceBinding(
                capture.Authority.GameId,
                responseUri,
                out var binding)
            || binding is null
            || !capture.Authority.TryBeginResponse(capture.Authority.Generation, binding))
            return;

        _ = CompleteResourceCaptureAsync(args, capture, binding);
    }

    private static async Task CompleteSessionProbeAsync(
        CoreWebView2WebResourceResponseReceivedEventArgs args,
        SessionProbeCapture capture)
    {
        byte[]? body = null;
        try
        {
            var response = args.Response;
            if (response.StatusCode is 401 or 403)
            {
                capture.TryComplete(PublisherAccountCatalog.ClassifySkportSessionResponse(
                    response.StatusCode,
                    response.Headers.GetHeader("Content-Type"),
                    ReadOnlyMemory<byte>.Empty));
                return;
            }
            if (response.StatusCode != 200
                || !HasJsonContentType(response.Headers.GetHeader("Content-Type")))
            {
                capture.TryComplete(PublisherAccountCatalog.ClassifySkportSessionResponse(
                    response.StatusCode,
                    response.Headers.GetHeader("Content-Type"),
                    ReadOnlyMemory<byte>.Empty));
                return;
            }

            using var content = await response.GetContentAsync().AsTask(capture.CancellationToken);
            using var stream = content.AsStreamForRead();
            body = await ReadBoundedAsync(
                stream,
                PublisherAccountCatalog.MaximumResourceResponseBytes,
                capture.CancellationToken);
            capture.TryComplete(body is null
                ? PublisherSessionProof.NeedsReview
                : PublisherAccountCatalog.ClassifySkportSessionResponse(
                    response.StatusCode,
                    response.Headers.GetHeader("Content-Type"),
                    body));
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
        }
        catch (Exception)
        {
            capture.TryComplete(PublisherSessionProof.NeedsReview);
        }
        finally
        {
            if (body is not null) Array.Clear(body);
        }
    }

    private async Task CompleteEndfieldIdentityCaptureAsync(
        CoreWebView2WebResourceResponseReceivedEventArgs args,
        EndfieldIdentityCapture? capture)
    {
        byte[]? body = null;
        var cancellationToken = capture?.CancellationToken ?? lifetime.Token;
        try
        {
            var response = args.Response;
            if (response.StatusCode != 200
                || !HasJsonContentType(response.Headers.GetHeader("Content-Type")))
            {
                capture?.TryComplete(null);
                return;
            }
            using var content = await response.GetContentAsync().AsTask(cancellationToken);
            using var stream = content.AsStreamForRead();
            body = await ReadBoundedAsync(
                stream,
                PublisherAccountCatalog.MaximumResourceResponseBytes,
                cancellationToken);
            if (body is not null
                && PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(body, out var identity))
            {
                Volatile.Write(ref reviewedEndfieldIdentity, identity);
                capture?.TryComplete(identity);
            }
            else
            {
                capture?.TryComplete(null);
            }
        }
        catch (OperationCanceledException)
        {
            capture?.Cancel();
        }
        catch (Exception)
        {
            capture?.TryComplete(null);
        }
        finally
        {
            if (body is not null) Array.Clear(body);
        }
    }

    private static async Task CompleteCheckInCaptureAsync(
        CoreWebView2WebResourceResponseReceivedEventArgs args,
        CheckInCapture capture)
    {
        try
        {
            var response = args.Response;
            var contentType = response.Headers.GetHeader("Content-Type");
            if (response.StatusCode != 200
                || !HasJsonContentType(contentType))
            {
                capture.MarkSelectedResponseDiagnostic(
                    PublisherCheckInCaptureDiagnostic.InvalidStatusOrType);
                capture.TryComplete(PublisherAccountCatalog.ClassifyCheckInResponse(
                    response.StatusCode,
                    contentType,
                    capture.GameId,
                    capture.Method,
                    ReadOnlyMemory<byte>.Empty,
                    capture.ExpectedDate,
                    capture.ExpectedInstant));
                return;
            }

            using var content = await response.GetContentAsync().AsTask(capture.CancellationToken);
            using var stream = content.AsStreamForRead();
            var body = await ReadBoundedAsync(
                stream,
                PublisherAccountCatalog.MaximumResourceResponseBytes,
                capture.CancellationToken);
            if (body is null)
            {
                capture.MarkSelectedResponseDiagnostic(
                    PublisherCheckInCaptureDiagnostic.InvalidBody);
                capture.TryComplete(PublisherAccountCatalog.ClassifyCheckInResponse(
                    response.StatusCode,
                    contentType,
                    capture.GameId,
                    capture.Method,
                    ReadOnlyMemory<byte>.Empty,
                    capture.ExpectedDate,
                    capture.ExpectedInstant));
                return;
            }
            try
            {
                var proof = PublisherAccountCatalog.ClassifyCheckInResponse(
                    response.StatusCode,
                    contentType,
                    capture.GameId,
                    capture.Method,
                    body,
                    capture.ExpectedDate,
                    capture.ExpectedInstant);
                if (proof == PublisherCheckInProof.Invalid)
                    capture.MarkSelectedResponseDiagnostic(
                        PublisherCheckInCaptureDiagnostic.InvalidBody);
                capture.TryComplete(proof);
            }
            finally
            {
                Array.Clear(body);
            }
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
        }
        catch (Exception)
        {
            capture.MarkSelectedResponseDiagnostic(
                PublisherCheckInCaptureDiagnostic.InvalidBody);
            capture.TryComplete(PublisherCheckInProof.Invalid);
        }
    }

    private static async Task CompleteResourceCaptureAsync(
        CoreWebView2WebResourceResponseReceivedEventArgs args,
        PendingResourceCapture capture,
        PublisherRoleBinding binding)
    {
        var authority = capture.Authority;
        var generation = authority.Generation;
        try
        {
            var response = args.Response;
            if (response.StatusCode is 401 or 403)
            {
                authority.CompleteResponse(
                    generation,
                    binding,
                    PublisherResourceProof.LoginNeeded,
                    null);
                return;
            }
            if (response.StatusCode != 200
                || !HasJsonContentType(response.Headers.GetHeader("Content-Type")))
            {
                authority.CompleteResponse(
                    generation,
                    binding,
                    PublisherResourceProof.Invalid,
                    null,
                    SafeResourceFailureDiagnostic(
                        authority.GameId,
                        PublisherResourceCaptureDiagnostic.RequestRejected));
                return;
            }

            using var content = await response.GetContentAsync().AsTask(capture.CancellationToken);
            using var stream = content.AsStreamForRead();
            var body = await ReadBoundedAsync(
                stream,
                PublisherAccountCatalog.MaximumResourceResponseBytes,
                capture.CancellationToken);
            if (body is null)
            {
                authority.CompleteResponse(
                    generation,
                    binding,
                    PublisherResourceProof.Invalid,
                    null,
                    SafeResourceFailureDiagnostic(
                        authority.GameId,
                        PublisherResourceCaptureDiagnostic.BoundsRejected));
                return;
            }
            try
            {
                var proof = PublisherAccountCatalog.ParseResourceResponse(
                    authority.GameId,
                    body,
                    DateTimeOffset.UtcNow,
                    out var snapshot,
                    out var diagnostic);
                authority.CompleteResponse(
                    generation,
                    binding,
                    proof,
                    snapshot,
                    SafeResourceFailureDiagnostic(authority.GameId, diagnostic));
            }
            finally
            {
                Array.Clear(body);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            authority.CompleteResponse(
                generation,
                binding,
                PublisherResourceProof.Invalid,
                null,
                SafeResourceFailureDiagnostic(
                    authority.GameId,
                    PublisherResourceCaptureDiagnostic.RequestRejected));
        }
    }

    private static PublisherResourceCaptureDiagnostic SafeResourceFailureDiagnostic(
        string gameId,
        PublisherResourceCaptureDiagnostic diagnostic) =>
        gameId is "hsr" or "zzz"
            ? diagnostic
            : PublisherResourceCaptureDiagnostic.ResponseRejected;

    private static bool HasJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes + 1];
        try
        {
            var length = 0;
            while (length <= maximumBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0) return buffer.AsSpan(0, length).ToArray();
                length += read;
            }
            return null;
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private async void Core_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (authorizedGameId is not null
            && PublisherVisibleConnectNavigationPolicy.IsAllowedPopup(
                provider,
                purpose,
                authorizedGameId,
                args.Uri,
                args.IsUserInitiated))
        {
            using var deferral = args.GetDeferral();
            try
            {
                await OpenSocialLoginWindowAsync(sender.Environment, args);
            }
            catch
            {
                CloseSocialLoginWindow();
            }
            return;
        }

        if (purpose != PublisherSessionPurpose.Connect
            || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target))
            return;
        if (IsAllowedConnectTopLevel(target))
            sender.Navigate(target.AbsoluteUri);
    }

    private async Task OpenSocialLoginWindowAsync(
        CoreWebView2Environment environment,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        CloseSocialLoginWindow();
        var popupWindow = new Window { Title = "Sign in to SKPORT" };
        var popupBrowser = new Microsoft.UI.Xaml.Controls.WebView2();
        popupWindow.Content = popupBrowser;
        popupWindow.AppWindow.Resize(new SizeInt32(700, 700));
        await popupBrowser.EnsureCoreWebView2Async(environment);
        if (windowClosed || lifetime.IsCancellationRequested)
        {
            popupBrowser.Close();
            popupWindow.Close();
            return;
        }
        var core = popupBrowser.CoreWebView2
            ?? throw new InvalidOperationException("Social sign-in browser did not initialize.");

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = passwordSavingEnabled;
        core.NavigationStarting += Core_SocialLoginNavigationStarting;
        core.NewWindowRequested += Core_SocialLoginNewWindowRequested;
        core.DownloadStarting += Core_DownloadStarting;
        core.PermissionRequested += Core_PermissionRequested;
        core.WindowCloseRequested += Core_SocialLoginWindowCloseRequested;
        popupWindow.Closed += SocialLoginWindow_Closed;

        socialLoginWindow = popupWindow;
        socialLoginBrowser = popupBrowser;
        args.NewWindow = core;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += Core_SocialLoginWebResourceRequested;
        popupWindow.Activate();
    }

    private void Core_SocialLoginNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
            || !IsAllowedSocialLoginTopLevel(target))
            args.Cancel = true;
    }

    private void Core_SocialLoginWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (args.Request.Method is not ("GET" or "POST")
            || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var target)
            || !IsAllowedSocialLoginTopLevel(target))
            TryBlockWebResourceRequest(sender, args);
    }

    private static bool IsAllowedSocialLoginTopLevel(Uri target)
    {
        if (string.Equals(target.OriginalString, "about:blank", StringComparison.Ordinal))
            return true;
        if (!target.IsAbsoluteUri
            || target.Scheme != Uri.UriSchemeHttps
            || !target.IsDefaultPort
            || !string.IsNullOrEmpty(target.UserInfo)
            || !string.IsNullOrEmpty(target.Fragment)
            || target.Query.Length > 2048)
            return false;

        var host = target.Host;
        if (host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.facebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals("as.gryphline.com", StringComparison.OrdinalIgnoreCase))
            return target.AbsolutePath is "/third_party/v1/google_callback"
                or "/third_party/v1/facebook_callback"
                or "/third_party/v1/apple_callback";
        return host.Equals("game.skport.com", StringComparison.OrdinalIgnoreCase)
            && target.AbsolutePath == "/endfield/sign-in"
            && IsAllowedSocialLoginReturnQuery(target.Query);
    }

    private static bool IsAllowedSocialLoginReturnQuery(string query)
    {
        if (query.Length <= 1) return false;
        foreach (var parameter in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            var key = separator < 0 ? parameter : parameter[..separator];
            if (key is not ("tpa_action" or "tpa_channelId" or "tpa_channelToken" or "tpa_state"))
                return false;
        }
        return true;
    }

    private static void Core_SocialLoginNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void Core_SocialLoginWindowCloseRequested(object? sender, object args) =>
        socialLoginWindow?.Close();

    private void SocialLoginWindow_Closed(object sender, WindowEventArgs args) =>
        CloseSocialLoginWindow(closeWindow: false);

    private void CloseSocialLoginWindow(bool closeWindow = true)
    {
        var popupWindow = socialLoginWindow;
        var popupBrowser = socialLoginBrowser;
        socialLoginWindow = null;
        socialLoginBrowser = null;
        if (popupWindow is not null)
            popupWindow.Closed -= SocialLoginWindow_Closed;
        var core = popupBrowser?.CoreWebView2;
        if (core is not null)
        {
            core.NavigationStarting -= Core_SocialLoginNavigationStarting;
            core.WebResourceRequested -= Core_SocialLoginWebResourceRequested;
            core.NewWindowRequested -= Core_SocialLoginNewWindowRequested;
            core.DownloadStarting -= Core_DownloadStarting;
            core.PermissionRequested -= Core_PermissionRequested;
            core.WindowCloseRequested -= Core_SocialLoginWindowCloseRequested;
            try { core.Stop(); } catch (Exception) { }
        }
        try { popupBrowser?.Close(); } catch (Exception) { }
        if (closeWindow)
        {
            try { popupWindow?.Close(); } catch (Exception) { }
        }
    }

    private bool IsAllowedConnectTopLevel(Uri target) =>
        authorizedGameId is not null
        && PublisherVisibleConnectNavigationPolicy.IsAllowed(
            provider,
            authorizedGameId,
            target);

    private static void Core_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        // Publisher sessions are only for account status and explicit daily
        // claims. They never need to write a site-provided file to the PC.
        args.Cancel = true;
    }

    private static void Core_PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        // Camera, microphone, location, notifications, and other browser
        // permissions are outside this isolated session's purpose.
        args.State = CoreWebView2PermissionState.Deny;
    }

    private static string? ReadScriptString(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind is JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        connectCompletion.TrySetResult(PublisherVisibleConnectCompletion.Canceled);
        Close();
    }

    private async void DoneButton_Click(object sender, RoutedEventArgs e) =>
        await TryCompleteVisibleConnectAsync(
            reportFailure: true,
            endfieldIdentity: null,
            cancellationToken: lifetime.Token);

    private async Task<bool> TryCompleteVisibleConnectAsync(
        bool reportFailure,
        PublisherEndfieldAccountIdentity? endfieldIdentity,
        CancellationToken cancellationToken)
    {
        if (windowClosed
            || purpose != PublisherSessionPurpose.Connect
            || Interlocked.CompareExchange(ref visibleConnectOperationInFlight, 1, 0) != 0)
            return false;

        DoneButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        StatusText.Text = "Checking whether the official page finished signing in…";
        try
        {
            endfieldIdentity ??= provider == "SKPORT"
                ? ReviewedEndfieldIdentity ?? await TryReadEndfieldRegionAsync(cancellationToken)
                : null;
            var proof = await GetSessionProofAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(ResourceCaptureTimeoutSeconds + 3), cancellationToken);
            if (proof == PublisherSessionProof.Authenticated)
            {
                if (provider == "SKPORT")
                {
                    var identity = endfieldIdentity
                        ?? await ReviewEndfieldAccountIdentityAsync(cancellationToken);
                    if (identity is null)
                    {
                        if (reportFailure)
                            StatusText.Text = "Login was confirmed, but the official page did not prove an Endfield region. Keep this window open and try Done again.";
                        return false;
                    }
                    Volatile.Write(ref reviewedEndfieldIdentity, identity);
                }
                connectCompletion.TrySetResult(PublisherVisibleConnectCompletion.Done);
                Close();
                return true;
            }

            if (reportFailure)
            {
                StatusText.Text = proof == PublisherSessionProof.LoginRequired
                    ? "Login was not detected. Finish signing in on the official page, then choose Done again."
                    : "Nyx could not confirm the login. Keep this window open and try Done again, or close it and choose Review.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (TimeoutException)
        {
            if (reportFailure)
                StatusText.Text = "The official page did not confirm the login in time. Try Done again.";
        }
        catch
        {
            if (reportFailure)
                StatusText.Text = "Nyx could not confirm the login. Keep this window open and try Done again.";
        }
        finally
        {
            Interlocked.Exchange(ref visibleConnectOperationInFlight, 0);
            if (!windowClosed)
            {
                DoneButton.IsEnabled = true;
                RetryButton.IsEnabled = true;
            }
        }

        return false;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (webView2RuntimeUnavailable)
        {
            RetryButton.IsEnabled = false;
            try
            {
                var opened = await Windows.System.Launcher.LaunchUriAsync(WebView2DownloadUri);
                StatusText.Text = opened
                    ? "Finish the Microsoft setup, close this window, then choose Connect again."
                    : "WebView2 is missing. Download it from Microsoft's official WebView2 page.";
            }
            catch
            {
                StatusText.Text = "WebView2 is missing. Download it from Microsoft's official WebView2 page.";
            }
            finally
            {
                if (!windowClosed) RetryButton.IsEnabled = true;
            }
            return;
        }

        var uri = visibleConnectUri;
        if (uri is null
            || windowClosed
            || Interlocked.Exchange(ref visibleConnectOperationInFlight, 1) != 0)
            return;

        DoneButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        try
        {
            await AttemptVisibleConnectPageAsync(
                uri,
                lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref visibleConnectOperationInFlight, 0);
            if (!windowClosed)
            {
                DoneButton.IsEnabled = true;
                RetryButton.IsEnabled = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        Interlocked.Increment(ref sessionProbeGeneration);
        Interlocked.Increment(ref endfieldIdentityGeneration);
        Interlocked.Increment(ref checkInGeneration);
        Interlocked.Increment(ref resourceGeneration);
        Interlocked.Exchange(ref pendingSessionProbe, null)?.Cancel();
        Interlocked.Exchange(ref pendingEndfieldIdentityCapture, null)?.Cancel();
        Interlocked.Exchange(ref pendingCheckInCapture, null)?.Cancel();
        Interlocked.Exchange(ref pendingResourceCapture, null)?.Cancel();
        CloseSocialLoginWindow();
        var core = Volatile.Read(ref browserCloseStarted) == 0
            ? Browser.CoreWebView2
            : null;
        if (core is not null)
        {
            core.NavigationStarting -= Core_NavigationStarting;
            core.WebResourceRequested -= Core_WebResourceRequested;
            core.WebResourceResponseReceived -= Core_WebResourceResponseReceived;
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.DownloadStarting -= Core_DownloadStarting;
            core.PermissionRequested -= Core_PermissionRequested;
            try { core.Stop(); } catch (Exception) { }
        }
        CloseBrowserOnce();
        Exception? teardownFailure = browserCloseFailure;
        try
        {
            if (!windowClosed) Close();
            await closed.Task;
        }
        catch (Exception exception)
        {
            teardownFailure ??= exception;
        }
        try
        {
            if (Volatile.Read(ref browserProcessExitBarrierArmed) != 0)
                await browserProcessExited.Task.WaitAsync(BrowserProcessExitTimeout);
        }
        catch (Exception exception)
        {
            teardownFailure ??= exception;
        }
        finally
        {
            try
            {
                DetachBrowserProcessExitHandler();
            }
            catch (Exception exception)
            {
                teardownFailure ??= exception;
            }
            lifetime.Dispose();
        }
        if (teardownFailure is not null)
            throw new PublisherSessionTeardownException(teardownFailure);
    }

    private void CloseBrowserOnce()
    {
        if (Interlocked.Exchange(ref browserCloseStarted, 1) != 0) return;
        try
        {
            Browser.Close();
        }
        catch (Exception exception)
        {
            browserCloseFailure = exception;
        }
    }

    private sealed class PendingResourceCapture
    {
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken cancellationToken;
        private int canceled;

        public PendingResourceCapture(
            PublisherResourceCaptureAuthority authority,
            string controllerKey,
            CancellationToken cancellationToken)
        {
            Authority = authority;
            ControllerKey = controllerKey;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.cancellationToken = cancellation.Token;
        }

        public PublisherResourceCaptureAuthority Authority { get; }
        public string ControllerKey { get; }
        public CancellationToken CancellationToken => cancellationToken;

        public void Cancel()
        {
            Authority.Cancel();
            if (Interlocked.Exchange(ref canceled, 1) != 0) return;
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class SessionProbeCapture(long generation, CancellationToken cancellationToken)
    {
        private int began;

        public long Generation { get; } = generation;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<PublisherSessionProof> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBegin() => Interlocked.CompareExchange(ref began, 1, 0) == 0;

        public void TryComplete(PublisherSessionProof proof) => Completion.TrySetResult(proof);

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed class EndfieldIdentityCapture(long generation, CancellationToken cancellationToken)
    {
        private int began;

        public long Generation { get; } = generation;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<PublisherEndfieldAccountIdentity?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBegin() => Interlocked.CompareExchange(ref began, 1, 0) == 0;

        public void TryComplete(PublisherEndfieldAccountIdentity? identity) =>
            Completion.TrySetResult(identity);

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private sealed class CheckInCapture(
        string gameId,
        string method,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant,
        PublisherRoleBinding? expectedBinding,
        bool allowAccountWideStatus,
        long generation,
        CancellationToken cancellationToken)
    {
        private readonly PublisherCheckInCaptureDiagnosticGate diagnostics = new();

        public string GameId { get; } = gameId;
        public string Method { get; } = method;
        public DateOnly ExpectedDate { get; } = expectedDate;
        public DateTimeOffset ExpectedInstant { get; } = expectedInstant;
        public PublisherRoleBinding? ExpectedBinding { get; } = expectedBinding;
        public bool AllowAccountWideStatus { get; } = allowAccountWideStatus;
        public long Generation { get; } = generation;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<PublisherCheckInProof> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PublisherCheckInCaptureDiagnostic Diagnostic => diagnostics.Current;

        public bool TryBegin() => diagnostics.TryBeginSelectedResponse();

        public void MarkCandidateDiagnostic(PublisherCheckInCaptureDiagnostic value) =>
            diagnostics.MarkCandidate(value);

        public void MarkSelectedResponseDiagnostic(PublisherCheckInCaptureDiagnostic value) =>
            diagnostics.MarkSelectedResponse(value);

        public void TryComplete(PublisherCheckInProof proof) => Completion.TrySetResult(proof);

        public void Cancel() => Completion.TrySetCanceled(CancellationToken);
    }

    private enum HsrAchievementListNetworkState
    {
        None,
        PreflightAllowed,
        RequestBlocked,
        BlockedWrongMethod,
        BlockedMissingQuery,
        BlockedExtraQuery,
        BlockedQueryValue,
        RequestAllowed,
        ResponseAccepted,
        ResponseFailed,
    }
}

internal sealed class PublisherSessionTeardownException(Exception innerException) :
    Exception("The isolated publisher browser did not stop cleanly.", innerException);
