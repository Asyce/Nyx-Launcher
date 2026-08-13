using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

/// <summary>
/// Opens only the visible, freshly revalidated Kuro launcher. It cannot update,
/// launch a game, elevate, use a shell, hide a window, or accept paths/arguments.
/// </summary>
public sealed class WuWaOfficialMaintenanceExecutor
{
    private static readonly WuWaOfficialLauncherAdmission ProductionFamilyAdmission = new();
    private readonly WuWaIdentityAdapter validator;
    private readonly IStrictRunningProcessInspector processInspector;
    private readonly IWuWaOfficialMaintenanceProcessStarter processStarter;
    private readonly WuWaOfficialLauncherAdmission familyAdmission;

    [SupportedOSPlatform("windows")]
    public WuWaOfficialMaintenanceExecutor()
        : this(
            new WuWaIdentityAdapter(),
            new WindowsRunningProcessInspector(),
            new WindowsWuWaOfficialMaintenanceProcessStarter(),
            ProductionFamilyAdmission)
    {
    }

    internal WuWaOfficialMaintenanceExecutor(
        WuWaIdentityAdapter validator,
        IStrictRunningProcessInspector processInspector,
        IWuWaOfficialMaintenanceProcessStarter processStarter,
        WuWaOfficialLauncherAdmission? familyAdmission = null)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        this.familyAdmission = familyAdmission ?? new WuWaOfficialLauncherAdmission();
    }

    public WuWaOfficialMaintenanceResult Check(OfficialMaintenanceHandoffRequest request) =>
        CheckProtected(request, start: false, CancellationToken.None);

    public WuWaOfficialMaintenanceResult Open(OfficialMaintenanceHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWuWaRequest(request))
        {
            return new(WuWaOfficialMaintenanceStatus.Unsupported, request);
        }

        using var admission = familyAdmission.TryEnter();
        return admission is null
            ? new(WuWaOfficialMaintenanceStatus.Busy, request)
            : CheckProtected(request, start: true, CancellationToken.None);
    }

    public async Task<WuWaOfficialMaintenanceResult> OpenOrObserveCurrentAsync(
        OfficialMaintenanceHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWuWaRequest(request))
        {
            return new(WuWaOfficialMaintenanceStatus.Unsupported, request);
        }

        var admission = familyAdmission.TryEnter();
        if (admission is null)
        {
            using var observationAdmission = await familyAdmission
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            return Check(request);
        }

        using (admission)
        {
            return await Task.Run(
                () => CheckProtected(request, start: true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private WuWaOfficialMaintenanceResult CheckProtected(
        OfficialMaintenanceHandoffRequest request,
        bool start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWuWaRequest(request))
        {
            return new(WuWaOfficialMaintenanceStatus.Unsupported, request);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var fresh = validator.InspectProtected(request.Target.CanonicalRoot);
        var result = fresh.Result;
        if (result.Status is PublisherGameInspectionStatus.NotFound)
        {
            return new(WuWaOfficialMaintenanceStatus.NotFound, request, result.Reason);
        }

        if (!HasExecutableMaintenanceProof(result)
            || result.MaintenanceTarget is null)
        {
            return new(WuWaOfficialMaintenanceStatus.NeedsReview, request, result.Reason);
        }

        OfficialMaintenanceHandoffRequest freshRequest;
        try
        {
            freshRequest = OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget);
        }
        catch (InvalidOperationException)
        {
            return new(WuWaOfficialMaintenanceStatus.Unsupported, request, result.Reason);
        }

        if (!RequestsMatch(request, freshRequest))
        {
            return new(WuWaOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        RunningProcessStatus running;
        try
        {
            running = processInspector.CheckStrict("launcher", freshRequest.Target.LauncherPath);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(WuWaOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        if (running is RunningProcessStatus.Running)
        {
            return new(WuWaOfficialMaintenanceStatus.Running, freshRequest, result.Reason);
        }

        if (running is not RunningProcessStatus.NotRunning
            || !fresh.RemainsCompleteAndStable())
        {
            return new(WuWaOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        if (!start)
        {
            return new(WuWaOfficialMaintenanceStatus.Ready, freshRequest, result.Reason);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            processStarter.Start(freshRequest);
            return new(WuWaOfficialMaintenanceStatus.Opened, freshRequest, result.Reason);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(WuWaOfficialMaintenanceStatus.Failed, freshRequest, result.Reason);
        }
    }

    private static bool HasExecutableMaintenanceProof(PublisherGameInspectionResult result) =>
        result.HasFullInstallMaintenanceProof
        && (result.Status, result.Reason) is (
                PublisherGameInspectionStatus.Ready,
                PublisherGameInspectionReason.None)
            or (
                PublisherGameInspectionStatus.NeedsReview,
                PublisherGameInspectionReason.VersionConflict);

    private static bool IsWuWaRequest(OfficialMaintenanceHandoffRequest request) =>
        string.Equals(request.Target.GameId, "wuwa", StringComparison.Ordinal)
        && request.Arguments.Count == 0
        && request.RequiresUserInteraction
        && request.RequiresImmediateRevalidation
        && request.RequiresFullInstallRevalidation
        && request.RequiresProtectedExecutableBinding
        && !request.AllowsDirectUpdate
        && !request.AllowsDirectGameLaunch;

    private static bool RequestsMatch(
        OfficialMaintenanceHandoffRequest first,
        OfficialMaintenanceHandoffRequest fresh) =>
        string.Equals(first.Target.GameId, fresh.Target.GameId, StringComparison.Ordinal)
        && string.Equals(
            first.Target.CanonicalRoot,
            fresh.Target.CanonicalRoot,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            first.Target.LauncherPath,
            fresh.Target.LauncherPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            first.Target.LauncherVersion,
            fresh.Target.LauncherVersion,
            StringComparison.Ordinal)
        && string.Equals(first.Instructions, fresh.Instructions, StringComparison.Ordinal)
        && first.Arguments.SequenceEqual(fresh.Arguments, StringComparer.Ordinal);

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or InvalidOperationException
            or Win32Exception;
}

internal interface IWuWaOfficialMaintenanceProcessStarter
{
    void Start(OfficialMaintenanceHandoffRequest request);
}

internal sealed class WindowsWuWaOfficialMaintenanceProcessStarter
    : IWuWaOfficialMaintenanceProcessStarter
{
    public void Start(OfficialMaintenanceHandoffRequest request)
    {
        var startInfo = CreateStartInfo(request);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Kuro launcher did not start.");
    }

    internal static ProcessStartInfo CreateStartInfo(OfficialMaintenanceHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        if (!string.Equals(target.GameId, "wuwa", StringComparison.Ordinal)
            || request.Arguments.Count != 0
            || !string.Equals(
                Path.GetFileName(target.LauncherPath),
                "launcher.exe",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(target.LauncherPath),
                target.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the sealed WuWa maintenance handoff can start.");
        }

        return new ProcessStartInfo
        {
            FileName = target.LauncherPath,
            WorkingDirectory = target.CanonicalRoot,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
        };
    }
}

internal sealed class WuWaOfficialLauncherAdmission
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public WuWaOfficialLauncherLease? TryEnter() =>
        gate.Wait(0) ? new(this) : null;

    public async Task<WuWaOfficialLauncherLease> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(this);
    }

    internal void Release() => gate.Release();
}

internal sealed class WuWaOfficialLauncherLease : IDisposable
{
    private WuWaOfficialLauncherAdmission? owner;

    internal WuWaOfficialLauncherLease(WuWaOfficialLauncherAdmission owner)
    {
        this.owner = owner;
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
}
