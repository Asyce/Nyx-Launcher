namespace Nyx.Desktop.Tests.AccountStatus;

using Nyx.Desktop.Core.AccountStatus;

public sealed class HoyoLabAccountSlotServiceTests
{
    private static readonly string Service = File.ReadAllText(FindRepositoryFile(
        "Desktop",
        "src",
        "Nyx.Desktop.App",
        "PublisherAccountService.cs"));

    [Fact]
    public void Service_exposes_slot_management_without_launcher_state_or_ui_coupling()
    {
        Assert.Contains("private readonly HoyoLabAccountSlotStore hoyoSlots", Service, StringComparison.Ordinal);
        Assert.Contains("hoyoSlots.TryInitialize()", Service, StringComparison.Ordinal);
        Assert.Contains("public HoyoLabAccountSlotManagerState HoyoLabAccounts", Service, StringComparison.Ordinal);
        Assert.Contains("public HoyoLabAccountIdentity? GetHoyoLabIdentity", Service, StringComparison.Ordinal);
        Assert.Contains("AddHoyoLabAccountAsync", Service, StringComparison.Ordinal);
        Assert.Contains("RenameHoyoLabAccountAsync", Service, StringComparison.Ordinal);
        Assert.Contains("UseHoyoLabAccountAsync", Service, StringComparison.Ordinal);
        Assert.Contains("ForgetHoyoLabAccountAsync", Service, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherState", Service, StringComparison.Ordinal);
        Assert.DoesNotContain("MainPage", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_projection_prefers_nickname_and_formats_bound_character()
    {
        var slot = Slot("Local account");
        var identity = HoyoLabAccountIdentity.Create(
            "gi",
            slot,
            new(
                new("123456789", "os_euro"),
                "Paimon",
                "Europe"));

        Assert.True(identity.IsBound);
        Assert.Equal("Paimon", identity.DisplayName);
        Assert.Equal("123456789", identity.FullUid);
        Assert.Equal("Europe", identity.ReadableRegion);
        Assert.Equal("Paimon · 123456789 · Europe", identity.CharacterSummary);
    }

    [Fact]
    public void Identity_projection_uses_local_label_and_choose_character_when_unbound()
    {
        var identity = HoyoLabAccountIdentity.Create("hsr", Slot("Main account"), null);

        Assert.False(identity.IsBound);
        Assert.Equal("Main account", identity.DisplayName);
        Assert.Null(identity.FullUid);
        Assert.Null(identity.ReadableRegion);
        Assert.Equal("Main account · Choose Region", identity.CharacterSummary);
    }

    [Fact]
    public void Identity_projection_preserves_v1_binding_shape_for_later_enrichment()
    {
        var legacyV1Shape = new PublisherRoleRecord(
            new("800000001", "prod_official_asia"),
            Nickname: null,
            ReadableRegion: "Asia");

        var identity = HoyoLabAccountIdentity.Create("hsr", Slot("Old account"), legacyV1Shape);

        Assert.Equal("Old account", identity.DisplayName);
        Assert.Equal("800000001", identity.FullUid);
        Assert.Equal("Asia", identity.ReadableRegion);
        Assert.Equal("Old account · 800000001 · Asia", identity.CharacterSummary);
    }

    [Fact]
    public void Identity_projection_and_service_ToString_do_not_expose_identity_values()
    {
        var identity = HoyoLabAccountIdentity.Create(
            "zzz",
            Slot("Private slot"),
            new(new("13000000001", "prod_gf_us"), "Secret nickname", "Americas"));

        Assert.Equal(nameof(HoyoLabAccountIdentity), identity.ToString());
        Assert.DoesNotContain(identity.SlotId, identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.LocalLabel, identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Nickname!, identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.FullUid!, identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.ReadableRegion!, identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Service_identity_reads_only_the_exact_revalidated_active_slot_store()
    {
        var method = Slice("public HoyoLabAccountIdentity? GetHoyoLabIdentity", "public async Task<PublisherConnectionState> AddHoyoLabAccountAsync");
        AssertOrdered(
            method,
            "lock (sync)",
            "hoyoSlots.TryLoad()",
            "persisted?.ActiveSlotId",
            "hoyoSlots.TryGetProtectedStateRoot(active",
            "new PublisherRoleBindingStore(protectedRoot).TryLoadRecord(gameId)",
            "hoyoSlots.TryLoad()",
            "revalidated?.ActiveSlotId",
            "revalidatedActive != active",
            "HoyoLabAccountIdentity.Create(gameId, active, record)");
        Assert.DoesNotContain("roleBindings.TryLoadRecord", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherAccountSummary", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Immutable_operation_context_captures_exact_slot_paths_and_guards_publication()
    {
        Assert.Contains("private sealed record HoyoOperationContext(", Service, StringComparison.Ordinal);
        Assert.Contains("string ProfilePath", Service, StringComparison.Ordinal);
        Assert.Contains("string ProtectedStateRoot", Service, StringComparison.Ordinal);
        Assert.Contains("public override string ToString() => nameof(HoyoOperationContext)", Service, StringComparison.Ordinal);
        Assert.Contains("operation.HoyoContext", Service, StringComparison.Ordinal);
        Assert.Contains("IsCurrentHoyoContext(hoyoContext)", Service, StringComparison.Ordinal);
        Assert.Contains("CreateWindow(entry.Provider, operation)", Service, StringComparison.Ordinal);
        Assert.Contains("operation.HoyoContext?.ProfilePath", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_cancels_then_persists_clears_loads_and_probes_in_order()
    {
        var method = Slice("public async Task<PublisherConnectionState> UseHoyoLabAccountAsync", "public async Task<bool> ForgetHoyoLabAccountAsync");
        AssertOrdered(
            method,
            "BeginRotatedOperation",
            "previousSession.CancelAsync",
            "gate.WaitAsync",
            "TrySetActiveSlot",
            "RefreshActiveHoyoSlot",
            "ClearProviderState",
            "RestoreCachedResources",
            "ProbeConnectionCoreAsync");
        Assert.DoesNotContain("First(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_and_use_publish_terminal_review_state_after_window_or_probe_failure()
    {
        var add = Slice("public async Task<PublisherConnectionState> AddHoyoLabAccountAsync", "public async Task<bool> RenameHoyoLabAccountAsync");
        AssertOrdered(
            add,
            "selectedOperation = CreateOperation",
            "PublisherConnectionState.Connecting",
            "catch",
            "TrySetConnectionForGeneration(",
            "PublisherConnectionState.NeedsReview",
            "rotationOperation.Generation");

        var use = Slice("public async Task<PublisherConnectionState> UseHoyoLabAccountAsync", "public async Task<bool> ForgetHoyoLabAccountAsync");
        AssertOrdered(
            use,
            "selectedOperation = CreateOperation",
            "PublisherConnectionState.Connecting",
            "ProbeConnectionCoreAsync",
            "catch",
            "TrySetConnectionForGeneration(",
            "PublisherConnectionState.NeedsReview",
            "rotationOperation.Generation");
    }

    [Fact]
    public void Explicit_local_removal_detaches_sync_then_clears_only_the_revalidated_account()
    {
        var method = Slice("public async Task<bool> ForgetHoyoLabAccountAsync", "public void ApplyPasswordSavingPreference");
        var syncService = File.ReadAllText(FindRepositoryFile(
            "Desktop", "src", "Nyx.Desktop.App", "PublisherAccountService.HoyoSync.cs"));
        var cleanup = syncService[syncService.IndexOf("private bool TryRemoveHoyoSlotLocally", StringComparison.Ordinal)..];
        AssertOrdered(method, "removeEverywhere: false", "previousSession.CancelAsync", "gate.WaitAsync",
            "hoyoSlots.TryGetProtectedStateRoot", "syncCleanup.Detach", "TryPublishHoyoSyncCleanup(operation",
            "TryRemoveHoyoSlotLocally", "&& removed");
        AssertOrdered(cleanup, "OwnsProfile(\"HoYoLAB\")", "hoyoSlots.TryLoad()", "hoyoSlots.IsSlotRemoved",
            "TryMarkRemovalPending", "TryGetWebView2ProfilePath", "TryDeleteExactDirectory(legacyProfile)",
            "TryGetSlotContainerPath", "TryDeleteManagedDirectory", "TryRemoveSlot");
        Assert.Contains("target.RemovalPending", cleanup, StringComparison.Ordinal);
        Assert.Contains("target.IsLegacy", cleanup, StringComparison.Ordinal);
        Assert.Contains("new PublisherRoleBindingStore(root).DeleteProvider(\"HoYoLAB\")", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(root, \"HoYoLAB\")", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DeletePendingAsync", cleanup, StringComparison.Ordinal);
        Assert.Contains("SetConnection(\"HoYoLAB\", PublisherConnectionState.NotConnected)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Revocation_deletes_all_hoyo_roots_and_index_last()
    {
        var method = Slice("private bool TryDeleteAllHoyoState(PublisherOperation operation)", "private bool TryDeleteExactDirectory");
        Assert.Contains("Accounts\", \"HoYoLAB", method, StringComparison.Ordinal);
        Assert.Contains(".protected-role-bindings", method, StringComparison.Ordinal);
        Assert.Contains(".protected-resource-snapshots", method, StringComparison.Ordinal);
        Assert.Contains(".protected-hoyolab-game-bundles", method, StringComparison.Ordinal);
        Assert.Contains("CanDeleteAllHoyoProtectedState(operation)", method, StringComparison.Ordinal);
        AssertOrdered(method, "TryDeleteExactDirectory(legacyResources)", "hoyoSlots.TryDeleteIndex()");
    }

    [Fact]
    public void Hsr_bundle_public_surface_is_slot_revalidated_and_supports_only_proven_consents()
    {
        var snapshot = Slice(
            "public async Task<HoyoLabGameBundle?> GetHsrGameBundleSnapshotAsync",
            "public async Task<bool> SetHsrCapabilityConsentAsync");
        AssertOrdered(
            snapshot,
            "CreateOperation(\"HoYoLAB\"",
            "gate.WaitAsync",
            "ProfileAccessAllowedAfterGate",
            "TryMigrateHsrBundleFromV1(operation)",
            "CanPublish(\"HoYoLAB\", operation)",
            "hoyoGameBundle.TryLoad()",
            "snapshot is not null && CanPublish(\"HoYoLAB\", operation)");

        var setter = Slice(
            "public async Task<bool> SetHsrCapabilityConsentAsync",
            "public HoyoLabAccountIdentity? GetHoyoLabIdentity");
        Assert.Contains("HoyoLabGameBundleRules.Resources", setter, StringComparison.Ordinal);
        Assert.Contains("HoyoLabGameBundleRules.Achievements", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("HoyoLabGameBundleRules.Inventory", setter, StringComparison.Ordinal);
        AssertOrdered(
            setter,
            "gate.WaitAsync",
            "ProfileAccessAllowedAfterGate",
            "TryMigrateHsrBundleFromV1(operation)",
            "CanPublish(\"HoYoLAB\", operation)",
            "hoyoGameBundle.TrySetCapabilityConsent(");
    }

    [Fact]
    public void V1_remains_authoritative_while_hsr_bundle_mirrors_are_best_effort()
    {
        var helpers = Slice(
            "private bool TryMigrateHsrBundleFromV1",
            "private PublisherResourceSnapshot? TryLoadResourceSnapshot");
        AssertOrdered(
            helpers,
            "roleBindings.TryLoadRecord(HoyoLabGameBundleRules.GameId)",
            "resourceSnapshots.TryLoad(HoyoLabGameBundleRules.GameId, role.Binding)",
            "CanPublish(\"HoYoLAB\", operation)",
            "hoyoGameBundle.TryMigrateFromV1(");
        Assert.Contains("var saved = hoyoGameBundle.TrySelectRole(", helpers, StringComparison.Ordinal);
        Assert.Contains("var saved = hoyoGameBundle.TryRecordResource", helpers, StringComparison.Ordinal);
        Assert.Contains("var saved = hoyoGameBundle.TryRecordCompletedAchievements", helpers, StringComparison.Ordinal);

        var refresh = Slice(
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        AssertOrdered(
            refresh,
            "resourceRead.Candidates is { Count: 1 } selectedSingleRole",
            "activeBinding = selectedSingleRole[0].Binding",
            "SaveRoleRecord(",
            "resourceSnapshots.Save(snapshot with { IsStale = false }, activeBinding)",
            "TryMirrorHsrResource(activeBinding, snapshot, operation)",
            "return CanPublish(entry.Provider, operation) ? snapshot : null");

        var export = Slice(
            "private async Task<ExportArtifactMetadata> ExportHsrAchievementsCoreAsync",
            "private async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsWithVisibleRecoveryAsync");
        AssertOrdered(
            export,
            "SaveRoleBinding(gameId, result.Role, operation)",
            "achievementWriter.WriteAsync(",
            "TryMirrorHsrAchievements(result.Role, result.AchievementIds, operation)",
            "return artifact");
    }

    [Fact]
    public void Legacy_compatibility_never_reads_writes_or_migrates_the_v2_bundle()
    {
        var availability = Slice(
            "private bool CanUseHsrGameBundle",
            "private bool CanDeleteAllHoyoProtectedState");
        Assert.Contains("hoyoSlotManagerAvailable", availability, StringComparison.Ordinal);
        Assert.Contains("LegacyCompatibility: false", availability, StringComparison.Ordinal);
        Assert.Contains("CanMutateHoyoProtectedState(operation)", availability, StringComparison.Ordinal);

        var snapshot = Slice(
            "public async Task<HoyoLabGameBundle?> GetHsrGameBundleSnapshotAsync",
            "public async Task<bool> SetHsrCapabilityConsentAsync");
        AssertOrdered(
            snapshot,
            "ProfileAccessAllowedAfterGate",
            "CanUseHsrGameBundle(operation)",
            "TryMigrateHsrBundleFromV1(operation)",
            "hoyoGameBundle.TryLoad()");

        var setter = Slice(
            "public async Task<bool> SetHsrCapabilityConsentAsync",
            "public HoyoLabAccountIdentity? GetHoyoLabIdentity");
        AssertOrdered(
            setter,
            "ProfileAccessAllowedAfterGate",
            "CanUseHsrGameBundle(operation)",
            "TryMigrateHsrBundleFromV1(operation)",
            "hoyoGameBundle.TrySetCapabilityConsent(");

        var helpers = Slice(
            "private bool TryMigrateHsrBundleFromV1",
            "private PublisherResourceSnapshot? TryLoadResourceSnapshot");
        Assert.Equal(4, helpers.Split(
            "CanUseHsrGameBundle(operation)",
            StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Exact_hsr_role_cleanup_tombstones_v2_before_v1_and_rechecks_the_slot()
    {
        var cleanup = Slice(
            "private bool TryDeleteProtectedGameState(",
            "private bool TryDeleteProtectedProviderState(");
        AssertOrdered(
            cleanup,
            "operation?.HoyoContext is { LegacyCompatibility: false }",
            "lock (sync)",
            "CanUseHsrGameBundle(operation)",
            "hoyoGameBundle.TryDeleteRole(binding, operation.Cancellation.Token)",
            "CanUseHsrGameBundle(operation)",
            "PublisherProtectedStateDeletionPolicy.TryDeleteGameState(");
        Assert.Contains("catch (OperationCanceledException)", cleanup, StringComparison.Ordinal);
        Assert.Contains("QuarantineProvider(provider, operation)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Best_effort_hsr_mirrors_contain_store_cancellation_after_v1_success()
    {
        foreach (var helper in new[]
                 {
                     Slice("private bool TryMirrorHsrRole", "private bool TryMirrorHsrResource"),
                     Slice("private bool TryMirrorHsrResource", "private bool TryMirrorHsrAchievements"),
                     Slice("private bool TryMirrorHsrAchievements", "private PublisherResourceSnapshot? TryLoadResourceSnapshot"),
                 })
        {
            Assert.Contains("try", helper, StringComparison.Ordinal);
            Assert.Contains("operation.Cancellation.Token", helper, StringComparison.Ordinal);
            Assert.Contains("catch (OperationCanceledException)", helper, StringComparison.Ordinal);
            Assert.Contains("return false", helper, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void V2_mutations_hold_the_generation_lock_through_store_write_and_final_recheck()
    {
        var setter = Slice(
            "public async Task<bool> SetHsrCapabilityConsentAsync",
            "public HoyoLabAccountIdentity? GetHoyoLabIdentity");
        AssertLockedMutation(setter, "hoyoGameBundle.TrySetCapabilityConsent");

        var migration = Slice(
            "private bool TryMigrateHsrBundleFromV1",
            "private bool TryMirrorHsrRole");
        AssertLockedMutation(migration, "hoyoGameBundle.TryMigrateFromV1");

        var role = Slice("private bool TryMirrorHsrRole", "private bool TryMirrorHsrResource");
        AssertLockedMutation(role, "hoyoGameBundle.TrySelectRole");

        var resource = Slice(
            "private bool TryMirrorHsrResource",
            "private bool TryMirrorHsrAchievements");
        AssertLockedMutation(resource, "hoyoGameBundle.TryRecordResource");

        var achievements = Slice(
            "private bool TryMirrorHsrAchievements",
            "private PublisherResourceSnapshot? TryLoadResourceSnapshot");
        AssertLockedMutation(achievements, "hoyoGameBundle.TryRecordCompletedAchievements");

        var rotation = Slice(
            "PublisherProfileMutationSnapshot ProfileSnapshot) BeginRotatedOperation(",
            "private CancellationTokenSource RotateSession");
        AssertOrdered(rotation, "lock (sync)", "GenerationFor(provider).Advance()");
        var sessionRotation = Slice(
            "private CancellationTokenSource RotateSession",
            "private void ApplyProviderConsentSnapshot");
        AssertOrdered(sessionRotation, "lock (sync)", "GenerationFor(provider).Advance()");
    }

    [Fact]
    public void Disconnect_returns_the_state_that_protected_cleanup_actually_committed()
    {
        var disconnect = Slice(
            "private async Task<PublisherConnectionState> DisconnectCoreAsync",
            "private SemaphoreSlim GateFor");
        Assert.Contains("return CommitInterruptedProfileChange(", disconnect, StringComparison.Ordinal);
        Assert.Contains("return CommitDeletedProfile(entry.Provider, operation)", disconnect, StringComparison.Ordinal);
        AssertOrdered(
            disconnect,
            "CanCommitInterruptedProfileChange(entry.Provider, operation, enteredGate)",
            "return CommitInterruptedProfileChange(",
            "return PublisherConnectionState.NeedsReview");

        var commit = Slice(
            "private PublisherConnectionState CommitInterruptedProfileChange(",
            "private Task DeleteProfileDirectoryAsync(");
        AssertOrdered(
            commit,
            "TryDeleteCapturedHoyoProtectedState(operation)",
            "QuarantineProvider(provider)",
            "return PublisherConnectionState.NeedsReview");
        AssertOrdered(
            commit,
            "TryDeleteProtectedProviderState(provider, operation)",
            "return PublisherConnectionState.NeedsReview");
        Assert.Contains("return terminalState", commit, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_store_rotates_with_the_exact_slot_and_all_provider_cleanup_paths_include_it()
    {
        var constructor = Slice("public PublisherAccountService(", "public event EventHandler? Updated");
        Assert.Contains("hoyoGameBundle = new(protectedStateRoot)", constructor, StringComparison.Ordinal);

        var rotation = Slice("private void RefreshActiveHoyoSlot()", "private string ResolveCurrentHoyoProtectedStateRootOrLegacy");
        AssertOrdered(
            rotation,
            "ResolveCurrentHoyoProtectedStateRootOrLegacy()",
            "roleBindings = new(protectedRoot)",
            "resourceSnapshots = new(protectedRoot)",
            "hoyoGameBundle = new(protectedRoot)");

        var quarantine = Slice("private void QuarantineProvider(", "private bool TryDeleteProtectedGameState(");
        Assert.Contains("CanMutateHoyoProtectedState(operation)", quarantine, StringComparison.Ordinal);
        Assert.Contains("hoyoGameBundle.TryDelete()", quarantine, StringComparison.Ordinal);

        var providerDelete = Slice("private bool TryDeleteProtectedProviderState(", "private bool CanMutateHoyoProtectedState");
        AssertOrdered(
            providerDelete,
            "PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(",
            "CanMutateHoyoProtectedState(operation)",
            "hoyoGameBundle.TryDelete()");

        var interrupted = Slice(
            "private PublisherConnectionState CommitInterruptedProfileChange(",
            "private Task DeleteProfileDirectoryAsync(");
        AssertOrdered(
            interrupted,
            "provider == \"HoYoLAB\" && !CanMutateHoyoProtectedState(operation)",
            "TryDeleteCapturedHoyoProtectedState(operation)",
            "TryDeleteProtectedProviderState(provider, operation)",
            "if (provider == \"HoYoLAB\") hoyo = terminalState");

        var capturedDelete = Slice(
            "private bool TryDeleteCapturedHoyoProtectedState",
            "private void SetQuarantinedResourceFailure(");
        Assert.Contains("operation.HoyoContext is not { } context", capturedDelete, StringComparison.Ordinal);
        Assert.Contains("new PublisherResourceSnapshotStore(context.ProtectedStateRoot)", capturedDelete, StringComparison.Ordinal);
        Assert.Contains("new PublisherRoleBindingStore(context.ProtectedStateRoot)", capturedDelete, StringComparison.Ordinal);
        Assert.Contains("new HoyoLabGameBundleStore", capturedDelete, StringComparison.Ordinal);
        Assert.Contains("legacyDeleted && bundleDeleted", capturedDelete, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyoGameBundle.TryDelete()", capturedDelete, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_and_hsr_export_use_the_captured_slot_context()
    {
        var passwords = Slice("private async Task<bool> ClearAllHoyoSavedPasswordsAsync", "private bool TryGetHoyoPasswordCleanupTargets");
        AssertOrdered(
            passwords,
            "BeginRotatedOperation(provider",
            "previousSession.CancelAsync",
            "gate.WaitAsync",
            "TryGetHoyoPasswordCleanupTargets",
            "foreach (var profile in profiles)",
            "AreHoyoPasswordCleanupTargetsCurrent",
            "CreatePasswordCleanupWindow(profile)",
            "succeeded: true");
        Assert.Contains("return Failed()", passwords, StringComparison.Ordinal);

        var export = Slice("private async Task<ExportArtifactMetadata> ExportHsrAchievementsCoreAsync", "private static bool RequiresVisibleHsrAchievementLogin");
        Assert.Contains("TryLoadRoleRecord(gameId, operation)", export, StringComparison.Ordinal);
        Assert.Contains("operation.HoyoContext", export, StringComparison.Ordinal);
        Assert.Contains("CreateWindow(provider, operation)", export, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_cleanup_keeps_the_legacy_profile_deduplicates_and_rejects_target_changes()
    {
        var targets = Slice("private bool TryGetHoyoPasswordCleanupTargets", "private bool AreHoyoPasswordCleanupTargetsCurrent");
        AssertOrdered(
            targets,
            "hoyoSlots.TryLoad()",
            "Path.Combine(root, \"HoYoLAB\")",
            "IsSafePublisherProfilePath(indexedLegacyProfile, allowMissingLeaf: true)",
            "new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "foreach (var slot in index.Slots)",
            "hoyoSlots.TryGetWebView2ProfilePath(slot",
            "resolved.Add(profile)",
            "profiles = resolved.ToArray()");
        Assert.Contains("indexedLegacyProfile,", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("index.LegacyFallback", targets, StringComparison.Ordinal);
        Assert.Contains("hoyoSlots.IsLegacyCompatibilityStillSafe()", targets, StringComparison.Ordinal);

        var revalidate = Slice("private bool AreHoyoPasswordCleanupTargetsCurrent", "private static bool HoyoSlotIndexesMatch");
        Assert.Contains("HoyoSlotIndexesMatch(expectedIndex, current)", revalidate, StringComparison.Ordinal);
        Assert.Contains("hoyoSlots.IsLegacyCompatibilityStillSafe()", revalidate, StringComparison.Ordinal);
    }

    [Fact]
    public void Hoyo_password_shutdown_is_permanent_while_skport_keeps_the_saved_preference()
    {
        var constructor = Slice("public PublisherAccountService(", "public event EventHandler? Updated");
        AssertOrdered(
            constructor,
            "hoyoPasswordStorage = new(",
            "passwordSavingEnabled: false",
            "skportPasswordStorage = new(",
            "publisherPasswordSavingEnabled");
        Assert.Contains("HoyoProfilesNeedPasswordCleanup()", constructor, StringComparison.Ordinal);
        var preference = Slice("public void ApplyPasswordSavingPreference", "public Task<bool> ClearSavedHoyoLabPasswordsAsync");
        Assert.Contains("skportPasswordStorage.ApplyPreference(", preference, StringComparison.Ordinal);
        Assert.Contains("enabled,", preference, StringComparison.Ordinal);
        Assert.DoesNotContain("hoyoPasswordStorage", preference, StringComparison.Ordinal);
        Assert.DoesNotContain("HoyoProfilesNeedPasswordCleanup", preference, StringComparison.Ordinal);

        var need = Slice("private bool HoyoProfilesNeedPasswordCleanup", "private bool PublisherProfileEntryExistsOrUnknown");
        Assert.Contains("TryGetHoyoPasswordCleanupTargets", need, StringComparison.Ordinal);
        Assert.Contains("profiles.Select(PublisherProfileEntryExistsOrUnknown)", need, StringComparison.Ordinal);
        Assert.Contains("HoyoLabPasswordCleanupRules.RequiresCleanup", need, StringComparison.Ordinal);

        var ordinaryWindow = Slice("private PublisherSessionWindow CreateWindow", "private async Task<bool> ClearSavedSkportPasswordsCoreAsync(");
        Assert.Contains("PendingCleanup is PublisherProfileCleanupScope.PasswordsOnly", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("Every HoYoLAB account must finish password cleanup", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("provider == \"HoYoLAB\"", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("? static () => { }", ordinaryWindow, StringComparison.Ordinal);

        var hoyoCleanup = Slice("public Task<bool> ClearSavedHoyoLabPasswordsAsync", "public Task<bool> ClearSavedSkportPasswordsAsync");
        Assert.Contains("ClearAllHoyoSavedPasswordsAsync(cancellationToken)", hoyoCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("SKPORT", hoyoCleanup, StringComparison.Ordinal);
        var skportCleanup = Slice("public Task<bool> ClearSavedSkportPasswordsAsync", "public bool HasPendingConsentRevocation");
        Assert.Contains("skportPasswordStorage.ApplyPreference(", skportCleanup, StringComparison.Ordinal);
        Assert.Contains("ClearSavedSkportPasswordsCoreAsync(cancellationToken)", skportCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("HoYoLAB", skportCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAllHoyoSavedPasswordsAsync", skportCleanup, StringComparison.Ordinal);

        var skportCore = Slice("private async Task<bool> ClearSavedSkportPasswordsCoreAsync", "public async Task<PublisherEndfieldAccountReviewResult>");
        Assert.Contains("const string provider = \"SKPORT\"", skportCore, StringComparison.Ordinal);
        Assert.DoesNotContain("HoYoLAB", skportCore, StringComparison.Ordinal);
        Assert.DoesNotContain("operation.HoyoContext", skportCore, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAllHoyoSavedPasswordsAsync", skportCore, StringComparison.Ordinal);
        Assert.DoesNotContain("private async Task<bool> ClearSavedPasswordsAsync(", Service, StringComparison.Ordinal);

        var allSlotCleanup = Slice("private async Task<bool> ClearAllHoyoSavedPasswordsAsync", "private bool TryGetHoyoPasswordCleanupTargets");
        Assert.Contains("succeeded: false", allSlotCleanup, StringComparison.Ordinal);
        Assert.Contains("succeeded: true", allSlotCleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_index_state_cannot_fall_back_to_the_legacy_profile()
    {
        Assert.Contains("private bool hoyoLegacyCompatibilityAvailable", Service, StringComparison.Ordinal);
        Assert.Contains("HoyoLabAccountSlotInitializationState.LegacyCompatibility", Service, StringComparison.Ordinal);
        var usable = Slice("private bool HasUsableHoyoAccount()", "private HoyoLabAccountSlot? FindHoyoSlot");
        Assert.Contains("hoyoLegacyCompatibilityAvailable", usable, StringComparison.Ordinal);
        Assert.Contains("hoyoSlots.IsLegacyCompatibilityStillSafe()", usable, StringComparison.Ordinal);
        var capture = Slice("private HoyoOperationContext? CaptureHoyoContextNoLock()", "private bool IsCurrentHoyoContext");
        AssertOrdered(
            capture,
            "if (!hoyoLegacyCompatibilityAvailable",
            "hoyoSlots.IsLegacyCompatibilityStillSafe()",
            "Path.Combine(root, \"HoYoLAB\")",
            "IsSafePublisherProfilePath");
        var revoke = Slice("private async Task<PublisherConnectionState> DisconnectCoreAsync", "private SemaphoreSlim GateFor");
        Assert.Contains("requireSelectedSlot: consentRequired", revoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_role_rollback_reacquires_gate_and_revalidates_exact_context()
    {
        var method = Slice("public async Task<PublisherResourceSnapshot?> ChangeRoleAsync", "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync");
        AssertOrdered(
            method,
            "var result = await RefreshResourceAsync",
            "if (result is null && previous is not null)",
            "await gate.WaitAsync(operation.Cancellation.Token)",
            "ProfileAccessAllowedAfterGate",
            "operation.HoyoContext.ProtectedStateRoot",
            "exactProtectedRoot",
            "exactStore.SaveRecord(gameId, previous)",
            "QuarantineProvider(\"HoYoLAB\", operation)");
    }

    [Fact]
    public void Fresh_official_candidates_enrich_matching_v1_roles_in_refresh_and_daily()
    {
        var refresh = Slice("private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync", "public Task<DailyCheckInResult> CheckInAsync");
        Assert.Contains("var storedRecord =", refresh, StringComparison.Ordinal);
        Assert.Contains("RoleRecordNeedsRefresh(storedRecord, activeBinding, officialCandidates)", refresh, StringComparison.Ordinal);
        var daily = Slice("private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync", "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        Assert.Contains("RoleRecordNeedsRefresh(", daily, StringComparison.Ordinal);
        Assert.Contains("SaveRoleRecord(", daily, StringComparison.Ordinal);
        var rule = Slice("private static bool RoleRecordNeedsRefresh", "private bool TryResolveProfilePath");
        Assert.Contains("candidate.Nickname", rule, StringComparison.Ordinal);
        Assert.Contains("CanonicalRegionLabel", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Cached_resources_are_built_locally_then_committed_atomically()
    {
        var method = Slice("private void RestoreCachedResources()", "private void TrySetCanceledConnectState");
        AssertOrdered(
            method,
            "var restoredResources = new Dictionary",
            "var restoredStates = new Dictionary",
            "foreach (var gameId",
            "lock (sync)",
            "if (!CanPublish(\"HoYoLAB\", operation)) return",
            "resources[gameId] = snapshot",
            "resourceStates[gameId] = state");
        Assert.Contains("snapshot.ObservedAt > now", method, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceRefreshPolicy.IsFresh(snapshot.ObservedAt, now)", method, StringComparison.Ordinal);
        Assert.Contains("snapshot with { IsStale = !fresh }", method, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceState.Fresh", method, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.IsStale", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_restore_and_resource_refresh_durations_are_local_fail_closed_surfaces()
    {
        var surface = Slice(
            "public TimeSpan? LastAccountRestoreDuration",
            "public HoyoLabAccountSlotManagerState HoyoLabAccounts");
        Assert.Contains("Volatile.Read(ref lastAccountRestoreDurationTicks)", surface, StringComparison.Ordinal);
        Assert.Contains("public bool TryGetResourceRefreshDuration(string? gameId, out TimeSpan duration)", surface, StringComparison.Ordinal);
        Assert.Contains("\"gi\" => Volatile.Read(ref giResourceRefreshDurationTicks)", surface, StringComparison.Ordinal);
        Assert.Contains("\"hsr\" => Volatile.Read(ref hsrResourceRefreshDurationTicks)", surface, StringComparison.Ordinal);
        Assert.Contains("\"zzz\" => Volatile.Read(ref zzzResourceRefreshDurationTicks)", surface, StringComparison.Ordinal);
        Assert.Contains("_ => -1", surface, StringComparison.Ordinal);
        Assert.Contains("return ticks >= 0", surface, StringComparison.Ordinal);

        var refresh = Slice(
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        AssertOrdered(
            refresh,
            "var started = Stopwatch.GetTimestamp()",
            "try",
            "finally",
            "SetResourceRefreshDuration(entry.GameId, Stopwatch.GetElapsedTime(started))");

        var restore = Slice("private void RestoreCachedResources()", "private void TrySetCanceledConnectState");
        AssertOrdered(
            restore,
            "var started = Stopwatch.GetTimestamp()",
            "try",
            "finally",
            "Volatile.Write(",
            "Stopwatch.GetElapsedTime(started).Ticks");
    }

    private static string Slice(string start, string end)
    {
        var startIndex = Service.IndexOf(start, StringComparison.Ordinal);
        var endIndex = Service.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return Service[startIndex..endIndex];
    }

    private static void AssertOrdered(string value, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = value.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the prior marker.");
            previous = current;
        }
    }

    private static void AssertLockedMutation(string value, string mutation)
    {
        AssertOrdered(
            value,
            "lock (sync)",
            "if (!CanPublish(\"HoYoLAB\", operation)) return false",
            mutation,
            "operation.Cancellation.Token",
            "return saved && CanPublish(\"HoYoLAB\", operation)");
    }

    private static HoyoLabAccountSlot Slot(string label) => new(
        "0123456789abcdef0123456789abcdef",
        label,
        IsLegacy: false,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        RemovalPending: false);

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository source file was not found.");
    }
}
