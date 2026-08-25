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
    public void Forget_is_write_ahead_pending_retriable_and_never_deletes_legacy_profile()
    {
        var method = Slice("public async Task<bool> ForgetHoyoLabAccountAsync", "public void ApplyPasswordSavingPreference");
        AssertOrdered(method, "TryMarkRemovalPending", "TryDeleteManagedDirectory", "TryRemoveSlot");
        Assert.Contains("target.RemovalPending", method, StringComparison.Ordinal);
        Assert.Contains("if (!target.IsLegacy)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(root, \"HoYoLAB\")", method, StringComparison.Ordinal);
        Assert.Contains("SetConnection(\"HoYoLAB\", PublisherConnectionState.NotConnected)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Revocation_deletes_all_hoyo_roots_and_index_last()
    {
        var method = Slice("private bool TryDeleteAllHoyoState()", "private bool TryDeleteExactDirectory");
        Assert.Contains("Accounts\", \"HoYoLAB", method, StringComparison.Ordinal);
        Assert.Contains(".protected-role-bindings", method, StringComparison.Ordinal);
        Assert.Contains(".protected-resource-snapshots", method, StringComparison.Ordinal);
        AssertOrdered(method, "TryDeleteExactDirectory(legacyResources)", "hoyoSlots.TryDeleteIndex()");
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
    public void Password_cleanup_enumerates_every_indexed_profile_and_rejects_target_changes()
    {
        var targets = Slice("private bool TryGetHoyoPasswordCleanupTargets", "private bool AreHoyoPasswordCleanupTargetsCurrent");
        AssertOrdered(
            targets,
            "hoyoSlots.TryLoad()",
            "foreach (var slot in index.Slots)",
            "hoyoSlots.TryGetWebView2ProfilePath(slot",
            "resolved.Add(profile)");
        Assert.Contains("hoyoSlots.IsLegacyCompatibilityStillSafe()", targets, StringComparison.Ordinal);

        var revalidate = Slice("private bool AreHoyoPasswordCleanupTargetsCurrent", "private static bool HoyoSlotIndexesMatch");
        Assert.Contains("HoyoSlotIndexesMatch(expectedIndex, current)", revalidate, StringComparison.Ordinal);
        Assert.Contains("hoyoSlots.IsLegacyCompatibilityStillSafe()", revalidate, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_opt_out_restart_checks_all_slots_and_only_all_slot_cleanup_can_complete()
    {
        var constructor = Slice("public PublisherAccountService(", "public event EventHandler? Updated");
        Assert.Contains("HoyoProfilesNeedPasswordCleanup()", constructor, StringComparison.Ordinal);
        var preference = Slice("public void ApplyPasswordSavingPreference", "public async Task<bool> ClearSavedPasswordsAsync");
        Assert.Contains("provider == \"HoYoLAB\"", preference, StringComparison.Ordinal);
        Assert.Contains("HoyoProfilesNeedPasswordCleanup()", preference, StringComparison.Ordinal);

        var need = Slice("private bool HoyoProfilesNeedPasswordCleanup", "private bool PublisherProfileEntryExistsOrUnknown");
        Assert.Contains("TryGetHoyoPasswordCleanupTargets", need, StringComparison.Ordinal);
        Assert.Contains("profiles.Select(PublisherProfileEntryExistsOrUnknown)", need, StringComparison.Ordinal);
        Assert.Contains("HoyoLabPasswordCleanupRules.RequiresCleanup", need, StringComparison.Ordinal);

        var ordinaryWindow = Slice("private PublisherSessionWindow CreateWindow", "private async Task<bool> ClearSavedPasswordsAsync(");
        Assert.Contains("PendingCleanup is PublisherProfileCleanupScope.PasswordsOnly", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("Every HoYoLAB account must finish password cleanup", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("provider == \"HoYoLAB\"", ordinaryWindow, StringComparison.Ordinal);
        Assert.Contains("? static () => { }", ordinaryWindow, StringComparison.Ordinal);

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
