using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

/// <summary>
/// Opens only the visible, freshly revalidated root GRYPHLINK launcher. It
/// cannot update, launch a game, elevate, hide UI, use a shell, or accept a path.
/// </summary>
public sealed class EndfieldOfficialMaintenanceExecutor
{
    private static readonly EndfieldOfficialLauncherAdmission ProductionFamilyAdmission = new();
    private readonly EndfieldIdentityAdapter validator;
    private readonly IStrictRunningProcessInspector processInspector;
    private readonly IEndfieldOfficialMaintenanceProcessStarter processStarter;
    private readonly EndfieldOfficialLauncherAdmission familyAdmission;

    [SupportedOSPlatform("windows")]
    public EndfieldOfficialMaintenanceExecutor()
        : this(
            new EndfieldIdentityAdapter(),
            new WindowsRunningProcessInspector(),
            new WindowsEndfieldOfficialMaintenanceProcessStarter(),
            ProductionFamilyAdmission)
    {
    }

    internal EndfieldOfficialMaintenanceExecutor(
        EndfieldIdentityAdapter validator,
        IStrictRunningProcessInspector processInspector,
        IEndfieldOfficialMaintenanceProcessStarter processStarter,
        EndfieldOfficialLauncherAdmission? familyAdmission = null)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        this.familyAdmission = familyAdmission ?? new EndfieldOfficialLauncherAdmission();
    }

    public EndfieldOfficialMaintenanceResult Check(OfficialMaintenanceHandoffRequest request) =>
        CheckProtected(request, start: false, CancellationToken.None);

    public EndfieldOfficialMaintenanceResult Open(OfficialMaintenanceHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEndfieldRequest(request))
        {
            return new(EndfieldOfficialMaintenanceStatus.Unsupported, request);
        }

        using var admission = familyAdmission.TryEnter();
        return admission is null
            ? new(EndfieldOfficialMaintenanceStatus.Busy, request)
            : CheckProtected(request, start: true, CancellationToken.None);
    }

    public async Task<EndfieldOfficialMaintenanceResult> OpenOrObserveCurrentAsync(
        OfficialMaintenanceHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEndfieldRequest(request))
        {
            return new(EndfieldOfficialMaintenanceStatus.Unsupported, request);
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

    private EndfieldOfficialMaintenanceResult CheckProtected(
        OfficialMaintenanceHandoffRequest request,
        bool start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEndfieldRequest(request))
        {
            return new(EndfieldOfficialMaintenanceStatus.Unsupported, request);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var fresh = validator.InspectProtected(request.Target.CanonicalRoot);
        var result = fresh.Result;
        if (result.Status is PublisherGameInspectionStatus.NotFound)
        {
            return new(EndfieldOfficialMaintenanceStatus.NotFound, request, result.Reason);
        }

        if (!HasExecutableMaintenanceProof(result)
            || result.MaintenanceTarget is null)
        {
            return new(EndfieldOfficialMaintenanceStatus.NeedsReview, request, result.Reason);
        }

        OfficialMaintenanceHandoffRequest freshRequest;
        try
        {
            freshRequest = OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget);
        }
        catch (InvalidOperationException)
        {
            return new(EndfieldOfficialMaintenanceStatus.Unsupported, request, result.Reason);
        }

        if (!RequestsMatch(request, freshRequest))
        {
            return new(EndfieldOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        RunningProcessStatus running;
        try
        {
            running = processInspector.CheckStrict("Launcher", freshRequest.Target.LauncherPath);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(EndfieldOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        if (running is RunningProcessStatus.Running)
        {
            return new(EndfieldOfficialMaintenanceStatus.Running, freshRequest, result.Reason);
        }

        if (running is not RunningProcessStatus.NotRunning
            || !fresh.RemainsCompleteAndStable())
        {
            return new(EndfieldOfficialMaintenanceStatus.NeedsReview, freshRequest, result.Reason);
        }

        if (!start)
        {
            return new(EndfieldOfficialMaintenanceStatus.Ready, freshRequest, result.Reason);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            processStarter.Start(freshRequest);
            return new(EndfieldOfficialMaintenanceStatus.Opened, freshRequest, result.Reason);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(EndfieldOfficialMaintenanceStatus.Failed, freshRequest, result.Reason);
        }
    }

    private static bool HasExecutableMaintenanceProof(PublisherGameInspectionResult result) =>
        result.HasFullInstallMaintenanceProof
        && (result.Status, result.Reason) is (
            PublisherGameInspectionStatus.NeedsReview,
            PublisherGameInspectionReason.VersionUnavailable);

    private static bool IsEndfieldRequest(OfficialMaintenanceHandoffRequest request) =>
        string.Equals(request.Target.GameId, "ae", StringComparison.Ordinal)
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
        && string.Equals(first.Target.CanonicalRoot, fresh.Target.CanonicalRoot, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Target.LauncherPath, fresh.Target.LauncherPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Target.LauncherVersion, fresh.Target.LauncherVersion, StringComparison.Ordinal)
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

internal interface IEndfieldOfficialMaintenanceProcessStarter
{
    void Start(OfficialMaintenanceHandoffRequest request);
}

internal sealed class WindowsEndfieldOfficialMaintenanceProcessStarter
    : IEndfieldOfficialMaintenanceProcessStarter
{
    public void Start(OfficialMaintenanceHandoffRequest request)
    {
        var startInfo = CreateStartInfo(request);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("GRYPHLINK did not start.");
    }

    internal static ProcessStartInfo CreateStartInfo(OfficialMaintenanceHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        if (!string.Equals(target.GameId, "ae", StringComparison.Ordinal)
            || request.Arguments.Count != 0
            || !string.Equals(Path.GetFileName(target.LauncherPath), "Launcher.exe", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(target.LauncherPath), target.CanonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the sealed Endfield maintenance handoff can start.");
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

internal sealed class EndfieldOfficialLauncherAdmission
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public EndfieldOfficialLauncherLease? TryEnter() =>
        gate.Wait(0) ? new(this) : null;

    public async Task<EndfieldOfficialLauncherLease> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(this);
    }

    internal void Release() => gate.Release();
}

internal sealed class EndfieldOfficialLauncherLease : IDisposable
{
    private EndfieldOfficialLauncherAdmission? owner;

    internal EndfieldOfficialLauncherLease(EndfieldOfficialLauncherAdmission owner)
    {
        this.owner = owner;
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
}

public sealed class EndfieldOfficialMaintenanceService
{
    private readonly Func<string?> locateSavedRoot;
    private readonly EndfieldIdentityAdapter validator;
    private readonly EndfieldOfficialMaintenanceExecutor executor;

    [SupportedOSPlatform("windows")]
    public EndfieldOfficialMaintenanceService(EndfieldInstallRootStore rootStore)
        : this(
            (rootStore ?? throw new ArgumentNullException(nameof(rootStore))).Load,
            new EndfieldIdentityAdapter(),
            new EndfieldOfficialMaintenanceExecutor())
    {
    }

    internal EndfieldOfficialMaintenanceService(
        Func<string?> locateSavedRoot,
        EndfieldIdentityAdapter validator,
        EndfieldOfficialMaintenanceExecutor executor)
    {
        this.locateSavedRoot = locateSavedRoot ?? throw new ArgumentNullException(nameof(locateSavedRoot));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public EndfieldOfficialMaintenanceResult Check()
    {
        var request = CreateFreshRequest();
        return request.Result ?? executor.Check(request.Request!);
    }

    public async Task<EndfieldOfficialMaintenanceResult> OpenOrObserveCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = CreateFreshRequest();
        return request.Result ?? await executor
            .OpenOrObserveCurrentAsync(request.Request!, cancellationToken)
            .ConfigureAwait(false);
    }

    private RequestCreation CreateFreshRequest()
    {
        var root = locateSavedRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return new(Result: new(
                EndfieldOfficialMaintenanceStatus.NotFound,
                InspectionReason: PublisherGameInspectionReason.PathNotProvided));
        }

        var inspection = validator.Inspect(root);
        if (inspection.Status is PublisherGameInspectionStatus.NotFound)
        {
            return new(Result: new(
                EndfieldOfficialMaintenanceStatus.NotFound,
                InspectionReason: inspection.Reason));
        }

        if (!inspection.HasFullInstallMaintenanceProof
            || inspection.MaintenanceTarget is null
            || inspection.Reason is not PublisherGameInspectionReason.VersionUnavailable)
        {
            return new(Result: new(
                EndfieldOfficialMaintenanceStatus.NeedsReview,
                InspectionReason: inspection.Reason));
        }

        return new(Request: OfficialMaintenanceHandoffFactory.Create(inspection.MaintenanceTarget));
    }

    private sealed record RequestCreation(
        OfficialMaintenanceHandoffRequest? Request = null,
        EndfieldOfficialMaintenanceResult? Result = null);
}
