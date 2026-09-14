using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nyx.Desktop.Update;

internal static class StableUpdateRunner
{
    internal static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ChildShutdownGrace = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ExpirationLockWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ExpirationClaimTimeout = TimeSpan.FromSeconds(15);

    public static void Launch(UpdateLayout layout)
    {
        using var monitorLock = TryAcquireMonitor(layout);
        if (monitorLock is null)
        {
            return;
        }

        if (!RecoverForLaunchLocked(layout))
        {
            using var process = StartApp(layout);
            return;
        }

        LaunchAndMonitor(layout);
    }

    internal static bool RecoverForLaunch(UpdateLayout layout)
    {
        using var monitorLock = AcquireMonitor(layout);
        return RecoverForLaunchLocked(layout);
    }

    private static bool RecoverForLaunchLocked(UpdateLayout layout)
    {
        UpdateTransaction.Reconcile(layout);
        UpdateTransaction.CleanupDeadStableArtifacts(
            layout,
            BoundAppProcess.IsProcessInstanceRunning);
        return UpdateTransaction.ReadPending(layout.PendingPath) is not null;
    }

    public static bool ConfirmCurrent(UpdateLayout layout, int callerProcessId)
    {
        using var caller = BoundAppProcess.Open(layout, callerProcessId);
        caller.RequireRunning();
        return UpdateTransaction.ConfirmCurrent(layout, caller.Version, caller.RequireRunning);
    }

    public static void Handoff(
        UpdateLayout layout,
        string manifestPath,
        string packagePath,
        int parentProcessId,
        TextReader input,
        TextWriter output)
    {
        string? staged = null;
        using var parent = BoundAppProcess.Open(layout, parentProcessId);
        using var monitorLock = AcquireMonitor(layout);
        var handoff = RequireHandoffFiles(layout, manifestPath, packagePath);
        ClaimHandoff(handoff, parent);
        try
        {
            var manifestBytes = File.ReadAllBytes(handoff.ManifestPath);
            var manifest = UpdateManifestReader.Parse(manifestBytes);
            if (!string.Equals(manifest.Channel, "stable", StringComparison.Ordinal)
                || !string.Equals(manifest.Version, handoff.Owner.TargetVersion, StringComparison.Ordinal))
            {
                throw new UpdateContractException("StableUpgradeRejected");
            }

            staged = UpdatePackageStager.StageStable(
                manifest,
                handoff.PackagePath,
                layout.StagingRoot,
                handoff.ArtifactId);
            var apply = RunApplyGate(input, output, parent.WaitForExit);
            if (!apply)
            {
                DiscardPrepared(
                    handoff.ManifestPath,
                    handoff.PackagePath,
                    staged,
                    handoff.OwnerPath);
                staged = null;
                return;
            }

            UpdateTransaction.ApplyStable(
                layout,
                manifest,
                staged,
                manifestBytes,
                parent.Version);
            staged = null;

            LaunchAndMonitor(layout);
        }
        finally
        {
            if (staged is not null
                && !File.Exists(layout.TransactionPath)
                && !File.Exists(layout.PendingPath)
                && Directory.Exists(staged))
            {
                SafePaths.DeleteTreeWithoutFollowingLinks(staged);
            }

            DeleteExactHandoffFile(handoff.PackagePath);
            DeleteExactHandoffFile(handoff.ManifestPath);
            DeleteExactHandoffFile(handoff.OwnerPath);
        }
    }

    internal static bool RunApplyGate(
        TextReader input,
        TextWriter output,
        Action waitForParent)
    {
        output.WriteLine("READY");
        output.Flush();
        if (!string.Equals(input.ReadLine(), "APPLY", StringComparison.Ordinal)) return false;
        waitForParent();
        return true;
    }

    internal static void DiscardPrepared(string manifestPath, string packagePath, string staged)
        => DiscardPrepared(manifestPath, packagePath, staged, ownerPath: null);

    internal static void DiscardPrepared(
        string manifestPath,
        string packagePath,
        string staged,
        string? ownerPath)
    {
        if (Directory.Exists(staged)) SafePaths.DeleteTreeWithoutFollowingLinks(staged);
        DeleteExactHandoffFile(packagePath);
        DeleteExactHandoffFile(manifestPath);
        if (ownerPath is not null) DeleteExactHandoffFile(ownerPath);
    }

