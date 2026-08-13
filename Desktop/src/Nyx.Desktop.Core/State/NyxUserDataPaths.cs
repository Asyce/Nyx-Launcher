namespace Nyx.Desktop.Core.State;

/// <summary>The single per-user data location shared by the app and updater.</summary>
public static class NyxUserDataPaths
{
    public const string PublisherDirectoryName = "Pengo";
    public const string ProductDirectoryName = "Nyx";

    public static string CanonicalRoot(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(
            Path.GetFullPath(localApplicationData),
            PublisherDirectoryName,
            ProductDirectoryName);
    }

    /// <summary>Pre-packaging builds stored state here. This is migration input only.</summary>
    public static string LegacyRoot(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(Path.GetFullPath(localApplicationData), ProductDirectoryName);
    }
}
