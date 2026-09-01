using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

internal static class PublisherVisibleConnectNavigationPolicy
{
    internal static Uri HoyoLabGenshinLoginUri { get; } =
        new("https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys");

    internal static Uri HoyoLabHsrLoginUri { get; } =
        new("https://account.hoyolab.com/login-platform/index.html?st=https%3A%2F%2Fact.hoyolab.com%2Fapp%2Fcommunity-game-records-sea%2Frpg%2Findex.html%3Fhyl_auth_required%3Dtrue%23%2Fhsr&token_type=6&client_type=4&app_id=c9oqaq3s3gu8&game_biz=hkrpg_global&lang=en-us&theme=dark-hoyolab&hide_logo=0&ux_mode=popup&iframe_level=1#/password-login");

    public static Uri GetInitialUri(PublisherAccountCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Provider == "HoYoLAB"
            ? entry.GameId == "gi"
                ? HoyoLabGenshinLoginUri
                : entry.GameId == "hsr"
                    ? HoyoLabHsrLoginUri
                    : entry.ResourceUri
                        ?? throw new InvalidOperationException("No official account page is configured.")
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
                target)
            || purpose == PublisherSessionPurpose.Connect
                && provider == "HoYoLAB"
                && gameId == "gi"
                && target.IsAbsoluteUri
                && string.Equals(
                    target.OriginalString,
                    HoyoLabGenshinLoginUri.AbsoluteUri,
                    StringComparison.Ordinal)
            || purpose == PublisherSessionPurpose.Connect
                && provider == "HoYoLAB"
                && gameId == "hsr"
                && target.IsAbsoluteUri
                && string.Equals(
                    target.OriginalString,
                    HoyoLabHsrLoginUri.AbsoluteUri,
                    StringComparison.Ordinal);
    }

    public static bool IsAllowed(
        string provider,
        string gameId,
        Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return PublisherAccountCatalog.IsOfficialPublisherUri(provider, gameId, target);
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
