using Windows.Media.Core;
using Windows.Media.Playback;

namespace Nyx_Desktop_App;

internal static class LauncherMotionReadiness
{
    // MediaOpened can succeed even when the decoder never produces a frame.
    // Prime in frame-server mode; restore normal rendering before revealing it.
    internal static async Task<bool> WaitForFrameAsync(
        MediaPlayer player,
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void FrameAvailable(MediaPlayer sender, object args)
        {
            if (ReferenceEquals(sender.Source, source)) ready.TrySetResult(true);
        }
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (ReferenceEquals(sender.Source, source)) ready.TrySetResult(false);
        }

        player.VideoFrameAvailable += FrameAvailable;
        player.MediaFailed += Failed;
        try
        {
            player.IsVideoFrameServerEnabled = true;
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException) { return false; }
        finally
        {
            player.VideoFrameAvailable -= FrameAvailable;
            player.MediaFailed -= Failed;
            if (player.Source is null || ReferenceEquals(player.Source, source))
                player.IsVideoFrameServerEnabled = false;
        }
    }
}
