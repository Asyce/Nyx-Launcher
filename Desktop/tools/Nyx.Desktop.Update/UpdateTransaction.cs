using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Security.Cryptography;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Update;

public sealed record UpdateLayout(
    string InstallRoot,
    string UserDataRoot,
    string LegacyUserDataRoot,
    string StartMenuShortcut)
{
    public string AppRoot => Path.Combine(InstallRoot, "app");
    public string ControlRoot => Path.Combine(InstallRoot, "control");
    public string MetadataRoot => Path.Combine(InstallRoot, "metadata");
    public string StagingRoot => Path.Combine(InstallRoot, "staging");
    public string RollbackRoot => Path.Combine(InstallRoot, "rollback");
    public string PendingPath => Path.Combine(MetadataRoot, "pending-update.json");
    public string PendingManifestPath => Path.Combine(MetadataRoot, "pending-release.json");
    public string TransactionPath => Path.Combine(MetadataRoot, "update-transaction.json");
    public string ActivePath => Path.Combine(MetadataRoot, "active-release.json");
    public string LockPath => Path.Combine(InstallRoot, ".update.lock");

    public static UpdateLayout ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return ForUserRoots(local, roaming);
    }

    public static UpdateLayout ForUserRoots(string localApplicationData, string roamingApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingApplicationData);
        return new(
            Path.Combine(localApplicationData, "Programs", "Pengo Nyx"),
            NyxUserDataPaths.CanonicalRoot(localApplicationData),
            NyxUserDataPaths.LegacyRoot(localApplicationData),
            Path.Combine(roamingApplicationData, "Microsoft", "Windows", "Start Menu", "Programs", "Pengo", "Nyx Desktop.lnk"));
    }
}

public sealed record PendingUpdate(
    int SchemaVersion,
    string TargetVersion,
    string? PreviousVersion,
    string? BackupDirectoryName,
    string? ManifestSha256 = null,
    bool ConfirmationExpired = false);

public sealed record ActiveRelease(int SchemaVersion, string Version, string Channel);

internal enum UpdateTransactionCheckpoint
{
    ApplyJournalPrepared,
    ApplyOldAppMoved,
    ApplyOldMoveRecorded,
    ApplyNewAppMoved,
    ApplyNewMoveRecorded,
    ApplyPendingPublished,
    ApplyJournalCleared,
    RollbackJournalPrepared,
    RollbackCurrentAppMoved,
    RollbackCurrentMoveRecorded,
    RollbackBackupRestored,
    RollbackRestoreRecorded,
    RollbackPendingDeleted,
    RollbackJournalCleared,
    AbandonJournalPrepared,
    AbandonAppMoved,
    AbandonMoveRecorded,
    AbandonPendingDeleted,
    AbandonJournalCleared,
}

internal enum ConfirmationExpirationResult
{
    Expired,
    Confirmed,
    Busy,
}

internal sealed record UpdateJournal(
    int SchemaVersion,
    string Operation,
    string Phase,
    string TargetVersion,
    string? PreviousVersion,
    string? BackupDirectoryName,
    string? StagedDirectoryName,
    string? DisplacedDirectoryName,
    string? ManifestSha256 = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(ActiveRelease))]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class UpdateMetadataJsonContext : JsonSerializerContext
{
}

public static class UpdateTransaction
{
    private const string ApplyOperation = "apply";
    private const string RollbackOperation = "rollback";
    private const string AbandonOperation = "abandon";
    private const string PreparedPhase = "prepared";
    private const string CurrentMovedPhase = "current-moved";
    private const string ReplacementMovedPhase = "replacement-moved";

    public static void Reconcile(UpdateLayout layout)
    {
        ValidateLayout(layout);
        if (!Directory.Exists(layout.InstallRoot))
        {
            return;
        }

        SafePaths.RequireNoReparseComponents(layout.InstallRoot);
        using var updateLock = AcquireLock(layout);
        _ = ReconcileLocked(layout, checkpoint: null);
    }

    public static void Apply(UpdateLayout layout, UpdateReleaseManifest manifest, string stagedRoot) =>
        ApplyCore(layout, manifest, stagedRoot, stable: null, checkpoint: null);

    public static void ApplyStable(
        UpdateLayout layout,
        UpdateReleaseManifest manifest,
        string stagedRoot,
        byte[] manifestBytes,
        string parentVersion) =>
        ApplyCore(
            layout,
            manifest,
            stagedRoot,
            new StableApply(manifestBytes, parentVersion),
            checkpoint: null);

