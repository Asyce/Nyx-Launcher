using System.Reflection;

namespace Nyx_Desktop_App;

internal static class StableUpdateBuildIdentity
{
    public static string Channel { get; } = typeof(App).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "PengoReleaseChannel")?
        .Value ?? "development";

    public static Uri PengoSiteOrigin { get; } = Channel == "development"
        ? new("http://127.0.0.1:5173")
        : new("https://pengo.gg");

    public static string Version { get; } =
        typeof(App).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";
}
