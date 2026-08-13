using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Installations;

namespace Nyx.Desktop.Infrastructure.Installations;

public sealed class SandboxInstallationProbe
{
    private readonly string sandboxRoot;
    private readonly string sandboxRootWithSeparator;

    public SandboxInstallationProbe(string sandboxRoot)
    {
        this.sandboxRoot = NormalizeLocalDrivePath(sandboxRoot, nameof(sandboxRoot));
        EnsureNoReparsePoints(this.sandboxRoot, nameof(sandboxRoot));
        if (!Directory.Exists(this.sandboxRoot))
        {
            throw new DirectoryNotFoundException($"Sandbox root does not exist: {this.sandboxRoot}");
        }

        sandboxRootWithSeparator = this.sandboxRoot + Path.DirectorySeparatorChar;
    }

    public InstallationProbeResult Probe(string? gameId, string candidatePath)
    {
        var game = GameCatalog.GetRequired(gameId);

        var normalizedCandidate = NormalizeLocalDrivePath(candidatePath, nameof(candidatePath));
        EnsureContained(normalizedCandidate);
        EnsureNoReparsePoints(normalizedCandidate, nameof(candidatePath));

        var status = Directory.Exists(normalizedCandidate)
            ? InstallationStatus.Found
            : InstallationStatus.Missing;

        return new(game, normalizedCandidate, status);
    }

    private void EnsureContained(string candidatePath)
    {
        if (string.Equals(candidatePath, sandboxRoot, StringComparison.OrdinalIgnoreCase)
            || candidatePath.StartsWith(sandboxRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(candidatePath),
            candidatePath,
            "Installation probes are limited to the configured sandbox root.");
    }

    private static string NormalizeLocalDrivePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length < 3
            || !char.IsAsciiLetter(path[0])
            || path[1] != Path.VolumeSeparatorChar
            || (path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Only fully qualified local drive paths are allowed.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureNoReparsePoints(string path, string parameterName)
    {
        var driveRoot = Path.GetPathRoot(path)
            ?? throw new ArgumentException("The path does not have a local drive root.", parameterName);
        var relativePath = Path.GetRelativePath(driveRoot, path);
        var currentPath = driveRoot;

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);

            var entry = new DirectoryInfo(currentPath);
            if (entry.LinkTarget is not null)
            {
                throw new ArgumentException(
                    "Installation probes cannot follow links or junctions.",
                    parameterName);
            }

            entry.Refresh();
            if (!entry.Exists)
            {
                return;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Installation probes cannot follow links or junctions.",
                    parameterName);
            }
        }
    }
}
