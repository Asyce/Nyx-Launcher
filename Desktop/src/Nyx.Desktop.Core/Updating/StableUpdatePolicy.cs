namespace Nyx.Desktop.Core.Updating;

public sealed record StableUpdateInstallation(
    string InstallRoot,
    string ControlUpdaterPath,
    string StagingRoot,
    string CurrentVersion);

public static class StableUpdatePolicy
{
    public static StableUpdateInstallation? FindInstalled(
        string baseDirectory,
        string localApplicationData,
        string releaseChannel,
        string currentVersion)
    {
        try
        {
            if (!string.Equals(releaseChannel, "stable", StringComparison.Ordinal)
                || !UpdateManifestReader.TryParseVersion(currentVersion))
            {
                return null;
            }

            var installRoot = Path.GetFullPath(Path.Combine(localApplicationData, "Programs", "Pengo Nyx"));
            var appRoot = Path.Combine(installRoot, "app");
            if (!string.Equals(
                    Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    appRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var controlUpdater = Path.Combine(installRoot, "control", "Nyx.Desktop.Update.exe");
            var stagingRoot = Path.Combine(installRoot, "staging");
            if (!File.Exists(controlUpdater)
                || HasReparseComponent(controlUpdater)
                || Directory.Exists(stagingRoot) && HasReparseComponent(stagingRoot))
            {
                return null;
            }

            return new(
                installRoot,
                controlUpdater,
                stagingRoot,
                currentVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsStrictUpgrade(string currentVersion, string targetVersion) =>
        UpdateManifestReader.TryParseVersion(currentVersion)
        && UpdateManifestReader.TryParseVersion(targetVersion)
        && Version.Parse(targetVersion) > Version.Parse(currentVersion);

    private static bool HasReparseComponent(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }
}
