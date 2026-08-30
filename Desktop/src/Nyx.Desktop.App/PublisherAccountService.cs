using System.Collections.ObjectModel;
using System.Diagnostics;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.AccountStatus;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx_Desktop_App;

public sealed class PublisherAccountService : IAsyncDisposable
{
    private readonly string root;
    private readonly PublisherAccountConsentGate consent;
    private readonly HoyoLabAccountSlotStore hoyoSlots;
    private PublisherRoleBindingStore roleBindings;
    private PublisherResourceSnapshotStore resourceSnapshots;
    private HoyoLabGameBundleStore hoyoGameBundle;
    private readonly PublisherConsentRevocationStore revocations;
    private readonly Func<string, bool, bool?, bool>? persistCleanupPending;
    private readonly PengoAchievementCatalogReader achievementCatalog;
    private readonly PengoAchievementExportWriter achievementWriter;
    private readonly AchievementAccountBindingStore achievementBindings;
    private readonly SemaphoreSlim hoyoGate = new(1, 1);
    private readonly SemaphoreSlim skportGate = new(1, 1);
    private readonly IReadOnlyDictionary<string, PublisherSingleFlight<DailyCheckInResult>> checkInSingleFlights =
        new ReadOnlyDictionary<string, PublisherSingleFlight<DailyCheckInResult>>(
            new Dictionary<string, PublisherSingleFlight<DailyCheckInResult>>(StringComparer.Ordinal)
            {
                ["gi"] = new(),
                ["hsr"] = new(),
                ["zzz"] = new(),
                ["ae"] = new(),
            });
    private readonly IReadOnlyDictionary<string, PublisherSingleFlight<PublisherResourceSnapshot?>> resourceSingleFlights =
        new ReadOnlyDictionary<string, PublisherSingleFlight<PublisherResourceSnapshot?>>(
            new Dictionary<string, PublisherSingleFlight<PublisherResourceSnapshot?>>(StringComparer.Ordinal)
            {
                ["gi"] = new(),
                ["hsr"] = new(),
                ["zzz"] = new(),
            });
    private readonly PublisherGeneration hoyoGeneration = new();
    private readonly PublisherGeneration skportGeneration = new();
    private readonly PublisherProfileMutationJournal hoyoProfileMutations = new();
    private readonly PublisherProfileMutationJournal skportProfileMutations = new();
    private readonly PublisherPasswordStoragePolicy hoyoPasswordStorage;
    private readonly PublisherPasswordStoragePolicy skportPasswordStorage;
    private readonly Semaphore hoyoProfileOwner;
    private readonly Semaphore skportProfileOwner;
    private readonly bool ownsHoyoProfile;
    private readonly bool ownsSkportProfile;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object sync = new();
    private readonly Dictionary<string, PublisherResourceSnapshot> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublisherResourceState> resourceStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublisherResourceCaptureDiagnostic> resourceDiagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DailyCheckInResult> checkIns = new(StringComparer.Ordinal);
    private PublisherConnectionState hoyo = PublisherConnectionState.NotConnected;
    private PublisherConnectionState skport = PublisherConnectionState.NotConnected;
    private PublisherEndfieldAccountIdentity? endfieldIdentity;
    private CancellationTokenSource hoyoSession = new();
    private CancellationTokenSource skportSession = new();
    private bool hoyoQuarantined;
    private bool skportQuarantined;
    private bool hoyoCleanupPending;
    private bool skportCleanupPending;
    private bool hoyoSlotManagerAvailable;
    private bool hoyoLegacyCompatibilityAvailable;
    private HoyoLabAccountSlot? activeHoyoSlot;
    private bool disposed;
    private long lastAccountRestoreDurationTicks = -1;
    private long giResourceRefreshDurationTicks = -1;
    private long hsrResourceRefreshDurationTicks = -1;
    private long zzzResourceRefreshDurationTicks = -1;

