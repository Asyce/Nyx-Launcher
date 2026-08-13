namespace Nyx.Desktop.Core.PublisherGames;

public static class IndependentMaintenanceLaneRunner
{
    public static Task RunAsync(Func<Task> first, Func<Task> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var firstTask = RunLaneAsync(first);
        var secondTask = RunLaneAsync(second);
        return Task.WhenAll(firstTask, secondTask);
    }

    private static async Task RunLaneAsync(Func<Task> lane)
    {
        try
        {
            await lane().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Each lane owns its fail-closed UI state. One unexpected lane failure
            // must not cancel, delay, or overwrite the other publisher family.
        }
    }
}
