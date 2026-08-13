using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Update;

namespace Nyx.Desktop.Packaging.Tests;

internal sealed class PackageFixture : IDisposable
{
    public PackageFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "NyxPackagingTests", Guid.NewGuid().ToString("N"));
        Bundle = Path.Combine(Root, "bundle");
        Staging = Path.Combine(Root, "install", "staging");
        Directory.CreateDirectory(Bundle);
        Directory.CreateDirectory(Staging);
    }

    public string Root { get; }
    public string Bundle { get; }
    public string Staging { get; }
    public string PackagePath => Path.Combine(Bundle, "Nyx-Desktop-2.0.0.0-win-x64.zip");
    public string ManifestPath => Path.Combine(Bundle, "release.json");

    public UpdateReleaseManifest CreatePackage(
        IReadOnlyDictionary<string, string>? contents = null,
        IReadOnlyDictionary<string, string>? manifestContents = null,
        string channel = "development",
        string? packageUrl = null)
    {
        contents ??= new Dictionary<string, string>
        {
            ["Assets/data.txt"] = "payload",
            ["Nyx.Desktop.App.exe"] = "new-app",
        };
        manifestContents ??= contents;

        using (var stream = File.Create(PackagePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var pair in contents)
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
                entry.LastWriteTime = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(pair.Value);
            }
        }

        var files = manifestContents
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UpdateFileEntry(
                pair.Key,
                Encoding.UTF8.GetByteCount(pair.Value),
                Sha256(pair.Value)))
            .ToArray();
        var info = new FileInfo(PackagePath);
        var manifest = new UpdateReleaseManifest(
            1,
            "nyx-desktop",
            channel,
            "2.0.0.0",
            "win-x64",
            Path.GetFileName(PackagePath),
            info.Length,
            SafePaths.ComputeSha256(PackagePath),
            "Nyx.Desktop.App.exe",
            packageUrl,
            files);
        WriteManifest(manifest);
        return manifest;
    }

    public void WriteManifest(UpdateReleaseManifest manifest)
    {
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    public UpdateLayout CreateLayout()
    {
        var install = Path.Combine(Root, "installed");
        var data = Path.Combine(Root, "user-data");
        var legacyData = Path.Combine(Root, "legacy-user-data");
        var shortcut = Path.Combine(Root, "start-menu", "Nyx Desktop.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        return new UpdateLayout(install, data, legacyData, shortcut);
    }

    public string CreateReadyTree(UpdateReleaseManifest manifest)
    {
        var root = Path.Combine(Root, "installed", "staging", $"ready-{manifest.Version}");
        foreach (var file in manifest.Files)
        {
            var path = SafePaths.CombineUnder(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var value = file.Path == "Nyx.Desktop.App.exe" ? "new-app" : "payload";
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }

        return root;
    }

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