    internal static void ApplyWithFaultInjection(
        UpdateLayout layout,
        UpdateReleaseManifest manifest,
        string stagedRoot,
        Action<UpdateTransactionCheckpoint> checkpoint) =>
        ApplyCore(
            layout,
            manifest,
            stagedRoot,
            stable: null,
            checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)));

    internal static void ApplyStableWithFaultInjection(
        UpdateLayout layout,
        UpdateReleaseManifest manifest,
        string stagedRoot,
        byte[] manifestBytes,
        string parentVersion,
        Action<UpdateTransactionCheckpoint> checkpoint) =>
        ApplyCore(
            layout,
            manifest,
            stagedRoot,
            new StableApply(manifestBytes, parentVersion),
            checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)));

    private static void ApplyCore(
        UpdateLayout layout,
        UpdateReleaseManifest manifest,
        string stagedRoot,
        StableApply? stable,
        Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        ValidateLayout(layout);
        UpdateManifestReader.Validate(manifest);
        SafePaths.CreateDirectoryTree(layout.InstallRoot);
        SafePaths.CreateDirectoryTree(layout.MetadataRoot);
        SafePaths.CreateDirectoryTree(layout.RollbackRoot);
        SafePaths.CreateDirectoryTree(layout.StagingRoot);
        SafePaths.RequireNoReparseComponents(layout.InstallRoot);
        using var updateLock = AcquireLock(layout);
        _ = ReconcileLocked(layout, checkpoint: null);

        if (File.Exists(layout.PendingPath))
        {
            throw new UpdateContractException("PendingUpdateExists");
        }

        var safeStage = SafePaths.RequireExistingDirectory(stagedRoot);
        var staging = SafePaths.RequireExistingDirectory(layout.StagingRoot);
        var stagingPrefix = staging + Path.DirectorySeparatorChar;
        if (!safeStage.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateContractException("StageOutsideInstallRoot");
        }

        UpdatePackageStager.VerifyTree(manifest, safeStage);
        var active = ReadActive(layout.ActivePath);
        string? manifestSha256 = null;
        if (stable is not null)
        {
            var boundManifest = UpdateManifestReader.Parse(stable.ManifestBytes);
            if (!ManifestMatches(boundManifest, manifest)
                || !string.Equals(manifest.Channel, "stable", StringComparison.Ordinal)
                || active is null
                || !string.Equals(active.Channel, "stable", StringComparison.Ordinal)
                || !string.Equals(active.Version, stable.ParentVersion, StringComparison.Ordinal)
                || !StableUpdatePolicy.IsStrictUpgrade(active.Version, manifest.Version)
                || !Directory.Exists(layout.AppRoot))
            {
                throw new UpdateContractException("StableUpgradeRejected");
            }

            manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(stable.ManifestBytes));
            AtomicFile.Write(layout.PendingManifestPath, stable.ManifestBytes);
        }

        if (File.Exists(layout.AppRoot))
        {
            throw new UpdateContractException("UnsafePath");
        }

        var backupName = Directory.Exists(layout.AppRoot) ? $"previous-{Guid.NewGuid():N}" : null;
        if (backupName is not null)
        {
            SafePaths.RequireExistingDirectory(layout.AppRoot);
        }

        var stagedName = Path.GetRelativePath(staging, safeStage).Replace(Path.DirectorySeparatorChar, '/');
        _ = SafePaths.RequireRelativeFile(stagedName);
        var journal = new UpdateJournal(
            1,
            ApplyOperation,
            PreparedPhase,
            manifest.Version,
            active?.Version,
            backupName,
            stagedName,
            null,
            manifestSha256);
        AtomicJson.Write(layout.TransactionPath, journal, UpdateMetadataJsonContext.Default.UpdateJournal);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyJournalPrepared);
        _ = ReconcileLocked(layout, checkpoint);
    }

    public static void Confirm(UpdateLayout layout, UpdateReleaseManifest manifest)
    {
        ValidateLayout(layout);
        using var updateLock = AcquireLock(layout);
        _ = ReconcileLocked(layout, checkpoint: null);
        var pending = ReadPending(layout.PendingPath);
        if (pending is null || !string.Equals(pending.TargetVersion, manifest.Version, StringComparison.Ordinal))
        {
            throw new UpdateContractException("PendingUpdateMismatch");
        }

        if (pending.ManifestSha256 is not null)
        {
            throw new UpdateContractException("PendingManifestRequired");
        }

        RequireConfirmationOpen(pending);
        UpdatePackageStager.VerifyTree(manifest, layout.AppRoot);
        AtomicJson.Write(
            layout.ActivePath,
            new ActiveRelease(1, manifest.Version, manifest.Channel),
            UpdateMetadataJsonContext.Default.ActiveRelease);
        File.Delete(layout.PendingPath);
    }

    public static bool ConfirmCurrent(
        UpdateLayout layout,
        string callerVersion,
        Action requireCallerRunning)
    {
        ValidateLayout(layout);
        if (!UpdateManifestReader.TryParseVersion(callerVersion))
            throw new UpdateContractException("CallerProcessInvalid");
        ArgumentNullException.ThrowIfNull(requireCallerRunning);
        using var updateLock = AcquireLock(layout);
        var pending = ReadPending(layout.PendingPath);
        if (pending is not null)
        {
            _ = RequireBoundManifest(layout, pending.ManifestSha256, pending.TargetVersion);
        }

        if (pending is null || File.Exists(layout.TransactionPath))
        {
            _ = ReconcileLocked(layout, checkpoint: null);
            pending = ReadPending(layout.PendingPath);
        }

        if (pending is null) return false;
        if (!string.Equals(pending.TargetVersion, callerVersion, StringComparison.Ordinal))
            throw new UpdateContractException("CallerProcessInvalid");

        var manifest = RequireBoundManifest(layout, pending.ManifestSha256, pending.TargetVersion);
        UpdatePackageStager.VerifyTree(manifest, layout.AppRoot);
        RequireConfirmationOpen(pending);
        requireCallerRunning();
        AtomicJson.Write(
            layout.ActivePath,
            new ActiveRelease(1, manifest.Version, manifest.Channel),
            UpdateMetadataJsonContext.Default.ActiveRelease);
        File.Delete(layout.PendingPath);
        File.Delete(layout.PendingManifestPath);
        return true;
    }

    internal static ConfirmationExpirationResult TryExpireConfirmation(
        UpdateLayout layout,
        TimeSpan lockWait)
    {
        ValidateLayout(layout);
        if (lockWait <= TimeSpan.Zero || lockWait.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lockWait));

        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var updateLock = AcquireLock(layout);
                var pending = ReadPending(layout.PendingPath);
                if (pending is null) return ConfirmationExpirationResult.Confirmed;
                if (!pending.ConfirmationExpired)
                {
                    AtomicJson.Write(
                        layout.PendingPath,
                        pending with { ConfirmationExpired = true },
                        UpdateMetadataJsonContext.Default.PendingUpdate);
                }

                return ConfirmationExpirationResult.Expired;
            }
            catch (UpdateContractException exception) when (exception.Code == "UpdateBusy")
            {
                var remaining = lockWait - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) return ConfirmationExpirationResult.Busy;
                Thread.Sleep((int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 25));
            }
        }
    }

    public static bool Rollback(UpdateLayout layout) => RollbackCore(layout, checkpoint: null);

    internal static bool RollbackWithFaultInjection(
        UpdateLayout layout,
        Action<UpdateTransactionCheckpoint> checkpoint) =>
        RollbackCore(layout, checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)));

    private static bool RollbackCore(UpdateLayout layout, Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        ValidateLayout(layout);
        using var updateLock = AcquireLock(layout);
        var reconciled = ReconcileLocked(
            layout,
            checkpoint: null,
            recoverPendingManifestMismatch: false);
        var pending = ReadPending(layout.PendingPath);
        if (pending is null)
        {
            return reconciled is ReconcileResult.RollbackCompleted;
        }

        _ = BeginRollbackLocked(layout, pending, checkpoint);
        return true;
    }

    public static bool AbandonUnconfirmedFirstInstall(UpdateLayout layout) =>
        AbandonCore(layout, checkpoint: null);

    internal static bool AbandonWithFaultInjection(
        UpdateLayout layout,
        Action<UpdateTransactionCheckpoint> checkpoint) =>
        AbandonCore(layout, checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)));

    private static bool AbandonCore(UpdateLayout layout, Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        ValidateLayout(layout);
        using var updateLock = AcquireLock(layout);
        var reconciled = ReconcileLocked(layout, checkpoint: null);
        var pending = ReadPending(layout.PendingPath);
        if (pending is null)
        {
            return reconciled is ReconcileResult.AbandonCompleted;
        }

        if (pending.BackupDirectoryName is not null)
        {
            return false;
        }

        var journal = new UpdateJournal(
            1,
            AbandonOperation,
            PreparedPhase,
            pending.TargetVersion,
            pending.PreviousVersion,
            null,
            null,
            $"abandoned-{Guid.NewGuid():N}",
            pending.ManifestSha256);
        AtomicJson.Write(layout.TransactionPath, journal, UpdateMetadataJsonContext.Default.UpdateJournal);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonJournalPrepared);
        _ = ReconcileLocked(layout, checkpoint);
        return true;
    }

    public static PendingUpdate? ReadPending(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var pending = ReadBounded(path, UpdateMetadataJsonContext.Default.PendingUpdate);
        if (pending.SchemaVersion != 1
            || !UpdateManifestReader.TryParseVersion(pending.TargetVersion)
            || (pending.PreviousVersion is not null && !UpdateManifestReader.TryParseVersion(pending.PreviousVersion))
            || (pending.BackupDirectoryName is not null && !IsGeneratedDirectoryName(pending.BackupDirectoryName, "previous-"))
            || (pending.ManifestSha256 is not null && !UpdateManifestReader.IsSha256(pending.ManifestSha256)))
        {
            throw new UpdateContractException("PendingMetadataInvalid");
        }

        return pending;
    }

    public static ActiveRelease? ReadActive(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var active = ReadBounded(path, UpdateMetadataJsonContext.Default.ActiveRelease);
        if (active.SchemaVersion != 1 || !UpdateManifestReader.TryParseVersion(active.Version)
            || active.Channel is not ("development" or "preview" or "stable"))
        {
            throw new UpdateContractException("ActiveMetadataInvalid");
        }

        return active;
    }

    internal static void CleanupDeadStableArtifacts(
        UpdateLayout layout,
        Func<int, long, bool> ownerIsLive)
    {
        ArgumentNullException.ThrowIfNull(ownerIsLive);
        ValidateLayout(layout);
        if (!Directory.Exists(layout.StagingRoot)) return;

        SafePaths.RequireNoReparseComponents(layout.InstallRoot);
        using var updateLock = AcquireLock(layout);
        var staging = SafePaths.RequireExistingDirectory(layout.StagingRoot);
        var referencedStage = ReadJournal(layout.TransactionPath)?.StagedDirectoryName;
        foreach (var ownerPath in Directory.EnumerateFiles(
                     staging,
                     "handoff-*.owner.json",
                     SearchOption.TopDirectoryOnly))
        {
            var ownerName = Path.GetFileName(ownerPath);
            if (!StableUpdateArtifactContract.TryGetIdFromOwnerFileName(ownerName, out var id))
                continue;

            StableUpdateArtifactOwner owner;
            StableUpdateArtifactNames names;
            try
            {
                var safeOwner = SafePaths.RequireExistingFile(ownerPath);
                var length = new FileInfo(safeOwner).Length;
                if (length is <= 0 or > StableUpdateArtifactContract.MaximumOwnerBytes) continue;
                owner = StableUpdateArtifactContract.ParseOwner(File.ReadAllBytes(safeOwner));
                names = StableUpdateArtifactContract.CreateNames(id, owner.TargetVersion);
            }
            catch (Exception exception) when (exception is
                UpdateContractException or JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(names.ReadyDirectoryName, referencedStage, StringComparison.OrdinalIgnoreCase)
                || ownerIsLive(owner.OwnerProcessId, owner.OwnerProcessStartedAtFileTime))
            {
                continue;
            }

            try
            {
                DeleteOwnedDirectory(SafePaths.CombineUnder(staging, names.IncomingDirectoryName));
                DeleteOwnedDirectory(SafePaths.CombineUnder(staging, names.ReadyDirectoryName));
                DeleteOwnedFile(SafePaths.CombineUnder(staging, names.PackageFileName));
                DeleteOwnedFile(SafePaths.CombineUnder(staging, names.ManifestFileName));
                DeleteOwnedFile(ownerPath);
            }
            catch (Exception exception) when (exception is
                UpdateContractException or IOException or UnauthorizedAccessException)
            {
                // Keep the owner marker so a later launch can safely resume exact cleanup.
            }
        }
    }

    private static ReconcileResult ReconcileLocked(
        UpdateLayout layout,
        Action<UpdateTransactionCheckpoint>? checkpoint,
        bool recoverPendingManifestMismatch = true)
    {
        var journal = ReadJournal(layout.TransactionPath);
        if (journal is null)
        {
            var pending = ReadPending(layout.PendingPath);
            if (recoverPendingManifestMismatch
                && pending?.ManifestSha256 is not null
                && !BoundManifestMatches(layout, pending.ManifestSha256, pending.TargetVersion))
            {
                return BeginRollbackLocked(layout, pending, checkpoint);
            }

            return ReconcileResult.None;
        }

        return journal.Operation switch
        {
            ApplyOperation => ReconcileApplyOrRollback(layout, journal, checkpoint),
            RollbackOperation => ReconcileRollback(layout, journal, checkpoint),
            AbandonOperation => ReconcileAbandon(layout, journal, checkpoint),
            _ => throw new UpdateContractException("TransactionMetadataInvalid"),
        };
    }

    private static ReconcileResult ReconcileApplyOrRollback(
        UpdateLayout layout,
        UpdateJournal journal,
        Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        if (journal.ManifestSha256 is null
            || BoundManifestMatches(layout, journal.ManifestSha256, journal.TargetVersion))
        {
            return ReconcileApply(layout, journal, checkpoint);
        }

        _ = ReconcileApply(layout, journal, checkpoint, requireBoundManifest: false);
        var pending = ReadPending(layout.PendingPath)
            ?? throw new UpdateContractException("TransactionStateInvalid");
        return BeginRollbackLocked(layout, pending, checkpoint);
    }

    private static ReconcileResult ReconcileApply(
        UpdateLayout layout,
        UpdateJournal journal,
        Action<UpdateTransactionCheckpoint>? checkpoint,
        bool requireBoundManifest = true)
    {
        if (requireBoundManifest && journal.ManifestSha256 is not null)
        {
            _ = RequireBoundManifest(layout, journal.ManifestSha256, journal.TargetVersion);
        }

        var stage = StagedPath(layout, journal);
        var backup = journal.BackupDirectoryName is null ? null : BackupPath(layout, journal.BackupDirectoryName);
        var pending = ReadPending(layout.PendingPath);
        if (pending is not null)
        {
            RequireMatchingPending(pending, journal);
            RequireState(layout.AppRoot, expected: true);
            RequireState(stage, expected: false);
            if (backup is not null)
            {
                RequireState(backup, expected: true);
            }

            File.Delete(layout.TransactionPath);
            checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyJournalCleared);
            return ReconcileResult.ApplyCompleted;
        }

        if (journal.Phase == PreparedPhase)
        {
            if (backup is null)
            {
                if (DirectoryState(layout.AppRoot) && !DirectoryState(stage))
                {
                    journal = WritePhase(layout, journal, ReplacementMovedPhase);
                    checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewMoveRecorded);
                }
                else
                {
                    RequireState(layout.AppRoot, expected: false);
                    RequireState(stage, expected: true);
                    MoveDirectory(stage, layout.AppRoot);
                    checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewAppMoved);
                    journal = WritePhase(layout, journal, ReplacementMovedPhase);
                    checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewMoveRecorded);
                }
            }
            else if (!DirectoryState(layout.AppRoot) && DirectoryState(backup) && DirectoryState(stage))
            {
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyOldMoveRecorded);
            }
            else
            {
                RequireState(layout.AppRoot, expected: true);
                RequireState(backup, expected: false);
                RequireState(stage, expected: true);
                MoveDirectory(layout.AppRoot, backup);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyOldAppMoved);
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyOldMoveRecorded);
            }
        }

        if (journal.Phase == CurrentMovedPhase)
        {
            if (backup is null)
            {
                throw new UpdateContractException("TransactionStateInvalid");
            }

            if (DirectoryState(layout.AppRoot) && !DirectoryState(stage) && DirectoryState(backup))
            {
                journal = WritePhase(layout, journal, ReplacementMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewMoveRecorded);
            }
            else
            {
                RequireState(layout.AppRoot, expected: false);
                RequireState(stage, expected: true);
                RequireState(backup, expected: true);
                MoveDirectory(stage, layout.AppRoot);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewAppMoved);
                journal = WritePhase(layout, journal, ReplacementMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyNewMoveRecorded);
            }
        }

        if (journal.Phase != ReplacementMovedPhase)
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }

        RequireState(layout.AppRoot, expected: true);
        RequireState(stage, expected: false);
        if (backup is not null)
        {
            RequireState(backup, expected: true);
        }

        AtomicJson.Write(
            layout.PendingPath,
            new PendingUpdate(
                1,
                journal.TargetVersion,
                journal.PreviousVersion,
                journal.BackupDirectoryName,
                journal.ManifestSha256),
            UpdateMetadataJsonContext.Default.PendingUpdate);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyPendingPublished);
        File.Delete(layout.TransactionPath);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.ApplyJournalCleared);
        return ReconcileResult.ApplyCompleted;
    }

    private static ReconcileResult BeginRollbackLocked(
        UpdateLayout layout,
        PendingUpdate pending,
        Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        if (pending.BackupDirectoryName is null)
        {
            throw new UpdateContractException("RollbackUnavailable");
        }

        var backup = BackupPath(layout, pending.BackupDirectoryName);
        if (!Directory.Exists(backup))
        {
            throw new UpdateContractException("RollbackUnavailable");
        }

        SafePaths.RequireExistingDirectory(backup);
        var journal = new UpdateJournal(
            1,
            RollbackOperation,
            PreparedPhase,
            pending.TargetVersion,
            pending.PreviousVersion,
            pending.BackupDirectoryName,
            null,
            $"failed-{Guid.NewGuid():N}",
            pending.ManifestSha256);
        AtomicJson.Write(layout.TransactionPath, journal, UpdateMetadataJsonContext.Default.UpdateJournal);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackJournalPrepared);
        return ReconcileRollback(layout, journal, checkpoint);
    }

    private static ReconcileResult ReconcileRollback(
        UpdateLayout layout,
        UpdateJournal journal,
        Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        var backup = BackupPath(layout, journal.BackupDirectoryName!);
        var displaced = DisplacedPath(layout, journal.DisplacedDirectoryName!, "failed-");
        var pending = ReadPending(layout.PendingPath);
        if (pending is not null)
        {
            RequireMatchingPending(pending, journal);
        }

        if (journal.Phase == PreparedPhase)
        {
            if (!DirectoryState(layout.AppRoot) && DirectoryState(displaced) && DirectoryState(backup))
            {
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackCurrentMoveRecorded);
            }
            else
            {
                RequireState(layout.AppRoot, expected: true);
                RequireState(displaced, expected: false);
                RequireState(backup, expected: true);
                MoveDirectory(layout.AppRoot, displaced);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackCurrentAppMoved);
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackCurrentMoveRecorded);
            }
        }

        if (journal.Phase == CurrentMovedPhase)
        {
            if (DirectoryState(layout.AppRoot) && DirectoryState(displaced) && !DirectoryState(backup))
            {
                journal = WritePhase(layout, journal, ReplacementMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackRestoreRecorded);
            }
            else
            {
                RequireState(layout.AppRoot, expected: false);
                RequireState(displaced, expected: true);
                RequireState(backup, expected: true);
                MoveDirectory(backup, layout.AppRoot);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackBackupRestored);
                journal = WritePhase(layout, journal, ReplacementMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackRestoreRecorded);
            }
        }

        if (journal.Phase != ReplacementMovedPhase)
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }

        RequireState(layout.AppRoot, expected: true);
        RequireState(displaced, expected: true);
        RequireState(backup, expected: false);
        if (pending is not null)
        {
            File.Delete(layout.PendingPath);
            if (journal.ManifestSha256 is not null)
            {
                File.Delete(layout.PendingManifestPath);
            }
        }
        else if (journal.ManifestSha256 is not null && File.Exists(layout.PendingManifestPath))
        {
            File.Delete(layout.PendingManifestPath);
        }

        checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackPendingDeleted);
        File.Delete(layout.TransactionPath);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.RollbackJournalCleared);
        return ReconcileResult.RollbackCompleted;
    }

    private static ReconcileResult ReconcileAbandon(
        UpdateLayout layout,
        UpdateJournal journal,
        Action<UpdateTransactionCheckpoint>? checkpoint)
    {
        var abandoned = DisplacedPath(layout, journal.DisplacedDirectoryName!, "abandoned-");
        var pending = ReadPending(layout.PendingPath);
        if (pending is not null)
        {
            RequireMatchingPending(pending, journal);
        }

        if (journal.Phase == PreparedPhase)
        {
            if (!DirectoryState(layout.AppRoot) && DirectoryState(abandoned))
            {
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonMoveRecorded);
            }
            else
            {
                RequireState(layout.AppRoot, expected: true);
                RequireState(abandoned, expected: false);
                MoveDirectory(layout.AppRoot, abandoned);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonAppMoved);
                journal = WritePhase(layout, journal, CurrentMovedPhase);
                checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonMoveRecorded);
            }
        }

        if (journal.Phase != CurrentMovedPhase)
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }

        RequireState(layout.AppRoot, expected: false);
        RequireState(abandoned, expected: true);
        if (pending is not null)
        {
            File.Delete(layout.PendingPath);
        }

        checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonPendingDeleted);
        File.Delete(layout.TransactionPath);
        checkpoint?.Invoke(UpdateTransactionCheckpoint.AbandonJournalCleared);
        return ReconcileResult.AbandonCompleted;
    }

    private static UpdateJournal? ReadJournal(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var journal = ReadBounded(path, UpdateMetadataJsonContext.Default.UpdateJournal);
        var validOperation = journal.Operation is ApplyOperation or RollbackOperation or AbandonOperation;
        var validPhase = journal.Phase is PreparedPhase or CurrentMovedPhase or ReplacementMovedPhase;
        var validBackup = journal.BackupDirectoryName is null
            || IsGeneratedDirectoryName(journal.BackupDirectoryName, "previous-");
        var validStage = journal.StagedDirectoryName is null || IsSafeRelativePath(journal.StagedDirectoryName);
        var validDisplaced = journal.DisplacedDirectoryName is null
            || IsGeneratedDirectoryName(journal.DisplacedDirectoryName,
                journal.Operation == AbandonOperation ? "abandoned-" : "failed-");
        var validShape = journal.Operation switch
        {
            ApplyOperation => journal.StagedDirectoryName is not null && journal.DisplacedDirectoryName is null,
            RollbackOperation => journal.BackupDirectoryName is not null
                && journal.StagedDirectoryName is null && journal.DisplacedDirectoryName is not null,
            AbandonOperation => journal.BackupDirectoryName is null
                && journal.StagedDirectoryName is null && journal.DisplacedDirectoryName is not null
                && journal.Phase is not ReplacementMovedPhase,
            _ => false,
        };
        if (journal.SchemaVersion != 1 || !validOperation || !validPhase || !validBackup || !validStage
            || !validDisplaced || !validShape || !UpdateManifestReader.TryParseVersion(journal.TargetVersion)
            || (journal.PreviousVersion is not null && !UpdateManifestReader.TryParseVersion(journal.PreviousVersion))
            || (journal.ManifestSha256 is not null && !UpdateManifestReader.IsSha256(journal.ManifestSha256))
            || (journal.Operation == AbandonOperation && journal.ManifestSha256 is not null))
        {
            throw new UpdateContractException("TransactionMetadataInvalid");
        }

        return journal;
    }

    private static T ReadBounded<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        var safe = SafePaths.RequireExistingFile(path);
        if (new FileInfo(safe).Length is <= 0 or > 64 * 1024)
        {
            throw new UpdateContractException("MetadataInvalid");
        }

        using var stream = File.OpenRead(safe);
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new UpdateContractException("MetadataInvalid");
    }

    private static UpdateJournal WritePhase(UpdateLayout layout, UpdateJournal journal, string phase)
    {
        journal = journal with { Phase = phase };
        AtomicJson.Write(layout.TransactionPath, journal, UpdateMetadataJsonContext.Default.UpdateJournal);
        return journal;
    }

    private static string StagedPath(UpdateLayout layout, UpdateJournal journal) =>
        SafePaths.CombineUnder(layout.StagingRoot, journal.StagedDirectoryName!);

    private static string BackupPath(UpdateLayout layout, string name) =>
        SafePaths.CombineUnder(layout.RollbackRoot, name);

    private static string DisplacedPath(UpdateLayout layout, string name, string prefix)
    {
        if (!IsGeneratedDirectoryName(name, prefix))
        {
            throw new UpdateContractException("TransactionMetadataInvalid");
        }

        return SafePaths.CombineUnder(layout.RollbackRoot, name);
    }

    private static bool DirectoryState(string path)
    {
        if (File.Exists(path))
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        SafePaths.RequireExistingDirectory(path);
        return true;
    }

    private static void RequireState(string path, bool expected)
    {
        if (DirectoryState(path) != expected)
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        RequireState(source, expected: true);
        RequireState(destination, expected: false);
        SafePaths.RequireNoReparseComponents(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    private static void RequireConfirmationOpen(PendingUpdate pending)
    {
        if (pending.ConfirmationExpired)
            throw new UpdateContractException("ConfirmationExpired");
    }

    private static void DeleteOwnedDirectory(string path)
    {
        if (File.Exists(path)) throw new UpdateContractException("UnsafePath");
        if (Directory.Exists(path)) SafePaths.DeleteTreeWithoutFollowingLinks(path);
    }

    private static void DeleteOwnedFile(string path)
    {
        if (Directory.Exists(path)) throw new UpdateContractException("UnsafePath");
        if (File.Exists(path)) File.Delete(SafePaths.RequireExistingFile(path));
    }

    private static void RequireMatchingPending(PendingUpdate pending, UpdateJournal journal)
    {
        if (!string.Equals(pending.TargetVersion, journal.TargetVersion, StringComparison.Ordinal)
            || !string.Equals(pending.PreviousVersion, journal.PreviousVersion, StringComparison.Ordinal)
            || !string.Equals(pending.BackupDirectoryName, journal.BackupDirectoryName, StringComparison.Ordinal)
            || !string.Equals(pending.ManifestSha256, journal.ManifestSha256, StringComparison.Ordinal))
        {
            throw new UpdateContractException("TransactionStateInvalid");
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        try
        {
            return string.Equals(SafePaths.RequireRelativeFile(path), path, StringComparison.Ordinal);
        }
        catch (UpdateContractException)
        {
            return false;
        }
    }

    private static UpdateReleaseManifest RequireBoundManifest(
        UpdateLayout layout,
        string? expectedSha256,
        string targetVersion)
    {
        if (!UpdateManifestReader.IsSha256(expectedSha256)
            || !File.Exists(layout.PendingManifestPath))
        {
            throw new UpdateContractException("PendingManifestMismatch");
        }

        var safePath = SafePaths.RequireExistingFile(layout.PendingManifestPath);
        if (new FileInfo(safePath).Length is <= 0 or > UpdateManifestReader.MaximumManifestBytes)
        {
            throw new UpdateContractException("PendingManifestMismatch");
        }

        var bytes = File.ReadAllBytes(safePath);
        if (!string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw new UpdateContractException("PendingManifestMismatch");
        }

        var manifest = UpdateManifestReader.Parse(bytes);
        if (!string.Equals(manifest.Channel, "stable", StringComparison.Ordinal)
            || !string.Equals(manifest.Version, targetVersion, StringComparison.Ordinal))
        {
            throw new UpdateContractException("PendingManifestMismatch");
        }

        return manifest;
    }

    private static bool BoundManifestMatches(
        UpdateLayout layout,
        string expectedSha256,
        string targetVersion)
    {
        try
        {
            _ = RequireBoundManifest(layout, expectedSha256, targetVersion);
            return true;
        }
        catch (Exception exception) when (exception is
            UpdateContractException or JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ManifestMatches(UpdateReleaseManifest left, UpdateReleaseManifest right) =>
        left.SchemaVersion == right.SchemaVersion
        && string.Equals(left.Product, right.Product, StringComparison.Ordinal)
        && string.Equals(left.Channel, right.Channel, StringComparison.Ordinal)
        && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
        && string.Equals(left.Architecture, right.Architecture, StringComparison.Ordinal)
        && string.Equals(left.PackageFile, right.PackageFile, StringComparison.Ordinal)
        && left.PackageSize == right.PackageSize
        && string.Equals(left.PackageSha256, right.PackageSha256, StringComparison.Ordinal)
        && string.Equals(left.EntryPoint, right.EntryPoint, StringComparison.Ordinal)
        && string.Equals(left.PackageUrl, right.PackageUrl, StringComparison.Ordinal)
        && left.Files.SequenceEqual(right.Files);

    private static bool IsGeneratedDirectoryName(string name, string prefix) =>
        name.Length == prefix.Length + 32
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && name[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static FileStream AcquireLock(UpdateLayout layout)
    {
        SafePaths.CreateDirectoryTree(layout.InstallRoot);
        try
        {
            return new FileStream(
                layout.LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            throw new UpdateContractException("UpdateBusy");
        }
    }

    private static void ValidateLayout(UpdateLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var install = SafePaths.RequireAbsoluteLocal(layout.InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
        var data = SafePaths.RequireAbsoluteLocal(layout.UserDataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var legacy = SafePaths.RequireAbsoluteLocal(layout.LegacyUserDataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var shortcut = SafePaths.RequireAbsoluteLocal(layout.StartMenuShortcut);
        var roots = new[] { install, data, legacy };
        if (data.Equals(legacy, StringComparison.OrdinalIgnoreCase)
            || shortcut.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateContractException("LayoutInvalid");
        }

        for (var left = 0; left < roots.Length; left++)
        {
            for (var right = left + 1; right < roots.Length; right++)
            {
                if (roots[left].Equals(roots[right], StringComparison.OrdinalIgnoreCase)
                    || roots[left].StartsWith(roots[right] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || roots[right].StartsWith(roots[left] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateContractException("LayoutInvalid");
                }
            }
        }
    }

    private enum ReconcileResult
    {
        None,
        ApplyCompleted,
        RollbackCompleted,
        AbandonCompleted,
    }

    private sealed record StableApply(byte[] ManifestBytes, string ParentVersion);
}

public static class AtomicJson
{
    public static void Write(string path, ActiveRelease value) =>
        Write(path, value, UpdateMetadataJsonContext.Default.ActiveRelease);

    internal static void Write(string path, UpdateJournal value) =>
        Write(path, value, UpdateMetadataJsonContext.Default.UpdateJournal);

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new UpdateContractException("MetadataInvalid");
        SafePaths.CreateDirectoryTree(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

internal static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new UpdateContractException("MetadataInvalid");
        SafePaths.CreateDirectoryTree(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
