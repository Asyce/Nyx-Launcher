using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Infrastructure.Hoyo;

/// <summary>
/// Opens the visible official HoYoPlay window for one sealed game handoff.
/// It never updates files itself and has no hidden, shell, elevation, or generic
/// argument/path capability.
/// </summary>
public sealed class HoyoPlayHandoffExecutor
{
    private readonly HoyoPlayGlobalValidator validator;
    private readonly IStrictRunningProcessInspector processInspector;
    private readonly IHoyoPlayProcessStarter processStarter;
    private readonly OfficialLauncherFamilyAdmission familyAdmission;

    [SupportedOSPlatform("windows")]
    public HoyoPlayHandoffExecutor()
        : this(
            new HoyoPlayGlobalValidator(),
            new WindowsRunningProcessInspector(),
            new WindowsHoyoPlayProcessStarter(),
            new OfficialLauncherFamilyAdmission())
    {
    }

    internal HoyoPlayHandoffExecutor(
        HoyoPlayGlobalValidator validator,
        IStrictRunningProcessInspector processInspector,
        IHoyoPlayProcessStarter processStarter)
        : this(validator, processInspector, processStarter, new OfficialLauncherFamilyAdmission())
    {
    }

    internal HoyoPlayHandoffExecutor(
        HoyoPlayGlobalValidator validator,
        IStrictRunningProcessInspector processInspector,
        IHoyoPlayProcessStarter processStarter,
        OfficialLauncherFamilyAdmission familyAdmission)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        this.familyAdmission = familyAdmission ?? throw new ArgumentNullException(nameof(familyAdmission));
    }

    public HoyoPlayOpenResult Check(string gameId, string? root)
    {
        HoyoPlayValidationResult validation;
        try
        {
            validation = validator.Validate(root);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(HoyoPlayOpenStatus.NeedsReview);
        }

        if (validation.Status is not HoyoInspectionStatus.Ready
            || validation.Installation is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                validation.Installation.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                HoyoPlayOpenStatus.NeedsReview,
                InspectionReason: validation.Reason);
        }

        HoyoPlayHandoffRequest request;
        try
        {
            request = HoyoPlayHandoffFactory.Create(gameId, validation.Installation);
        }
        catch (Exception exception) when (exception is ArgumentException or UnsupportedGameException)
        {
            return new(HoyoPlayOpenStatus.NeedsReview);
        }

        RunningProcessStatus running;
        try
        {
            running = processInspector.CheckStrict("launcher", validation.Installation.LauncherPath);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(HoyoPlayOpenStatus.NeedsReview, request);
        }

        return running switch
        {
            RunningProcessStatus.NotRunning => new(HoyoPlayOpenStatus.Ready, request),
            RunningProcessStatus.Running => new(HoyoPlayOpenStatus.Running, request),
            _ => new(HoyoPlayOpenStatus.NeedsReview, request),
        };
    }

    public HoyoPlayOpenResult Open(string gameId, string? root)
    {
        using var admission = familyAdmission.TryEnter();
        if (admission is null)
        {
            return new(HoyoPlayOpenStatus.Busy);
        }

        return OpenAdmitted(gameId, root, CancellationToken.None);
    }

    public async Task<HoyoPlayOpenResult> OpenOrObserveCurrentAsync(
        string gameId,
        string? root,
        CancellationToken cancellationToken = default)
    {
        var admission = familyAdmission.TryEnter();
        if (admission is null)
        {
            using var observationAdmission = await familyAdmission
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            return Check(gameId, root);
        }

        using (admission)
        {
            return await Task.Run(
                () => OpenAdmitted(gameId, root, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private HoyoPlayOpenResult OpenAdmitted(
        string gameId,
        string? root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var first = Check(gameId, root);
        if (first.Status is not HoyoPlayOpenStatus.Ready || first.Request is null)
        {
            return first;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fresh = Check(gameId, root);
        if (fresh.Status is not HoyoPlayOpenStatus.Ready
            || fresh.Request is null
            || !RequestsMatch(first.Request, fresh.Request))
        {
            return fresh.Status is HoyoPlayOpenStatus.Ready
                ? new(HoyoPlayOpenStatus.NeedsReview, fresh.Request)
                : fresh;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            processStarter.Start(fresh.Request);
            return new(HoyoPlayOpenStatus.Opened, fresh.Request);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(HoyoPlayOpenStatus.Failed, fresh.Request);
        }
    }

    private static bool RequestsMatch(
        HoyoPlayHandoffRequest left,
        HoyoPlayHandoffRequest right) =>
        string.Equals(left.Game.Id, right.Game.Id, StringComparison.Ordinal)
        && string.Equals(
            left.Installation.CanonicalRoot,
            right.Installation.CanonicalRoot,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            left.Installation.LauncherPath,
            right.Installation.LauncherPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Installation.Version, right.Installation.Version, StringComparison.Ordinal)
        && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or InvalidOperationException
            or Win32Exception;
}

internal sealed class WindowsHoyoPlayProcessStarter : IHoyoPlayProcessStarter
{
    public void Start(HoyoPlayHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = request.Installation;
        var argumentsAreExact = request.Game.Id switch
        {
            "gi" => request.Arguments.Count == 0,
            "hsr" => request.Arguments.SequenceEqual(["--game=hkrpg_global"], StringComparer.Ordinal),
            "zzz" => request.Arguments.SequenceEqual(["--game=nap_global"], StringComparer.Ordinal),
            _ => false,
        };
        if (!argumentsAreExact
            || !string.Equals(
                Path.GetDirectoryName(installation.LauncherPath),
                installation.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(installation.LauncherPath),
                "launcher.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only a sealed HoYoPlay game handoff can start.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.LauncherPath,
            WorkingDirectory = installation.CanonicalRoot,
            UseShellExecute = false,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("HoYoPlay did not start.");
    }
}
