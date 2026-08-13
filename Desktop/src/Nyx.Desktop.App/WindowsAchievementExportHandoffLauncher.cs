using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx_Desktop_App;

internal sealed class WindowsAchievementExportHandoffLauncher :
    IAchievementExportHandoffLauncher
{
    public async ValueTask<bool> OpenBrowserAsync(
        Uri browserUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFixedPengoHandoff(browserUri))
            throw new InvalidOperationException("The achievement handoff URI is not approved.");
        return await Windows.System.Launcher.LaunchUriAsync(browserUri);
    }

    public async ValueTask<bool> OpenFallbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.Combine(WindowsDocumentsDirectory.Get(), "Pengo Exports");
        Directory.CreateDirectory(folder);
        return await Windows.System.Launcher.LaunchFolderPathAsync(folder);
    }

    private static bool IsFixedPengoHandoff(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "pengo.gg"
        && uri.IsDefaultPort
        && uri.Query.Length == 0
        && uri.AbsolutePath is "/genshin/achievements" or "/hsr/achievements"
        && uri.Fragment.StartsWith("#nyx-import=v1&port=", StringComparison.Ordinal)
        && uri.Fragment.Contains("&nonce=", StringComparison.Ordinal);
}
