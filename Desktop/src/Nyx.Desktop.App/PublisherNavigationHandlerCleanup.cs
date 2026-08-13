using System.Runtime.InteropServices;

namespace Nyx_Desktop_App;

internal static class PublisherNavigationHandlerCleanup
{
    public static void Remove(
        Action removeStarting,
        Action removeCompleted,
        Func<bool> browserCloseStarted)
    {
        ArgumentNullException.ThrowIfNull(removeStarting);
        ArgumentNullException.ThrowIfNull(removeCompleted);
        ArgumentNullException.ThrowIfNull(browserCloseStarted);

        // Closing WebView2 owns its remaining event registrations. Avoid
        // touching its COM object after Close, while keeping normal removal
        // failures visible when no close is in progress.
        if (browserCloseStarted()) return;
        try
        {
            removeStarting();
            removeCompleted();
        }
        catch (Exception exception) when (
            browserCloseStarted()
            && exception is InvalidOperationException or COMException)
        {
        }
    }
}
