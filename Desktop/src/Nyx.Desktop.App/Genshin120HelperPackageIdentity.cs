using System.Reflection;

namespace Nyx_Desktop_App;

internal static class Genshin120HelperPackageIdentity
{
    internal const string FileName = "Nyx.Genshin120.Helper.exe";

    internal static string Sha256 => typeof(Genshin120HelperPackageIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "PengoGenshin120HelperSha256")
        .Value ?? string.Empty;
}
