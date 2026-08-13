using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal sealed class PublisherGameDirectLaunchIdentityValidator
    : IPublisherGameDirectLaunchIdentityValidator
{
    private readonly WuWaIdentityAdapter wuwa;
    private readonly EndfieldIdentityAdapter endfield;

    public PublisherGameDirectLaunchIdentityValidator(
        WuWaIdentityAdapter wuwa,
        EndfieldIdentityAdapter endfield)
    {
        this.wuwa = wuwa ?? throw new ArgumentNullException(nameof(wuwa));
        this.endfield = endfield ?? throw new ArgumentNullException(nameof(endfield));
    }

    public IProtectedPublisherGameInspection InspectProtected(string gameId, string? root) =>
        gameId switch
        {
            "wuwa" => wuwa.InspectProtected(root),
            "ae" => endfield.InspectProtected(root),
            _ => throw new ArgumentOutOfRangeException(
                nameof(gameId),
                "Only sealed WuWa and Endfield profiles are supported."),
        };
}