    private static HandoffFiles RequireHandoffFiles(
        UpdateLayout layout,
        string manifestPath,
        string packagePath)
    {
        var safeManifest = SafePaths.RequireExistingFile(manifestPath);
        var safePackage = SafePaths.RequireExistingFile(packagePath);
        var staging = SafePaths.RequireExistingDirectory(layout.StagingRoot);
        var manifestName = Path.GetFileName(safeManifest);
        if (!string.Equals(Path.GetDirectoryName(safeManifest), staging, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(safePackage), staging, StringComparison.OrdinalIgnoreCase)
            || !StableUpdateArtifactContract.TryGetIdFromManifestFileName(manifestName, out var id))
        {
            throw new UpdateContractException("HandoffWorkspaceInvalid");
        }

        var ownerName = StableUpdateArtifactContract.CreateNames(id, "0.0.0.0").OwnerFileName;
        var ownerPath = SafePaths.RequireExistingFile(SafePaths.CombineUnder(staging, ownerName));
        var ownerLength = new FileInfo(ownerPath).Length;
        if (ownerLength is <= 0 or > StableUpdateArtifactContract.MaximumOwnerBytes)
            throw new UpdateContractException("HandoffWorkspaceInvalid");
        var owner = StableUpdateArtifactContract.ParseOwner(File.ReadAllBytes(ownerPath));
        var names = StableUpdateArtifactContract.CreateNames(id, owner.TargetVersion);
        if (!string.Equals(Path.GetFileName(safePackage), names.PackageFileName, StringComparison.Ordinal))
        {
            throw new UpdateContractException("HandoffWorkspaceInvalid");
        }

        return new(id, safeManifest, safePackage, ownerPath, owner);
    }

    private static void ClaimHandoff(HandoffFiles handoff, BoundAppProcess parent)
    {
        if (handoff.Owner.OwnerProcessId != parent.ProcessId
            || handoff.Owner.OwnerProcessStartedAtFileTime != parent.StartedAtFileTime)
        {
            throw new UpdateContractException("HandoffWorkspaceInvalid");
        }

        using var updater = Process.GetCurrentProcess();
        var claimed = handoff.Owner with
        {
            OwnerProcessId = Environment.ProcessId,
            OwnerProcessStartedAtFileTime = updater.StartTime.ToUniversalTime().ToFileTimeUtc(),
        };
        AtomicFile.Write(
            handoff.OwnerPath,
            StableUpdateArtifactContract.SerializeOwner(claimed));
    }

    private static void DeleteExactHandoffFile(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(SafePaths.RequireExistingFile(path));
    }

    private static void LaunchAndMonitor(UpdateLayout layout)
    {
        Process process;
        try
        {
            process = StartApp(layout);
        }
        catch
        {
            RollbackAndRelaunch(layout);
            throw;
        }

        using (process)
        {
            var result = WaitForConfirmation(
                () => File.Exists(layout.PendingPath),
                process.WaitForExit,
                ConfirmationTimeout);
            if (result is PendingMonitorResult.Confirmed || !File.Exists(layout.PendingPath))
            {
                return;
            }

            RecoverUnconfirmedChild(
                result,
                lockWait => UpdateTransaction.TryExpireConfirmation(layout, lockWait),
                ExpirationClaimTimeout,
                () =>
                {
                    try
                    {
                        return process.HasExited || process.CloseMainWindow();
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                },
                milliseconds =>
                {
                    try
                    {
                        return process.WaitForExit(milliseconds);
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                },
                () =>
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: false);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                },
                () =>
                {
                    if (File.Exists(layout.PendingPath)) RollbackAndRelaunch(layout);
                },
                ChildShutdownGrace);
        }
    }

    internal static PendingMonitorResult WaitForConfirmation(
        Func<bool> pendingExists,
        Func<int, bool> waitForExit,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(pendingExists);
        ArgumentNullException.ThrowIfNull(waitForExit);
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var timer = Stopwatch.StartNew();
        while (pendingExists())
        {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return PendingMonitorResult.TimedOut;
            var milliseconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 250);
            if (waitForExit(milliseconds)) return PendingMonitorResult.Exited;
        }

