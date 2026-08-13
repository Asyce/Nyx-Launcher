namespace Nyx_Desktop_App;

internal sealed class UserAssetStore
{
    private static readonly HashSet<string> AllowedImageExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".ico"],
        StringComparer.OrdinalIgnoreCase);

    private readonly string root;

    public UserAssetStore(string launcherDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDataDirectory);
        root = Path.Combine(launcherDataDirectory, "UserAssets");
    }

    public string CopyImage(string gameId, string role, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists
            || source.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !AllowedImageExtensions.Contains(source.Extension))
        {
            throw new InvalidDataException("Choose a local PNG, JPG, WebP, or ICO image.");
        }

        var directory = Path.Combine(root, gameId);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{role}{source.Extension.ToLowerInvariant()}");
        var temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
