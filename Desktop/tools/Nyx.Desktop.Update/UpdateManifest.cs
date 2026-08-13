using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nyx.Desktop.Update;

public sealed record UpdateFileEntry(string Path, long Size, string Sha256);

public sealed record UpdateReleaseManifest(
    int SchemaVersion,
    string Product,
    string Channel,
    string Version,
    string Architecture,
    string PackageFile,
    long PackageSize,
    string PackageSha256,
    string EntryPoint,
    string? PackageUrl,
    IReadOnlyList<UpdateFileEntry> Files);

public static partial class UpdateManifestReader
{
    public const long MaximumManifestBytes = 4 * 1024 * 1024;
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumFileCount = 8192;
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static UpdateReleaseManifest Read(string manifestPath)
    {
        var safePath = SafePaths.RequireExistingFile(manifestPath);
        var info = new FileInfo(safePath);
        if (info.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new UpdateContractException("ManifestSizeInvalid");
        }

        using var stream = new FileStream(
            safePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var manifest = JsonSerializer.Deserialize<UpdateReleaseManifest>(stream, Options)
            ?? throw new UpdateContractException("ManifestMissing");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(UpdateReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1 || !string.Equals(manifest.Product, "nyx-desktop", StringComparison.Ordinal))
        {
            throw new UpdateContractException("IdentityInvalid");
        }

        if (manifest.Channel is not ("development" or "preview" or "stable"))
        {
            throw new UpdateContractException("ChannelInvalid");
        }

        if (!TryParseVersion(manifest.Version) || !string.Equals(manifest.Architecture, "win-x64", StringComparison.Ordinal))
        {
            throw new UpdateContractException("VersionInvalid");
        }

        var expectedPackageName = $"Nyx-Desktop-{manifest.Version}-win-x64.zip";
        if (!string.Equals(manifest.PackageFile, expectedPackageName, StringComparison.Ordinal)
            || manifest.PackageSize is <= 0 or > MaximumPackageBytes
            || !IsSha256(manifest.PackageSha256)
            || !string.Equals(manifest.EntryPoint, "Nyx.Desktop.App.exe", StringComparison.Ordinal))
        {
            throw new UpdateContractException("PackageInvalid");
        }

        ValidatePackageUrl(manifest);
        if (manifest.Files is null || manifest.Files.Count is <= 0 or > MaximumFileCount)
        {
            throw new UpdateContractException("FileSetInvalid");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new UpdateContractException("FileSetInvalid");
            }

            var normalized = SafePaths.RequireRelativeFile(file.Path);
            if (!string.Equals(normalized, file.Path, StringComparison.Ordinal)
                || file.Size is < 0 or > MaximumFileBytes
                || !IsSha256(file.Sha256)
                || !names.Add(file.Path)
                || (previous is not null && string.CompareOrdinal(previous, file.Path) >= 0))
            {
                throw new UpdateContractException("FileSetInvalid");
            }

            try
            {
                total = checked(total + file.Size);
            }
            catch (OverflowException)
            {
                throw new UpdateContractException("FileSetInvalid");
            }

            if (total > MaximumPackageBytes)
            {
                throw new UpdateContractException("FileSetInvalid");
            }

            previous = file.Path;
        }

        if (!names.Contains(manifest.EntryPoint))
        {
            throw new UpdateContractException("EntryPointMissing");
        }
    }

    public static bool IsSha256(string? value) =>
        value is not null && Sha256Regex().IsMatch(value);

    public static bool TryParseVersion(string? value)
    {
        if (value is null || !VersionRegex().IsMatch(value))
        {
            return false;
        }

        return value.Split('.').All(part => ushort.TryParse(part, out _));
    }

    private static void ValidatePackageUrl(UpdateReleaseManifest manifest)
    {
        if (manifest.PackageUrl is null)
        {
            if (!string.Equals(manifest.Channel, "development", StringComparison.Ordinal))
            {
                throw new UpdateContractException("PackageUrlMissing");
            }

            return;
        }

        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.IdnHost, "pengo.gg", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(
                uri.AbsolutePath,
                $"/desktop/updates/{manifest.Channel}/{manifest.PackageFile}",
                StringComparison.Ordinal))
        {
            throw new UpdateContractException("PackageUrlInvalid");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}

public sealed class UpdateContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
