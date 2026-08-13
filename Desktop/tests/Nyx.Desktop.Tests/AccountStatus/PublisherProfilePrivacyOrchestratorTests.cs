using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherProfilePrivacyOrchestratorTests
{
    [Fact]
    public async Task Password_cleanup_exception_prevents_navigation_and_a_later_success_can_retry()
    {
        var gate = new PublisherPasswordNavigationGate(passwordSavingEnabled: false);
        var navigations = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.NavigateAsync(
            (_, _) => throw new InvalidOperationException("simulated-cleanup-failure"),
            _ =>
            {
                navigations++;
                return Task.CompletedTask;
            }));

        Assert.Equal(0, navigations);

        await gate.NavigateAsync(
            (_, _) => Task.CompletedTask,
            _ =>
            {
                navigations++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, navigations);
    }

    [Fact]
    public async Task Password_opt_out_requests_only_password_autosave_before_navigation()
    {
        var gate = new PublisherPasswordNavigationGate(passwordSavingEnabled: false);
        var operations = new List<string>();
        var requestedData = PublisherBrowsingDataKind.None;

        await gate.NavigateAsync(
            (dataKind, _) =>
            {
                requestedData = dataKind;
                operations.Add("password-cleanup");
                return Task.CompletedTask;
            },
            _ =>
            {
                operations.Add("navigation");
                return Task.CompletedTask;
            });

        Assert.Equal(PublisherBrowsingDataKind.PasswordAutosave, requestedData);
        Assert.False(requestedData.HasFlag(PublisherBrowsingDataKind.Cookies));
        Assert.Equal(["password-cleanup", "navigation"], operations);
    }

    [Fact]
    public async Task Password_opt_in_navigates_without_requesting_cleanup()
    {
        var gate = new PublisherPasswordNavigationGate(passwordSavingEnabled: true);
        var cleanupRequested = false;
        var navigationRequested = false;

        await gate.NavigateAsync(
            (_, _) =>
            {
                cleanupRequested = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                navigationRequested = true;
                return Task.CompletedTask;
            });

        Assert.False(cleanupRequested);
        Assert.True(navigationRequested);
    }

    [Fact]
    public async Task Disconnect_requires_and_performs_recursive_full_profile_cleanup()
    {
        var policy = new PublisherPasswordStoragePolicy(
            passwordSavingEnabled: true,
            profileExists: true);
        var recursive = false;
        var fullCleanupWasPendingInsideBoundary = false;

        await PublisherProfilePrivacyOrchestrator.DeleteFullProfileAsync(
            policy,
            (requestedRecursive, _) =>
            {
                recursive = requestedRecursive;
                fullCleanupWasPendingInsideBoundary =
                    policy.Snapshot.PendingCleanup is PublisherProfileCleanupScope.FullProfile;
                return Task.CompletedTask;
            });

        Assert.True(recursive);
        Assert.True(fullCleanupWasPendingInsideBoundary);
        Assert.Null(policy.Snapshot.PendingCleanup);
        Assert.True(policy.Snapshot.CanOpenPublisherPage);
    }

    [Fact]
    public async Task Full_profile_deletion_failure_remains_pending_and_blocks_navigation()
    {
        var policy = new PublisherPasswordStoragePolicy(
            passwordSavingEnabled: true,
            profileExists: true);

        await Assert.ThrowsAsync<IOException>(() =>
            PublisherProfilePrivacyOrchestrator.DeleteFullProfileAsync(
                policy,
                (_, _) => throw new IOException("simulated-delete-failure")));

        Assert.Equal(
            PublisherProfileCleanupScope.FullProfile,
            policy.Snapshot.PendingCleanup);
        Assert.False(policy.Snapshot.CanOpenPublisherPage);
    }
}
