using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class SessionUiLifetimeTests
{
    [Fact]
    public void Unload_or_close_invalidates_already_queued_mutation()
    {
        var lifetime = new SessionUiLifetime();
        var lease = lifetime.Activate();
        var mutations = 0;
        Action queuedMutation = () =>
            lifetime.TryRun(lease, () => Interlocked.Increment(ref mutations));

        lifetime.Deactivate(lease);
        queuedMutation();

        Assert.Equal(0, mutations);
        Assert.True(lease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Deactivate_waits_for_admitted_mutation_then_blocks_every_later_one()
    {
        var lifetime = new SessionUiLifetime();
        var lease = lifetime.Activate();
        var mutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = 0;
        var mutation = Task.Run(() => lifetime.TryRun(lease, () =>
        {
            mutationEntered.TrySetResult();
            releaseMutation.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref mutations);
        }));
        await mutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var deactivate = Task.Run(() => lifetime.Deactivate(lease));
        await Task.Delay(40);
        Assert.False(deactivate.IsCompleted);
        releaseMutation.TrySetResult();
        await deactivate.WaitAsync(TimeSpan.FromSeconds(1));
        await mutation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, mutations);
        Assert.False(lifetime.TryRun(lease, () => Interlocked.Increment(ref mutations)));
        Assert.Equal(1, mutations);
    }

    [Fact]
    public void Stale_page_deactivation_cannot_invalidate_new_page()
    {
        var lifetime = new SessionUiLifetime();
        var oldLease = lifetime.Activate();
        var currentLease = lifetime.Activate();
        var mutations = 0;

        lifetime.Deactivate(oldLease);

        Assert.False(lifetime.TryRun(oldLease, () => Interlocked.Increment(ref mutations)));
        Assert.True(lifetime.TryRun(currentLease, () => Interlocked.Increment(ref mutations)));
        Assert.Equal(1, mutations);
        Assert.False(currentLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Old_and_new_page_handlers_and_queued_callbacks_never_cross_leases()
    {
        var lifetime = new SessionUiLifetime();
        var oldLease = lifetime.Activate();
        var oldMutations = 0;
        var newMutations = 0;
        Action oldSubscribedHandler = () =>
            lifetime.TryRun(oldLease, () => Interlocked.Increment(ref oldMutations));
        var oldQueuedCallback = oldSubscribedHandler;

        var newLease = lifetime.Activate();
        Action newSubscribedHandler = () =>
            lifetime.TryRun(newLease, () => Interlocked.Increment(ref newMutations));
        lifetime.Deactivate(oldLease);

        oldSubscribedHandler();
        oldQueuedCallback();
        newSubscribedHandler();

        Assert.Equal(0, oldMutations);
        Assert.Equal(1, newMutations);
        Assert.False(newLease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Terminal_window_close_invalidates_current_and_every_future_lifetime()
    {
        var lifetime = new SessionUiLifetime();
        var lease = lifetime.Activate();
        var mutations = 0;

        lifetime.Terminate();

        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.False(lifetime.TryRun(lease, () => Interlocked.Increment(ref mutations)));
        Assert.Throws<ObjectDisposedException>(lifetime.Activate);
        Assert.Equal(0, mutations);
    }
}
