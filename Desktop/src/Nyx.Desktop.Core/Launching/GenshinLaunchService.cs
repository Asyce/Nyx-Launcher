using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Launching;

public enum GenshinLaunchStatus
{
    Ready,
    Running,
    LaunchFailed,
    NeedsReview,
}

public enum GenshinLaunchFailureReason
{
    None,
    ElevationRequired,
    ElevationCancelled,
    ElevatedStartFailed,
    WindowsStartFailed,
    FpsHelperUnavailable,
    FpsHelperFailed,
    FpsHelperTimedOut,
    FpsAttachFailed,
    FpsAttachTimedOut,
    FpsLaunchUnconfirmed,
}

public enum Genshin120FpsStartStatus
{
    Ready,
    GameStartedAttachFailed,
    GameStartedAttachTimedOut,
    HelperUnavailable,
    ElevationCancelled,
    Failed,
    TimedOut,
    GameStartUnconfirmed,
}

public enum RunningProcessStatus
{
    NotRunning,
    Running,
    Uncertain,
}

public sealed record LaunchSpecification(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute);

public sealed record GenshinLaunchResult(
    GenshinLaunchStatus Status,
    LaunchSpecification? Specification = null,
    GenshinInspectionReason InspectionReason = GenshinInspectionReason.None,
    GenshinLaunchFailureReason FailureReason = GenshinLaunchFailureReason.None);

public interface IGenshinLaunchIdentityValidator
{
    GenshinInspectionResult ValidateGame(string? root);
}

public interface IRunningProcessInspector
{
    RunningProcessStatus Check(string processName, string expectedExecutablePath);
}

public interface ILaunchProcessStarter
{
    void Start(LaunchSpecification specification);
}

public sealed class ValidatedGenshinElevationRequest
{
    internal ValidatedGenshinElevationRequest(LaunchSpecification specification)
    {
        Specification = specification;
    }

    public LaunchSpecification Specification { get; }
}

public interface IGenshinElevatedProcessStarter
{
    void StartValidatedGenshin(ValidatedGenshinElevationRequest request);
}

public sealed class ValidatedGenshin120FpsRequest
{
    internal ValidatedGenshin120FpsRequest(LaunchSpecification specification)
    {
        Specification = specification;
    }

    public LaunchSpecification Specification { get; }
}

public interface IGenshin120FpsProcessStarter
{
    Genshin120FpsStartStatus StartValidatedGenshin120Fps(
        ValidatedGenshin120FpsRequest request,
        CancellationToken cancellationToken);
}

public sealed class GenshinLaunchService
{
    private readonly IGenshinLaunchIdentityValidator validator;
    private readonly IRunningProcessInspector processInspector;
    private readonly ILaunchProcessStarter processStarter;
    private readonly IGenshinElevatedProcessStarter? elevatedProcessStarter;
    private readonly IGenshin120FpsProcessStarter? fps120ProcessStarter;

