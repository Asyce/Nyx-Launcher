using System.Runtime.InteropServices;
using Nyx_Desktop_App;

namespace Nyx.Desktop.Tests.UI;

public sealed class PublisherNavigationHandlerCleanupTests
{
    [Fact]
    public void Normal_completion_removes_both_handlers()
    {
        var removed = new List<string>();

        PublisherNavigationHandlerCleanup.Remove(
            () => removed.Add("starting"),
            () => removed.Add("completed"),
            () => false);

        Assert.Equal(["starting", "completed"], removed);
    }

    [Fact]
    public void Close_before_cleanup_leaves_handler_teardown_to_the_closed_browser()
    {
        var removeCalls = 0;

        PublisherNavigationHandlerCleanup.Remove(
            () => removeCalls++,
            () => removeCalls++,
            () => true);

        Assert.Equal(0, removeCalls);
    }

    [Fact]
    public void Close_racing_with_cleanup_cannot_replace_expected_cancellation()
    {
        var closeStarted = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Record.Exception(() =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                PublisherNavigationHandlerCleanup.Remove(
                    () => closeStarted = true,
                    () => throw new COMException("The WebView is already closed."),
                    () => closeStarted);
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    [Fact]
    public void Unsubscribe_failure_without_browser_close_is_not_hidden()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublisherNavigationHandlerCleanup.Remove(
                () => throw new InvalidOperationException("unexpected"),
                () => { },
                () => false));

        Assert.Equal("unexpected", exception.Message);
    }
}
