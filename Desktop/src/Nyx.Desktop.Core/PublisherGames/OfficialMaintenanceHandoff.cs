using System.Collections.ObjectModel;

namespace Nyx.Desktop.Core.PublisherGames;

public sealed class OfficialMaintenanceHandoffRequest
{
    internal OfficialMaintenanceHandoffRequest(
        ValidatedOfficialMaintenanceTarget target,
        string instructions)
    {
        Target = target;
        Instructions = instructions;
        Arguments = new ReadOnlyCollection<string>([]);
    }

    public ValidatedOfficialMaintenanceTarget Target { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string Instructions { get; }

    public bool RequiresUserInteraction => true;

    public bool RequiresImmediateRevalidation => true;

    public bool RequiresFullInstallRevalidation => true;

    public bool RequiresProtectedExecutableBinding => true;

    public bool AllowsDirectUpdate => false;

    public bool AllowsDirectGameLaunch => false;
}

public static class OfficialMaintenanceHandoffFactory
{
    public static OfficialMaintenanceHandoffRequest Create(
        ValidatedOfficialMaintenanceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var instructions = target.GameId switch
        {
            "wuwa" => "Use the validated Kuro launcher to maintain Wuthering Waves.",
            "ae" => "In GRYPHLINK, select Arknights: Endfield and use the official maintenance controls.",
            _ => throw new InvalidOperationException("Unsupported validated maintenance family."),
        };

        return new(target, instructions);
    }
}

public enum WuWaOfficialMaintenanceStatus
{
    Ready,
    Running,
    Opened,
    NotFound,
    NeedsReview,
    Unsupported,
    Failed,
    Busy,
}

public sealed record WuWaOfficialMaintenanceResult(
    WuWaOfficialMaintenanceStatus Status,
    OfficialMaintenanceHandoffRequest? Request = null,
    PublisherGameInspectionReason InspectionReason = PublisherGameInspectionReason.None);

public enum EndfieldOfficialMaintenanceStatus
{
    Ready,
    Running,
    Opened,
    NotFound,
    NeedsReview,
    Unsupported,
    Failed,
    Busy,
}

public sealed record EndfieldOfficialMaintenanceResult(
    EndfieldOfficialMaintenanceStatus Status,
    OfficialMaintenanceHandoffRequest? Request = null,
    PublisherGameInspectionReason InspectionReason = PublisherGameInspectionReason.None);
