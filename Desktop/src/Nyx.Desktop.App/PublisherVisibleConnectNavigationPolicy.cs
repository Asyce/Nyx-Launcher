using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

internal static class PublisherVisibleConnectNavigationPolicy
{
    internal static Uri HoyoLabLoginUri { get; } =
        new("https://account.hoyolab.com/login-platform/index.html?app_id=c9oqaq3s3gu8");

    public static Uri GetInitialUri(PublisherAccountCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Provider == "HoYoLAB"
            ? HoyoLabLoginUri
            : entry.CheckInUri ?? entry.ResourceUri
                ?? throw new InvalidOperationException("No official account page is configured.");
    }

    public static bool IsAllowedInitial(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            provider,
            purpose,
            gameId,
            target);
    }

    public static bool IsAllowed(
        string provider,
        string gameId,
        Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri
            || !string.Equals(
                target.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !target.IsDefaultPort
            || !string.IsNullOrEmpty(target.UserInfo))
            return false;

        if (provider == "HoYoLAB")
        {
            var host = target.Host;
            return host.Equals("hoyolab.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".hoyolab.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("hoyoverse.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".hoyoverse.com", StringComparison.OrdinalIgnoreCase);
        }

        return PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            provider,
            PublisherSessionPurpose.Connect,
            gameId,
            target);
    }

    public static bool IsAllowedPopup(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        string target,
        bool isUserInitiated) =>
        isUserInitiated
        && purpose == PublisherSessionPurpose.Connect
        && provider == "SKPORT"
        && gameId == "ae"
        && string.Equals(target, "about:blank", StringComparison.Ordinal);
}
