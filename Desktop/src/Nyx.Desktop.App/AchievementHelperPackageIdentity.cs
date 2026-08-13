using System.Reflection;

namespace Nyx_Desktop_App;

internal static class AchievementHelperPackageIdentity
{
    internal static string Sha256 => typeof(AchievementHelperPackageIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "PengoAchievementHelperSha256")
        .Value ?? string.Empty;
}
