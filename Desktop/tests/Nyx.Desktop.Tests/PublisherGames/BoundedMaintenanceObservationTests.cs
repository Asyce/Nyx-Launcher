using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class BoundedMaintenanceObservationTests
{
    [Fact]
    public async Task Delayed_launcher_appearance_wins_before_the_bounded_window_closes()
    {
        var observations = new Queue<string>(["Ready", "Ready", "Running"]);
        var delays = 0;

        var result = await BoundedMaintenanceObservation.ObserveAsync(
            _ => Task.FromResult(observations.Dequeue()),
            status => status == "Running",
            maximumObservations: 6,
            interval: TimeSpan.FromMilliseconds(500),
            delayAsync: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.Equal("Running", result);
        Assert.Equal(3, delays);
        Assert.Empty(observations);
    }

    [Fact]
    public async Task Genuine_exit_requires_every_observation_in_the_bounded_window_to_be_absent()
    {
        const int maximumObservations = 6;
        var observations = 0;

        var result = await BoundedMaintenanceObservation.ObserveAsync(
            _ =>
            {
                observations++;
                return Task.FromResult("Ready");
            },
            status => status == "Running",
            maximumObservations,
            TimeSpan.FromMilliseconds(500),
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.Equal("Ready", result);
        Assert.Equal(maximumObservations, observations);
    }
}
