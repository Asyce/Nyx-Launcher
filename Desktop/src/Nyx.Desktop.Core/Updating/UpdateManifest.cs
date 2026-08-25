using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nyx.Desktop.Core.Updating;

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

public sealed record StableUpdateArtifactOwner(
    int SchemaVersion,
    int OwnerProcessId,
    long OwnerProcessStartedAtFileTime,
    string TargetVersion);

public sealed record StableUpdateArtifactNames(
    string OwnerFileName,
    string ManifestFileName,
    string PackageFileName,
    string IncomingDirectoryName,
    string ReadyDirectoryName);

public static class StableUpdateArtifactContract
{
    public const int MaximumOwnerBytes = 4 * 1024;

    private const string Prefix = "handoff-";
    private const string OwnerSuffix = ".owner.json";
    private const string ManifestSuffix = ".release.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static StableUpdateArtifactNames CreateNames(string id, string targetVersion)
    {
        if (!IsId(id) || !UpdateManifestReader.TryParseVersion(targetVersion))
            throw new UpdateContractException("StableArtifactMetadataInvalid");

        return new(
            $"{Prefix}{id}{OwnerSuffix}",
            $"{Prefix}{id}{ManifestSuffix}",
            $"{Prefix}{id}.package",
            $"stable-incoming-{id}",
            $"ready-{targetVersion}-{id}");
    }

    public static bool TryGetIdFromManifestFileName(string fileName, out string id) =>
        TryGetId(fileName, ManifestSuffix, out id);

    public static bool TryGetIdFromOwnerFileName(string fileName, out string id) =>
        TryGetId(fileName, OwnerSuffix, out id);

    public static byte[] SerializeOwner(StableUpdateArtifactOwner owner)
    {
        ValidateOwner(owner);
        return JsonSerializer.SerializeToUtf8Bytes(owner, Options);
    }

    public static StableUpdateArtifactOwner ParseOwner(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumOwnerBytes)
            throw new UpdateContractException("StableArtifactMetadataInvalid");

        var owner = JsonSerializer.Deserialize<StableUpdateArtifactOwner>(bytes, Options)
            ?? throw new UpdateContractException("StableArtifactMetadataInvalid");
        ValidateOwner(owner);
        return owner;
    }

    private static void ValidateOwner(StableUpdateArtifactOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.SchemaVersion != 1
            || owner.OwnerProcessId <= 0
            || owner.OwnerProcessStartedAtFileTime <= 0
            || !UpdateManifestReader.TryParseVersion(owner.TargetVersion))
        {
            throw new UpdateContractException("StableArtifactMetadataInvalid");
        }
    }

    private static bool TryGetId(string fileName, string suffix, out string id)
    {
        id = string.Empty;
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = fileName[Prefix.Length..^suffix.Length];
        if (!IsId(candidate)) return false;
        id = candidate;
        return true;
    }

    private static bool IsId(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static partial class UpdateManifestReader
{
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumFileCount = 8192;
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static UpdateReleaseManifest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new UpdateContractException("ManifestSizeInvalid");
        }

        var manifest = JsonSerializer.Deserialize<UpdateReleaseManifest>(bytes, Options)
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

            var normalized = RequireRelativeFile(file.Path);
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

    public static string RequireRelativeFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new UpdateContractException("UnsafeRelativePath");
        }

        var segments = relativePath.Split('/');
        if (segments.Length is <= 0 or > 32)
        {
            throw new UpdateContractException("UnsafeRelativePath");
        }

        foreach (var segment in segments)
        {
            if (segment.Length is <= 0 or > 128 || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(segment))
            {
                throw new UpdateContractException("UnsafeRelativePath");
            }
        }

        return string.Join('/', segments);
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

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
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
