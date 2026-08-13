using System.Runtime.Versioning;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Launching;

/// <summary>
/// Creates the production-only WuWa/Endfield direct-launch boundary without
/// exposing its protected-validation seam to App or other public callers.
/// </summary>
public static class PublisherGameDirectLaunchFactory
{
    [SupportedOSPlatform("windows")]
    public static PublisherGameDirectLaunchService Create() =>
        new(
            new PublisherGameDirectLaunchIdentityValidator(
                new WuWaIdentityAdapter(),
                new EndfieldIdentityAdapter()),
            new WindowsRunningProcessInspector(),
            new DotNetLaunchProcessStarter());
}
