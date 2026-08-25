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

public sealed class OfficialLauncherFamilyAdmission : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object admissionSync = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? disposal;
    private TaskCompletionSource? operationsDrained;
    private int activeOperations;
    private bool admissionClosed;

    public OfficialLauncherFamilyLease? TryEnter()
    {
        lock (admissionSync)
        {
            ObjectDisposedException.ThrowIf(admissionClosed, this);
            if (!gate.Wait(0)) return null;
            activeOperations++;
            return new(this);
        }
    }

    public async Task<OfficialLauncherFamilyLease> EnterAsync(CancellationToken cancellationToken)
    {
        lock (admissionSync)
        {
            ObjectDisposedException.ThrowIf(admissionClosed, this);
            activeOperations++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdown.Token);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            lock (admissionSync)
            {
                if (admissionClosed)
                {
                    gate.Release();
                    throw new ObjectDisposedException(nameof(OfficialLauncherFamilyAdmission));
                }
            }
            return new(this);
        }
        catch
        {
            ReleaseOperation();
            throw;
        }
    }

    internal void Release()
    {
        gate.Release();
        ReleaseOperation();
    }

    public ValueTask DisposeAsync()
    {
        lock (admissionSync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private void ReleaseOperation()
    {
        TaskCompletionSource? drained = null;
        lock (admissionSync)
        {
            activeOperations--;
            if (admissionClosed && activeOperations == 0)
            {
                drained = operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        Task drain;
        lock (admissionSync)
        {
            admissionClosed = true;
            drain = activeOperations == 0
                ? Task.CompletedTask
                : (operationsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await shutdown.CancelAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);
        gate.Dispose();
        shutdown.Dispose();
    }
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
