using Nyx.Desktop.Infrastructure.AccountStatus;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherConsentRevocationStoreTests
{
    [Fact]
    public void Pending_marker_contains_no_account_material_and_survives_restart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-revocation-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new PublisherConsentRevocationStore(root);
            Assert.False(first.IsPending("HoYoLAB"));
            Assert.True(first.MarkPending("HoYoLAB"));
            Assert.True(first.IsPending("HoYoLAB"));
            Assert.False(first.IsPending("SKPORT"));

            var second = new PublisherConsentRevocationStore(root);
            Assert.True(second.IsPending("HoYoLAB"));
            var marker = Directory.GetFiles(root, "*.pending", SearchOption.AllDirectories);
            var path = Assert.Single(marker);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.True(second.Clear("HoYoLAB"));
            Assert.False(second.IsPending("HoYoLAB"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("../HoYoLAB")]
    public void Unknown_provider_fails_closed_without_creating_a_marker(string provider)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-revocation-invalid-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PublisherConsentRevocationStore(root);
            Assert.True(store.IsPending(provider));
            Assert.False(store.MarkPending(provider));
            Assert.False(store.Clear(provider));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Failed_quarantine_cache_delete_stays_pending_across_restart_and_retry_clears_it()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-quarantine-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new CopyProtector();
            var bindings = new PublisherRoleBindingStore(root, protector);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "prod_official_eur");
            var snapshot = new PublisherResourceSnapshot(
                "hsr",
                "Trailblaze Power",
                100,
                300,
                DateTimeOffset.UtcNow,
                RecoverySeconds: 100,
                Reserve: 10);
            Assert.True(bindings.Save("hsr", binding));
            Assert.True(snapshots.Save(snapshot, binding));

            var snapshotPath = Path.Combine(
                root,
                ".protected-resource-snapshots",
                "hsr.bin");
            using (new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.False(PublisherQuarantineCleanupStore.TryClean(
                    "HoYoLAB",
                    revocations,
                    bindings,
                    snapshots));
                Assert.True(revocations.IsPending("HoYoLAB"));
                Assert.Null(bindings.TryLoad("hsr"));
                Assert.True(File.Exists(snapshotPath));

                var restarted = new PublisherConsentRevocationStore(root);
                Assert.True(restarted.IsPending("HoYoLAB"));
            }

            Assert.True(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots));
            Assert.False(new PublisherConsentRevocationStore(root).IsPending("HoYoLAB"));
            Assert.False(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Marker_write_and_cache_delete_failure_suppress_restart_restore_until_retry()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-quarantine-marker-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new CopyProtector();
            var bindings = new PublisherRoleBindingStore(root, protector);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            var snapshot = new PublisherResourceSnapshot(
                "gi",
                "Original Resin",
                100,
                200,
                DateTimeOffset.UtcNow,
                RecoverySeconds: 100);
            Assert.True(bindings.Save("gi", binding));
            Assert.True(snapshots.Save(snapshot, binding));

            var markerRoot = Path.Combine(root, ".pending-account-revocations");
            File.WriteAllBytes(markerRoot, []);
            var snapshotPath = Path.Combine(
                root,
                ".protected-resource-snapshots",
                "gi.bin");
            using (new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.False(PublisherQuarantineCleanupStore.TryClean(
                    "HoYoLAB",
                    revocations,
                    bindings,
                    snapshots));
                Assert.True(File.Exists(snapshotPath));
                Assert.True(new PublisherConsentRevocationStore(root).IsPending("HoYoLAB"));
            }

            File.Delete(markerRoot);
            Assert.True(new PublisherConsentRevocationStore(root).IsPending("HoYoLAB"));
            Assert.True(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots));
            Assert.False(new PublisherConsentRevocationStore(root).IsPending("HoYoLAB"));
            Assert.False(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Both_marker_writes_and_cache_delete_failure_use_independent_pending_bit_across_restart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-quarantine-independent-pending-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new CopyProtector();
            var bindings = new PublisherRoleBindingStore(root, protector);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            var snapshot = new PublisherResourceSnapshot(
                "gi",
                "Original Resin",
                100,
                200,
                DateTimeOffset.UtcNow,
                RecoverySeconds: 100);
            Assert.True(bindings.Save("gi", binding));
            Assert.True(snapshots.Save(snapshot, binding));

            var markerRoot = Path.Combine(root, ".pending-account-revocations");
            var primaryMarker = Path.Combine(markerRoot, "hoyolab.pending");
            var fallbackMarker = Path.Combine(root, ".hoyolab.pending");
            Directory.CreateDirectory(primaryMarker);
            Directory.CreateDirectory(fallbackMarker);
            var snapshotPath = Path.Combine(
                root,
                ".protected-resource-snapshots",
                "gi.bin");
            var durablePending = false;
            var pendingWrites = new List<bool>();
            bool PersistPending(string provider, bool pending)
            {
                Assert.Equal("HoYoLAB", provider);
                pendingWrites.Add(pending);
                durablePending = pending;
                return true;
            }

            using (new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.False(PublisherQuarantineCleanupStore.TryClean(
                    "HoYoLAB",
                    revocations,
                    bindings,
                    snapshots,
                    PersistPending));
                Assert.True(revocations.IsPending("HoYoLAB"));
                Assert.True(durablePending);
                Assert.Equal([true], pendingWrites);
                Assert.True(File.Exists(snapshotPath));

                var restartedConsent = new PublisherAccountConsentGate(
                    hoyoLabEnabled: true && !durablePending,
                    skportEnabled: false);
                Assert.False(restartedConsent.IsEnabled("HoYoLAB"));
            }

            Directory.Delete(primaryMarker);
            Directory.Delete(fallbackMarker);
            Assert.True(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots,
                PersistPending));
            Assert.False(durablePending);
            Assert.Equal([true, true, false], pendingWrites);
            Assert.False(revocations.IsPending("HoYoLAB"));
            Assert.False(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Failed_independent_pending_bit_keeps_marker_and_never_reports_cleanup_complete()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-quarantine-pending-bit-failure-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new CopyProtector();
            var bindings = new PublisherRoleBindingStore(root, protector);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.True(bindings.Save("gi", binding));
            Assert.True(snapshots.Save(
                new PublisherResourceSnapshot(
                    "gi",
                    "Original Resin",
                    100,
                    200,
                    DateTimeOffset.UtcNow,
                    RecoverySeconds: 100),
                binding));

            var callbackCalls = new List<bool>();
            Assert.False(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots,
                (provider, pending) =>
                {
                    Assert.Equal("HoYoLAB", provider);
                    callbackCalls.Add(pending);
                    return false;
                }));
            Assert.Equal([true], callbackCalls);
            Assert.True(revocations.IsPending("HoYoLAB"));
            Assert.Null(bindings.TryLoad("gi"));

            Assert.True(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots,
                (provider, pending) =>
                {
                    Assert.Equal("HoYoLAB", provider);
                    callbackCalls.Add(pending);
                    return true;
                }));
            Assert.Equal([true, true, false], callbackCalls);
            Assert.False(revocations.IsPending("HoYoLAB"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Both_durable_channels_failing_never_clear_pending_or_report_success()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-quarantine-both-channels-failure-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new CopyProtector();
            var bindings = new PublisherRoleBindingStore(root, protector);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.True(bindings.Save("gi", binding));
            Assert.True(snapshots.Save(
                new PublisherResourceSnapshot(
                    "gi",
                    "Original Resin",
                    100,
                    200,
                    DateTimeOffset.UtcNow,
                    RecoverySeconds: 100),
                binding));

            var markerRoot = Path.Combine(root, ".pending-account-revocations");
            Directory.CreateDirectory(Path.Combine(markerRoot, "hoyolab.pending"));
            Directory.CreateDirectory(Path.Combine(root, ".hoyolab.pending"));
            var snapshotPath = Path.Combine(
                root,
                ".protected-resource-snapshots",
                "gi.bin");
            var callbackCalls = new List<bool>();
            using (new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.False(PublisherQuarantineCleanupStore.TryClean(
                    "HoYoLAB",
                    revocations,
                    bindings,
                    snapshots,
                    (provider, pending) =>
                    {
                        Assert.Equal("HoYoLAB", provider);
                        callbackCalls.Add(pending);
                        return false;
                    }));
            }

            Assert.Equal([true], callbackCalls);
            Assert.True(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unexpected_directory_at_either_marker_path_is_pending_after_restart(
        bool primaryMarker)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-revocation-marker-type-" + Guid.NewGuid().ToString("N"));
        try
        {
            var markerPath = primaryMarker
                ? Path.Combine(root, ".pending-account-revocations", "hoyolab.pending")
                : Path.Combine(root, ".hoyolab.pending");
            Directory.CreateDirectory(markerPath);

            var restarted = new PublisherConsentRevocationStore(root);

            Assert.True(restarted.IsPending("HoYoLAB"));
            Assert.False(restarted.Clear("HoYoLAB"));
            Assert.True(Directory.Exists(markerPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Marker_failure_and_interrupted_opt_out_recover_redundant_pending_state()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-revocation-interrupted-" + Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(root, "state");
        var profileRoot = Path.Combine(root, "profiles");
        try
        {
            var stateStore = new LauncherStateStore(stateRoot);
            stateStore.Save(LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = LauncherFeatureFlags.Defaults() with
                    {
                        HoyoLabAccountAccess = true,
                    },
                },
            });
            Directory.CreateDirectory(Path.Combine(
                profileRoot,
                ".pending-account-revocations",
                "hoyolab.opt-out.pending"));
            Directory.CreateDirectory(Path.Combine(
                profileRoot,
                ".hoyolab.opt-out.pending"));
            var revocations = new PublisherConsentRevocationStore(profileRoot);

            var stateRecorded = true;
            try
            {
                stateStore.UpdatePublisherCleanupPending(
                    "HoYoLAB",
                    cleanupPending: true,
                    accountAccess: false);
            }
            catch (IOException)
            {
                stateRecorded = false;
            }
            var markerRecorded = revocations.MarkOptOutPending("HoYoLAB");

            Assert.True(stateRecorded);
            Assert.False(markerRecorded);
            File.WriteAllText(stateStore.StatePath, "{bad");

            var restarted = new LauncherStateStore(stateRoot).Load();
            Assert.Equal(LauncherStateReadStatus.Recovered, restarted.Status);
            Assert.False(restarted.State!.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.True(restarted.State.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.True(new PublisherConsentRevocationStore(profileRoot).IsPending("HoYoLAB"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Marker_only_opt_out_stays_off_after_restart_and_cleanup()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-marker-only-opt-out-" + Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(root, "state");
        var profileRoot = Path.Combine(root, "profiles");
        try
        {
            var stateStore = new LauncherStateStore(stateRoot);
            var enabled = LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = LauncherFeatureFlags.Defaults() with
                    {
                        HoyoLabAccountAccess = true,
                    },
                },
            };
            stateStore.Save(enabled);
            stateStore.Save(enabled with { SelectedGameId = "hsr" });
            var primaryBefore = File.ReadAllBytes(stateStore.StatePath);
            var backupBefore = File.ReadAllBytes(stateStore.BackupPath);
            var revocations = new PublisherConsentRevocationStore(profileRoot);

            Assert.True(revocations.MarkOptOutPending("HoYoLAB"));
            Assert.Equal(primaryBefore, File.ReadAllBytes(stateStore.StatePath));
            Assert.Equal(backupBefore, File.ReadAllBytes(stateStore.BackupPath));

            var restartedMarker = new PublisherConsentRevocationStore(profileRoot);
            var restartedState = new LauncherStateStore(stateRoot).Load().State!;
            Assert.True(restartedMarker.IsPending("HoYoLAB"));
            Assert.True(restartedMarker.IsOptOutPending("HoYoLAB"));
            Assert.True(restartedState.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.False(restartedState.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);

            stateStore.UpdatePublisherCleanupPending(
                "HoYoLAB",
                cleanupPending: true,
                accountAccess: restartedMarker.IsOptOutPending("HoYoLAB")
                    ? false
                    : null);
            Assert.True(restartedMarker.Clear("HoYoLAB"));
            stateStore.UpdatePublisherCleanupPending(
                "HoYoLAB",
                cleanupPending: false);

            var completed = new LauncherStateStore(stateRoot).Load().State!;
            Assert.False(completed.Preferences.FeatureFlags.HoyoLabAccountAccess);
            Assert.False(completed.Preferences.FeatureFlags.HoyoLabAccountCleanupPending);
            Assert.False(new PublisherConsentRevocationStore(profileRoot)
                .IsPending("HoYoLAB"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("HoYoLAB")]
    [InlineData("SKPORT")]
    public void Quarantine_marker_cleanup_never_clears_concurrent_opt_out_intent(
        string provider)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-opt-out-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PublisherConsentRevocationStore(root);
            Assert.True(store.MarkPending(provider));
            Assert.False(store.RecoveryMustDisableAccess(
                provider,
                stateAccountAccess: true,
                stateCleanupPending: true));
            Assert.True(store.MarkOptOutPending(provider));

            Assert.True(store.ClearCleanupPending(provider));

            var restarted = new PublisherConsentRevocationStore(root);
            Assert.True(restarted.IsPending(provider));
            Assert.False(restarted.IsCleanupPending(provider));
            Assert.True(restarted.IsOptOutPending(provider));
            Assert.True(restarted.RecoveryMustDisableAccess(
                provider,
                stateAccountAccess: true,
                stateCleanupPending: false));
            Assert.True(restarted.Clear(provider));
            Assert.False(restarted.IsPending(provider));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("HoYoLAB")]
    [InlineData("SKPORT")]
    public void Legacy_generic_marker_only_is_treated_as_opt_out_for_both_providers(
        string provider)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-legacy-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PublisherConsentRevocationStore(root);
            Assert.True(store.MarkPending(provider));

            Assert.True(store.RecoveryMustDisableAccess(
                provider,
                stateAccountAccess: true,
                stateCleanupPending: false));
            Assert.False(store.RecoveryMustDisableAccess(
                provider,
                stateAccountAccess: true,
                stateCleanupPending: true));
            Assert.True(store.RecoveryMustDisableAccess(
                provider,
                stateAccountAccess: false,
                stateCleanupPending: true));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("HoYoLAB")]
    [InlineData("SKPORT")]
    public void Legacy_marker_state_write_failure_retries_without_reenabling_access(
        string provider)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-publisher-legacy-retry-" + Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(root, "state");
        var profileRoot = Path.Combine(root, "profiles");
        try
        {
            var stateStore = new LauncherStateStore(stateRoot);
            var flags = LauncherFeatureFlags.Defaults() with
            {
                HoyoLabAccountAccess = provider == "HoYoLAB",
                SkportAccountAccess = provider == "SKPORT",
            };
            var enabled = LauncherState.Defaults() with
            {
                Preferences = LauncherState.Defaults().Preferences with
                {
                    FeatureFlags = flags,
                },
            };
            stateStore.Save(enabled);
            stateStore.Save(enabled with { SelectedGameId = "hsr" });
            var revocations = new PublisherConsentRevocationStore(profileRoot);
            Assert.True(revocations.MarkPending(provider));
            File.WriteAllText(stateStore.StatePath, "{bad");
            var backupBefore = File.ReadAllBytes(stateStore.BackupPath);
            var recovered = stateStore.Load().State!;
            var recoveredFlags = recovered.Preferences.FeatureFlags;
            var stateAccess = provider == "HoYoLAB"
                ? recoveredFlags.HoyoLabAccountAccess
                : recoveredFlags.SkportAccountAccess;
            var statePending = provider == "HoYoLAB"
                ? recoveredFlags.HoyoLabAccountCleanupPending
                : recoveredFlags.SkportAccountCleanupPending;
            Assert.True(revocations.RecoveryMustDisableAccess(
                provider,
                stateAccess,
                statePending));

            Assert.Throws<IOException>(() =>
                stateStore.UpdatePublisherCleanupPending(
                    provider,
                    cleanupPending: true,
                    accountAccess: false));
            Assert.True(revocations.IsPending(provider));
            Assert.Equal(backupBefore, File.ReadAllBytes(stateStore.BackupPath));

            Assert.True(stateStore.RestoreLastKnownGood().IsUsable);
            stateStore.UpdatePublisherCleanupPending(
                provider,
                cleanupPending: true,
                accountAccess: false);
            Assert.True(revocations.Clear(provider));
            stateStore.UpdatePublisherCleanupPending(
                provider,
                cleanupPending: false);

            var completedFlags = stateStore.Load().State!.Preferences.FeatureFlags;
            Assert.False(provider == "HoYoLAB"
                ? completedFlags.HoyoLabAccountAccess
                : completedFlags.SkportAccountAccess);
            Assert.False(provider == "HoYoLAB"
                ? completedFlags.HoyoLabAccountCleanupPending
                : completedFlags.SkportAccountCleanupPending);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CopyProtector : IPublisherRoleBindingProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext];
        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext];
    }
}
