using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class StableUpdateTests
{
    [Fact]
    public void Older_pending_metadata_without_an_expiration_field_remains_readable()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        Directory.CreateDirectory(layout.MetadataRoot);
        File.WriteAllText(
            layout.PendingPath,
            """
            {
              "SchemaVersion": 1,
              "TargetVersion": "2.0.0.0",
              "PreviousVersion": "1.0.0.0",
              "BackupDirectoryName": null,
              "ManifestSha256": null
            }
            """);

        var pending = UpdateTransaction.ReadPending(layout.PendingPath);

        Assert.NotNull(pending);
        Assert.False(pending.ConfirmationExpired);
    }

    [Fact]
    public void Stable_apply_binds_exact_manifest_and_confirm_current_promotes_it()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var manifestBytes = File.ReadAllBytes(fixture.ManifestPath);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));

        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            manifestBytes,
            "1.0.0.0");

        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        Assert.Equal(expectedHash, UpdateTransaction.ReadPending(layout.PendingPath)!.ManifestSha256);
        Assert.Equal(manifestBytes, File.ReadAllBytes(layout.PendingManifestPath));
        Assert.True(ConfirmCurrent(layout));
        Assert.Equal("2.0.0.0", UpdateTransaction.ReadActive(layout.ActivePath)!.Version);
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(File.Exists(layout.PendingManifestPath));
    }

    [Theory]
    [InlineData("stable", "stable", "2.0.0.0", "2.0.0.0")]
    [InlineData("stable", "stable", "3.0.0.0", "3.0.0.0")]
    [InlineData("development", "stable", "1.0.0.0", "1.0.0.0")]
    [InlineData("stable", "development", "1.0.0.0", "1.0.0.0")]
    [InlineData("stable", "stable", "1.0.0.0", "1.0.0.1")]
    public void Stable_apply_rejects_equal_lower_channel_and_active_parent_mismatches(
        string targetChannel,
        string activeChannel,
        string activeVersion,
        string parentVersion)
    {
        using var fixture = new PackageFixture();
        var packageUrl = targetChannel == "stable"
            ? $"https://pengo.gg/desktop/updates/stable/{Path.GetFileName(fixture.PackagePath)}"
            : null;
        var manifest = fixture.CreatePackage(channel: targetChannel, packageUrl: packageUrl);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, activeVersion, activeChannel));

        var exception = Assert.Throws<UpdateContractException>(() => UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            parentVersion));

        Assert.Equal("StableUpgradeRejected", exception.Code);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(File.Exists(layout.TransactionPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_swapped_bound_manifest_cannot_confirm_but_rollback_restores_the_old_tree(
        bool deleteManifest)
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var exactBytes = File.ReadAllBytes(fixture.ManifestPath);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            exactBytes,
            "1.0.0.0");

        if (deleteManifest) File.Delete(layout.PendingManifestPath);
        else File.AppendAllText(layout.PendingManifestPath, "\n", new UTF8Encoding(false));

        Assert.Equal(
            "PendingManifestMismatch",
            Assert.Throws<UpdateContractException>(() => ConfirmCurrent(layout)).Code);
        Assert.True(UpdateTransaction.Rollback(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(File.Exists(layout.PendingManifestPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_rollback_with_a_missing_or_swapped_manifest_remains_recoverable(
        bool deleteManifest)
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");

        if (deleteManifest) File.Delete(layout.PendingManifestPath);
        else File.AppendAllText(layout.PendingManifestPath, "\n", new UTF8Encoding(false));

        Assert.Throws<SimulatedTermination>(() => UpdateTransaction.RollbackWithFaultInjection(
            layout,
            checkpoint =>
            {
                if (checkpoint == UpdateTransactionCheckpoint.RollbackCurrentAppMoved)
                    throw new SimulatedTermination();
            }));

        Assert.True(File.Exists(layout.TransactionPath));
        Assert.True(File.Exists(layout.PendingPath));
        Assert.Equal(!deleteManifest, File.Exists(layout.PendingManifestPath));
        Assert.Contains(
            Directory.EnumerateDirectories(layout.RollbackRoot),
            path => Path.GetFileName(path).StartsWith("previous-", StringComparison.Ordinal));

        UpdateTransaction.Reconcile(layout);

        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.TransactionPath));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(File.Exists(layout.PendingManifestPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launch_reconciliation_rolls_back_an_interrupted_apply_when_its_manifest_is_unusable(
        bool deleteManifest)
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));

        Assert.Throws<SimulatedTermination>(() => UpdateTransaction.ApplyStableWithFaultInjection(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0",
            checkpoint =>
            {
                if (checkpoint == UpdateTransactionCheckpoint.ApplyOldAppMoved)
                    throw new SimulatedTermination();
            }));

        if (deleteManifest) File.Delete(layout.PendingManifestPath);
        else File.AppendAllText(layout.PendingManifestPath, "\n", new UTF8Encoding(false));

        Assert.False(StableUpdateRunner.RecoverForLaunch(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.TransactionPath));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(File.Exists(layout.PendingManifestPath));
    }

    [Fact]
    public void Tampered_relaunched_tree_cannot_confirm_and_still_rolls_back()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        File.WriteAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe"), "tampered");

        Assert.Equal(
            "StagedTreeMismatch",
            Assert.Throws<UpdateContractException>(() => ConfirmCurrent(layout)).Code);
        Assert.True(File.Exists(layout.PendingPath));
        Assert.True(File.Exists(layout.PendingManifestPath));
        Assert.True(UpdateTransaction.Rollback(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
    }

    [Fact]
    public void Launch_reconciliation_recovers_a_stop_after_the_old_app_rename()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));

        Assert.Throws<SimulatedTermination>(() => UpdateTransaction.ApplyStableWithFaultInjection(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0",
            checkpoint =>
            {
                if (checkpoint == UpdateTransactionCheckpoint.ApplyOldAppMoved)
                    throw new SimulatedTermination();
            }));

        Assert.True(StableUpdateRunner.RecoverForLaunch(layout));
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.Equal("2.0.0.0", UpdateTransaction.ReadPending(layout.PendingPath)!.TargetVersion);
        Assert.True(UpdateTransaction.Rollback(layout));
    }

    [Fact]
    public void Eof_before_apply_never_waits_or_mutates_the_app()
    {
        using var fixture = new PackageFixture();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        var waited = false;
        using var output = new StringWriter();

        var accepted = StableUpdateRunner.RunApplyGate(
            new StringReader(string.Empty),
            output,
            () => waited = true);

        Assert.False(accepted);
        Assert.False(waited);
        Assert.Equal("READY" + Environment.NewLine, output.ToString());
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
    }

    [Fact]
    public void Apply_gate_waits_for_the_already_open_parent_before_returning()
    {
        var order = new List<string>();
        using var output = new StringWriter();

        Assert.True(StableUpdateRunner.RunApplyGate(
            new StringReader("APPLY\n"),
            output,
            () => order.Add("parent-exited")));
        order.Add("apply");

        Assert.Equal(["parent-exited", "apply"], order);
    }

    [Fact]
    public void Eof_cleanup_removes_only_the_known_handoff_files_and_ready_tree()
    {
        using var fixture = new PackageFixture();
        var handoff = Path.Combine(fixture.Staging, $"handoff-{Guid.NewGuid():N}");
        var manifest = $"{handoff}.release.json";
        var package = $"{handoff}.package";
        var owner = $"{handoff}.owner.json";
        var ready = Path.Combine(fixture.Staging, "ready-2.0.0.0");
        var outside = Path.Combine(fixture.Root, "outside");
        File.WriteAllText(manifest, "manifest");
        File.WriteAllText(package, "package");
        File.WriteAllText(owner, "owner");
        Directory.CreateDirectory(ready);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

        StableUpdateRunner.DiscardPrepared(manifest, package, ready, owner);

        Assert.False(File.Exists(manifest));
        Assert.False(File.Exists(package));
        Assert.False(File.Exists(owner));
        Assert.False(Directory.Exists(ready));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Living_unconfirmed_child_timeout_closes_terminates_and_rolls_back_in_order()
    {
        var result = StableUpdateRunner.WaitForConfirmation(
            () => true,
            _ => false,
            TimeSpan.Zero);
        var order = new List<string>();
        var waits = 0;

        StableUpdateRunner.RecoverUnconfirmedChild(
            result,
            _ =>
            {
                order.Add("expire-confirmation");
                return ConfirmationExpirationResult.Expired;
            },
            TimeSpan.FromSeconds(1),
            () =>
            {
                order.Add("close-exact-child");
                return true;
            },
            _ =>
            {
                order.Add("wait-exact-child");
                return ++waits == 2;
            },
            () => order.Add("terminate-exact-child"),
            () => order.Add("rollback-and-relaunch"),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(StableUpdateRunner.PendingMonitorResult.TimedOut, result);
        Assert.Equal(
            [
                "expire-confirmation",
                "close-exact-child",
                "wait-exact-child",
                "terminate-exact-child",
                "wait-exact-child",
                "rollback-and-relaunch",
            ],
            order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_swapped_manifest_confirmation_failure_is_recovered_by_monitor_timeout(
        bool deleteManifest)
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        if (deleteManifest) File.Delete(layout.PendingManifestPath);
        else File.AppendAllText(layout.PendingManifestPath, "\n", new UTF8Encoding(false));

        Assert.Throws<UpdateContractException>(() => ConfirmCurrent(layout));
        var relaunched = false;
        StableUpdateRunner.RecoverUnconfirmedChild(
            StableUpdateRunner.PendingMonitorResult.TimedOut,
            lockWait => UpdateTransaction.TryExpireConfirmation(layout, lockWait),
            TimeSpan.FromSeconds(1),
            () => true,
            _ => true,
            () => throw new InvalidOperationException("A cooperative exact child must not be terminated."),
            () =>
            {
                Assert.True(UpdateTransaction.Rollback(layout));
                relaunched = true;
            },
            TimeSpan.FromMilliseconds(1));

        Assert.True(relaunched);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
    }

    [Fact]
    public async Task Timeout_retries_the_real_confirm_lock_then_expires_closes_and_rolls_back()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        var order = new List<string>();
        var attempts = 0;
        using var confirmationAtCommit = new ManualResetEventSlim();
        using var releaseConfirmation = new ManualResetEventSlim();
        var confirmation = Task.Run(() =>
        {
            var exception = Assert.Throws<UpdateContractException>(() =>
                UpdateTransaction.ConfirmCurrent(
                    layout,
                    "2.0.0.0",
                    () =>
                    {
                        confirmationAtCommit.Set();
                        releaseConfirmation.Wait();
                        throw new UpdateContractException("CallerProcessInvalid");
                    }));
            Assert.Equal("CallerProcessInvalid", exception.Code);
        });
        Assert.True(confirmationAtCommit.Wait(TimeSpan.FromSeconds(5)));

        var recovery = Task.Run(() => StableUpdateRunner.RecoverUnconfirmedChild(
            StableUpdateRunner.PendingMonitorResult.TimedOut,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                var result = UpdateTransaction.TryExpireConfirmation(
                    layout,
                    TimeSpan.FromMilliseconds(20));
                if (result is ConfirmationExpirationResult.Expired)
                    order.Add("expire-under-update-lock");
                return result;
            },
            TimeSpan.FromSeconds(5),
            () =>
            {
                order.Add("close-exact-child");
                Assert.Equal(
                    "ConfirmationExpired",
                    Assert.Throws<UpdateContractException>(() => ConfirmCurrent(layout)).Code);
                order.Add("late-confirm-rejected");
                Assert.True(File.Exists(layout.PendingPath));
                return true;
            },
            milliseconds =>
            {
                if (milliseconds == 0) return false;
                order.Add("wait-during-grace");
                return true;
            },
            () => throw new InvalidOperationException("The cooperative child must not be terminated."),
            () =>
            {
                Assert.True(UpdateTransaction.Rollback(layout));
                order.Add("rollback-and-relaunch");
            },
            TimeSpan.FromMilliseconds(1)));

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref attempts) >= 2,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseConfirmation.Set();
        }
        await confirmation.WaitAsync(TimeSpan.FromSeconds(5));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "expire-under-update-lock",
                "close-exact-child",
                "late-confirm-rejected",
                "wait-during-grace",
                "rollback-and-relaunch",
            ],
            order);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
    }

    [Fact]
    public async Task Timeout_retry_observes_confirmation_as_the_real_lock_winner_without_rollback()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        var attempts = 0;
        using var confirmationAtCommit = new ManualResetEventSlim();
        using var releaseConfirmation = new ManualResetEventSlim();
        var confirmation = Task.Run(() => UpdateTransaction.ConfirmCurrent(
            layout,
            "2.0.0.0",
            () =>
            {
                confirmationAtCommit.Set();
                releaseConfirmation.Wait();
            }));
        Assert.True(confirmationAtCommit.Wait(TimeSpan.FromSeconds(5)));

        var recovery = Task.Run(() => StableUpdateRunner.RecoverUnconfirmedChild(
            StableUpdateRunner.PendingMonitorResult.TimedOut,
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return UpdateTransaction.TryExpireConfirmation(
                    layout,
                    TimeSpan.FromMilliseconds(20));
            },
            TimeSpan.FromSeconds(5),
            () => throw new InvalidOperationException("A confirmed child must not be closed."),
            milliseconds => milliseconds == 0
                ? false
                : throw new InvalidOperationException("A confirmed child must not be awaited for shutdown."),
            () => throw new InvalidOperationException("A confirmed child must not be terminated."),
            () => throw new InvalidOperationException("A confirmed update must not roll back."),
            TimeSpan.FromMilliseconds(1)));

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref attempts) >= 2,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseConfirmation.Set();
        }
        Assert.True(await confirmation.WaitAsync(TimeSpan.FromSeconds(5)));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(layout.PendingPath));
        Assert.Equal("2.0.0.0", UpdateTransaction.ReadActive(layout.ActivePath)!.Version);
    }

    [Fact]
    public void Permanently_busy_expiration_returns_when_the_exact_child_exits_and_preserves_retry_state()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        var pending = UpdateTransaction.ReadPending(layout.PendingPath)!;
        var backup = Path.Combine(layout.RollbackRoot, pending.BackupDirectoryName!);
        var attempts = 0;

        using (OpenExclusiveLock(layout.LockPath))
        using (OpenExclusiveLock(Path.Combine(layout.InstallRoot, ".pending-monitor.lock")))
        {
            StableUpdateRunner.RecoverUnconfirmedChild(
                StableUpdateRunner.PendingMonitorResult.TimedOut,
                _ =>
                {
                    Interlocked.Increment(ref attempts);
                    return UpdateTransaction.TryExpireConfirmation(
                        layout,
                        TimeSpan.FromMilliseconds(20));
                },
                TimeSpan.FromSeconds(1),
                () => throw new InvalidOperationException("An unclaimed child must not be closed."),
                milliseconds =>
                {
                    Assert.Equal(0, milliseconds);
                    return Volatile.Read(ref attempts) >= 2;
                },
                () => throw new InvalidOperationException("An unclaimed child must not be terminated."),
                () => throw new InvalidOperationException("An unclaimed update must not roll back."),
                TimeSpan.FromMilliseconds(1));

            Assert.True(attempts >= 2);
            Assert.True(File.Exists(layout.PendingPath));
            Assert.True(File.Exists(layout.PendingManifestPath));
            Assert.True(Directory.Exists(backup));
        }

        Assert.True(StableUpdateRunner.RecoverForLaunch(layout));
        Assert.True(File.Exists(layout.PendingPath));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void Permanently_busy_expiration_honors_the_overall_bound_without_touching_a_living_child()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");
        var pending = UpdateTransaction.ReadPending(layout.PendingPath)!;
        var backup = Path.Combine(layout.RollbackRoot, pending.BackupDirectoryName!);
        var attempts = 0;
        var timer = Stopwatch.StartNew();

        using (OpenExclusiveLock(layout.LockPath))
        using (OpenExclusiveLock(Path.Combine(layout.InstallRoot, ".pending-monitor.lock")))
        {
            StableUpdateRunner.RecoverUnconfirmedChild(
                StableUpdateRunner.PendingMonitorResult.TimedOut,
                lockWait =>
                {
                    Interlocked.Increment(ref attempts);
                    return UpdateTransaction.TryExpireConfirmation(layout, lockWait);
                },
                TimeSpan.FromMilliseconds(75),
                () => throw new InvalidOperationException("An unclaimed child must not be closed."),
                milliseconds =>
                {
                    Assert.Equal(0, milliseconds);
                    return false;
                },
                () => throw new InvalidOperationException("An unclaimed child must not be terminated."),
                () => throw new InvalidOperationException("An unclaimed update must not roll back."),
                TimeSpan.FromMilliseconds(1));

            timer.Stop();
            Assert.True(attempts >= 1);
            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(layout.PendingPath));
            Assert.True(File.Exists(layout.PendingManifestPath));
            Assert.True(Directory.Exists(backup));
        }

        Assert.True(StableUpdateRunner.RecoverForLaunch(layout));
        Assert.True(File.Exists(layout.PendingPath));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void Confirm_current_rejects_a_caller_version_that_is_not_the_pending_target()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        UpdateTransaction.ApplyStable(
            layout,
            manifest,
            fixture.CreateReadyTree(manifest),
            File.ReadAllBytes(fixture.ManifestPath),
            "1.0.0.0");

        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() =>
                UpdateTransaction.ConfirmCurrent(layout, "1.0.0.0", static () => { })).Code);
        Assert.True(File.Exists(layout.PendingPath));
    }

    [Fact]
    public void Caller_binding_accepts_only_the_exact_older_living_process_handle()
    {
        var currentPath = Environment.ProcessPath ?? throw new InvalidOperationException();
        using var caller = StableUpdateRunner.BoundAppProcess.OpenExpected(
            currentPath,
            Environment.ProcessId,
            long.MaxValue);

        caller.RequireRunning();
        Assert.True(UpdateManifestReader.TryParseVersion(caller.Version));
        using (var current = Process.GetCurrentProcess())
        {
            Assert.Equal(
                current.StartTime.ToUniversalTime().ToFileTimeUtc(),
                caller.StartedAtFileTime);
        }

        var wrongImage = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() => StableUpdateRunner.BoundAppProcess.OpenExpected(
                wrongImage,
                Environment.ProcessId,
                long.MaxValue)).Code);
        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() => StableUpdateRunner.BoundAppProcess.OpenExpected(
                currentPath,
                Environment.ProcessId,
                updaterStartedAtFileTime: 0)).Code);
        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() => StableUpdateRunner.BoundAppProcess.OpenExpected(
                currentPath,
                processId: -1,
                long.MaxValue)).Code);
        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() => StableUpdateRunner.BoundAppProcess.OpenExpected(
                currentPath,
                processId: int.MaxValue,
                long.MaxValue)).Code);
    }

    [Fact]
    public void Caller_binding_rejects_an_exited_process()
    {
        using var exited = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ArgumentList = { "/d", "/c", "exit", "0" },
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException();
        exited.WaitForExit();

        Assert.Equal(
            "CallerProcessInvalid",
            Assert.Throws<UpdateContractException>(() => StableUpdateRunner.BoundAppProcess.OpenExpected(
                exited.StartInfo.FileName,
                exited.Id,
                long.MaxValue)).Code);
    }

    [Fact]
    public void Stable_stage_retry_uses_a_new_ready_tree_after_post_stage_process_death()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var flatPackage = Path.Combine(fixture.Bundle, $"handoff-{Guid.NewGuid():N}.package");
        File.Copy(fixture.PackagePath, flatPackage);

        var orphaned = UpdatePackageStager.StageStable(
            manifest,
            flatPackage,
            fixture.Staging,
            Guid.NewGuid().ToString("N"));
        var retried = UpdatePackageStager.StageStable(
            manifest,
            flatPackage,
            fixture.Staging,
            Guid.NewGuid().ToString("N"));

        Assert.NotEqual(orphaned, retried);
        Assert.StartsWith($"ready-{manifest.Version}-", Path.GetFileName(orphaned), StringComparison.Ordinal);
        Assert.True(Directory.Exists(orphaned));
        Assert.True(Directory.Exists(retried));
    }

    [Fact]
    public void Post_stage_death_is_durably_owned_and_next_locked_launch_cleans_then_allows_retry()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "stable"));
        var deadId = Guid.NewGuid().ToString("N");
        var deadNames = WriteArtifactOwner(layout, deadId, manifest.Version, int.MaxValue, 1);
        var deadManifest = Path.Combine(layout.StagingRoot, deadNames.ManifestFileName);
        var deadPackage = Path.Combine(layout.StagingRoot, deadNames.PackageFileName);
        File.Copy(fixture.ManifestPath, deadManifest);
        File.Copy(fixture.PackagePath, deadPackage);

        Assert.Throws<SimulatedTermination>(() => UpdatePackageStager.StageStable(
            manifest,
            deadPackage,
            layout.StagingRoot,
            deadId,
            checkpoint =>
            {
                if (checkpoint is StableUpdateStageCheckpoint.ReadyPublished)
                    throw new SimulatedTermination();
            }));

        Assert.True(File.Exists(Path.Combine(layout.StagingRoot, deadNames.OwnerFileName)));
        Assert.True(Directory.Exists(Path.Combine(layout.StagingRoot, deadNames.ReadyDirectoryName)));
        Assert.False(StableUpdateRunner.RecoverForLaunch(layout));
        Assert.False(File.Exists(Path.Combine(layout.StagingRoot, deadNames.OwnerFileName)));
        Assert.False(File.Exists(deadManifest));
        Assert.False(File.Exists(deadPackage));
        Assert.False(Directory.Exists(Path.Combine(layout.StagingRoot, deadNames.ReadyDirectoryName)));

        var retryId = Guid.NewGuid().ToString("N");
        var retryNames = WriteArtifactOwner(layout, retryId, manifest.Version, Environment.ProcessId, 1);
        var retryPackage = Path.Combine(layout.StagingRoot, retryNames.PackageFileName);
        File.Copy(fixture.PackagePath, retryPackage);
        var retried = UpdatePackageStager.StageStable(
            manifest,
            retryPackage,
            layout.StagingRoot,
            retryId);
        Assert.True(Directory.Exists(retried));
    }

    [Fact]
    public void Artifact_cleanup_preserves_active_transaction_owned_and_unknown_names()
    {
        using var fixture = new PackageFixture();
        var manifest = StablePackage(fixture);
        var layout = fixture.CreateLayout();
        Directory.CreateDirectory(layout.StagingRoot);
        Directory.CreateDirectory(layout.MetadataRoot);

        const string activeId = "11111111111111111111111111111111";
        var active = WriteArtifactOwner(layout, activeId, manifest.Version, 111, 222);
        File.WriteAllText(Path.Combine(layout.StagingRoot, active.ManifestFileName), "active");

        const string transactionId = "22222222222222222222222222222222";
        var transaction = WriteArtifactOwner(layout, transactionId, manifest.Version, 333, 444);
        Directory.CreateDirectory(Path.Combine(layout.StagingRoot, transaction.ReadyDirectoryName));
        File.WriteAllText(
            Path.Combine(layout.StagingRoot, transaction.ReadyDirectoryName, "owned.txt"),
            "transaction");
        AtomicJson.Write(
            layout.TransactionPath,
            new UpdateJournal(
                1,
                "apply",
                "prepared",
                manifest.Version,
                null,
                null,
                transaction.ReadyDirectoryName,
                null));

        var unknownFile = Path.Combine(layout.StagingRoot, "notes.txt");
        var unknownDirectory = Path.Combine(layout.StagingRoot, "ready-user-data");
        File.WriteAllText(unknownFile, "user");
        Directory.CreateDirectory(unknownDirectory);
        File.WriteAllText(Path.Combine(unknownDirectory, "keep.txt"), "keep");

        UpdateTransaction.CleanupDeadStableArtifacts(
            layout,
            (processId, startedAt) => processId == 111 && startedAt == 222);

        Assert.True(File.Exists(Path.Combine(layout.StagingRoot, active.OwnerFileName)));
        Assert.True(File.Exists(Path.Combine(layout.StagingRoot, active.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(layout.StagingRoot, transaction.OwnerFileName)));
        Assert.True(Directory.Exists(Path.Combine(layout.StagingRoot, transaction.ReadyDirectoryName)));
        Assert.Equal("user", File.ReadAllText(unknownFile));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unknownDirectory, "keep.txt")));
    }

    [Fact]
    public void Handoff_protocol_opens_exact_parent_before_ready_and_never_reopens_its_pid()
    {
        var desktopRoot = FindDesktopRoot();
        var runner = File.ReadAllText(Path.Combine(
            desktopRoot,
            "tools",
            "Nyx.Desktop.Update",
            "StableUpdateRunner.cs"));
        var program = File.ReadAllText(Path.Combine(
            desktopRoot,
            "tools",
            "Nyx.Desktop.Update",
            "Program.cs"));

        Assert.Contains("\"handoff\"", program, StringComparison.Ordinal);
        Assert.Contains("\"--manifest\", var handoffManifestPath", program, StringComparison.Ordinal);
        Assert.Contains("\"--package\", var handoffPackagePath", program, StringComparison.Ordinal);
        Assert.Contains("\"--parent-pid\", var parentProcessIdText", program, StringComparison.Ordinal);
        Assert.Contains("\"confirm-current\", \"--caller-pid\", var callerProcessIdText", program, StringComparison.Ordinal);
        Assert.True(
            runner.IndexOf("BoundAppProcess.Open", StringComparison.Ordinal)
            < runner.IndexOf("UpdatePackageStager.StageStable", StringComparison.Ordinal));
        Assert.True(
            runner.IndexOf("UpdatePackageStager.StageStable", StringComparison.Ordinal)
            < runner.IndexOf("RunApplyGate(input, output, parent.WaitForExit)", StringComparison.Ordinal));
        Assert.Contains("QueryFullProcessImageNameW", runner, StringComparison.Ordinal);
        Assert.Contains("GetProcessTimes", runner, StringComparison.Ordinal);
        Assert.Contains("WaitForSingleObject(handle, Infinite)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessById", runner, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", runner, StringComparison.Ordinal);
    }

    private static UpdateReleaseManifest StablePackage(PackageFixture fixture) =>
        fixture.CreatePackage(
            channel: "stable",
            packageUrl: $"https://pengo.gg/desktop/updates/stable/{Path.GetFileName(fixture.PackagePath)}");

    private static bool ConfirmCurrent(UpdateLayout layout) =>
        UpdateTransaction.ConfirmCurrent(layout, "2.0.0.0", static () => { });

    private static StableUpdateArtifactNames WriteArtifactOwner(
        UpdateLayout layout,
        string id,
        string version,
        int processId,
        long startedAtFileTime)
    {
        Directory.CreateDirectory(layout.StagingRoot);
        var names = StableUpdateArtifactContract.CreateNames(id, version);
        File.WriteAllBytes(
            Path.Combine(layout.StagingRoot, names.OwnerFileName),
            StableUpdateArtifactContract.SerializeOwner(new(
                1,
                processId,
                startedAtFileTime,
                version)));
        return names;
    }

    private static FileStream OpenExclusiveLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
    }

    private static void WriteOldInstall(UpdateLayout layout)
    {
        Directory.CreateDirectory(Path.Combine(layout.AppRoot, "Assets"));
        File.WriteAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe"), "old-app", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(layout.AppRoot, "Assets", "data.txt"), "old-data", new UTF8Encoding(false));
    }

    private static string FindDesktopRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Nyx.Desktop.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private sealed class SimulatedTermination : Exception
    {
    }
}
