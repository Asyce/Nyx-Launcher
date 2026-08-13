using System.Text;
using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class UpdateTransactionTests
{
    public static IEnumerable<object[]> ApplyCrashCheckpoints =>
        Enum.GetValues<UpdateTransactionCheckpoint>()
            .Where(value => value.ToString().StartsWith("Apply", StringComparison.Ordinal))
            .Select(value => new object[] { value.ToString() });

    public static IEnumerable<object[]> RollbackCrashCheckpoints =>
        Enum.GetValues<UpdateTransactionCheckpoint>()
            .Where(value => value.ToString().StartsWith("Rollback", StringComparison.Ordinal))
            .Select(value => new object[] { value.ToString() });

    public static IEnumerable<object[]> AbandonCrashCheckpoints =>
        Enum.GetValues<UpdateTransactionCheckpoint>()
            .Where(value => value.ToString().StartsWith("Abandon", StringComparison.Ordinal))
            .Select(value => new object[] { value.ToString() });

    [Fact]
    public void Apply_is_pending_and_rollback_restores_exact_previous_tree()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "development"));
        var stage = fixture.CreateReadyTree(manifest);

        UpdateTransaction.Apply(layout, manifest, stage);

        Assert.Equal("new-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.True(File.Exists(layout.PendingPath));
        Assert.Equal("2.0.0.0", UpdateTransaction.ReadPending(layout.PendingPath)!.TargetVersion);

        Assert.True(UpdateTransaction.Rollback(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.Equal("old-data", File.ReadAllText(Path.Combine(layout.AppRoot, "Assets", "data.txt")));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.Equal("1.0.0.0", UpdateTransaction.ReadActive(layout.ActivePath)!.Version);
    }

    [Fact]
    public void Confirmation_rechecks_active_files_and_tampering_leaves_rollback_available()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "development"));
        UpdateTransaction.Apply(layout, manifest, fixture.CreateReadyTree(manifest));
        File.WriteAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe"), "tampered", new UTF8Encoding(false));

        var exception = Assert.Throws<UpdateContractException>(() => UpdateTransaction.Confirm(layout, manifest));

        Assert.Equal("StagedTreeMismatch", exception.Code);
        Assert.True(File.Exists(layout.PendingPath));
        Assert.True(UpdateTransaction.Rollback(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
    }

    [Fact]
    public void Invalid_staged_tree_never_moves_current_app()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        var stage = fixture.CreateReadyTree(manifest);
        File.WriteAllText(Path.Combine(stage, "Nyx.Desktop.App.exe"), "tampered", new UTF8Encoding(false));

        Assert.Throws<UpdateContractException>(() => UpdateTransaction.Apply(layout, manifest, stage));

        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.False(File.Exists(layout.PendingPath));
    }

    [Fact]
    public void Second_apply_is_blocked_until_first_update_is_confirmed_or_rolled_back()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        UpdateTransaction.Apply(layout, manifest, fixture.CreateReadyTree(manifest));
        var secondStage = Path.Combine(layout.StagingRoot, "ready-second");
        CopyTree(layout.AppRoot, secondStage);

        var exception = Assert.Throws<UpdateContractException>(
            () => UpdateTransaction.Apply(layout, manifest, secondStage));

        Assert.Equal("PendingUpdateExists", exception.Code);
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
    }

    [Fact]
    public void Failed_first_install_can_be_abandoned_without_touching_user_data()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        Directory.CreateDirectory(layout.UserDataRoot);
        File.WriteAllText(Path.Combine(layout.UserDataRoot, "keep.txt"), "keep");
        UpdateTransaction.Apply(layout, manifest, fixture.CreateReadyTree(manifest));

        Assert.True(UpdateTransaction.AbandonUnconfirmedFirstInstall(layout));

        Assert.False(Directory.Exists(layout.AppRoot));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(layout.UserDataRoot, "keep.txt")));
    }

    [Theory]
    [MemberData(nameof(ApplyCrashCheckpoints))]
    public void Every_apply_crash_phase_is_reconciled_and_keeps_rollback_available(string crashAtName)
    {
        var crashAt = Enum.Parse<UpdateTransactionCheckpoint>(crashAtName);
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "development"));
        var reached = false;

        Assert.Throws<SimulatedProcessTerminationException>(() =>
            UpdateTransaction.ApplyWithFaultInjection(layout, manifest, fixture.CreateReadyTree(manifest), checkpoint =>
            {
                if (checkpoint == crashAt)
                {
                    reached = true;
                    throw new SimulatedProcessTerminationException();
                }
            }));

        Assert.True(reached);
        UpdateTransaction.Reconcile(layout);
        Assert.False(File.Exists(layout.TransactionPath));
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.Equal("2.0.0.0", UpdateTransaction.ReadPending(layout.PendingPath)!.TargetVersion);
        Assert.True(UpdateTransaction.Rollback(layout));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
    }

    [Theory]
    [MemberData(nameof(RollbackCrashCheckpoints))]
    public void Every_rollback_crash_phase_is_reconciled_to_the_previous_tree(string crashAtName)
    {
        var crashAt = Enum.Parse<UpdateTransactionCheckpoint>(crashAtName);
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        AtomicJson.Write(layout.ActivePath, new ActiveRelease(1, "1.0.0.0", "development"));
        UpdateTransaction.Apply(layout, manifest, fixture.CreateReadyTree(manifest));
        var reached = false;

        Assert.Throws<SimulatedProcessTerminationException>(() =>
            UpdateTransaction.RollbackWithFaultInjection(layout, checkpoint =>
            {
                if (checkpoint == crashAt)
                {
                    reached = true;
                    throw new SimulatedProcessTerminationException();
                }
            }));

        Assert.True(reached);
        UpdateTransaction.Reconcile(layout);
        Assert.False(File.Exists(layout.TransactionPath));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
        Assert.Equal("old-data", File.ReadAllText(Path.Combine(layout.AppRoot, "Assets", "data.txt")));
    }

    [Theory]
    [MemberData(nameof(AbandonCrashCheckpoints))]
    public void Every_first_install_abandon_crash_phase_is_reconciled_without_touching_user_data(string crashAtName)
    {
        var crashAt = Enum.Parse<UpdateTransactionCheckpoint>(crashAtName);
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        Directory.CreateDirectory(layout.UserDataRoot);
        File.WriteAllText(Path.Combine(layout.UserDataRoot, "keep.txt"), "keep");
        UpdateTransaction.Apply(layout, manifest, fixture.CreateReadyTree(manifest));
        var reached = false;

        Assert.Throws<SimulatedProcessTerminationException>(() =>
            UpdateTransaction.AbandonWithFaultInjection(layout, checkpoint =>
            {
                if (checkpoint == crashAt)
                {
                    reached = true;
                    throw new SimulatedProcessTerminationException();
                }
            }));

        Assert.True(reached);
        UpdateTransaction.Reconcile(layout);
        Assert.False(File.Exists(layout.TransactionPath));
        Assert.False(File.Exists(layout.PendingPath));
        Assert.False(Directory.Exists(layout.AppRoot));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(layout.UserDataRoot, "keep.txt")));
    }

    [Fact]
    public void Reconciliation_fails_closed_when_a_crashed_stage_is_replaced_by_a_link()
    {
        using var fixture = new PackageFixture();
        var manifest = fixture.CreatePackage();
        var layout = fixture.CreateLayout();
        WriteOldInstall(layout);
        var stage = fixture.CreateReadyTree(manifest);
        Assert.Throws<SimulatedProcessTerminationException>(() =>
            UpdateTransaction.ApplyWithFaultInjection(layout, manifest, stage, checkpoint =>
            {
                if (checkpoint == UpdateTransactionCheckpoint.ApplyJournalPrepared)
                {
                    throw new SimulatedProcessTerminationException();
                }
            }));
        Directory.Delete(stage, recursive: true);
        var outside = Path.Combine(fixture.Root, "outside-stage");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(stage, outside);

        try
        {
            var exception = Assert.Throws<UpdateContractException>(() => UpdateTransaction.Reconcile(layout));
            Assert.Equal("UnsafePath", exception.Code);
            Assert.Equal("old-app", File.ReadAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Directory.Delete(stage);
        }
    }

    private static void WriteOldInstall(UpdateLayout layout)
    {
        Directory.CreateDirectory(Path.Combine(layout.AppRoot, "Assets"));
        File.WriteAllText(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe"), "old-app", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(layout.AppRoot, "Assets", "data.txt"), "old-data", new UTF8Encoding(false));
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class SimulatedProcessTerminationException : Exception
    {
    }
}