    public GenshinLaunchService(
        IGenshinLaunchIdentityValidator validator,
        IRunningProcessInspector processInspector,
        ILaunchProcessStarter processStarter,
        IGenshin120FpsProcessStarter? fps120ProcessStarter = null)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        elevatedProcessStarter = processStarter as IGenshinElevatedProcessStarter;
        this.fps120ProcessStarter = fps120ProcessStarter;
    }

    public GenshinLaunchResult CheckGame(
        string? gameRoot,
        IReadOnlyList<string>? launchArguments = null) =>
        Check(gameRoot, validator.ValidateGame, "GenshinImpact.exe", "GenshinImpact", launchArguments);

    public GenshinLaunchResult LaunchGame(
        string? gameRoot,
        IReadOnlyList<string>? launchArguments = null) =>
        Launch(
            gameRoot,
            validator.ValidateGame,
            "GenshinImpact.exe",
            "GenshinImpact",
            launchArguments);

    public GenshinLaunchResult LaunchGameWith120Fps(
        string? gameRoot,
        IReadOnlyList<string>? launchArguments = null,
        CancellationToken cancellationToken = default)
    {
        var checkedResult = CheckGame(gameRoot, launchArguments);
        if (checkedResult.Status is not GenshinLaunchStatus.Ready)
        {
            return checkedResult;
        }

        var freshResult = CheckGame(gameRoot, launchArguments);
        if (freshResult.Status is not GenshinLaunchStatus.Ready
            || freshResult.Specification is null
            || !SpecificationsMatch(checkedResult.Specification!, freshResult.Specification))
        {
            return freshResult.Status is GenshinLaunchStatus.Ready
                ? new(GenshinLaunchStatus.NeedsReview, freshResult.Specification)
                : freshResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (fps120ProcessStarter is null)
        {
            return Failed(freshResult, GenshinLaunchFailureReason.FpsHelperUnavailable);
        }

        var status = fps120ProcessStarter.StartValidatedGenshin120Fps(
            new ValidatedGenshin120FpsRequest(freshResult.Specification),
            cancellationToken);
        return status switch
        {
            Genshin120FpsStartStatus.Ready =>
                freshResult with { Status = GenshinLaunchStatus.Running },
            Genshin120FpsStartStatus.GameStartedAttachFailed =>
                freshResult with
                {
                    Status = GenshinLaunchStatus.Running,
                    FailureReason = GenshinLaunchFailureReason.FpsAttachFailed,
                },
            Genshin120FpsStartStatus.GameStartedAttachTimedOut =>
                freshResult with
                {
                    Status = GenshinLaunchStatus.Running,
                    FailureReason = GenshinLaunchFailureReason.FpsAttachTimedOut,
                },
            Genshin120FpsStartStatus.GameStartUnconfirmed =>
                freshResult with
                {
                    Status = GenshinLaunchStatus.Running,
                    FailureReason = GenshinLaunchFailureReason.FpsLaunchUnconfirmed,
                },
            Genshin120FpsStartStatus.HelperUnavailable =>
                Failed(freshResult, GenshinLaunchFailureReason.FpsHelperUnavailable),
            Genshin120FpsStartStatus.ElevationCancelled =>
                Failed(freshResult, GenshinLaunchFailureReason.ElevationCancelled),
            Genshin120FpsStartStatus.TimedOut =>
                Failed(freshResult, GenshinLaunchFailureReason.FpsHelperTimedOut),
            _ => Failed(freshResult, GenshinLaunchFailureReason.FpsHelperFailed),
        };
    }

    private GenshinLaunchResult Launch(
        string? root,
        Func<string?, GenshinInspectionResult> revalidate,
        string executableName,
        string processName,
        IReadOnlyList<string>? launchArguments)
    {
        var checkedResult = Check(root, revalidate, executableName, processName, launchArguments);
        if (checkedResult.Status is not GenshinLaunchStatus.Ready)
        {
            return checkedResult;
        }

        var freshResult = Check(root, revalidate, executableName, processName, launchArguments);
        if (freshResult.Status is not GenshinLaunchStatus.Ready
            || freshResult.Specification is null
            || !SpecificationsMatch(checkedResult.Specification!, freshResult.Specification))
        {
            return freshResult.Status is GenshinLaunchStatus.Ready
                ? new(GenshinLaunchStatus.NeedsReview, freshResult.Specification)
                : freshResult;
        }

        try
        {
            processStarter.Start(freshResult.Specification);
            return freshResult with { Status = GenshinLaunchStatus.Running };
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            if (exception is not System.ComponentModel.Win32Exception { NativeErrorCode: 740 })
            {
                return Failed(freshResult, GenshinLaunchFailureReason.WindowsStartFailed);
            }

            if (elevatedProcessStarter is null)
            {
                return Failed(freshResult, GenshinLaunchFailureReason.ElevationRequired);
            }

            return LaunchElevatedGame(
                root,
                revalidate,
                executableName,
                processName,
                launchArguments,
                freshResult.Specification);
        }
    }

    private GenshinLaunchResult LaunchElevatedGame(
        string? root,
        Func<string?, GenshinInspectionResult> revalidate,
        string executableName,
        string processName,
        IReadOnlyList<string>? launchArguments,
        LaunchSpecification originalSpecification)
    {
        var freshResult = Check(root, revalidate, executableName, processName, launchArguments);
        if (freshResult.Status is not GenshinLaunchStatus.Ready
            || freshResult.Specification is null
            || !SpecificationsMatch(originalSpecification, freshResult.Specification))
        {
            return freshResult.Status is GenshinLaunchStatus.Ready
                ? new(GenshinLaunchStatus.NeedsReview, freshResult.Specification)
                : freshResult;
        }

        try
        {
            elevatedProcessStarter!.StartValidatedGenshin(
                new ValidatedGenshinElevationRequest(freshResult.Specification));
            return freshResult with { Status = GenshinLaunchStatus.Running };
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            var reason = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? GenshinLaunchFailureReason.ElevationCancelled
                : GenshinLaunchFailureReason.ElevatedStartFailed;
            return Failed(freshResult, reason);
        }
    }

    private static bool SpecificationsMatch(LaunchSpecification left, LaunchSpecification right) =>
        string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
        && left.UseShellExecute == right.UseShellExecute
        && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

    private static GenshinLaunchResult Failed(
        GenshinLaunchResult result,
        GenshinLaunchFailureReason reason) =>
        result with
        {
            Status = GenshinLaunchStatus.LaunchFailed,
            FailureReason = reason,
        };

    private GenshinLaunchResult Check(
        string? root,
        Func<string?, GenshinInspectionResult> revalidate,
        string executableName,
        string processName,
        IReadOnlyList<string>? launchArguments)
    {
        GenshinInspectionResult inspection;
        try
        {
            inspection = revalidate(root);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(GenshinLaunchStatus.NeedsReview);
        }

        if (inspection.Status is not GenshinInspectionStatus.Ready
            || string.IsNullOrWhiteSpace(inspection.CanonicalRoot)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                inspection.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(GenshinLaunchStatus.NeedsReview, InspectionReason: inspection.Reason);
        }

        if (!CustomArgumentParser.TryCombine(null, launchArguments ?? Array.Empty<string>(), out var arguments))
            return new(GenshinLaunchStatus.NeedsReview, InspectionReason: inspection.Reason);

        var specification = new LaunchSpecification(
            Path.Combine(inspection.CanonicalRoot, executableName),
            inspection.CanonicalRoot,
            arguments,
            UseShellExecute: false);

        RunningProcessStatus runningStatus;
        try
        {
            runningStatus = processInspector.Check(processName, specification.FileName);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(GenshinLaunchStatus.NeedsReview, specification);
        }

        return runningStatus switch
        {
            RunningProcessStatus.NotRunning => new(GenshinLaunchStatus.Ready, specification),
            RunningProcessStatus.Running => new(GenshinLaunchStatus.Running, specification),
            _ => new(GenshinLaunchStatus.NeedsReview, specification),
        };
    }

    private static bool IsInspectionFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or System.ComponentModel.Win32Exception;

    private static bool IsStartFailure(Exception exception) =>
        IsInspectionFailure(exception)
        || exception is InvalidOperationException
            or System.ComponentModel.Win32Exception;
}