        return PendingMonitorResult.Confirmed;
    }

    internal static void RecoverUnconfirmedChild(
        PendingMonitorResult result,
        Func<TimeSpan, ConfirmationExpirationResult> claimExpiration,
        TimeSpan expirationClaimTimeout,
        Func<bool> requestClose,
        Func<int, bool> waitForExit,
        Action terminate,
        Action rollbackAndRelaunch,
        TimeSpan shutdownGrace)
    {
        if (result is PendingMonitorResult.Confirmed) return;
        if (expirationClaimTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expirationClaimTimeout));
        if (shutdownGrace <= TimeSpan.Zero || shutdownGrace.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));

        if (result is PendingMonitorResult.TimedOut)
        {
            var expirationTimer = Stopwatch.StartNew();
            while (true)
            {
                var remaining = expirationClaimTimeout - expirationTimer.Elapsed;
                if (remaining <= TimeSpan.Zero) return;
                var lockWait = remaining < ExpirationLockWait ? remaining : ExpirationLockWait;
                var expiration = claimExpiration(lockWait);
                if (expiration is ConfirmationExpirationResult.Confirmed) return;
                if (expiration is ConfirmationExpirationResult.Expired) break;
                if (waitForExit(0) || expirationTimer.Elapsed >= expirationClaimTimeout) return;
            }

            var milliseconds = (int)Math.Ceiling(shutdownGrace.TotalMilliseconds);
            if (!requestClose() || !waitForExit(milliseconds))
            {
                terminate();
                if (!waitForExit(milliseconds))
                    throw new UpdateContractException("ChildTerminationFailed");
            }
        }

        rollbackAndRelaunch();
    }

    private static void RollbackAndRelaunch(UpdateLayout layout)
    {
        if (!UpdateTransaction.Rollback(layout)) return;
        using var process = StartApp(layout);
    }

    private static Process StartApp(UpdateLayout layout)
    {
        var app = SafePaths.RequireExistingFile(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe"));
        return Process.Start(new ProcessStartInfo
        {
            FileName = app,
            WorkingDirectory = layout.AppRoot,
            UseShellExecute = false,
        }) ?? throw new UpdateContractException("AppLaunchFailed");
    }

    private static FileStream AcquireMonitor(UpdateLayout layout) =>
        TryAcquireMonitor(layout) ?? throw new UpdateContractException("UpdateBusy");

    private static FileStream? TryAcquireMonitor(UpdateLayout layout)
    {
        SafePaths.CreateDirectoryTree(layout.InstallRoot);
        try
        {
            return new FileStream(
                Path.Combine(layout.InstallRoot, ".pending-monitor.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal enum PendingMonitorResult
    {
        Confirmed,
        Exited,
        TimedOut,
    }

    private sealed record HandoffFiles(
        string ArtifactId,
        string ManifestPath,
        string PackagePath,
        string OwnerPath,
        StableUpdateArtifactOwner Owner);

    internal sealed class BoundAppProcess(
        SafeProcessHandle handle,
        string version,
        int processId,
        long startedAtFileTime) : IDisposable
    {
        private const uint Synchronize = 0x00100000;
        private const uint QueryLimitedInformation = 0x00001000;
        private const uint Infinite = 0xFFFFFFFF;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 0x00000102;
        private const int ErrorInvalidParameter = 87;

        public string Version { get; } = version;
        public int ProcessId { get; } = processId;
        public long StartedAtFileTime { get; } = startedAtFileTime;

        public static BoundAppProcess Open(UpdateLayout layout, int processId)
        {
            using var updater = Process.GetCurrentProcess();
            return OpenExpected(
                SafePaths.RequireExistingFile(Path.Combine(layout.AppRoot, "Nyx.Desktop.App.exe")),
                processId,
                updater.StartTime.ToUniversalTime().ToFileTimeUtc());
        }

        internal static BoundAppProcess OpenExpected(
            string expectedPath,
            int processId,
            long updaterStartedAtFileTime)
        {
            if (processId <= 0) throw new UpdateContractException("CallerProcessInvalid");
            var handle = OpenProcess(Synchronize | QueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new UpdateContractException("CallerProcessInvalid");
            }

            try
            {
                if (!GetProcessTimes(
                        handle,
                        out var callerStartedAtFileTime,
                        out _,
                        out _,
                        out _)
                    || callerStartedAtFileTime >= updaterStartedAtFileTime)
                {
                    throw new UpdateContractException("CallerProcessInvalid");
                }

                var capacity = 32_768;
                var image = new StringBuilder(capacity);
                if (!QueryFullProcessImageNameW(handle, 0, image, ref capacity))
                {
                    throw new UpdateContractException("CallerProcessInvalid");
                }

                var expected = SafePaths.RequireExistingFile(expectedPath);
                if (!string.Equals(Path.GetFullPath(image.ToString()), expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateContractException("CallerProcessInvalid");
                }

                var version = FileVersionInfo.GetVersionInfo(expected).FileVersion;
                if (!UpdateManifestReader.TryParseVersion(version))
                {
                    throw new UpdateContractException("CallerProcessInvalid");
                }

                var bound = new BoundAppProcess(
                    handle,
                    version!,
                    processId,
                    callerStartedAtFileTime);
                bound.RequireRunning();
                return bound;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void RequireRunning()
        {
            var result = WaitForSingleObject(handle, 0);
            if (result == WaitTimeout) return;
            if (result == WaitObject0) throw new UpdateContractException("CallerProcessInvalid");
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void WaitForExit()
        {
            if (WaitForSingleObject(handle, Infinite) is not WaitObject0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        internal static bool IsProcessInstanceRunning(int processId, long startedAtFileTime)
        {
            if (processId <= 0 || startedAtFileTime <= 0) return false;
            var process = OpenProcess(Synchronize | QueryLimitedInformation, false, processId);
            if (process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                process.Dispose();
                return error != ErrorInvalidParameter;
            }

            using (process)
            {
                if (!GetProcessTimes(process, out var actualStart, out _, out _, out _)) return true;
                if (actualStart != startedAtFileTime) return false;
                return WaitForSingleObject(process, 0) != WaitObject0;
            }
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(
            SafeProcessHandle process,
            uint flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            SafeProcessHandle process,
            out long creationTime,
            out long exitTime,
            out long kernelTime,
            out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}
