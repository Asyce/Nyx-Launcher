using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Launching;

public enum PublisherGameLaunchStatus
{
    Ready,
    Running,
    LaunchFailed,
    NeedsReview,
}

public enum PublisherGameLaunchFailureReason
{
    None,
    ElevationRequired,
    ElevationCancelled,
    ElevatedStartFailed,
    WindowsStartFailed,
}

public enum PublisherGameRenderingMode
{
    PublisherDefault,
    DirectX11,
}

public sealed record PublisherGameDirectLaunchResult(
    PublisherGameLaunchStatus Status,
    LaunchSpecification? Specification = null,
    PublisherGameInspectionReason InspectionReason = PublisherGameInspectionReason.None,
    PublisherGameLaunchFailureReason FailureReason = PublisherGameLaunchFailureReason.None,
    RunningProcessStatus Bootstrap = RunningProcessStatus.NotRunning,
    RunningProcessStatus Runtime = RunningProcessStatus.NotRunning,
    bool StartedByThisCall = false);

public sealed class ValidatedPublisherGameElevationRequest
{
    internal ValidatedPublisherGameElevationRequest(
        string gameId,
        string canonicalRoot,
        LaunchSpecification specification)
    {
        GameId = gameId;
        CanonicalRoot = canonicalRoot;
        Specification = specification;
    }

    public string GameId { get; }

    internal string CanonicalRoot { get; }

    public LaunchSpecification Specification { get; }
}

public interface IPublisherGameElevatedProcessStarter
{
    void StartValidatedPublisherGame(ValidatedPublisherGameElevationRequest request);
}

/// <summary>
/// The only direct-start admission for WuWa and Endfield. It accepts no caller
/// executable, process name, arguments, shell verb, or launcher action.
/// </summary>
public sealed class PublisherGameDirectLaunchService
{
    private static readonly IReadOnlyDictionary<string, LaunchProfile> Profiles =
        new Dictionary<string, LaunchProfile>(StringComparer.Ordinal)
        {
            ["wuwa"] = new(
                @"Wuthering Waves Game\Wuthering Waves.exe",
                [
                    new("Wuthering Waves", @"Wuthering Waves Game\Wuthering Waves.exe", IsBootstrap: true),
                    new("Client-Win64-Shipping", @"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe", IsBootstrap: false),
                ],
                PublisherGameInspectionReason.VersionConflict,
                PublisherGameVersionState.Conflict,
                AllowsVersionedReady: true),
            ["ae"] = new(
                @"games\EndField Game\Endfield.exe",
                [new("Endfield", @"games\EndField Game\Endfield.exe", IsBootstrap: false)],
                PublisherGameInspectionReason.VersionUnavailable,
                PublisherGameVersionState.Unavailable,
                AllowsVersionedReady: false),
        };

    private readonly IPublisherGameDirectLaunchIdentityValidator validator;
    private readonly IStrictRunningProcessInspector processInspector;
    private readonly ILaunchProcessStarter processStarter;
    private readonly IPublisherGameElevatedProcessStarter? elevatedProcessStarter;

