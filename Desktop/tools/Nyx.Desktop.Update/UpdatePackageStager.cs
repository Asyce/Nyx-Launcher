using System.IO.Compression;
using System.Security.Cryptography;

namespace Nyx.Desktop.Update;

public static class UpdatePackageStager
{
    public static void VerifyDownload(UpdateReleaseManifest manifest, string packagePath)
    {
        var verificationRoot = Path.Combine(
            Path.GetTempPath(),
            "Pengo",
            "NyxPackageVerification",
            Guid.NewGuid().ToString("N"));
        try
        {
            SafePaths.CreateDirectoryTree(verificationRoot);
            var staged = Stage(manifest, packagePath, verificationRoot);
            VerifyTree(manifest, staged);
        }
        finally
        {
            if (Directory.Exists(verificationRoot))
            {
                SafePaths.DeleteTreeWithoutFollowingLinks(verificationRoot);
            }
        }
    }

    public static string Stage(
        UpdateReleaseManifest manifest,
        string packagePath,
        string stagingRoot)
    {
        UpdateManifestReader.Validate(manifest);
        var safePackage = SafePaths.RequireExistingFile(packagePath);
        if (!string.Equals(Path.GetFileName(safePackage), manifest.PackageFile, StringComparison.Ordinal)
            || new FileInfo(safePackage).Length != manifest.PackageSize
            || !string.Equals(SafePaths.ComputeSha256(safePackage), manifest.PackageSha256, StringComparison.Ordinal))
        {
            throw new UpdateContractException("PackageHashMismatch");
        }

        var safeStagingRoot = SafePaths.CreateDirectoryTree(stagingRoot);
        var temporary = Path.Combine(safeStagingRoot, $"incoming-{Guid.NewGuid():N}");
        var ready = Path.Combine(safeStagingRoot, $"ready-{manifest.Version}");
        if (Directory.Exists(ready) || File.Exists(ready))
        {
            throw new UpdateContractException("StageAlreadyExists");
        }

        SafePaths.CreateDirectoryTree(temporary);
        try
        {
            ExtractAndVerify(manifest, safePackage, temporary);
            Directory.Move(temporary, ready);
            return ready;
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                SafePaths.DeleteTreeWithoutFollowingLinks(temporary);
            }

            throw;
        }
    }

    public static void VerifyTree(UpdateReleaseManifest manifest, string root)
    {
        var safeRoot = SafePaths.RequireExistingDirectory(root);
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(safeRoot, "*", SearchOption.AllDirectories).ToArray();
        if (actual.Length != expected.Count)
        {
            throw new UpdateContractException("StagedTreeMismatch");
        }

        foreach (var path in actual)
        {
            SafePaths.RequireNoReparseComponents(path);
            var relative = Path.GetRelativePath(safeRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!expected.TryGetValue(relative, out var file)
                || new FileInfo(path).Length != file.Size
                || !string.Equals(SafePaths.ComputeSha256(path), file.Sha256, StringComparison.Ordinal))
            {
                throw new UpdateContractException("StagedTreeMismatch");
            }
        }
    }

    private static void ExtractAndVerify(UpdateReleaseManifest manifest, string packagePath, string destination)
    {
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != expected.Count || archive.Entries.Count > UpdateManifestReader.MaximumFileCount)
        {
            throw new UpdateContractException("ArchiveEntrySetInvalid");
        }

        foreach (var entry in archive.Entries)
        {
            var relative = SafePaths.RequireRelativeFile(entry.FullName);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!string.Equals(relative, entry.FullName, StringComparison.Ordinal)
                || unixType == 0xA000
                || (((FileAttributes)entry.ExternalAttributes) & FileAttributes.ReparsePoint) != 0
                || !expected.TryGetValue(relative, out var expectedFile)
                || !seen.Add(relative)
                || entry.Length != expectedFile.Size)
            {
                throw new UpdateContractException("ArchiveEntryInvalid");
            }

            var outputPath = SafePaths.CombineUnder(destination, relative);
            SafePaths.CreateDirectoryTree(Path.GetDirectoryName(outputPath)!);
            using var input = entry.Open();
            using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written = checked(written + read);
                if (written > expectedFile.Size)
                {
                    throw new UpdateContractException("ArchiveEntryInvalid");
                }

                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }

            output.Flush(flushToDisk: true);
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (written != expectedFile.Size || !string.Equals(digest, expectedFile.Sha256, StringComparison.Ordinal))
            {
                throw new UpdateContractException("FileHashMismatch");
            }
        }

        if (seen.Count != expected.Count)
        {
            throw new UpdateContractException("ArchiveEntrySetInvalid");
        }
    }
}
