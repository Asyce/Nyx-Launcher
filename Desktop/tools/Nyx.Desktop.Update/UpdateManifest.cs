using Nyx.Desktop.Core.Updating;

namespace Nyx.Desktop.Update;

internal static class UpdateManifestFile
{
    public static UpdateReleaseManifest Read(string manifestPath)
    {
        var safePath = SafePaths.RequireExistingFile(manifestPath);
        var info = new FileInfo(safePath);
        if (info.Length is <= 0 or > UpdateManifestReader.MaximumManifestBytes)
        {
            throw new UpdateContractException("ManifestSizeInvalid");
        }

        return UpdateManifestReader.Parse(File.ReadAllBytes(safePath));
    }
}
