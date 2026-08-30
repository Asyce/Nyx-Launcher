using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class GenshinReviewRecoveryTests
{
    [Fact]
    public void Authenticated_done_keeps_the_account_connected_when_the_resource_page_needs_review()
    {
        var result = new PublisherResourceReadResult(
            null,
            PublisherResourceReadOutcome.NeedsReview);

        Assert.Equal(
            PublisherConnectionState.Connected,
            PublisherAccountStatePolicy.ForAuthenticatedResourceRead(result));
    }

    [Fact]
    public void Authenticated_resource_projection_still_requires_login_for_an_explicit_login_failure()
    {
        var result = new PublisherResourceReadResult(
            null,
            PublisherResourceReadOutcome.LoginRequired);

        Assert.Equal(
            PublisherConnectionState.LoginRequired,
            PublisherAccountStatePolicy.ForAuthenticatedResourceRead(result));
    }

    [Fact]
    public void Visible_review_starts_at_the_fixed_home_without_dom_modal_automation()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");

        Assert.Contains(
            "PublisherVisibleConnectNavigationPolicy.GetInitialUri(entry)",
            connect,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "entry.ResourceUri ?? entry.CheckInUri",
            connect,
            StringComparison.Ordinal);

        var lowered = browser.ToLowerInvariant();
        Assert.DoesNotContain("mhy-announcement", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement_close_btn", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("qrcode", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("qr-code", lowered, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_review_failure_has_bounded_retry_guidance_not_an_account_review_label()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(
            page,
            "private void RenderPublisherAccountStatus",
            "public static string FormatPublisherResource");

        Assert.Contains(
            "PublisherResourceState.NeedsReview => $\"{entry.ResourceName.ToUpperInvariant()} CHECK NEEDS REVIEW\"",
            render,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceState.NeedsReview => \"TRY AGAIN\"",
            render,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PublisherResourceState.NeedsReview => \"ACCOUNT NEEDS REVIEW\"",
            render,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PublisherResourceState.NeedsReview => \"REVIEW\"",
            render,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_daily_failure_keeps_the_full_safe_reason_available_to_accessibility_tools()
    {
        var page = ReadAppFile("MainPage.xaml.cs");
        var render = Slice(
            page,
            "private void RenderPublisherAccountStatus",
            "public static string FormatPublisherResource");

        Assert.Contains(
            "currentCheckIn?.State == DailyCheckInState.CouldNotCheck",
            render,
            StringComparison.Ordinal);
        Assert.Contains("currentCheckIn.Message", render, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetName(WuWaAccountFreshnessText, accessibleFreshness)",
            render,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetHelpText(WuWaAccountFreshnessText, accessibleFreshness)",
            render,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToolTipService.SetToolTip(WuWaAccountFreshnessText, accessibleFreshness)",
            render,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_refresh_uses_the_authenticated_projection_after_session_proof()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");

        var proof = refresh.IndexOf(
            "if (sessionProof != PublisherSessionProof.Authenticated)",
            StringComparison.Ordinal);
        var projection = refresh.IndexOf(
            "PublisherAccountStatePolicy.ForAuthenticatedResourceRead(resourceRead)",
            StringComparison.Ordinal);
        Assert.True(proof >= 0 && proof < projection);
    }

    [Fact]
    public void Authenticated_needs_review_retains_the_cached_snapshot_and_binding_as_stale()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var transient = Slice(
            refresh,
            "if (resourceRead.Outcome is PublisherResourceReadOutcome.NeedsReview",
            "RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);");

        Assert.Contains(
            "resourceRead.Outcome is not (PublisherResourceReadOutcome.Valid",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "or PublisherResourceReadOutcome.NeedsReview",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "var retained = MarkResourceStaleIfCurrent(",
            transient,
            StringComparison.Ordinal);
        Assert.Contains("PublisherProtectedStateAuthority.NeedsReview", transient, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherConnectionState.Connected",
            transient,
            StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveResourceIfCurrent", transient, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_required_resource_refresh_retains_verified_state_when_present_and_demotes_account()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var transient = Slice(
            refresh,
            "if (resourceRead.Outcome is PublisherResourceReadOutcome.NeedsReview",
            "RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);");

        Assert.Contains("MarkResourceStaleIfCurrent(", transient, StringComparison.Ordinal);
        Assert.Contains("PublisherProtectedStateAuthority.LoginRequired", transient, StringComparison.Ordinal);
        Assert.Contains("PublisherConnectionState.LoginRequired", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", transient, StringComparison.Ordinal);
        Assert.Contains(
            "or PublisherResourceReadOutcome.LoginRequired)",
            refresh,
            StringComparison.Ordinal);
        Assert.Equal(
            PublisherConnectionState.LoginRequired,
            PublisherAccountStatePolicy.ForAuthenticatedResourceRead(
                new(null, PublisherResourceReadOutcome.LoginRequired)));
        Assert.Equal(
            PublisherResourceState.Stale,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.LoginRequired,
                hasVerifiedSnapshot: true));
        Assert.Equal(
            PublisherResourceState.LoginRequired,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.LoginRequired,
                hasVerifiedSnapshot: false));
    }

    [Fact]
    public void Session_proof_needs_review_retains_resource_state_with_and_without_a_cached_snapshot()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var sessionFailure = Slice(
            refresh,
            "if (sessionProof != PublisherSessionProof.Authenticated)",
            "var storedRecord = entry.Provider == \"HoYoLAB\"");

        Assert.Contains("PublisherProtectedStateAuthority.NeedsReview", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("MarkResourceStaleIfCurrent(", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("ProjectTransientResourceState(", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveResourceIfCurrent", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.Equal(
            PublisherResourceState.Stale,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.NeedsReview,
                hasVerifiedSnapshot: true));
        Assert.Equal(
            PublisherResourceState.NeedsReview,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.NeedsReview,
                hasVerifiedSnapshot: false));
    }

    [Fact]
    public void Daily_transient_needs_review_has_no_authority_to_invalidate_the_saved_role()
    {
        var saved = new PublisherRoleBinding("123456789", "os_euro");

        var resolution = PublisherDailyRolePolicy.Resolve(
            "gi",
            new(null, PublisherResourceReadOutcome.NeedsReview),
            saved);

        Assert.Equal(PublisherDailyRoleResolutionState.NeedsReview, resolution.State);
        Assert.False(resolution.StoredBindingStillMatches);
        Assert.False(resolution.StoredBindingWasProvenMissing);
    }

    [Fact]
    public void Daily_fresh_candidates_can_prove_that_the_saved_role_disappeared()
    {
        var saved = new PublisherRoleBinding("123456789", "os_euro");
        var current = new PublisherRoleBinding("987654321", "os_usa");

        var resolution = PublisherDailyRolePolicy.Resolve(
            "gi",
            new(
                null,
                PublisherResourceReadOutcome.SelectionRequired,
                [new(current, null)]),
            saved);

        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, resolution.State);
        Assert.Equal(current, resolution.Binding);
        Assert.False(resolution.StoredBindingStillMatches);
        Assert.True(resolution.StoredBindingWasProvenMissing);
    }

    [Fact]
    public void Daily_resolver_marks_transient_cache_stale_but_clears_only_with_explicit_authority()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var transient = Slice(
            resolver,
            "if (resourceRead.Outcome is PublisherResourceReadOutcome.NeedsReview",
            "var shouldClearStoredBinding");

        Assert.Contains("MarkResourceStaleIfCurrent(", transient, StringComparison.Ordinal);
        Assert.Contains("PublisherProtectedStateAuthority.NeedsReview", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", transient, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", transient, StringComparison.Ordinal);

        Assert.Contains(
            "resolution.StoredBindingWasProvenMissing",
            resolver,
            StringComparison.Ordinal);
        Assert.Contains("TryDeleteProtectedGameState(", resolver, StringComparison.Ordinal);
        Assert.Contains("storedBinding,", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_session_login_retains_verified_snapshot_and_role_but_projects_login_required()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var sessionFailure = Slice(
            resolver,
            "if (sessionProof != PublisherSessionProof.Authenticated)",
            "var storedRecord = TryLoadRoleRecord(entry.GameId, operation);");

        Assert.Contains("if (!CanPublish(entry.Provider, operation))", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("MarkResourceStaleIfCurrent(", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("PublisherProtectedStateAuthority.LoginRequired", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("SetResourceStateIfCurrent(", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveResourceIfCurrent", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.Equal(
            PublisherResourceState.Stale,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.LoginRequired,
                hasVerifiedSnapshot: true));
        Assert.Equal(
            PublisherResourceState.LoginRequired,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.LoginRequired,
                hasVerifiedSnapshot: false));
    }

    [Fact]
    public void Daily_session_needs_review_retains_resource_state_with_and_without_a_cached_snapshot()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var sessionFailure = Slice(
            resolver,
            "if (sessionProof != PublisherSessionProof.Authenticated)",
            "var storedRecord = TryLoadRoleRecord(entry.GameId, operation);");

        Assert.Contains("PublisherProtectedStateAuthority.NeedsReview", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("MarkResourceStaleIfCurrent(", sessionFailure, StringComparison.Ordinal);
        Assert.Contains("ProjectTransientResourceState(", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveResourceIfCurrent", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings.Delete", sessionFailure, StringComparison.Ordinal);
        Assert.Equal(
            PublisherResourceState.Stale,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.NeedsReview,
                hasVerifiedSnapshot: true));
        Assert.Equal(
            PublisherResourceState.NeedsReview,
            PublisherProtectedStateRetentionPolicy.ProjectTransientResourceState(
                PublisherProtectedStateAuthority.NeedsReview,
                hasVerifiedSnapshot: false));
    }

    [Fact]
    public void Daily_proven_role_removal_clears_memory_and_projects_selection_or_empty_state()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var cleanup = Slice(
            resolver,
            "if (shouldClearStoredBinding)",
            "if (resolution.State == PublisherDailyRoleResolutionState.SelectionRequired");

        Assert.Contains(
            "RemoveResourceIfCurrent(entry.GameId, entry.Provider, operation);",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains("TryDeleteProtectedGameState(", cleanup, StringComparison.Ordinal);
        Assert.Contains("storedBinding,", cleanup, StringComparison.Ordinal);
        Assert.Contains("SetResourceStateIfCurrent(", cleanup, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceState.SelectionRequired", cleanup, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceState.NotStarted", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_resource_diagnostic_clears_and_replaces_only_for_the_current_generation()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var clear = resolver.IndexOf(
            "SetResourceDiagnosticIfCurrent(",
            StringComparison.Ordinal);
        var read = resolver.IndexOf(
            "var resourceRead = await roleWindow.ReadResourceAsync(",
            StringComparison.Ordinal);
        var save = resolver.IndexOf(
            "SaveRoleRecord(",
            read,
            StringComparison.Ordinal);
        var publish = resolver.IndexOf(
            "SetResourceDiagnosticIfCurrent(",
            save,
            StringComparison.Ordinal);
        var finalDiagnostic = resolver.IndexOf(
            "PublisherDailyRolePolicy.FinalDiagnostic(resourceRead, resolution)",
            publish,
            StringComparison.Ordinal);
        Assert.True(clear >= 0 && clear < read && read < save && save < publish);
        Assert.True(publish < finalDiagnostic);
        Assert.Contains(
            "PublisherResourceCaptureDiagnostic.NotAvailable",
            resolver[clear..read],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resourceRead.Diagnostic",
            resolver[read..publish],
            StringComparison.Ordinal);

        var guardedSetter = Slice(
            service,
            "private void SetResourceDiagnosticIfCurrent",
            "private bool SetResourceIfCurrent");
        var firstGuard = guardedSetter.IndexOf(
            "if (!CanPublish(provider, operation)) return;",
            StringComparison.Ordinal);
        var secondGuard = guardedSetter.IndexOf(
            "if (!CanPublish(provider, operation)) return;",
            firstGuard + 1,
            StringComparison.Ordinal);
        Assert.True(firstGuard >= 0 && secondGuard > firstGuard);

        var generation = new PublisherGeneration();
        var diagnostic = PublisherResourceCaptureDiagnostic.ResponseRejected;
        void Apply(long candidate, PublisherResourceCaptureDiagnostic value)
        {
            if (generation.CanPublish(candidate))
                diagnostic = value;
        }

        var first = generation.Advance();
        Apply(first, PublisherResourceCaptureDiagnostic.NotAvailable);
        Apply(first, PublisherResourceCaptureDiagnostic.Valid);
        Assert.Equal(PublisherResourceCaptureDiagnostic.Valid, diagnostic);

        var second = generation.Advance();
        Apply(second, PublisherResourceCaptureDiagnostic.NotAvailable);
        Apply(first, PublisherResourceCaptureDiagnostic.ResponseRejected);
        Assert.Equal(PublisherResourceCaptureDiagnostic.NotAvailable, diagnostic);
        Apply(second, PublisherResourceCaptureDiagnostic.ResponseIncomplete);
        Assert.Equal(PublisherResourceCaptureDiagnostic.ResponseIncomplete, diagnostic);
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            fileName));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker}.");
        Assert.True(end > start, $"Could not find {endMarker} after {startMarker}.");
        return source[start..end];
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "Desktop")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Nyx workspace root.");
    }
}