    public PublisherAccountService(
        string root,
        bool hoyoLabAccountAccess = false,
        bool skportAccountAccess = false,
        bool hoyoLabCleanupPending = false,
        bool skportCleanupPending = false,
        string? achievementBindingRoot = null,
        bool publisherPasswordSavingEnabled = false,
        string? hsrAchievementCatalogPath = null,
        Func<string, bool, bool?, bool>? persistCleanupPending = null)
    {
        this.root = Path.GetFullPath(root);
        this.persistCleanupPending = persistCleanupPending;
        Directory.CreateDirectory(this.root);
        if ((File.GetAttributes(this.root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Publisher profile root cannot be a reparse point.");
        hoyoSlots = new(this.root);
        var slotInitialization = hoyoSlots.TryInitialize();
        hoyoSlotManagerAvailable = slotInitialization.IsReady;
        hoyoLegacyCompatibilityAvailable = slotInitialization.State
            == HoyoLabAccountSlotInitializationState.LegacyCompatibility;
        activeHoyoSlot = slotInitialization.Index?.ActiveSlotId is { } activeSlotId
            ? slotInitialization.Index.Slots.SingleOrDefault(slot =>
                string.Equals(slot.Id, activeSlotId, StringComparison.Ordinal))
            : null;
        var protectedStateRoot = ResolveCurrentHoyoProtectedStateRootOrLegacy();
        roleBindings = new(protectedStateRoot);
        resourceSnapshots = new(protectedStateRoot);
        hoyoGameBundle = new(protectedStateRoot);
        revocations = new(this.root);
        achievementBindings = new(
            achievementBindingRoot ?? Path.Combine(this.root, ".achievement-account-binding"));
        achievementCatalog = new(
            hsrAchievementCatalogPath ?? Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Content",
                "Achievements",
                "hsr",
                "catalog.json"));
        achievementWriter = new(achievementCatalog);
        hoyoPasswordStorage = new(
            publisherPasswordSavingEnabled,
            HoyoProfilesNeedPasswordCleanup());
        skportPasswordStorage = new(
            publisherPasswordSavingEnabled,
            Directory.Exists(ResolveProfilePath("SKPORT")));
        this.hoyoCleanupPending = hoyoLabCleanupPending || revocations.IsPending("HoYoLAB");
        this.skportCleanupPending = skportCleanupPending || revocations.IsPending("SKPORT");
        consent = new(
            hoyoLabAccountAccess && !this.hoyoCleanupPending,
            skportAccountAccess && !this.skportCleanupPending);
        (hoyoProfileOwner, ownsHoyoProfile) = AcquireProfileOwnership("HoYoLAB");
        try
        {
            RestoreCachedResources();
            (skportProfileOwner, ownsSkportProfile) = AcquireProfileOwnership("SKPORT");
        }
        catch
        {
            if (ownsHoyoProfile) hoyoProfileOwner.Release();
            hoyoProfileOwner.Dispose();
            throw;
        }
    }

    public event EventHandler? Updated;

    public TimeSpan? LastAccountRestoreDuration
    {
        get
        {
            var ticks = Volatile.Read(ref lastAccountRestoreDurationTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public bool TryGetResourceRefreshDuration(string? gameId, out TimeSpan duration)
    {
        var ticks = gameId switch
        {
            "gi" => Volatile.Read(ref giResourceRefreshDurationTicks),
            "hsr" => Volatile.Read(ref hsrResourceRefreshDurationTicks),
            "zzz" => Volatile.Read(ref zzzResourceRefreshDurationTicks),
            _ => -1,
        };
        duration = ticks < 0 ? default : TimeSpan.FromTicks(ticks);
        return ticks >= 0;
    }

    public HoyoLabAccountSlotManagerState HoyoLabAccounts
    {
        get
        {
            lock (sync)
            {
                var index = hoyoSlots.CurrentIndex;
                return new(
                    hoyoSlotManagerAvailable,
                    index?.ActiveSlotId,
                    index?.Slots.ToArray() ?? Array.Empty<HoyoLabAccountSlot>());
            }
        }
    }

    public async Task<HoyoLabGameBundle?> GetHsrGameBundleSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!consent.IsEnabled("HoYoLAB")
            || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB"))
            return null;

        ThrowIfDisposed();
        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var gate = GateFor("HoYoLAB");
        await gate.WaitAsync(operation.Cancellation.Token);
        try
        {
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation))
                return null;
            _ = TryMigrateHsrBundleFromV1(operation);
            if (!CanPublish("HoYoLAB", operation)) return null;
            var snapshot = hoyoGameBundle.TryLoad();
            return snapshot is not null && CanPublish("HoYoLAB", operation)
                ? HoyoLabGameBundleRules.Normalize(snapshot)
                : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SetHsrCapabilityConsentAsync(
        string capability,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (capability is not (HoyoLabGameBundleRules.Resources
                or HoyoLabGameBundleRules.Achievements)
            || !consent.IsEnabled("HoYoLAB")
            || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB"))
            return false;

        ThrowIfDisposed();
        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var gate = GateFor("HoYoLAB");
        await gate.WaitAsync(operation.Cancellation.Token);
        try
        {
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation))
                return false;
            _ = TryMigrateHsrBundleFromV1(operation);
            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation)) return false;
                var saved = hoyoGameBundle.TrySetCapabilityConsent(
                    capability,
                    enabled,
                    operation.Cancellation.Token);
                return saved && CanPublish("HoYoLAB", operation);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public HoyoLabAccountIdentity? GetHoyoLabIdentity(string gameId)
    {
        if (gameId is not ("gi" or "hsr" or "zzz")) return null;
        lock (sync)
        {
            if (!hoyoSlotManagerAvailable || !consent.IsEnabled("HoYoLAB")) return null;
            var persisted = hoyoSlots.TryLoad();
            var active = persisted?.ActiveSlotId is { } activeId
                ? persisted.Slots.SingleOrDefault(slot =>
                    string.Equals(slot.Id, activeId, StringComparison.Ordinal))
                : null;
            if (active is null
                || active.RemovalPending
                || !string.Equals(activeHoyoSlot?.Id, active.Id, StringComparison.Ordinal)
                || !hoyoSlots.TryGetProtectedStateRoot(active, out var protectedRoot))
                return null;

            var record = new PublisherRoleBindingStore(protectedRoot).TryLoadRecord(gameId);
            var revalidated = hoyoSlots.TryLoad();
            var revalidatedActive = revalidated?.ActiveSlotId is { } revalidatedActiveId
                ? revalidated.Slots.SingleOrDefault(slot =>
                    string.Equals(slot.Id, revalidatedActiveId, StringComparison.Ordinal))
                : null;
            if (revalidatedActive != active
                || !string.Equals(activeHoyoSlot?.Id, active.Id, StringComparison.Ordinal))
                return null;
            return HoyoLabAccountIdentity.Create(gameId, active, record);
        }
    }

    public PublisherEndfieldAccountIdentity? EndfieldIdentity
    {
        get
        {
            lock (sync)
                return !disposed
                    && consent.IsEnabled("SKPORT")
                    && ownsSkportProfile
                    && !skportQuarantined
                    ? endfieldIdentity
                    : null;
        }
    }

    public async Task<PublisherConnectionState> AddHoyoLabAccountAsync(
        string label,
        string gameId = "gi",
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (entry.Provider != "HoYoLAB"
            || !consent.IsEnabled("HoYoLAB")
            || !hoyoSlotManagerAvailable
            || !OwnsProfile("HoYoLAB"))
            return PublisherConnectionState.NotConnected;

        var rotated = BeginRotatedOperation("HoYoLAB", cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var rotationOperation = rotated.Operation;
        var gate = GateFor("HoYoLAB");
        var enteredGate = false;
        PublisherOperation? selectedOperation = null;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(rotationOperation.Cancellation.Token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate(
                    "HoYoLAB",
                    consentRequired: true,
                    rotationOperation,
                    requireSelectedSlot: false)
                || !hoyoSlots.TryCreateAndSelectSlot(label, out var created)
                || created is null)
                return PublisherConnectionState.NeedsReview;

            ActivateHoyoSlot(created);
            ClearProviderState("HoYoLAB");
            RestoreCachedResources();
            selectedOperation = CreateOperation("HoYoLAB", rotationOperation.Cancellation.Token);
            var operation = selectedOperation;
            TrySetConnection("HoYoLAB", PublisherConnectionState.Connecting, operation);
            PublisherVisibleConnectCompletion completion;
            await using (var window = CreateWindow("HoYoLAB", operation))
            {
                await window.InitializeAsync(
                    PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry),
                    visible: true,
                    purpose: PublisherSessionPurpose.Connect,
                    gameId: entry.GameId,
                    heading: "Connect HoYoLAB",
                    operation.Cancellation.Token,
                    ProfileMutationsFor("HoYoLAB"));
                completion = await window.WaitForConnectCompletionAsync(operation.Cancellation.Token);
            }
            var state = await PublisherVisibleConnectFlow.CompleteAsync(
                completion,
                probeCancellation => ProbeConnectionCoreAsync(entry, operation, probeCancellation),
                operation.Cancellation.Token);
            TrySetConnection("HoYoLAB", state, operation);
            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TrySetConnectionForGeneration(
                "HoYoLAB",
                PublisherConnectionState.NeedsReview,
                rotationOperation.Generation);
            return PublisherConnectionState.NeedsReview;
        }
        finally
        {
            selectedOperation?.Dispose();
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    public async Task<bool> RenameHoyoLabAccountAsync(
        string slotId,
        string label,
        CancellationToken cancellationToken = default)
    {
        if (!hoyoSlotManagerAvailable) return false;
        var gate = GateFor("HoYoLAB");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!hoyoSlots.TryRenameSlot(slotId, label)) return false;
            RefreshActiveHoyoSlot();
            Updated?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PublisherConnectionState> UseHoyoLabAccountAsync(
        string slotId,
        string gameId = "gi",
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (entry.Provider != "HoYoLAB"
            || !consent.IsEnabled("HoYoLAB")
            || !hoyoSlotManagerAvailable
            || !OwnsProfile("HoYoLAB"))
            return PublisherConnectionState.NotConnected;
        var target = FindUsableHoyoSlot(slotId);
        if (target is null) return PublisherConnectionState.NeedsReview;

        var rotated = BeginRotatedOperation("HoYoLAB", cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var rotationOperation = rotated.Operation;
        var gate = GateFor("HoYoLAB");
        var enteredGate = false;
        PublisherOperation? selectedOperation = null;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(rotationOperation.Cancellation.Token);
            enteredGate = true;
            target = FindUsableHoyoSlot(slotId);
            if (target is null
                || !ProfileAccessAllowedAfterGate(
                    "HoYoLAB",
                    consentRequired: true,
                    rotationOperation,
                    requireSelectedSlot: false)
                || !hoyoSlots.TrySetActiveSlot(target.Id))
                return PublisherConnectionState.NeedsReview;

            RefreshActiveHoyoSlot();
            ClearProviderState("HoYoLAB");
            RestoreCachedResources();
            selectedOperation = CreateOperation("HoYoLAB", rotationOperation.Cancellation.Token);
            var operation = selectedOperation;
            TrySetConnection("HoYoLAB", PublisherConnectionState.Connecting, operation);
            var proof = await ProbeConnectionCoreAsync(entry, operation, operation.Cancellation.Token);
            var state = PublisherAccountStatePolicy.ForSessionProof(proof);
            TrySetConnection("HoYoLAB", state, operation);
            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TrySetConnectionForGeneration(
                "HoYoLAB",
                PublisherConnectionState.NeedsReview,
                rotationOperation.Generation);
            return PublisherConnectionState.NeedsReview;
        }
        finally
        {
            selectedOperation?.Dispose();
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    public async Task<bool> ForgetHoyoLabAccountAsync(
        string slotId,
        CancellationToken cancellationToken = default)
    {
        if (!hoyoSlotManagerAvailable || !OwnsProfile("HoYoLAB")) return false;
        var target = FindHoyoSlot(slotId);
        if (target is null) return false;
        var rotated = BeginRotatedOperation("HoYoLAB", cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var operation = rotated.Operation;
        var gate = GateFor("HoYoLAB");
        var enteredGate = false;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(operation.Cancellation.Token);
            enteredGate = true;
            target = FindHoyoSlot(slotId);
            if (target is null
                || (!target.RemovalPending
                    && !hoyoSlots.TryMarkRemovalPending(target.Id)))
                return false;
            var wasActive = string.Equals(activeHoyoSlot?.Id, target.Id, StringComparison.Ordinal);
            RefreshActiveHoyoSlot();
            target = FindHoyoSlot(slotId);
            if (target is null || !target.RemovalPending) return false;
            if (wasActive)
            {
                ClearProviderState("HoYoLAB");
                SetConnection("HoYoLAB", PublisherConnectionState.NotConnected);
            }
            if (!target.IsLegacy)
            {
                if (!hoyoSlots.TryGetSlotContainerPath(target, out var container)
                    || !TryDeleteManagedDirectory(container))
                    return false;
            }
            if (!hoyoSlots.TryRemoveSlot(target.Id)) return false;
            RefreshActiveHoyoSlot();
            Updated?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    public bool HasConsent(string provider) => consent.IsEnabled(provider);

    public void ApplyPasswordSavingPreference(bool enabled)
    {
        foreach (var provider in new[] { "HoYoLAB", "SKPORT" })
        {
            PasswordStorageFor(provider).ApplyPreference(
                enabled,
                provider == "HoYoLAB"
                    ? HoyoProfilesNeedPasswordCleanup()
                    : TryResolveProfilePath(provider, out var profile)
                        && Directory.Exists(profile));
        }
    }

    public async Task<bool> ClearSavedPasswordsAsync(CancellationToken cancellationToken = default)
    {
        ApplyPasswordSavingPreference(enabled: false);
        var hoyoCleared = await ClearSavedPasswordsAsync("HoYoLAB", cancellationToken);
        var skportCleared = await ClearSavedPasswordsAsync("SKPORT", cancellationToken);
        return hoyoCleared && skportCleared;
    }

    public bool HasPendingConsentRevocation(string provider) => provider switch
    {
        "HoYoLAB" => Volatile.Read(ref hoyoCleanupPending) || revocations.IsPending(provider),
        "SKPORT" => Volatile.Read(ref skportCleanupPending) || revocations.IsPending(provider),
        _ => true,
    };

    public bool PendingConsentRevocationDisablesAccess(
        string provider,
        bool stateAccountAccess,
        bool stateCleanupPending) =>
        revocations.RecoveryMustDisableAccess(
            provider,
            stateAccountAccess,
            stateCleanupPending);

    public bool EnableConsent(string provider)
    {
        if (HasPendingConsentRevocation(provider)) return false;
        if (provider == "HoYoLAB" && !EnsureHoyoSlotManagerInitialized()) return false;
        return SetConsentSynchronized(provider, enabled: true);
    }

    public void ApplyConsentSnapshot(
        bool hoyoLabEnabled,
        bool skportEnabled,
        bool hoyoLabCleanupPending,
        bool skportCleanupPending)
    {
        ApplyProviderConsentSnapshot("HoYoLAB", hoyoLabEnabled, hoyoLabCleanupPending);
        ApplyProviderConsentSnapshot("SKPORT", skportEnabled, skportCleanupPending);
    }

    public async Task<bool> PrepareConsentEnableAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (provider is not ("HoYoLAB" or "SKPORT")) return false;
        SetConsentSynchronized(provider, enabled: false);
        if (!HasPendingConsentRevocation(provider))
            return provider != "HoYoLAB" || EnsureHoyoSlotManagerInitialized();
        revocations.MarkPending(provider);
        var cleaned = await RetryPendingConsentRevocationAsync(provider, cancellationToken)
            == PublisherConnectionState.NotConnected;
        return cleaned
            && revocations.Clear(provider)
            && (provider != "HoYoLAB" || EnsureHoyoSlotManagerInitialized());
    }

    public async Task<PublisherConnectionState> RetryPendingConsentRevocationAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (provider is not ("HoYoLAB" or "SKPORT"))
            return PublisherConnectionState.NeedsReview;
        SetConsentSynchronized(provider, enabled: false);
        revocations.MarkPending(provider);
        return await DisconnectCoreAsync(
            PublisherAccountCatalog.Get(provider == "HoYoLAB" ? "gi" : "ae"),
            consentRequired: false,
            cancellationToken);
    }

    public bool CompleteConsentRevocation(
        string provider,
        bool clearOptOutIntent = true)
    {
        if (provider is not ("HoYoLAB" or "SKPORT")) return false;
        return clearOptOutIntent
            ? revocations.Clear(provider)
            : revocations.ClearCleanupPending(provider);
    }

    public async Task<bool> OpenOfficialResourcePageAsync(string gameId)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        return consent.IsEnabled(entry.Provider)
            && gameId == "ae"
            && entry.ResourceUri is not null
            && PublisherAccountCatalog.IsExactResourcePageUri(gameId, entry.ResourceUri)
            && await Windows.System.Launcher.LaunchUriAsync(entry.ResourceUri);
    }

    public ValueTask<IAchievementExportSession> StartHsrAchievementExportAsync(
        string? outputPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var completion = ExportHsrAchievementsCoreAsync(outputPath, linked.Token);
        return ValueTask.FromResult<IAchievementExportSession>(
            new PublisherAchievementExportSession(completion, linked));
    }

    private async Task<ExportArtifactMetadata> ExportHsrAchievementsCoreAsync(
        string? outputPath,
        CancellationToken cancellationToken)
    {
        const string provider = "HoYoLAB";
        const string gameId = "hsr";
        if (!consent.IsEnabled(provider))
            throw new ExportProviderException("hoyolab-consent-required");
        if (!HasUsableHoyoAccount())
            throw new ExportProviderException("hoyolab-profile-unavailable");
        if (!OwnsProfile(provider))
            throw new ExportProviderException("hoyolab-profile-unavailable");

        using var operation = CreateOperation(provider, cancellationToken);
        cancellationToken = operation.Cancellation.Token;
        var gate = GateFor(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!ProfileAccessAllowedAfterGate(provider, consentRequired: true, operation))
                throw new ExportProviderException("hoyolab-consent-required");
            ThrowIfDisposed();

            var catalog = await achievementCatalog.ReadCurrentHsrAsync(
                AchievementCatalogVersions.StarRail,
                cancellationToken);
            var role = TryLoadRoleRecord(gameId, operation)?.Binding;
            var result = await ReadHsrAchievementsWithVisibleRecoveryAsync(
                role,
                catalog.AchievementIds,
                operation,
                cancellationToken);
            if (!CanPublish(provider, operation))
                throw new OperationCanceledException(cancellationToken);
            if (!SaveRoleBinding(gameId, result.Role, operation))
                throw new ExportProviderException("achievement-binding-unavailable");
            var accountBinding = achievementBindings.Derive(gameId, result.Role);
            var artifact = await achievementWriter.WriteAsync(
                gameId,
                AchievementCatalogVersions.StarRail,
                result.AchievementIds,
                accountBinding,
                outputPath,
                new PublisherAchievementExportPublishAuthority(
                    this,
                    provider,
                    operation.Generation,
                    operation.HoyoContext,
                    operation.Cancellation.Token),
                cancellationToken);
            _ = TryMirrorHsrAchievements(result.Role, result.AchievementIds, operation);
            TrySetConnection(provider, PublisherConnectionState.Connected, operation);
            return artifact;
        }
        catch (PublisherSessionTeardownException)
        {
            QuarantineProvider(provider, operation);
            throw new ExportProviderException("hoyolab-profile-unavailable");
        }
        catch (ExportProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TrySetConnection(provider, PublisherConnectionState.NeedsReview, operation);
            throw new ExportProviderException("hoyolab-export-failed");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsWithVisibleRecoveryAsync(
        PublisherRoleBinding? role,
        IReadOnlySet<long> currentCatalogIds,
        PublisherOperation operation,
        CancellationToken cancellationToken)
    {
        const string provider = "HoYoLAB";
        const string gameId = "hsr";
        try
        {
            return await ReadHsrAchievementsOnceAsync(
                role,
                currentCatalogIds,
                operation,
                cancellationToken);
        }
        catch (ExportProviderException exception) when (
            RequiresVisibleHsrAchievementLogin(exception.Code))
        {
            // The normal HoYoLAB login can be valid while the separate official
            // cultivation tool still needs its own page to finish signing in.
            // One export click may open that exact page once with the normal
            // interactive publisher-page boundary; credentials remain entirely
            // inside the isolated official WebView2 profile.
        }

        if (!CanPublish(provider, operation))
            throw new OperationCanceledException(cancellationToken);

        PublisherVisibleConnectCompletion completion;
        await using (var window = CreateWindow(provider, operation))
        {
            await window.InitializeAsync(
                PublisherAccountCatalog.GetAchievementPageUri(gameId),
                visible: true,
                purpose: PublisherSessionPurpose.Connect,
                gameId,
                "Sign in to Star Rail achievements",
                cancellationToken,
                ProfileMutationsFor(provider));
            completion = await window.WaitForConnectCompletionAsync(cancellationToken);
        }
        if (completion != PublisherVisibleConnectCompletion.Done)
            throw new ExportProviderException("hoyolab-achievement-login-canceled");
        if (!CanPublish(provider, operation))
            throw new OperationCanceledException(cancellationToken);

        // Retry exactly once in a fresh hidden window after the visible page has
        // fully closed and its isolated profile changes are available.
        return await ReadHsrAchievementsOnceAsync(
            role,
            currentCatalogIds,
            operation,
            cancellationToken);
    }

    private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsOnceAsync(
        PublisherRoleBinding? role,
        IReadOnlySet<long> currentCatalogIds,
        PublisherOperation operation,
        CancellationToken cancellationToken)
    {
        const string provider = "HoYoLAB";
        const string gameId = "hsr";
        if (!CanPublish(provider, operation))
            throw new OperationCanceledException(cancellationToken);
        await using var window = CreateWindow(provider, operation);
        await window.InitializeAsync(
            PublisherAccountCatalog.GetAchievementPageUri(gameId),
            visible: false,
            purpose: PublisherSessionPurpose.Achievements,
            gameId,
            "Export Star Rail achievements",
            cancellationToken);
        var proof = await window.GetSessionProofAsync(cancellationToken);
        if (proof == PublisherSessionProof.LoginRequired)
            throw new ExportProviderException("hoyolab-login-required");
        if (proof != PublisherSessionProof.Authenticated)
            throw new ExportProviderException("hoyolab-session-review");
        return await window.ReadHsrAchievementsAsync(
            role,
            currentCatalogIds,
            cancellationToken);
    }

    private static bool RequiresVisibleHsrAchievementLogin(string code) => code is
        "hoyolab-login-required"
        or "hoyolab-api-cookie-missing"
        or "hoyolab-login-retcode--100";

    public PublisherAccountSummary Current
    {
        get
        {
            lock (sync)
            {
                return new(
                    hoyo,
                    skport,
                    new ReadOnlyDictionary<string, PublisherResourceSnapshot>(
                        new Dictionary<string, PublisherResourceSnapshot>(resources, StringComparer.Ordinal)),
                    new ReadOnlyDictionary<string, PublisherResourceState>(
                        new Dictionary<string, PublisherResourceState>(resourceStates, StringComparer.Ordinal)),
                    new ReadOnlyDictionary<string, PublisherResourceCaptureDiagnostic>(
                        new Dictionary<string, PublisherResourceCaptureDiagnostic>(
                            resourceDiagnostics,
                            StringComparer.Ordinal)),
                    new ReadOnlyDictionary<string, DailyCheckInResult>(
                        new Dictionary<string, DailyCheckInResult>(checkIns, StringComparer.Ordinal)));
            }
        }
    }

    public async Task<PublisherConnectionState> ConnectAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (entry.GameId == "ae")
            return (await ReviewEndfieldAccountAsync(cancellationToken)).Connection;
        if (!consent.IsEnabled(entry.Provider))
            return PublisherConnectionState.NotConnected;
        if (entry.Provider == "HoYoLAB" && !HasUsableHoyoAccount())
            return PublisherConnectionState.NotConnected;
        if (!OwnsProfile(entry.Provider))
        {
            SetConnection(entry.Provider, PublisherConnectionState.NeedsReview);
            return PublisherConnectionState.NeedsReview;
        }
        var rotated = BeginRotatedOperation(entry.Provider, cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var operation = rotated.Operation;
        var cancellationWrite = new PublisherConnectCancellationAuthority(
            operation.Generation,
            rotated.PreviousState,
            rotated.ProfileSnapshot);
        cancellationToken = operation.Cancellation.Token;
        var gate = GateFor(entry.Provider);
        var enteredGate = false;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate(entry.Provider, consentRequired: true, operation))
            {
                TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                return PublisherConnectionState.NeedsReview;
            }
            if (!TryDeleteProtectedProviderState(entry.Provider, operation))
                return PublisherConnectionState.NeedsReview;
            ClearProviderStateIfCurrent(entry.Provider, operation);
            ThrowIfDisposed();
            TrySetConnection(entry.Provider, PublisherConnectionState.Connecting, operation);
            PublisherVisibleConnectCompletion completion;
            await using (var window = CreateWindow(entry.Provider, operation))
            {
                await window.InitializeAsync(
                    PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry),
                    visible: true,
                    purpose: PublisherSessionPurpose.Connect,
                    gameId: entry.GameId,
                    heading: $"Connect {entry.Provider}",
                    cancellationToken,
                    ProfileMutationsFor(entry.Provider));
                completion = await window.WaitForConnectCompletionAsync(cancellationToken);
            }

            var state = await PublisherVisibleConnectFlow.CompleteAsync(
                completion,
                operationCancellation => ProbeConnectionCoreAsync(
                    entry,
                    operation,
                    operationCancellation),
                cancellationToken);
            TrySetConnection(entry.Provider, state, operation);
            return state;
        }
        catch (OperationCanceledException)
        {
            TrySetCanceledConnectState(entry.Provider, cancellationWrite);
            throw;
        }
        catch (PublisherSessionTeardownException exception)
        {
            if (cancellationToken.IsCancellationRequested)
                TrySetCanceledConnectState(entry.Provider, cancellationWrite);
            QuarantineProvider(entry.Provider, operation);
            PublisherTeardownCancellationPolicy.ThrowIfCanceled(cancellationToken, exception);
            return PublisherConnectionState.NeedsReview;
        }
        catch (Exception)
        {
            TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
            return PublisherConnectionState.NeedsReview;
        }
        finally
        {
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    public Task<PublisherResourceSnapshot?> RefreshResourceAsync(
        string gameId,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker = null,
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (!consent.IsEnabled(entry.Provider))
            return Task.FromResult<PublisherResourceSnapshot?>(null);
        if (entry.Provider == "HoYoLAB" && !HasUsableHoyoAccount())
            return Task.FromResult<PublisherResourceSnapshot?>(null);
        if (!entry.SupportsNumericResource
            || !resourceSingleFlights.TryGetValue(gameId, out var singleFlight))
            return Task.FromResult<PublisherResourceSnapshot?>(null);
        if (!OwnsProfile(entry.Provider))
        {
            SetConnection(entry.Provider, PublisherConnectionState.NeedsReview);
            return Task.FromResult<PublisherResourceSnapshot?>(null);
        }
        ThrowIfDisposed();
        return singleFlight.RunAsync(
            operationCancellation => RefreshResourceCoreAsync(entry, rolePicker, operationCancellation),
            shutdown.Token,
            cancellationToken);
    }

    public async Task<PublisherResourceSnapshot?> ChangeRoleAsync(
        string gameId,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>> rolePicker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rolePicker);
        var entry = PublisherAccountCatalog.Get(gameId);
        if (entry.Provider != "HoYoLAB" || !entry.SupportsNumericResource) return null;

        if (!consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()) return null;
        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var exactProtectedRoot = operation.HoyoContext?.ProtectedStateRoot
            ?? throw new InvalidOperationException("No HoYoLAB account is selected.");
        var exactStore = new PublisherRoleBindingStore(exactProtectedRoot);
        var gate = GateFor("HoYoLAB");
        await gate.WaitAsync(operation.Cancellation.Token);
        PublisherRoleRecord? previous;
        try
        {
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation))
                return null;
            previous = exactStore.TryLoadRecord(gameId);
            if (!exactStore.Delete(gameId)) return null;
        }
        finally
        {
            gate.Release();
        }
        var result = await RefreshResourceAsync(gameId, rolePicker, operation.Cancellation.Token);
        if (result is null && previous is not null)
        {
            await gate.WaitAsync(operation.Cancellation.Token);
            try
            {
                if (!ProfileAccessAllowedAfterGate(
                        "HoYoLAB",
                        consentRequired: true,
                        operation)
                    || operation.HoyoContext is null
                    || !string.Equals(
                        operation.HoyoContext.ProtectedStateRoot,
                        exactProtectedRoot,
                        StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!exactStore.SaveRecord(gameId, previous))
                    QuarantineProvider("HoYoLAB", operation);
            }
            finally
            {
                gate.Release();
            }
        }
        return result;
    }

    private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync(
        PublisherAccountCatalogEntry entry,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await RunResourceRefreshAsync(
                entry,
                rolePicker,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetResourceRefreshDuration(entry.GameId, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<PublisherResourceSnapshot?> RunResourceRefreshAsync(
        PublisherAccountCatalogEntry entry,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker,
        CancellationToken cancellationToken)
    {
        using var operation = CreateOperation(entry.Provider, cancellationToken);
        cancellationToken = operation.Cancellation.Token;
        var gate = GateFor(entry.Provider);
        var resourceDiagnostic = PublisherResourceCaptureDiagnostic.NotAvailable;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!ProfileAccessAllowedAfterGate(entry.Provider, consentRequired: true, operation))
            {
                RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
                TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                return null;
            }
            SetResourceDiagnosticIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                PublisherResourceCaptureDiagnostic.NotAvailable);
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                PublisherResourceState.Checking);
            ThrowIfDisposed();
            await using var window = CreateWindow(entry.Provider, operation);
            await window.InitializeAsync(
                entry.ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: entry.GameId,
                $"Refresh {entry.ResourceName}",
                cancellationToken);
            var sessionProof = await window.GetSessionProofAsync(cancellationToken);
            if (sessionProof != PublisherSessionProof.Authenticated)
            {
                var authority = sessionProof == PublisherSessionProof.LoginRequired
                    ? PublisherProtectedStateAuthority.LoginRequired
                    : PublisherProtectedStateAuthority.NeedsReview;
                var retained = MarkResourceStaleIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation);
                SetResourceStateIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                        authority,
                        retained));
                TrySetConnection(
                    entry.Provider,
                    PublisherAccountStatePolicy.ForSessionProof(sessionProof),
                    operation);
                return null;
            }
            var storedRecord = entry.Provider == "HoYoLAB"
                ? TryLoadRoleRecord(entry.GameId, operation)
                : null;
            if (entry.GameId == HoyoLabGameBundleRules.GameId)
                _ = TryMigrateHsrBundleFromV1(operation);
            var storedBinding = storedRecord?.Binding;
            var activeBinding = storedBinding;
            var resourceRead = await window.ReadResourceAsync(entry, storedBinding, cancellationToken);
            resourceDiagnostic = resourceRead.Diagnostic;
            SetResourceDiagnosticIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                resourceRead.Diagnostic);
            if (storedBinding is not null
                && resourceRead.Outcome is not (PublisherResourceReadOutcome.Valid
                    or PublisherResourceReadOutcome.NeedsReview
                    or PublisherResourceReadOutcome.LoginRequired))
            {
                if (!TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation))
                    return null;
            }

            if (resourceRead.Outcome == PublisherResourceReadOutcome.SelectionRequired)
            {
                var candidates = resourceRead.Candidates ?? Array.Empty<PublisherResourceCandidate>();
                var choices = PublisherAccountCatalog.CreateRoleChoices(entry.GameId, candidates);
                if (choices.Count < 2 || rolePicker is null)
                {
                    RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
                    SetResourceStateIfCurrent(
                        entry.GameId,
                        entry.Provider,
                        operation,
                        PublisherResourceState.SelectionRequired);
                    TrySetConnection(entry.Provider, PublisherConnectionState.Connected, operation);
                    return null;
                }

                var selectedBinding = await rolePicker(choices, cancellationToken);
                var selectedChoice = selectedBinding is null
                    ? null
                    : choices.SingleOrDefault(choice => choice.Binding == selectedBinding);
                if (selectedChoice is null)
                {
                    RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
                    SetResourceStateIfCurrent(
                        entry.GameId,
                        entry.Provider,
                        operation,
                        PublisherResourceState.SelectionRequired);
                    TrySetConnection(entry.Provider, PublisherConnectionState.Connected, operation);
                    return null;
                }

                var selectedSnapshot = PublisherAccountCatalog.SelectResourceForBinding(
                    candidates,
                    selectedChoice.Binding);
                if (selectedSnapshot is null)
                {
                    RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
                    SetResourceDiagnosticIfCurrent(
                        entry.GameId,
                        entry.Provider,
                        operation,
                        PublisherResourceCaptureDiagnostic.NotAvailable);
                    SetResourceStateIfCurrent(
                        entry.GameId,
                        entry.Provider,
                        operation,
                        PublisherResourceState.NeedsReview);
                    TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                    return null;
                }
                activeBinding = selectedChoice.Binding;
                resourceRead = new(
                    selectedSnapshot,
                    PublisherResourceReadOutcome.Valid,
                    [new(selectedChoice.Binding, selectedSnapshot)],
                    PublisherResourceCaptureDiagnostic.Valid);
                SetResourceDiagnosticIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    resourceRead.Diagnostic);
            }
            if (entry.Provider == "HoYoLAB"
                && activeBinding is null
                && resourceRead.Candidates is { Count: 1 } selectedSingleRole)
            {
                activeBinding = selectedSingleRole[0].Binding;
            }
            if (entry.Provider == "HoYoLAB"
                && activeBinding is not null
                && resourceRead.Outcome == PublisherResourceReadOutcome.Valid
                && resourceRead.Candidates is { Count: > 0 } officialCandidates
                && (storedRecord is null
                    || storedRecord.Binding != activeBinding
                    || RoleRecordNeedsRefresh(storedRecord, activeBinding, officialCandidates))
                && !SaveRoleRecord(
                    entry.GameId,
                    activeBinding,
                    officialCandidates,
                    operation))
            {
                RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
                SetResourceDiagnosticIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    PublisherResourceCaptureDiagnostic.NotAvailable);
                SetResourceStateIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    PublisherResourceState.NeedsReview);
                TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                return null;
            }
            var nextState = PublisherAccountStatePolicy.ForAuthenticatedResourceRead(resourceRead);
            var snapshot = resourceRead.Snapshot;
            if (resourceRead.Outcome == PublisherResourceReadOutcome.Valid
                && snapshot is not null
                && SetResourceIfCurrent(entry.Provider, operation, snapshot))
            {
                if (activeBinding is not null)
                {
                    if (!CanPublish(entry.Provider, operation)
                        || !resourceSnapshots.Save(snapshot with { IsStale = false }, activeBinding))
                        return null;
                    if (entry.GameId == HoyoLabGameBundleRules.GameId)
                        _ = TryMirrorHsrResource(activeBinding, snapshot, operation);
                }
                SetResourceStateIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    PublisherResourceState.Fresh);
                TrySetConnection(entry.Provider, nextState, operation);
                Updated?.Invoke(this, EventArgs.Empty);
                return CanPublish(entry.Provider, operation) ? snapshot : null;
            }

            if (resourceRead.Outcome is PublisherResourceReadOutcome.NeedsReview
                or PublisherResourceReadOutcome.LoginRequired)
            {
                var retained = MarkResourceStaleIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation);
                SetResourceStateIfCurrent(
                    entry.GameId,
                    entry.Provider,
                    operation,
                    PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                        resourceRead.Outcome == PublisherResourceReadOutcome.LoginRequired
                            ? PublisherProtectedStateAuthority.LoginRequired
                            : PublisherProtectedStateAuthority.NeedsReview,
                        retained));
                TrySetConnection(
                    entry.Provider,
                    resourceRead.Outcome == PublisherResourceReadOutcome.LoginRequired
                        ? PublisherConnectionState.LoginRequired
                        : PublisherConnectionState.Connected,
                    operation);
                return null;
            }

            RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
            if (!TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation))
                return null;
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                resourceRead.Outcome == PublisherResourceReadOutcome.LoginRequired
                    ? PublisherResourceState.LoginRequired
                    : PublisherResourceState.NeedsReview);
            TrySetConnection(entry.Provider, nextState, operation);
            return null;
        }
        catch (PublisherSessionTeardownException)
        {
            QuarantineProvider(entry.Provider, operation);
            SetQuarantinedResourceFailure(
                entry.GameId,
                entry.Provider,
                operation,
                resourceDiagnostic);
            return null;
        }
        catch (OperationCanceledException)
        {
            var retained = MarkResourceStaleIfCurrent(entry.GameId, entry.Provider, operation);
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                retained ? PublisherResourceState.Stale : PublisherResourceState.NotStarted);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var retained = MarkResourceStaleIfCurrent(entry.GameId, entry.Provider, operation);
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                retained ? PublisherResourceState.Stale : PublisherResourceState.Unavailable);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<DailyCheckInResult> CheckInAsync(
        string gameId,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var entry = PublisherAccountCatalog.Get(gameId);
        if (!entry.SupportsDailyCheckIn
            || !checkInSingleFlights.TryGetValue(gameId, out var singleFlight))
        {
            return Task.FromResult(SetCheckInResult(
                gameId,
                DailyCheckInState.Unavailable,
                "Daily check-in is not supported for this game."));
        }
        if (!consent.IsEnabled(entry.Provider))
        {
            return Task.FromResult(SetCheckInResult(
                gameId,
                DailyCheckInState.LoginNeeded,
                $"Connect {entry.Provider} first."));
        }
        if (entry.Provider == "HoYoLAB" && !HasUsableHoyoAccount())
        {
            return Task.FromResult(SetCheckInResult(
                gameId,
                DailyCheckInState.LoginNeeded,
                "Choose a HoYoLAB account first."));
        }
        return singleFlight.RunAsync(
            operationCancellation => CheckInCoreAsync(entry, rolePicker, operationCancellation),
            shutdown.Token,
            cancellationToken);
    }

    private async Task<DailyCheckInResult> CheckInCoreAsync(
        PublisherAccountCatalogEntry entry,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker,
        CancellationToken cancellationToken)
    {
        await RunProviderCheckInsAsync(
            entry.Provider,
            [entry.GameId],
            rolePicker,
            cancellationToken);
        lock (sync)
        {
            return checkIns.TryGetValue(entry.GameId, out var result)
                ? result
                : new(
                    entry.GameId,
                    DailyCheckInState.CouldNotCheck,
                    "The official page could not be checked.",
                    DateTimeOffset.UtcNow);
        }
    }

    private async Task RunProviderCheckInsAsync(
        string provider,
        string[] gameIds,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker,
        CancellationToken cancellationToken)
    {
        if (!consent.IsEnabled(provider)) return;
        if (!OwnsProfile(provider))
        {
            SetConnection(provider, PublisherConnectionState.NeedsReview);
            foreach (var gameId in gameIds)
                SetCouldNotCheck(gameId, "The isolated publisher profile is already in use.");
            return;
        }
        using var operation = CreateOperation(provider, cancellationToken);
        cancellationToken = operation.Cancellation.Token;
        var gate = GateFor(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!ProfileAccessAllowedAfterGate(provider, consentRequired: true, operation))
            {
                TrySetConnection(provider, PublisherConnectionState.NeedsReview, operation);
                foreach (var gameId in gameIds)
                {
                    SetCheckInIfCurrent(
                        provider,
                        operation,
                        new(
                            gameId,
                            DailyCheckInState.CouldNotCheck,
                            "The isolated publisher profile needs review.",
                            DateTimeOffset.UtcNow));
                }
                return;
            }
            for (var gameIndex = 0; gameIndex < gameIds.Length; gameIndex++)
            {
                var gameId = gameIds[gameIndex];
                var entry = PublisherAccountCatalog.Get(gameId);
                DailyCheckInResult result;
                try
                {
                    if (!CanPublish(provider, operation)) return;
                    SetCheckInIfCurrent(
                        provider,
                        operation,
                        new(gameId, DailyCheckInState.Opening, "Opening the official page.", DateTimeOffset.UtcNow));
                    var role = await ResolveDailyRoleAsync(
                        entry,
                        rolePicker,
                        operation,
                        cancellationToken);
                    if (role.State == PublisherDailyRoleResolutionState.Resolved
                        && (role.Binding is not null || entry.GameId == "ae"))
                    {
                        if (!CanPublish(provider, operation)) return;
                        await using var window = CreateWindow(provider, operation);
                        await window.InitializeAsync(
                            entry.CheckInUri!,
                            visible: false,
                            purpose: PublisherSessionPurpose.CheckIn,
                            gameId,
                            $"Check in {gameId}",
                            cancellationToken);
                        var sessionProof = await window.GetSessionProofAsync(cancellationToken);
                        if (sessionProof == PublisherSessionProof.Authenticated)
                        {
                            TrySetConnection(provider, PublisherConnectionState.Connected, operation);
                            result = await window.RunCheckInAsync(
                                entry,
                                role.Binding,
                                role.AccountWideStatusAllowed,
                                cancellationToken);
                        }
                        else if (sessionProof == PublisherSessionProof.LoginRequired)
                            result = new(gameId, DailyCheckInState.LoginNeeded, $"Connect {provider} first.", DateTimeOffset.UtcNow);
                        else
                            result = new(gameId, DailyCheckInState.CouldNotCheck, "The official session proof needs review.", DateTimeOffset.UtcNow);
                    }
                    else if (role.State == PublisherDailyRoleResolutionState.LoginRequired)
                        result = new(gameId, DailyCheckInState.LoginNeeded, $"Connect {provider} first.", DateTimeOffset.UtcNow);
                    else if (role.State == PublisherDailyRoleResolutionState.SelectionRequired)
                        result = new(gameId, DailyCheckInState.SelectionRequired, "Choose a character before Daily.", DateTimeOffset.UtcNow);
                    else
                        result = new(gameId, DailyCheckInState.CouldNotCheck, "The selected character could not be proven.", DateTimeOffset.UtcNow);
                }
                catch (PublisherSessionTeardownException)
                {
                    QuarantineProvider(provider, operation);
                    result = new(gameId, DailyCheckInState.CouldNotCheck, "The isolated browser needs review.", DateTimeOffset.UtcNow);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = new(gameId, DailyCheckInState.CouldNotCheck, "The official page could not be checked.", DateTimeOffset.UtcNow);
                }
                SetCheckInIfCurrent(provider, operation, result);
                var connectionState = PublisherAccountStatePolicy.ForCheckIn(result.State);
                if (connectionState.HasValue)
                    TrySetConnection(provider, connectionState.Value, operation);
                if (connectionState is PublisherConnectionState.LoginRequired
                    or PublisherConnectionState.NeedsReview)
                {
                    var remainingState = connectionState == PublisherConnectionState.LoginRequired
                        ? DailyCheckInState.LoginNeeded
                        : DailyCheckInState.CouldNotCheck;
                    var remainingMessage = connectionState == PublisherConnectionState.LoginRequired
                        ? $"Connect {provider} first."
                        : "The official page needs review.";
                    for (var remainingIndex = gameIndex + 1; remainingIndex < gameIds.Length; remainingIndex++)
                    {
                        SetCheckInIfCurrent(
                            provider,
                            operation,
                            new(
                                gameIds[remainingIndex],
                                remainingState,
                                remainingMessage,
                                DateTimeOffset.UtcNow));
                    }
                    return;
                }
                if (!OwnsProfile(provider)) return;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync(
        PublisherAccountCatalogEntry entry,
        Func<IReadOnlyList<PublisherRoleChoice>, CancellationToken, Task<PublisherRoleBinding?>>? rolePicker,
        PublisherOperation operation,
        CancellationToken cancellationToken)
    {
        if (entry.GameId == "ae" && entry.Provider == "SKPORT")
        {
            return new(
                PublisherDailyRoleResolutionState.Resolved,
                null,
                Array.Empty<PublisherRoleChoice>(),
                AccountWideStatusAllowed: true,
                StoredBindingStillMatches: false,
                StoredBindingWasProvenMissing: false);
        }

        if (entry.Provider != "HoYoLAB"
            || entry.GameId is not ("gi" or "hsr" or "zzz")
            || entry.ResourceUri is null
            || !entry.SupportsNumericResource)
        {
            return new(
                PublisherDailyRoleResolutionState.NeedsReview,
                null,
                Array.Empty<PublisherRoleChoice>(),
                AccountWideStatusAllowed: false,
                StoredBindingStillMatches: false,
                StoredBindingWasProvenMissing: false);
        }

        SetResourceDiagnosticIfCurrent(
            entry.GameId,
            entry.Provider,
            operation,
            PublisherResourceCaptureDiagnostic.NotAvailable);
        await using var roleWindow = CreateWindow(entry.Provider, operation);
        await roleWindow.InitializeAsync(
            entry.ResourceUri,
            visible: false,
            purpose: PublisherSessionPurpose.Resource,
            gameId: entry.GameId,
            $"Verify {entry.ResourceName} character",
            cancellationToken);
        var sessionProof = await roleWindow.GetSessionProofAsync(cancellationToken);
        if (sessionProof != PublisherSessionProof.Authenticated)
        {
            if (!CanPublish(entry.Provider, operation))
            {
                return new(
                    PublisherDailyRoleResolutionState.NeedsReview,
                    null,
                    Array.Empty<PublisherRoleChoice>(),
                    AccountWideStatusAllowed: false,
                    StoredBindingStillMatches: false,
                    StoredBindingWasProvenMissing: false);
            }
            var authority = sessionProof == PublisherSessionProof.LoginRequired
                ? PublisherProtectedStateAuthority.LoginRequired
                : PublisherProtectedStateAuthority.NeedsReview;
            var retained = MarkResourceStaleIfCurrent(
                entry.GameId,
                entry.Provider,
                operation);
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                    authority,
                    retained));
            return new(
                sessionProof == PublisherSessionProof.LoginRequired
                    ? PublisherDailyRoleResolutionState.LoginRequired
                    : PublisherDailyRoleResolutionState.NeedsReview,
                null,
                Array.Empty<PublisherRoleChoice>(),
                AccountWideStatusAllowed: false,
                StoredBindingStillMatches: false,
                StoredBindingWasProvenMissing: false);
        }

        var storedRecord = TryLoadRoleRecord(entry.GameId, operation);
        var storedBinding = storedRecord?.Binding;
        var resourceRead = await roleWindow.ReadResourceAsync(
            entry,
            expectedBinding: null,
            cancellationToken);
        var resolution = PublisherDailyRolePolicy.Resolve(
            entry.GameId,
            resourceRead,
            storedBinding);
        if (!CanPublish(entry.Provider, operation))
            return resolution with
            {
                State = PublisherDailyRoleResolutionState.NeedsReview,
                Binding = null,
            };

        if (resourceRead.Outcome is PublisherResourceReadOutcome.NeedsReview
            or PublisherResourceReadOutcome.LoginRequired)
        {
            var retained = MarkResourceStaleIfCurrent(
                entry.GameId,
                entry.Provider,
                operation);
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                    resourceRead.Outcome == PublisherResourceReadOutcome.LoginRequired
                        ? PublisherProtectedStateAuthority.LoginRequired
                        : PublisherProtectedStateAuthority.NeedsReview,
                    retained));
        }

        var shouldClearStoredBinding = storedBinding is not null
            && resolution.StoredBindingWasProvenMissing
            && PublisherProtectedStateRetentionPolicy.ClearsVerifiedState(
                PublisherProtectedStateAuthority.ProvenAccountOrRoleReplacement);
        if (shouldClearStoredBinding)
        {
            RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);
            if (!TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation))
                return resolution with { State = PublisherDailyRoleResolutionState.NeedsReview };
            storedBinding = null;
            storedRecord = null;
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                resolution.State == PublisherDailyRoleResolutionState.SelectionRequired
                    ? PublisherResourceState.SelectionRequired
                    : PublisherResourceState.NotStarted);
        }

        if (resolution.State == PublisherDailyRoleResolutionState.SelectionRequired
            && rolePicker is not null
            && resolution.Choices.Count >= 2)
        {
            var selectedBinding = await rolePicker(resolution.Choices, cancellationToken);
            if (!CanPublish(entry.Provider, operation))
                return resolution with
                {
                    State = PublisherDailyRoleResolutionState.NeedsReview,
                    Binding = null,
                };
            resolution = PublisherDailyRolePolicy.Resolve(
                entry.GameId,
                resourceRead,
                storedBinding,
                selectedBinding);
        }

        if (resolution.State == PublisherDailyRoleResolutionState.Resolved
            && resolution.Binding is not null
            && (!resolution.StoredBindingStillMatches
                || RoleRecordNeedsRefresh(
                    storedRecord,
                    resolution.Binding,
                    resourceRead.Candidates ?? Array.Empty<PublisherResourceCandidate>())))
        {
            if (!CanPublish(entry.Provider, operation))
                return resolution with
                {
                    State = PublisherDailyRoleResolutionState.NeedsReview,
                    Binding = null,
                };
            var candidates = resourceRead.Candidates ?? Array.Empty<PublisherResourceCandidate>();
            if (!SaveRoleRecord(
                    entry.GameId,
                    resolution.Binding,
                    candidates,
                    operation))
            {
                QuarantineProvider(entry.Provider, operation);
                return resolution with { State = PublisherDailyRoleResolutionState.NeedsReview };
            }
            SetResourceStateIfCurrent(
                entry.GameId,
                entry.Provider,
                operation,
                PublisherResourceState.NotStarted);
        }
        SetResourceDiagnosticIfCurrent(
            entry.GameId,
            entry.Provider,
            operation,
            PublisherDailyRolePolicy.FinalDiagnostic(resourceRead, resolution));
        return resolution;
    }

    private async Task<PublisherSessionProof> ProbeConnectionCoreAsync(
        PublisherAccountCatalogEntry entry,
        PublisherOperation operation,
        CancellationToken cancellationToken)
    {
        if (!CanPublish(entry.Provider, operation))
            throw new OperationCanceledException(cancellationToken);
        await using var window = CreateWindow(entry.Provider, operation);
        await window.InitializeAsync(
            entry.CheckInUri ?? entry.ResourceUri!,
            visible: false,
            purpose: PublisherSessionPurpose.ConnectionProbe,
            gameId: entry.GameId,
            heading: "Checking connection",
            cancellationToken);
        return await window.GetSessionProofAsync(cancellationToken);
    }

    private PublisherSessionWindow CreateWindow(
        string provider,
        PublisherOperation operation)
    {
        if (provider == "HoYoLAB" && !CanPublish(provider, operation))
            throw new OperationCanceledException(operation.Cancellation.Token);
        var passwordStorage = PasswordStorageFor(provider);
        var passwordState = passwordStorage.Snapshot;
        if (provider == "HoYoLAB"
            && passwordState.PendingCleanup is PublisherProfileCleanupScope.PasswordsOnly)
            throw new InvalidOperationException("Every HoYoLAB account must finish password cleanup before navigation.");
        if (passwordState.PendingCleanup is PublisherProfileCleanupScope.FullProfile)
            throw new InvalidOperationException("Publisher profile cleanup must finish before navigation.");

        return new(
            provider == "HoYoLAB"
                ? operation.HoyoContext?.ProfilePath
                    ?? throw new InvalidOperationException("No HoYoLAB account is selected.")
                : ResolveProfilePath(provider),
            provider,
            passwordSavingEnabled: passwordState.PasswordSavingEnabled,
            passwordCleanupCompleted: provider == "HoYoLAB"
                ? static () => { }
        : () => passwordStorage.CompleteCleanup(
            PublisherProfileCleanupScope.PasswordsOnly,
            succeeded: true));
    }

    private async Task<bool> ClearSavedPasswordsAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        if (provider == "HoYoLAB")
            return await ClearAllHoyoSavedPasswordsAsync(cancellationToken);

        var passwordStorage = PasswordStorageFor(provider);
        using var operation = CreateOperation(provider, cancellationToken);
        cancellationToken = operation.Cancellation.Token;
        var profile = provider == "HoYoLAB"
            ? operation.HoyoContext?.ProfilePath
            : ResolveProfilePath(provider);
        if (string.IsNullOrEmpty(profile))
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: true);
            return true;
        }
        if (!Directory.Exists(profile))
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: true);
            return true;
        }
        if (!OwnsProfile(provider))
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: false);
            return false;
        }

        var gate = GateFor(provider);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!ProfileAccessAllowedAfterGate(
                    provider,
                    consentRequired: false,
                    operation))
                return false;
            if (!Directory.Exists(profile))
            {
                passwordStorage.CompleteCleanup(
                    PublisherProfileCleanupScope.PasswordsOnly,
                    succeeded: true);
                return true;
            }
            await using var window = CreateWindow(provider, operation);
            await window.ClearSavedPasswordsAsync(cancellationToken);
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: false);
            // Keep saving disabled. Future publisher windows retry the exact
            // password-only deletion before they are allowed to navigate.
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PublisherEndfieldAccountReviewResult> ReviewEndfieldAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get("ae");
        if (!consent.IsEnabled(entry.Provider))
            return new(PublisherConnectionState.NotConnected, null);
        if (!OwnsProfile(entry.Provider))
        {
            SetConnection(entry.Provider, PublisherConnectionState.NeedsReview);
            return new(PublisherConnectionState.NeedsReview, null);
        }

        var rotated = BeginRotatedOperation(entry.Provider, cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var operation = rotated.Operation;
        var cancellationWrite = new PublisherConnectCancellationAuthority(
            operation.Generation,
            rotated.PreviousState,
            rotated.ProfileSnapshot);
        cancellationToken = operation.Cancellation.Token;
        var gate = GateFor(entry.Provider);
        var enteredGate = false;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate(entry.Provider, consentRequired: true, operation))
            {
                TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                return new(PublisherConnectionState.NeedsReview, null);
            }

            ThrowIfDisposed();
            TrySetConnection(entry.Provider, PublisherConnectionState.Connecting, operation);
            PublisherVisibleConnectCompletion completion;
            PublisherEndfieldAccountIdentity? identity;
            await using (var window = CreateWindow(entry.Provider, operation))
            {
                await window.InitializeAsync(
                    PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry),
                    visible: true,
                    purpose: PublisherSessionPurpose.Connect,
                    gameId: entry.GameId,
                    heading: "Review SKPORT account",
                    cancellationToken,
                    ProfileMutationsFor(entry.Provider));
                completion = await window.WaitForConnectCompletionAsync(cancellationToken);
                identity = window.ReviewedEndfieldIdentity;
            }

            if (completion == PublisherVisibleConnectCompletion.Done
                && identity is not null
                && TryPublishEndfieldReview(identity, operation))
            {
                return new(PublisherConnectionState.Connected, identity);
            }

            TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
            return new(PublisherConnectionState.NeedsReview, null);
        }
        catch (OperationCanceledException)
        {
            TrySetCanceledConnectState(entry.Provider, cancellationWrite);
            throw;
        }
        catch (PublisherSessionTeardownException exception)
        {
            if (cancellationToken.IsCancellationRequested)
                TrySetCanceledConnectState(entry.Provider, cancellationWrite);
            QuarantineProvider(entry.Provider, operation);
            PublisherTeardownCancellationPolicy.ThrowIfCanceled(cancellationToken, exception);
            return new(PublisherConnectionState.NeedsReview, null);
        }
        catch (Exception)
        {
            TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
            return new(PublisherConnectionState.NeedsReview, null);
        }
        finally
        {
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    private async Task<bool> ClearAllHoyoSavedPasswordsAsync(CancellationToken cancellationToken)
    {
        const string provider = "HoYoLAB";
        var passwordStorage = PasswordStorageFor(provider);
        bool Failed()
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: false);
            return false;
        }

        lock (sync)
        {
            if (!hoyoSlotManagerAvailable && !hoyoLegacyCompatibilityAvailable)
                return Failed();
        }
        if (!OwnsProfile(provider)) return Failed();

        var rotated = BeginRotatedOperation(provider, cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var operation = rotated.Operation;
        var gate = GateFor(provider);
        var enteredGate = false;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(operation.Cancellation.Token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate(
                    provider,
                    consentRequired: false,
                    operation,
                    requireSelectedSlot: false)
                || !TryGetHoyoPasswordCleanupTargets(out var expectedIndex, out var profiles))
                return Failed();

            foreach (var profile in profiles)
            {
                if (!ProfileAccessAllowedAfterGate(
                        provider,
                        consentRequired: false,
                        operation,
                        requireSelectedSlot: false)
                    || !AreHoyoPasswordCleanupTargetsCurrent(expectedIndex)
                    || !IsSafePublisherProfilePath(profile, allowMissingLeaf: true))
                    return Failed();
                if (!Directory.Exists(profile)) continue;
                await using var window = CreatePasswordCleanupWindow(profile);
                await window.ClearSavedPasswordsAsync(operation.Cancellation.Token);
            }

            if (!ProfileAccessAllowedAfterGate(
                    provider,
                    consentRequired: false,
                    operation,
                    requireSelectedSlot: false)
                || !AreHoyoPasswordCleanupTargetsCurrent(expectedIndex))
                return Failed();
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.PasswordsOnly,
                succeeded: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            Failed();
            throw;
        }
        catch
        {
            return Failed();
        }
        finally
        {
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    private bool TryGetHoyoPasswordCleanupTargets(
        out HoyoLabAccountSlotIndex? expectedIndex,
        out IReadOnlyList<string> profiles)
    {
        expectedIndex = null;
        profiles = Array.Empty<string>();
        lock (sync)
        {
            if (hoyoSlotManagerAvailable)
            {
                var index = hoyoSlots.TryLoad();
                if (index is null
                    || !string.Equals(
                        index.ActiveSlotId,
                        activeHoyoSlot?.Id,
                        StringComparison.Ordinal))
                    return false;
                var resolved = new List<string>(index.Slots.Count);
                foreach (var slot in index.Slots)
                {
                    if (!hoyoSlots.TryGetWebView2ProfilePath(slot, out var profile)) return false;
                    resolved.Add(profile);
                }
                expectedIndex = index;
                profiles = resolved;
                return true;
            }
            if (!hoyoLegacyCompatibilityAvailable
                || !hoyoSlots.IsLegacyCompatibilityStillSafe())
                return false;
            var legacyProfile = Path.GetFullPath(Path.Combine(root, "HoYoLAB"));
            if (!IsSafePublisherProfilePath(legacyProfile, allowMissingLeaf: true)) return false;
            profiles = [legacyProfile];
            return true;
        }
    }

    private bool HoyoProfilesNeedPasswordCleanup()
    {
        var targetsValidated = TryGetHoyoPasswordCleanupTargets(out _, out var profiles);
        return HoyoLabPasswordCleanupRules.RequiresCleanup(
            targetsValidated,
            targetsValidated
                ? profiles.Select(PublisherProfileEntryExistsOrUnknown).ToArray()
                : Array.Empty<bool>());
    }

    private bool PublisherProfileEntryExistsOrUnknown(string profile)
    {
        if (!IsSafePublisherProfilePath(profile, allowMissingLeaf: true)) return true;
        try
        {
            _ = File.GetAttributes(profile);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return true;
        }
    }

    private bool AreHoyoPasswordCleanupTargetsCurrent(HoyoLabAccountSlotIndex? expectedIndex)
    {
        lock (sync)
        {
            if (expectedIndex is null)
                return !hoyoSlotManagerAvailable
                    && hoyoLegacyCompatibilityAvailable
                    && hoyoSlots.IsLegacyCompatibilityStillSafe();
            if (!hoyoSlotManagerAvailable) return false;
            var current = hoyoSlots.TryLoad();
            return current is not null
                && string.Equals(current.ActiveSlotId, activeHoyoSlot?.Id, StringComparison.Ordinal)
                && HoyoSlotIndexesMatch(expectedIndex, current);
        }
    }

    private static bool HoyoSlotIndexesMatch(
        HoyoLabAccountSlotIndex expected,
        HoyoLabAccountSlotIndex current) =>
        expected.SchemaVersion == current.SchemaVersion
        && string.Equals(expected.ActiveSlotId, current.ActiveSlotId, StringComparison.Ordinal)
        && expected.LegacyFallback == current.LegacyFallback
        && expected.Slots.SequenceEqual(current.Slots);

    private PublisherSessionWindow CreatePasswordCleanupWindow(string profile)
    {
        var passwordState = hoyoPasswordStorage.Snapshot;
        if (passwordState.PendingCleanup is PublisherProfileCleanupScope.FullProfile)
            throw new InvalidOperationException("Publisher profile cleanup must finish before password cleanup.");
        return new(
            profile,
            "HoYoLAB",
            passwordSavingEnabled: false,
            passwordCleanupCompleted: static () => { });
    }

    private sealed class PublisherAchievementExportSession(
        Task<ExportArtifactMetadata> completion,
        CancellationTokenSource cancellation) : IAchievementExportSession
    {
        private int disposed;

        public Task Ready { get; } = Task.CompletedTask;
        public Task<ExportArtifactMetadata> Completion { get; } = completion;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            try
            {
                await Completion;
            }
            catch
            {
                // The coordinator owns and reports the sanitized completion state.
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    public async Task<PublisherConnectionState> DisconnectAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (!consent.IsEnabled(entry.Provider))
            return PublisherConnectionState.NotConnected;
        return await DisconnectCoreAsync(entry, consentRequired: true, cancellationToken);
    }

    public async Task<PublisherConnectionState> RevokeConsentAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        // Revocation becomes authoritative before cancellation, profile cleanup,
        // or any fallible disk operation.
        SetConsentSynchronized(entry.Provider, enabled: false);
        var previousSession = RotateSession(entry.Provider);
        try { previousSession.Cancel(); }
        catch (AggregateException) { }
        finally { previousSession.Dispose(); }
        ClearProviderState(entry.Provider);
        var stateRecorded = persistCleanupPending?.Invoke(
            entry.Provider,
            true,
            false) == true;
        var markerRecorded = revocations.MarkOptOutPending(entry.Provider);
        SetCleanupPending(entry.Provider, pending: true);
        if (!stateRecorded && !markerRecorded)
        {
            SetConnection(entry.Provider, PublisherConnectionState.NeedsReview);
            return PublisherConnectionState.NeedsReview;
        }
        return await DisconnectCoreAsync(entry, consentRequired: false, cancellationToken);
    }

    private async Task<PublisherConnectionState> DisconnectCoreAsync(
        PublisherAccountCatalogEntry entry,
        bool consentRequired,
        CancellationToken cancellationToken)
    {
        if (!OwnsProfile(entry.Provider))
        {
            SetConnection(entry.Provider, PublisherConnectionState.NeedsReview);
            return PublisherConnectionState.NeedsReview;
        }

        var rotated = BeginRotatedOperation(entry.Provider, cancellationToken);
        var previousSession = rotated.PreviousSession;
        using var operation = rotated.Operation;
        var initialProfile = rotated.ProfileSnapshot;
        var gate = GateFor(entry.Provider);
        var enteredGate = false;
        try
        {
            await previousSession.CancelAsync();
            await gate.WaitAsync(operation.Cancellation.Token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate(
                    entry.Provider,
                    consentRequired,
                    operation,
                    requireSelectedSlot: consentRequired))
            {
                TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
                return PublisherConnectionState.NeedsReview;
            }
            ThrowIfDisposed();
            bool roleBindingsCleared;
            bool resourceSnapshotsCleared;
            if (entry.Provider == "HoYoLAB" && !consentRequired)
            {
                roleBindingsCleared = TryDeleteAllHoyoState(operation);
                resourceSnapshotsCleared = roleBindingsCleared;
                if (roleBindingsCleared)
                {
                    lock (sync)
                    {
                        hoyoSlotManagerAvailable = false;
                        hoyoLegacyCompatibilityAvailable = false;
                        activeHoyoSlot = null;
                        roleBindings = new(root);
                        resourceSnapshots = new(root);
                        hoyoGameBundle = new(root);
                    }
                    ProfileMutationsFor(entry.Provider).MarkDeleted();
                }
            }
            else
            {
                roleBindingsCleared = roleBindings.DeleteProvider(entry.Provider);
                resourceSnapshotsCleared = resourceSnapshots.DeleteProvider(entry.Provider);
                await DeleteProfileDirectoryAsync(
                    entry.Provider,
                    ProfileMutationsFor(entry.Provider),
                    operation.Cancellation.Token);
            }
            if (!roleBindingsCleared || !resourceSnapshotsCleared)
            {
                return CommitInterruptedProfileChange(
                    entry.Provider,
                    PublisherConnectionState.NeedsReview,
                    operation);
            }
            // Profile deletion is irreversible. Once the directory is known
            // absent, cancellation cannot preserve old Connected data.
            if (entry.Provider == "HoYoLAB" && !consentRequired)
            {
                ClearProviderState("HoYoLAB");
                SetConnection("HoYoLAB", PublisherConnectionState.NotConnected);
                return PublisherConnectionState.NotConnected;
            }
            return CommitDeletedProfile(entry.Provider, operation);
        }
        catch (OperationCanceledException)
        {
            var currentProfile = ProfileMutationsFor(entry.Provider).Capture();
            CommitInterruptedDisconnectIfNeeded(
                entry.Provider,
                initialProfile,
                currentProfile,
                operation,
                enteredGate);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (operation.Cancellation.IsCancellationRequested)
            {
                CommitInterruptedDisconnectIfNeeded(
                    entry.Provider,
                    initialProfile,
                    ProfileMutationsFor(entry.Provider).Capture(),
                    operation,
                    enteredGate);
                throw new OperationCanceledException(operation.Cancellation.Token);
            }
            var currentProfile = ProfileMutationsFor(entry.Provider).Capture();
            if (PublisherProfileCommitPolicy.TryGetInterruptedDisconnectState(
                    initialProfile,
                    currentProfile,
                    out var terminalState))
            {
                if (CanCommitInterruptedProfileChange(entry.Provider, operation, enteredGate))
                    return CommitInterruptedProfileChange(
                        entry.Provider,
                        terminalState,
                        operation);
                return PublisherConnectionState.NeedsReview;
            }
            TrySetConnection(entry.Provider, PublisherConnectionState.NeedsReview, operation);
            return PublisherConnectionState.NeedsReview;
        }
        finally
        {
            if (enteredGate) gate.Release();
            previousSession.Dispose();
        }
    }

    private SemaphoreSlim GateFor(string provider) => provider == "HoYoLAB"
        ? hoyoGate
        : provider == "SKPORT"
            ? skportGate
            : throw new ArgumentOutOfRangeException(nameof(provider));

    private PublisherProfileMutationJournal ProfileMutationsFor(string provider) => provider == "HoYoLAB"
        ? hoyoProfileMutations
        : provider == "SKPORT"
            ? skportProfileMutations
            : throw new ArgumentOutOfRangeException(nameof(provider));

    private bool OwnsProfile(string provider)
    {
        lock (sync)
        {
            return provider switch
            {
                "HoYoLAB" => ownsHoyoProfile && !hoyoQuarantined,
                "SKPORT" => ownsSkportProfile && !skportQuarantined,
                _ => false,
            };
        }
    }

    // A queued operation may have passed its first ownership check before the
    // prior WebView teardown quarantined the shared profile. Always recheck only
    // after the provider gate is held and before touching that profile again.
    private bool ProfileAccessAllowedAfterGate(
        string provider,
        bool consentRequired,
        PublisherOperation operation,
        bool requireSelectedSlot = true) =>
        OwnsProfile(provider)
        && (!consentRequired || consent.IsEnabled(provider))
        && GenerationFor(provider).IsCurrent(operation.Generation)
        && (provider != "HoYoLAB"
            || (!requireSelectedSlot && operation.HoyoContext is null)
            || IsCurrentHoyoContext(operation.HoyoContext));

    private PublisherOperation CreateOperation(string provider, CancellationToken cancellationToken)
    {
        CancellationToken providerToken;
        long generation;
        lock (sync)
        {
            providerToken = provider switch
            {
                "HoYoLAB" => hoyoSession.Token,
                "SKPORT" => skportSession.Token,
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            generation = GenerationFor(provider).Current;
        }
        return new(
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token, providerToken),
            generation,
            provider == "HoYoLAB" ? CaptureHoyoContext() : null);
    }

    private (
        CancellationTokenSource PreviousSession,
        PublisherOperation Operation,
        PublisherConnectionState PreviousState,
        PublisherProfileMutationSnapshot ProfileSnapshot) BeginRotatedOperation(
            string provider,
            CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var previousSession = provider switch
            {
                "HoYoLAB" => hoyoSession,
                "SKPORT" => skportSession,
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            var previousState = provider == "HoYoLAB" ? hoyo : skport;
            var profileSnapshot = ProfileMutationsFor(provider).Capture();
            var nextSession = new CancellationTokenSource();
            var generation = GenerationFor(provider).Advance();
            if (provider == "HoYoLAB") hoyoSession = nextSession;
            else skportSession = nextSession;
            var operation = new PublisherOperation(
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    shutdown.Token,
                    nextSession.Token),
                generation,
                provider == "HoYoLAB" ? CaptureHoyoContextNoLock() : null);
            return (previousSession, operation, previousState, profileSnapshot);
        }
    }

    private CancellationTokenSource RotateSession(string provider)
    {
        lock (sync)
        {
            var previous = provider switch
            {
                "HoYoLAB" => hoyoSession,
                "SKPORT" => skportSession,
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            GenerationFor(provider).Advance();
            if (provider == "HoYoLAB") hoyoSession = new CancellationTokenSource();
            else skportSession = new CancellationTokenSource();
            return previous;
        }
    }

    private void ApplyProviderConsentSnapshot(
        string provider,
        bool enabled,
        bool cleanupPending)
    {
        if (provider == "HoYoLAB") Volatile.Write(ref hoyoCleanupPending, cleanupPending);
        else if (provider == "SKPORT") Volatile.Write(ref skportCleanupPending, cleanupPending);
        else throw new ArgumentOutOfRangeException(nameof(provider));
        enabled = enabled && !cleanupPending && !revocations.IsPending(provider);
        bool wasEnabled;
        lock (sync)
        {
            wasEnabled = consent.IsEnabled(provider);
            consent.Set(provider, enabled);
        }
        if (!wasEnabled || enabled) return;
        var previous = RotateSession(provider);
        try { previous.Cancel(); }
        catch (AggregateException) { }
        finally { previous.Dispose(); }
        ClearProviderState(provider);
        SetConnection(provider, PublisherConnectionState.NotConnected);
    }

    private void ClearProviderState(string provider)
    {
        var ids = provider == "HoYoLAB" ? new[] { "gi", "hsr", "zzz" } : new[] { "ae" };
        lock (sync)
        {
            if (provider == "SKPORT") endfieldIdentity = null;
            foreach (var id in ids)
            {
                resources.Remove(id);
                resourceStates.Remove(id);
                resourceDiagnostics.Remove(id);
                checkIns.Remove(id);
            }
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private PublisherConnectionState CommitDeletedProfile(
        string provider,
        PublisherOperation operation) =>
        CommitInterruptedProfileChange(
            provider,
            PublisherConnectionState.NotConnected,
            operation);

    private void CommitInterruptedDisconnectIfNeeded(
        string provider,
        PublisherProfileMutationSnapshot initialProfile,
        PublisherProfileMutationSnapshot currentProfile,
        PublisherOperation operation,
        bool enteredGate)
    {
        if (PublisherProfileCommitPolicy.TryGetInterruptedDisconnectState(
                initialProfile,
                currentProfile,
                out var terminalState)
            && CanCommitInterruptedProfileChange(provider, operation, enteredGate))
            CommitInterruptedProfileChange(provider, terminalState, operation);
    }

    private bool CanCommitInterruptedProfileChange(
        string provider,
        PublisherOperation operation,
        bool enteredGate) =>
        enteredGate || GenerationFor(provider).IsCurrent(operation.Generation);

    private PublisherConnectionState CommitInterruptedProfileChange(
        string provider,
        PublisherConnectionState terminalState,
        PublisherOperation operation)
    {
        if (terminalState is not (PublisherConnectionState.NotConnected
                or PublisherConnectionState.NeedsReview))
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        // The gate still protects the old captured slot after irreversible
        // profile deletion, even if a newer operation has advanced generation.
        // Delete that exact old root, never the mutable active-slot store.
        if (provider == "HoYoLAB" && !CanMutateHoyoProtectedState(operation))
        {
            if (!TryDeleteCapturedHoyoProtectedState(operation))
            {
                QuarantineProvider(provider);
                return PublisherConnectionState.NeedsReview;
            }
        }
        else if (!TryDeleteProtectedProviderState(provider, operation))
        {
            return PublisherConnectionState.NeedsReview;
        }
        var ids = provider == "HoYoLAB" ? new[] { "gi", "hsr", "zzz" } : new[] { "ae" };
        lock (sync)
        {
            foreach (var id in ids)
            {
                resources.Remove(id);
                resourceStates.Remove(id);
                resourceDiagnostics.Remove(id);
                checkIns.Remove(id);
            }
            if (provider == "HoYoLAB") hoyo = terminalState;
            else if (provider == "SKPORT") skport = terminalState;
            else throw new ArgumentOutOfRangeException(nameof(provider));
        }
        Updated?.Invoke(this, EventArgs.Empty);
        return terminalState;
    }

    private Task DeleteProfileDirectoryAsync(
        string provider,
        PublisherProfileMutationJournal profileMutations,
        CancellationToken cancellationToken) =>
        PublisherProfilePrivacyOrchestrator.DeleteFullProfileAsync(
            PasswordStorageFor(provider),
            (recursive, operationCancellation) => DeleteProfileDirectoryCoreAsync(
                provider,
                profileMutations,
                recursive,
                operationCancellation),
            cancellationToken);

    private async Task DeleteProfileDirectoryCoreAsync(
        string provider,
        PublisherProfileMutationJournal profileMutations,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (!recursive)
            throw new InvalidOperationException("Publisher profile deletion must be recursive.");
        var profile = ResolveProfilePath(provider);
        if (!Directory.Exists(profile))
        {
            profileMutations.MarkDeleted();
            return;
        }
        if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Publisher profile path cannot be a reparse point.");

        cancellationToken.ThrowIfCancellationRequested();
        // A recursive delete can remove part of a profile before Windows reports
        // a sharing failure. Record the irreversible boundary before attempting it.
        profileMutations.MarkMayHaveChanged();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(profile, recursive);
                if (!Directory.Exists(profile))
                {
                    profileMutations.MarkDeleted();
                    return;
                }
            }
            catch (IOException) when (attempt < 5)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken);
        }
        if (!Directory.Exists(profile))
        {
            profileMutations.MarkDeleted();
            return;
        }
        throw new IOException("Nyx could not clear the publisher profile.");
    }

    private static (Semaphore Semaphore, bool Owned) AcquireProfileOwnership(string provider)
    {
        var semaphore = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            $"Local\\Pengo.Nyx.Desktop.PublisherProfile.{provider}");
        try
        {
            return (semaphore, semaphore.WaitOne(0));
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    private void SetConnection(string provider, PublisherConnectionState state)
    {
        lock (sync)
        {
            if (provider == "HoYoLAB") hoyo = state;
            else skport = state;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreCachedResources()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            RestoreCachedResourcesCore();
        }
        finally
        {
            Volatile.Write(
                ref lastAccountRestoreDurationTicks,
                Stopwatch.GetElapsedTime(started).Ticks);
        }
    }

    private void RestoreCachedResourcesCore()
    {
        if (!consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()) return;
        using var operation = CreateOperation("HoYoLAB", CancellationToken.None);
        var restoredResources = new Dictionary<string, PublisherResourceSnapshot>(StringComparer.Ordinal);
        var restoredStates = new Dictionary<string, PublisherResourceState>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var gameId in new[] { "gi", "hsr", "zzz" })
        {
            var binding = TryLoadRoleRecord(gameId, operation)?.Binding;
            if (binding is null || !CanPublish("HoYoLAB", operation)) continue;
            var snapshot = TryLoadResourceSnapshot(gameId, binding, operation);
            if (snapshot is null
                || snapshot.ObservedAt > now
                || now - snapshot.ObservedAt > TimeSpan.FromDays(7))
            {
                resourceSnapshots.Delete(gameId);
                continue;
            }
            var fresh = PublisherResourceRefreshPolicy.IsFresh(snapshot.ObservedAt, now);
            restoredResources[gameId] = snapshot with { IsStale = !fresh };
            restoredStates[gameId] = fresh
                ? PublisherResourceState.Fresh
                : PublisherResourceState.Stale;
        }
        lock (sync)
        {
            if (!CanPublish("HoYoLAB", operation)) return;
            foreach (var gameId in new[] { "gi", "hsr", "zzz" })
            {
                resources.Remove(gameId);
                resourceStates.Remove(gameId);
                if (restoredResources.TryGetValue(gameId, out var snapshot))
                    resources[gameId] = snapshot;
                if (restoredStates.TryGetValue(gameId, out var state))
                    resourceStates[gameId] = state;
            }
        }
    }

    private void SetResourceRefreshDuration(string gameId, TimeSpan duration)
    {
        switch (gameId)
        {
            case "gi":
                Volatile.Write(ref giResourceRefreshDurationTicks, duration.Ticks);
                break;
            case "hsr":
                Volatile.Write(ref hsrResourceRefreshDurationTicks, duration.Ticks);
                break;
            case "zzz":
                Volatile.Write(ref zzzResourceRefreshDurationTicks, duration.Ticks);
                break;
        }
    }

    private void TrySetCanceledConnectState(
        string provider,
        PublisherConnectCancellationAuthority authority)
    {
        lock (sync)
        {
            if (!authority.TryConsume(
                    GenerationFor(provider).Current,
                    ProfileMutationsFor(provider).Capture(),
                    out var terminalState))
                return;
            if (provider == "HoYoLAB") hoyo = terminalState;
            else skport = terminalState;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void QuarantineProvider(
        string provider,
        PublisherOperation? operation = null)
    {
        if (operation is not null && !CanPublish(provider, operation)) return;
        lock (sync)
        {
            if (provider == "HoYoLAB") hoyoQuarantined = true;
            else if (provider == "SKPORT") skportQuarantined = true;
        }
        Func<string, bool, bool>? cleanupPersistence = persistCleanupPending is null
            ? null
            : (persistProvider, pending) =>
                persistCleanupPending(persistProvider, pending, null);
        var cleanupComplete = PublisherQuarantineCleanupStore.TryClean(
            provider,
            revocations,
            roleBindings,
            resourceSnapshots,
            cleanupPersistence,
            provider == "HoYoLAB"
                ? () => CanMutateHoyoProtectedState(operation)
                    && hoyoGameBundle.TryDelete()
                : null);
        SetCleanupPending(provider, !cleanupComplete);
        ClearProviderState(provider);
        SetConnection(provider, PublisherConnectionState.NeedsReview);
    }

    private bool TryDeleteProtectedGameState(
        string gameId,
        string provider,
        PublisherOperation? operation = null)
    {
        var deleted = PublisherProtectedStateDeletionPolicy.TryDeleteGameState(
            () => resourceSnapshots.Delete(gameId),
            () => provider != "HoYoLAB" || roleBindings.Delete(gameId));
        if (!deleted) QuarantineProvider(provider, operation);
        return deleted;
    }

    private bool TryDeleteProtectedProviderState(
        string provider,
        PublisherOperation? operation = null)
    {
        var legacyDeleted = PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(
            () => resourceSnapshots.DeleteProvider(provider),
            () => roleBindings.DeleteProvider(provider));
        var bundleDeleted = provider != "HoYoLAB"
            || (CanMutateHoyoProtectedState(operation)
                && hoyoGameBundle.TryDelete());
        var deleted = legacyDeleted && bundleDeleted;
        if (!deleted) QuarantineProvider(provider, operation);
        return deleted;
    }

    private bool CanMutateHoyoProtectedState(PublisherOperation? operation) =>
        operation is not null
        && operation.HoyoContext is not null
        && GenerationFor("HoYoLAB").IsCurrent(operation.Generation)
        && IsCurrentHoyoContext(operation.HoyoContext);

    private bool CanDeleteAllHoyoProtectedState(PublisherOperation operation) =>
        GenerationFor("HoYoLAB").IsCurrent(operation.Generation);

    private static bool TryDeleteCapturedHoyoProtectedState(PublisherOperation operation)
    {
        if (operation.HoyoContext is not { } context) return true;
        var legacyDeleted = PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(
            () => new PublisherResourceSnapshotStore(context.ProtectedStateRoot)
                .DeleteProvider("HoYoLAB"),
            () => new PublisherRoleBindingStore(context.ProtectedStateRoot)
                .DeleteProvider("HoYoLAB"));
        var bundleDeleted = new HoyoLabGameBundleStore(context.ProtectedStateRoot).TryDelete();
        return legacyDeleted && bundleDeleted;
    }

    private void SetQuarantinedResourceFailure(
        string gameId,
        string provider,
        PublisherOperation operation,
        PublisherResourceCaptureDiagnostic priorDiagnostic)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        if (!string.Equals(entry.Provider, provider, StringComparison.Ordinal))
            return;
        lock (sync)
        {
            var quarantined = provider switch
            {
                "HoYoLAB" => hoyoQuarantined,
                "SKPORT" => skportQuarantined,
                _ => false,
            };
            if (!quarantined) return;
            var diagnostic =
                PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(
                    gameId,
                    priorDiagnostic,
                    preservePriorEvidence: CanPublish(provider, operation));
            if (diagnostic == PublisherResourceCaptureDiagnostic.NotAvailable)
                return;
            resources.Remove(gameId);
            resourceStates[gameId] =
                diagnostic == PublisherResourceCaptureDiagnostic.LoginRequired
                    ? PublisherResourceState.LoginRequired
                    : PublisherResourceState.Unavailable;
            resourceDiagnostics[gameId] = diagnostic;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void SetCleanupPending(string provider, bool pending)
    {
        if (provider == "HoYoLAB") Volatile.Write(ref hoyoCleanupPending, pending);
        else if (provider == "SKPORT") Volatile.Write(ref skportCleanupPending, pending);
        else throw new ArgumentOutOfRangeException(nameof(provider));
    }

    private PublisherGeneration GenerationFor(string provider) => provider == "HoYoLAB"
        ? hoyoGeneration
        : provider == "SKPORT"
            ? skportGeneration
            : throw new ArgumentOutOfRangeException(nameof(provider));

    private bool SetConsentSynchronized(string provider, bool enabled)
    {
        lock (sync)
            return consent.Set(provider, enabled);
    }

    private bool CanPublish(string provider, PublisherOperation operation) =>
        CanPublish(
            provider,
            operation.Generation,
            operation.HoyoContext,
            operation.Cancellation.Token);

    private bool CanPublish(
        string provider,
        long generation,
        HoyoOperationContext? hoyoContext,
        CancellationToken cancellationToken) =>
        consent.IsEnabled(provider)
        && GenerationFor(provider).CanPublish(generation, cancellationToken)
        && (provider != "HoYoLAB" || IsCurrentHoyoContext(hoyoContext));

    private void TrySetConnection(
        string provider,
        PublisherConnectionState state,
        PublisherOperation operation)
    {
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            if (provider == "HoYoLAB") hoyo = state;
            else skport = state;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void SetCheckInIfCurrent(
        string provider,
        PublisherOperation operation,
        DailyCheckInResult result)
    {
        if (!CanPublish(provider, operation)) return;
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            checkIns[result.GameId] = result;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveResourceIfCurrent(
        string gameId,
        string provider,
        PublisherOperation operation)
    {
        if (!CanPublish(provider, operation)) return;
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            resources.Remove(gameId);
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private bool MarkResourceStaleIfCurrent(
        string gameId,
        string provider,
        PublisherOperation operation)
    {
        if (!CanPublish(provider, operation)) return false;
        lock (sync)
        {
            if (!CanPublish(provider, operation)
                || !resources.TryGetValue(gameId, out var snapshot))
                return false;
            resources[gameId] = snapshot with { IsStale = true };
            return true;
        }
    }

    private void SetResourceStateIfCurrent(
        string gameId,
        string provider,
        PublisherOperation operation,
        PublisherResourceState state)
    {
        if (!CanPublish(provider, operation)) return;
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            resourceStates[gameId] = state;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private bool TryPublishEndfieldReview(
        PublisherEndfieldAccountIdentity identity,
        PublisherOperation operation)
    {
        lock (sync)
        {
            if (!CanPublish("SKPORT", operation)) return false;
            endfieldIdentity = identity;
            skport = PublisherConnectionState.Connected;
        }
        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void TrySetConnectionForGeneration(
        string provider,
        PublisherConnectionState state,
        long generation)
    {
        lock (sync)
        {
            if (!GenerationFor(provider).IsCurrent(generation)) return;
            if (provider == "HoYoLAB") hoyo = state;
            else if (provider == "SKPORT") skport = state;
            else return;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void SetResourceDiagnosticIfCurrent(
        string gameId,
        string provider,
        PublisherOperation operation,
        PublisherResourceCaptureDiagnostic diagnostic)
    {
        if (!CanPublish(provider, operation)) return;
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            resourceDiagnostics[gameId] = diagnostic;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private bool SetResourceIfCurrent(
        string provider,
        PublisherOperation operation,
        PublisherResourceSnapshot snapshot)
    {
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return false;
            resources[snapshot.GameId] = snapshot;
            return true;
        }
    }

    private void ClearProviderStateIfCurrent(string provider, PublisherOperation operation)
    {
        lock (sync)
        {
            if (!CanPublish(provider, operation)) return;
            var ids = provider == "HoYoLAB" ? new[] { "gi", "hsr", "zzz" } : new[] { "ae" };
            foreach (var id in ids)
            {
                resources.Remove(id);
                resourceStates.Remove(id);
                resourceDiagnostics.Remove(id);
                checkIns.Remove(id);
            }
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private DailyCheckInResult SetCheckInResult(
        string gameId,
        DailyCheckInState state,
        string message)
    {
        var result = new DailyCheckInResult(gameId, state, message, DateTimeOffset.UtcNow);
        lock (sync)
            checkIns[gameId] = result;
        Updated?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void SetCouldNotCheck(string gameId, string message)
    {
        lock (sync)
            checkIns[gameId] = new(gameId, DailyCheckInState.CouldNotCheck, message, DateTimeOffset.UtcNow);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private bool HasUsableHoyoAccount()
    {
        if (hoyoSlotManagerAvailable)
        {
            var persisted = hoyoSlots.TryLoad();
            if (persisted is null) return false;
            lock (sync)
            {
                return activeHoyoSlot is not null
                    && string.Equals(
                        persisted.ActiveSlotId,
                        activeHoyoSlot.Id,
                        StringComparison.Ordinal);
            }
        }
        lock (sync)
            return hoyoLegacyCompatibilityAvailable
                && hoyoSlots.IsLegacyCompatibilityStillSafe();
    }

    private HoyoLabAccountSlot? FindHoyoSlot(string slotId) =>
        HoyoLabAccountSlotRules.IsValidSlotId(slotId)
            ? hoyoSlots.CurrentIndex?.Slots.SingleOrDefault(slot =>
                string.Equals(slot.Id, slotId, StringComparison.Ordinal))
            : null;

    private HoyoLabAccountSlot? FindUsableHoyoSlot(string slotId)
    {
        var slot = FindHoyoSlot(slotId);
        return slot is not null && !slot.RemovalPending ? slot : null;
    }

    private void ActivateHoyoSlot(HoyoLabAccountSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        RefreshActiveHoyoSlot();
        if (!string.Equals(activeHoyoSlot?.Id, slot.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected HoYoLAB account could not be activated.");
    }

    private bool EnsureHoyoSlotManagerInitialized()
    {
        lock (sync)
        {
            if (hoyoSlotManagerAvailable) return hoyoSlots.TryLoad() is not null;
            if (hoyoLegacyCompatibilityAvailable)
                return hoyoSlots.IsLegacyCompatibilityStillSafe();
        }
        var initialization = hoyoSlots.TryInitialize();
        if (!initialization.IsReady)
        {
            lock (sync)
                hoyoLegacyCompatibilityAvailable = initialization.State
                    == HoyoLabAccountSlotInitializationState.LegacyCompatibility;
            return hoyoLegacyCompatibilityAvailable;
        }
        lock (sync)
        {
            hoyoSlotManagerAvailable = true;
            hoyoLegacyCompatibilityAvailable = false;
            activeHoyoSlot = initialization.Index!.ActiveSlotId is { } activeId
                ? initialization.Index.Slots.SingleOrDefault(slot =>
                    string.Equals(slot.Id, activeId, StringComparison.Ordinal)
                    && !slot.RemovalPending)
                : null;
            var protectedRoot = ResolveCurrentHoyoProtectedStateRootOrLegacy();
            roleBindings = new(protectedRoot);
            resourceSnapshots = new(protectedRoot);
            hoyoGameBundle = new(protectedRoot);
        }
        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RefreshActiveHoyoSlot()
    {
        lock (sync)
        {
            var index = hoyoSlots.CurrentIndex;
            activeHoyoSlot = index?.ActiveSlotId is { } activeId
                ? index.Slots.SingleOrDefault(slot =>
                    string.Equals(slot.Id, activeId, StringComparison.Ordinal)
                    && !slot.RemovalPending)
                : null;
            var protectedRoot = ResolveCurrentHoyoProtectedStateRootOrLegacy();
            roleBindings = new(protectedRoot);
            resourceSnapshots = new(protectedRoot);
            hoyoGameBundle = new(protectedRoot);
        }
    }

    private string ResolveCurrentHoyoProtectedStateRootOrLegacy()
    {
        if (activeHoyoSlot is not null
            && hoyoSlots.TryGetProtectedStateRoot(activeHoyoSlot, out var protectedRoot))
            return protectedRoot;
        return root;
    }

    private HoyoOperationContext? CaptureHoyoContext()
    {
        if (hoyoSlotManagerAvailable)
        {
            var persisted = hoyoSlots.TryLoad();
            if (persisted is null) return null;
            lock (sync)
            {
                if (!string.Equals(
                        persisted.ActiveSlotId,
                        activeHoyoSlot?.Id,
                        StringComparison.Ordinal))
                    return null;
            }
        }
        lock (sync) return CaptureHoyoContextNoLock();
    }

    private HoyoOperationContext? CaptureHoyoContextNoLock()
    {
        if (hoyoSlotManagerAvailable)
        {
            if (activeHoyoSlot is null
                || !hoyoSlots.TryGetWebView2ProfilePath(activeHoyoSlot, out var profile)
                || !hoyoSlots.TryGetProtectedStateRoot(activeHoyoSlot, out var protectedRoot))
                return null;
            return new(activeHoyoSlot.Id, profile, protectedRoot, LegacyCompatibility: false);
        }
        if (!hoyoLegacyCompatibilityAvailable
            || !hoyoSlots.IsLegacyCompatibilityStillSafe())
            return null;
        var legacyProfile = Path.GetFullPath(Path.Combine(root, "HoYoLAB"));
        if (!IsSafePublisherProfilePath(legacyProfile, allowMissingLeaf: true)) return null;
        return new(null, legacyProfile, root, LegacyCompatibility: true);
    }

    private bool IsCurrentHoyoContext(HoyoOperationContext? context)
    {
        if (context is null) return false;
        lock (sync)
        {
            var current = CaptureHoyoContextNoLock();
            return current is not null
                && string.Equals(current.SlotId, context.SlotId, StringComparison.Ordinal)
                && string.Equals(current.ProfilePath, context.ProfilePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.ProtectedStateRoot, context.ProtectedStateRoot, StringComparison.OrdinalIgnoreCase)
                && current.LegacyCompatibility == context.LegacyCompatibility;
        }
    }

    private PublisherRoleRecord? TryLoadRoleRecord(
        string gameId,
        PublisherOperation operation)
    {
        if (!CanPublish("HoYoLAB", operation)) return null;
        var record = roleBindings.TryLoadRecord(gameId);
        if (record is not null) return record;
        var context = operation.HoyoContext;
        var active = activeHoyoSlot;
        if (context is null
            || active is null
            || !active.IsLegacy
            || hoyoSlots.CurrentIndex?.LegacyFallback != true)
            return null;
        var legacy = new PublisherRoleBindingStore(root).TryLoadRecord(gameId);
        if (legacy is not null && CanPublish("HoYoLAB", operation))
            roleBindings.SaveRecord(gameId, legacy);
        return legacy;
    }

    private bool SaveRoleBinding(
        string gameId,
        PublisherRoleBinding binding,
        PublisherOperation operation)
    {
        if (!CanPublish("HoYoLAB", operation) || !roleBindings.Save(gameId, binding))
            return false;
        if (gameId == HoyoLabGameBundleRules.GameId)
        {
            var record = roleBindings.TryLoadRecord(gameId);
            if (record is not null) _ = TryMirrorHsrRole(record, operation);
        }
        return true;
    }

    private bool TryMigrateHsrBundleFromV1(PublisherOperation operation)
    {
        if (!CanPublish("HoYoLAB", operation)) return false;
        var role = roleBindings.TryLoadRecord(HoyoLabGameBundleRules.GameId);
        if (role is null) return false;
        var resource = resourceSnapshots.TryLoad(HoyoLabGameBundleRules.GameId, role.Binding);
        lock (sync)
        {
            if (!CanPublish("HoYoLAB", operation)) return false;
            var saved = hoyoGameBundle.TryMigrateFromV1(
                role,
                resource,
                resource is null ? null : role.Binding,
                operation.Cancellation.Token);
            return saved && CanPublish("HoYoLAB", operation);
        }
    }

    private bool TryMirrorHsrRole(
        PublisherRoleRecord role,
        PublisherOperation operation)
    {
        _ = TryMigrateHsrBundleFromV1(operation);
        lock (sync)
        {
            if (!CanPublish("HoYoLAB", operation)) return false;
            var saved = hoyoGameBundle.TrySelectRole(
                role,
                operation.Cancellation.Token);
            return saved && CanPublish("HoYoLAB", operation);
        }
    }

    private bool TryMirrorHsrResource(
        PublisherRoleBinding binding,
        PublisherResourceSnapshot resource,
        PublisherOperation operation)
    {
        lock (sync)
        {
            if (!CanPublish("HoYoLAB", operation)) return false;
            var saved = hoyoGameBundle.TryRecordResource(
                binding,
                resource with { IsStale = true },
                operation.Cancellation.Token);
            return saved && CanPublish("HoYoLAB", operation);
        }
    }

    private bool TryMirrorHsrAchievements(
        PublisherRoleBinding binding,
        IReadOnlyList<long> completedIds,
        PublisherOperation operation)
    {
        var now = DateTimeOffset.UtcNow;
        var observedAt = new DateTimeOffset(
            now.Ticks - now.Ticks % TimeSpan.TicksPerSecond,
            TimeSpan.Zero);
        lock (sync)
        {
            if (!CanPublish("HoYoLAB", operation)) return false;
            var saved = hoyoGameBundle.TryRecordCompletedAchievements(
                binding,
                completedIds,
                observedAt,
                operation.Cancellation.Token);
            return saved && CanPublish("HoYoLAB", operation);
        }
    }

    private PublisherResourceSnapshot? TryLoadResourceSnapshot(
        string gameId,
        PublisherRoleBinding binding,
        PublisherOperation operation)
    {
        if (!CanPublish("HoYoLAB", operation)) return null;
        var snapshot = resourceSnapshots.TryLoad(gameId, binding);
        if (snapshot is not null) return snapshot;
        var active = activeHoyoSlot;
        if (active is null
            || !active.IsLegacy
            || hoyoSlots.CurrentIndex?.LegacyFallback != true)
            return null;
        var legacy = new PublisherResourceSnapshotStore(root).TryLoad(gameId, binding);
        if (legacy is not null && CanPublish("HoYoLAB", operation))
            resourceSnapshots.Save(legacy with { IsStale = false }, binding);
        return legacy;
    }

    private bool SaveRoleRecord(
        string gameId,
        PublisherRoleBinding binding,
        IReadOnlyCollection<PublisherResourceCandidate> candidates,
        PublisherOperation operation)
    {
        if (!CanPublish("HoYoLAB", operation)) return false;
        var nicknames = candidates
            .Where(candidate => candidate.Binding == binding)
            .Select(candidate => candidate.Nickname)
            .Where(nickname => nickname is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (nicknames.Length > 1) return false;
        var record = new PublisherRoleRecord(
            binding,
            nicknames.SingleOrDefault(),
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server));
        if (!roleBindings.SaveRecord(gameId, record)) return false;
        if (gameId == HoyoLabGameBundleRules.GameId)
            _ = TryMirrorHsrRole(record, operation);
        return true;
    }

    private static bool RoleRecordNeedsRefresh(
        PublisherRoleRecord? storedRecord,
        PublisherRoleBinding binding,
        IReadOnlyCollection<PublisherResourceCandidate> candidates)
    {
        if (storedRecord is null || storedRecord.Binding != binding) return true;
        var matchingCandidates = candidates
            .Where(candidate => candidate.Binding == binding)
            .ToArray();
        if (matchingCandidates.Length == 0) return false;
        var officialNicknames = matchingCandidates
            .Select(candidate => candidate.Nickname)
            .Where(nickname => nickname is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (officialNicknames.Length > 1) return true;
        if (officialNicknames.Length == 0) return storedRecord.Nickname is null;
        return !string.Equals(
                storedRecord.Nickname,
                officialNicknames[0],
                StringComparison.Ordinal)
            || !string.Equals(
                storedRecord.ReadableRegion,
                PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server),
                StringComparison.Ordinal);
    }

    private bool TryResolveProfilePath(string provider, out string profile)
    {
        if (provider == "HoYoLAB")
        {
            var context = CaptureHoyoContext();
            profile = context?.ProfilePath ?? string.Empty;
            return context is not null;
        }
        profile = ResolveProfilePath(provider);
        return true;
    }

    private bool IsSafePublisherProfilePath(string path, bool allowMissingLeaf)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                return false;
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var current = root;
            var components = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    return allowMissingLeaf;
                }
                catch (DirectoryNotFoundException)
                {
                    return allowMissingLeaf;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private string ResolveProfilePath(string provider)
    {
        if (provider == "HoYoLAB")
            return CaptureHoyoContext()?.ProfilePath
                ?? throw new InvalidOperationException("No HoYoLAB account is selected.");
        var leaf = provider switch
        {
            "SKPORT" => "SKPORT",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        var profile = Path.GetFullPath(Path.Combine(root, leaf));
        if (!string.Equals(Path.GetRelativePath(root, profile), leaf, StringComparison.Ordinal))
            throw new InvalidOperationException("Publisher profile path escaped the Nyx data folder.");
        if (!IsSafePublisherProfilePath(profile, allowMissingLeaf: true))
            throw new InvalidOperationException("Publisher profile path cannot be a reparse point.");
        return profile;
    }

    private bool TryDeleteManagedDirectory(string path)
    {
        var accountsRoot = Path.GetFullPath(Path.Combine(root, "Accounts", "HoYoLAB"));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(
                accountsRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return false;
        return TryDeleteExactDirectory(fullPath);
    }

    private bool TryDeleteAllHoyoState(PublisherOperation operation)
    {
        var accountsRoot = Path.GetFullPath(Path.Combine(root, "Accounts", "HoYoLAB"));
        var legacyProfile = Path.GetFullPath(Path.Combine(root, "HoYoLAB"));
        var legacyRoles = Path.GetFullPath(Path.Combine(root, ".protected-role-bindings"));
        var legacyResources = Path.GetFullPath(Path.Combine(root, ".protected-resource-snapshots"));
        var legacyBundles = Path.GetFullPath(Path.Combine(root, ".protected-hoyolab-game-bundles"));
        var cleaned = CanDeleteAllHoyoProtectedState(operation)
            && TryDeleteExactDirectory(accountsRoot)
            && TryDeleteExactDirectory(legacyProfile)
            && TryDeleteExactDirectory(legacyRoles)
            && TryDeleteExactDirectory(legacyResources)
            && CanDeleteAllHoyoProtectedState(operation)
            && TryDeleteExactDirectory(legacyBundles);
        return cleaned && hoyoSlots.TryDeleteIndex();
    }

    private bool TryDeleteExactDirectory(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            return false;
        try
        {
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(fullPath);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var pending = new Stack<string>();
            pending.Push(fullPath);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            Directory.Delete(fullPath, recursive: true);
            return !Directory.Exists(fullPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    private PublisherPasswordStoragePolicy PasswordStorageFor(string provider) => provider switch
    {
        "HoYoLAB" => hoyoPasswordStorage,
        "SKPORT" => skportPasswordStorage,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        shutdown.Cancel();
        var oldHoyoSession = RotateSession("HoYoLAB");
        var oldSkportSession = RotateSession("SKPORT");
        await Task.WhenAll(oldHoyoSession.CancelAsync(), oldSkportSession.CancelAsync());
        var allProviderWorkStopped = false;
        try
        {
            await Task.WhenAll(hoyoGate.WaitAsync(), skportGate.WaitAsync())
                .WaitAsync(TimeSpan.FromSeconds(15));
            allProviderWorkStopped = true;
        }
        catch (TimeoutException)
        {
            // Keep the named profile lease and live synchronization objects.
            // Process teardown may continue, but another process cannot reuse
            // a folder while an old WebView might still own it.
        }
        lock (sync)
        {
            endfieldIdentity = null;
            resources.Clear();
            checkIns.Clear();
        }
        if (!allProviderWorkStopped) return;
        hoyoGate.Dispose();
        skportGate.Dispose();
        oldHoyoSession.Dispose();
        oldSkportSession.Dispose();
        hoyoSession.Dispose();
        skportSession.Dispose();
        if (ownsHoyoProfile && !hoyoQuarantined)
            hoyoProfileOwner.Release();
        if (ownsSkportProfile && !skportQuarantined)
            skportProfileOwner.Release();
        if (!hoyoQuarantined)
            hoyoProfileOwner.Dispose();
        if (!skportQuarantined)
            skportProfileOwner.Dispose();
        shutdown.Dispose();
    }

    private sealed class PublisherOperation(
        CancellationTokenSource cancellation,
        long generation,
        HoyoOperationContext? hoyoContext) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long Generation { get; } = generation;
        public HoyoOperationContext? HoyoContext { get; } = hoyoContext;

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed record HoyoOperationContext(
        string? SlotId,
        string ProfilePath,
        string ProtectedStateRoot,
        bool LegacyCompatibility)
    {
        public override string ToString() => nameof(HoyoOperationContext);
    }

    private sealed class PublisherAchievementExportPublishAuthority(
        PublisherAccountService owner,
        string provider,
        long generation,
        HoyoOperationContext? hoyoContext,
        CancellationToken cancellationToken) : IAchievementExportPublishAuthority
    {
        public bool IsCurrent
        {
            get
            {
                lock (owner.sync)
                    return owner.CanPublish(provider, generation, hoyoContext, cancellationToken);
            }
        }

        public bool TryPublish(Action publish)
        {
            ArgumentNullException.ThrowIfNull(publish);
            lock (owner.sync)
            {
                if (!owner.CanPublish(provider, generation, hoyoContext, cancellationToken))
                    return false;
                publish();
                return true;
            }
        }
    }
}
