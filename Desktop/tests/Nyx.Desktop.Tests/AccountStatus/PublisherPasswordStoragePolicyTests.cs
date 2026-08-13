using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherPasswordStoragePolicyTests
{
    [Fact]
    public void Omitted_low_level_preference_is_fail_safe_off()
    {
        var noProfile = new PublisherPasswordStoragePolicy();
        var existingProfile = new PublisherPasswordStoragePolicy(profileExists: true);

        Assert.False(noProfile.Snapshot.PasswordSavingEnabled);
        Assert.Null(noProfile.Snapshot.PendingCleanup);
        Assert.True(noProfile.Snapshot.CanOpenPublisherPage);
        Assert.False(existingProfile.Snapshot.PasswordSavingEnabled);
        Assert.Equal(
            PublisherProfileCleanupScope.PasswordsOnly,
            existingProfile.Snapshot.PendingCleanup);
        Assert.False(existingProfile.Snapshot.CanOpenPublisherPage);
    }

    [Fact]
    public void Explicit_opt_in_enables_WebView2_password_storage()
    {
        var policy = new PublisherPasswordStoragePolicy(
            passwordSavingEnabled: false,
            profileExists: true);

        var optedIn = policy.ApplyPreference(enabled: true, profileExists: true);

        Assert.True(optedIn.PasswordSavingEnabled);
        Assert.Null(optedIn.PendingCleanup);
        Assert.True(optedIn.CanOpenPublisherPage);
    }

    [Fact]
    public void Opt_out_requires_password_only_cleanup_and_preserves_navigation_after_success()
    {
        var policy = new PublisherPasswordStoragePolicy(passwordSavingEnabled: true, profileExists: true);

        var optedOut = policy.ApplyPreference(enabled: false, profileExists: true);
        var cleared = policy.CompleteCleanup(
            PublisherProfileCleanupScope.PasswordsOnly,
            succeeded: true);

        Assert.False(optedOut.PasswordSavingEnabled);
        Assert.Equal(PublisherProfileCleanupScope.PasswordsOnly, optedOut.PendingCleanup);
        Assert.False(optedOut.CanOpenPublisherPage);
        Assert.False(cleared.PasswordSavingEnabled);
        Assert.Null(cleared.PendingCleanup);
        Assert.True(cleared.CanOpenPublisherPage);
    }

    [Fact]
    public void Password_cleanup_failure_stays_disabled_and_fails_closed()
    {
        var policy = new PublisherPasswordStoragePolicy(passwordSavingEnabled: true, profileExists: true);
        policy.ApplyPreference(enabled: false, profileExists: true);

        var failed = policy.CompleteCleanup(
            PublisherProfileCleanupScope.PasswordsOnly,
            succeeded: false);

        Assert.False(failed.PasswordSavingEnabled);
        Assert.Equal(PublisherProfileCleanupScope.PasswordsOnly, failed.PendingCleanup);
        Assert.False(failed.CanOpenPublisherPage);
    }

    [Fact]
    public void Disconnect_requires_full_profile_cleanup_and_password_only_cleanup_cannot_satisfy_it()
    {
        var policy = new PublisherPasswordStoragePolicy(passwordSavingEnabled: true, profileExists: true);

        var disconnecting = policy.RequireFullProfileCleanup();
        var wrongCleanup = policy.CompleteCleanup(
            PublisherProfileCleanupScope.PasswordsOnly,
            succeeded: true);
        var deleted = policy.CompleteCleanup(
            PublisherProfileCleanupScope.FullProfile,
            succeeded: true);

        Assert.Equal(PublisherProfileCleanupScope.FullProfile, disconnecting.PendingCleanup);
        Assert.False(disconnecting.CanOpenPublisherPage);
        Assert.Equal(PublisherProfileCleanupScope.FullProfile, wrongCleanup.PendingCleanup);
        Assert.False(wrongCleanup.CanOpenPublisherPage);
        Assert.Null(deleted.PendingCleanup);
        Assert.True(deleted.CanOpenPublisherPage);
    }

    [Fact]
    public void Restart_with_only_an_inactive_profile_keeps_all_slot_cleanup_pending()
    {
        var profileEntriesExist = new[] { false, true };
        var cleanupRequired = HoyoLabPasswordCleanupRules.RequiresCleanup(
            targetsValidated: true,
            profileEntriesExist);

        var restarted = new PublisherPasswordStoragePolicy(
            passwordSavingEnabled: false,
            profileExists: cleanupRequired);
        var failedCleanup = restarted.CompleteCleanup(
            PublisherProfileCleanupScope.PasswordsOnly,
            succeeded: false);

        Assert.True(cleanupRequired);
        Assert.Equal(PublisherProfileCleanupScope.PasswordsOnly, restarted.Snapshot.PendingCleanup);
        Assert.Equal(PublisherProfileCleanupScope.PasswordsOnly, failedCleanup.PendingCleanup);
        Assert.False(failedCleanup.CanOpenPublisherPage);
    }

    [Fact]
    public void Invalid_or_mutated_slot_targets_require_cleanup_even_when_no_profile_was_resolved()
    {
        Assert.True(HoyoLabPasswordCleanupRules.RequiresCleanup(
            targetsValidated: false,
            Array.Empty<bool>()));
        Assert.False(HoyoLabPasswordCleanupRules.RequiresCleanup(
            targetsValidated: true,
            [false, false]));
    }
}
