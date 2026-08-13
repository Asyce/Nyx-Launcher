using Nyx_Desktop_App;

namespace Nyx.Desktop.Tests.UI;

public sealed class HoyoLabExportUiReservationTests
{
    [Fact]
    public async Task Delayed_first_workflow_rejects_second_acquire_until_release()
    {
        var reservation = new HoyoLabExportUiReservation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RunDelayedAsync(reservation, entered, release);
        await entered.Task;

        Assert.True(reservation.IsHeld);
        Assert.Null(reservation.TryAcquire());

        release.SetResult();
        await first;

        Assert.False(reservation.IsHeld);
        var second = reservation.TryAcquire();
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void Lease_release_is_idempotent_after_success()
    {
        var reservation = new HoyoLabExportUiReservation();
        var lease = reservation.TryAcquire();
        Assert.NotNull(lease);

        lease!.Dispose();
        lease.Dispose();

        Assert.False(reservation.IsHeld);
    }

    [Fact]
    public async Task Lease_releases_when_workflow_throws()
    {
        var reservation = new HoyoLabExportUiReservation();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunThrowingAsync(reservation));

        Assert.False(reservation.IsHeld);
    }

    [Fact]
    public async Task Lease_releases_when_workflow_is_canceled()
    {
        var reservation = new HoyoLabExportUiReservation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCanceledAsync(reservation, cancellation.Token));

        Assert.False(reservation.IsHeld);
    }

    private static async Task RunDelayedAsync(
        HoyoLabExportUiReservation reservation,
        TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        using var lease = reservation.TryAcquire();
        Assert.NotNull(lease);
        entered.SetResult();
        await release.Task;
    }

    private static async Task RunThrowingAsync(HoyoLabExportUiReservation reservation)
    {
        using var lease = reservation.TryAcquire();
        Assert.NotNull(lease);
        await Task.Yield();
        throw new InvalidOperationException("controlled provider failure");
    }

    private static async Task RunCanceledAsync(
        HoyoLabExportUiReservation reservation,
        CancellationToken cancellationToken)
    {
        using var lease = reservation.TryAcquire();
        Assert.NotNull(lease);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