    internal PublisherGameDirectLaunchService(
        IPublisherGameDirectLaunchIdentityValidator validator,
        IStrictRunningProcessInspector processInspector,
        ILaunchProcessStarter processStarter)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        elevatedProcessStarter = processStarter as IPublisherGameElevatedProcessStarter;
    }

    public PublisherGameDirectLaunchResult CheckGame(
        string gameId,
        string? root,
        PublisherGameRenderingMode renderingMode = PublisherGameRenderingMode.PublisherDefault,
        IReadOnlyList<string>? launchArguments = null)
    {
        var profile = GetProfile(gameId);
        try
        {
            using var inspection = validator.InspectProtected(gameId, root);
            return EvaluateProtected(gameId, root, profile, inspection, renderingMode, launchArguments);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(PublisherGameLaunchStatus.NeedsReview);
        }
    }

    public PublisherGameDirectLaunchResult LaunchGame(
        string gameId,
        string? root,
        PublisherGameRenderingMode renderingMode = PublisherGameRenderingMode.PublisherDefault,
        IReadOnlyList<string>? launchArguments = null)
    {
        var profile = GetProfile(gameId);
        var initial = CheckGame(gameId, root, renderingMode, launchArguments);
        if (initial.Status is not PublisherGameLaunchStatus.Ready)
        {
            return initial;
        }

        try
        {
            using var freshInspection = validator.InspectProtected(gameId, root);
            var fresh = EvaluateProtected(gameId, root, profile, freshInspection, renderingMode, launchArguments);
            if (fresh.Status is not PublisherGameLaunchStatus.Ready
                || fresh.Specification is null
                || !SpecificationsMatch(initial.Specification!, fresh.Specification))
            {
                return fresh.Status is PublisherGameLaunchStatus.Ready
                    ? new(PublisherGameLaunchStatus.NeedsReview, fresh.Specification)
                    : fresh;
            }

            try
            {
                processStarter.Start(fresh.Specification);
                return fresh with { Status = PublisherGameLaunchStatus.Running, StartedByThisCall = true };
            }
            catch (Exception exception) when (IsBoundaryFailure(exception))
            {
                if (exception is not System.ComponentModel.Win32Exception { NativeErrorCode: 740 })
                {
                    return Failed(fresh, PublisherGameLaunchFailureReason.WindowsStartFailed);
                }

                if (elevatedProcessStarter is null)
                {
                    return Failed(fresh, PublisherGameLaunchFailureReason.ElevationRequired);
                }

                return LaunchElevated(gameId, root, profile, renderingMode, launchArguments, fresh.Specification);
            }
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(PublisherGameLaunchStatus.NeedsReview);
        }
    }

    private PublisherGameDirectLaunchResult LaunchElevated(
        string gameId,
        string? root,
        LaunchProfile profile,
        PublisherGameRenderingMode renderingMode,
        IReadOnlyList<string>? launchArguments,
        LaunchSpecification originalSpecification)
    {
        using var inspection = validator.InspectProtected(gameId, root);
        var fresh = EvaluateProtected(gameId, root, profile, inspection, renderingMode, launchArguments);
        if (fresh.Status is not PublisherGameLaunchStatus.Ready
            || fresh.Specification is null
            || !SpecificationsMatch(originalSpecification, fresh.Specification))
        {
            return fresh.Status is PublisherGameLaunchStatus.Ready
                ? new(PublisherGameLaunchStatus.NeedsReview, fresh.Specification)
                : fresh;
        }

        try
        {
            elevatedProcessStarter!.StartValidatedPublisherGame(
                new ValidatedPublisherGameElevationRequest(
                    gameId,
                    inspection.Result.CanonicalRoot!,
                    fresh.Specification));
            return fresh with { Status = PublisherGameLaunchStatus.Running, StartedByThisCall = true };
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            var reason = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? PublisherGameLaunchFailureReason.ElevationCancelled
                : PublisherGameLaunchFailureReason.ElevatedStartFailed;
            return Failed(fresh, reason);
        }
    }

    private PublisherGameDirectLaunchResult EvaluateProtected(
        string gameId,
        string? suppliedRoot,
        LaunchProfile profile,
        IProtectedPublisherGameInspection protectedInspection,
        PublisherGameRenderingMode renderingMode,
        IReadOnlyList<string>? launchArguments)
    {
        var inspection = protectedInspection.Result;
        if (!IsAdmissibleInspection(gameId, suppliedRoot, profile, inspection))
        {
            return new(
                PublisherGameLaunchStatus.NeedsReview,
                InspectionReason: inspection.Reason);
        }

        var root = inspection.CanonicalRoot!;
        var executablePath = Path.Combine(root, profile.ExecutableRelativePath);
        var fixedArgument = renderingMode switch
        {
            PublisherGameRenderingMode.PublisherDefault => null,
            PublisherGameRenderingMode.DirectX11 when gameId == "wuwa" => "-dx11",
            _ => throw new ArgumentOutOfRangeException(
                nameof(renderingMode),
                "DirectX 11 is sealed to the WuWa profile."),
        };
        if (!CustomArgumentParser.TryCombine(
                fixedArgument,
                launchArguments ?? Array.Empty<string>(),
                out var arguments))
            return new(PublisherGameLaunchStatus.NeedsReview, InspectionReason: inspection.Reason);

        var specification = new LaunchSpecification(
            executablePath,
            Path.GetDirectoryName(executablePath)!,
            arguments,
            UseShellExecute: false);
        var bootstrap = RunningProcessStatus.NotRunning;
        var runtime = RunningProcessStatus.NotRunning;

        foreach (var process in profile.Processes)
        {
            var processStatus = processInspector.CheckStrict(
                process.ProcessName,
                Path.Combine(root, process.ExecutableRelativePath));
            if (process.IsBootstrap)
            {
                bootstrap = processStatus;
            }
            else
            {
                runtime = processStatus;
            }
        }

        if (!protectedInspection.RemainsCompleteAndStable())
        {
            return new(
                PublisherGameLaunchStatus.NeedsReview,
                specification,
                PublisherGameInspectionReason.TargetChangedDuringInspection,
                Bootstrap: bootstrap,
                Runtime: runtime);
        }

        if (bootstrap is RunningProcessStatus.Uncertain
            || runtime is RunningProcessStatus.Uncertain)
        {
            return new(
                PublisherGameLaunchStatus.NeedsReview,
                specification,
                inspection.Reason,
                Bootstrap: bootstrap,
                Runtime: runtime);
        }

        var status = bootstrap is RunningProcessStatus.Running
            || runtime is RunningProcessStatus.Running
                ? PublisherGameLaunchStatus.Running
                : PublisherGameLaunchStatus.Ready;
        return new(
            status,
            specification,
            inspection.Reason,
            Bootstrap: bootstrap,
            Runtime: runtime,
            StartedByThisCall: false);
    }

    private static bool IsAdmissibleInspection(
        string gameId,
        string? suppliedRoot,
        LaunchProfile profile,
        PublisherGameInspectionResult inspection) =>
        ((profile.AllowsVersionedReady
             && inspection.Status is PublisherGameInspectionStatus.Ready
             && inspection.Reason is PublisherGameInspectionReason.None
             && inspection.VersionState is PublisherGameVersionState.Available
             && !string.IsNullOrWhiteSpace(inspection.Version))
         || (inspection.Status is PublisherGameInspectionStatus.NeedsReview
             && inspection.Reason == profile.RequiredReason
             && inspection.VersionState == profile.RequiredVersionState
             && inspection.Version is null)
         || (gameId is "wuwa"
             && inspection.PreInstallAvailable
             && inspection.Status is PublisherGameInspectionStatus.NeedsReview
             && inspection.Reason is PublisherGameInspectionReason.VersionUnavailable
             && inspection.VersionState is PublisherGameVersionState.Unavailable
             && inspection.Version is null))
        && inspection.HasFullInstallMaintenanceProof
        && string.Equals(inspection.GameId, gameId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(inspection.CanonicalRoot)
        && string.Equals(
            Path.TrimEndingDirectorySeparator(suppliedRoot ?? string.Empty),
            inspection.CanonicalRoot,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            inspection.MaintenanceTarget!.GameId,
            gameId,
            StringComparison.Ordinal)
        && string.Equals(
            inspection.MaintenanceTarget.CanonicalRoot,
            inspection.CanonicalRoot,
            StringComparison.OrdinalIgnoreCase);

    private static LaunchProfile GetProfile(string? gameId)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        return Profiles.TryGetValue(gameId, out var profile)
            ? profile
            : throw new ArgumentOutOfRangeException(
                nameof(gameId),
                "Only sealed WuWa and Endfield profiles are supported.");
    }

    private static bool SpecificationsMatch(LaunchSpecification left, LaunchSpecification right) =>
        string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
        && left.UseShellExecute == right.UseShellExecute
        && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

    private static PublisherGameDirectLaunchResult Failed(
        PublisherGameDirectLaunchResult result,
        PublisherGameLaunchFailureReason reason) =>
        result with
        {
            Status = PublisherGameLaunchStatus.LaunchFailed,
            FailureReason = reason,
        };

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception;

    private sealed record LaunchProfile(
        string ExecutableRelativePath,
        IReadOnlyList<ProcessProfile> Processes,
        PublisherGameInspectionReason RequiredReason,
        PublisherGameVersionState RequiredVersionState,
        bool AllowsVersionedReady);

    private sealed record ProcessProfile(
        string ProcessName,
        string ExecutableRelativePath,
        bool IsBootstrap);
}
