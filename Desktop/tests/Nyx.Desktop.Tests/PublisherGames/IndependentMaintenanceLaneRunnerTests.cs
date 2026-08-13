using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class IndependentMaintenanceLaneRunnerTests
{
    [Fact]
    public async Task Delayed_first_lane_does_not_delay_second_lane_completion()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var run = IndependentMaintenanceLaneRunner.RunAsync(
            () => releaseFirst.Task,
            () =>
            {
                secondFinished.SetResult();
                return Task.CompletedTask;
            });

        await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        releaseFirst.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failed_first_lane_cannot_cancel_or_overwrite_second_lane()
    {
        var secondRuns = 0;

        await IndependentMaintenanceLaneRunner.RunAsync(
            () => Task.FromException(new IOException("fake lane failure")),
            () =>
            {
                Interlocked.Increment(ref secondRuns);
                return Task.CompletedTask;
            });

        Assert.Equal(1, secondRuns);
    }
}
