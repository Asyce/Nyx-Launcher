using System.Collections.ObjectModel;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Hoyo;

public sealed class HoyoPlayHandoffRequest
{
    internal HoyoPlayHandoffRequest(
        GameDefinition game,
        ValidatedHoyoPlayInstallation installation,
        string? exactGameArgument)
    {
        Game = game;
        Installation = installation;
        Arguments = new ReadOnlyCollection<string>(
            exactGameArgument is null ? [] : [exactGameArgument]);
    }

    public GameDefinition Game { get; }

    public ValidatedHoyoPlayInstallation Installation { get; }

    public IReadOnlyList<string> Arguments { get; }

    public bool RequiresUserInteraction => true;

    public bool AllowsDirectUpdate => false;
}

public static class HoyoPlayHandoffFactory
{
    public static HoyoPlayHandoffRequest Create(
        string? gameId,
        ValidatedHoyoPlayInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var game = GameCatalog.GetRequired(gameId);
        var exactArgument = game.Id switch
        {
            "gi" => null,
            "hsr" => "--game=hkrpg_global",
            "zzz" => "--game=nap_global",
            _ => throw new UnsupportedGameException(gameId),
        };

        return new(game, installation, exactArgument);
    }
}

public enum HoyoPlayOpenStatus
{
    Ready,
    Running,
    Opened,
    NeedsReview,
    Failed,
    Busy,
}

public sealed record HoyoPlayOpenResult(
    HoyoPlayOpenStatus Status,
    HoyoPlayHandoffRequest? Request = null,
    HoyoInspectionReason InspectionReason = HoyoInspectionReason.None);

public interface IHoyoPlayProcessStarter
{
    void Start(HoyoPlayHandoffRequest request);
}

public sealed class OfficialLauncherFamilyAdmission
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public OfficialLauncherFamilyLease? TryEnter() =>
        gate.Wait(0)
            ? new(this)
            : null;

    public async Task<OfficialLauncherFamilyLease> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(this);
    }

    internal void Release() => gate.Release();
}

public sealed class OfficialLauncherFamilyLease : IDisposable
{
    private OfficialLauncherFamilyAdmission? owner;

    internal OfficialLauncherFamilyLease(OfficialLauncherFamilyAdmission owner)
    {
        this.owner = owner;
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
}
