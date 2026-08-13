using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class LatestGenerationGateTests
{
    [Theory]
    [InlineData("Running")]
    [InlineData("Ready")]
    public void Stale_failure_cannot_overwrite_a_newer_observation(string newerStatus)
    {
        var gate = new LatestGenerationGate();
        var staleGeneration = gate.Next();
        var newerGeneration = gate.Next();
        var status = "Opened";

        Assert.True(gate.TryApply(newerGeneration, () => status = newerStatus));
        Assert.False(gate.TryApply(staleGeneration, () => status = "Failed"));
        Assert.Equal(newerStatus, status);
    }

    [Fact]
    public async Task Activation_during_click_owned_delayed_observation_retains_running_and_disallows_reopen()
    {
        var generationGate = new LatestGenerationGate();
        var clickGeneration = generationGate.Next();
        var actionInFlight = true;
        var status = "Opened";
        var observations = new Queue<string>(["Ready", "Ready", "Running"]);

        var activationAllowed = WuWaMaintenanceInteractionPolicy.AllowsActivationRefresh(
            actionInFlight);
        if (activationAllowed)
        {
            _ = generationGate.Next();
        }

        var observed = await BoundedMaintenanceObservation.ObserveAsync(
            _ => Task.FromResult(observations.Dequeue()),
            candidate => candidate == "Running",
            maximumObservations: 6,
            interval: TimeSpan.FromMilliseconds(500),
            delayAsync: (_, _) => Task.CompletedTask);
        var applied = generationGate.TryApply(clickGeneration, () => status = observed);

        Assert.False(activationAllowed);
        Assert.True(generationGate.IsCurrent(clickGeneration));
        Assert.True(applied);
        Assert.Equal("Running", status);
        Assert.False(WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
            maintenanceReady: status == "Ready",
            actionInFlight,
            hasRequest: true));

        actionInFlight = false;
        Assert.True(WuWaMaintenanceInteractionPolicy.AllowsOpenOfficial(
            maintenanceReady: status == "Ready",
            actionInFlight,
            hasRequest: true));
    }
}
