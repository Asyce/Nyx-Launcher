using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.Installations;

public enum InstallationStatus
{
    Missing,
    Found,
}

public sealed record InstallationProbeResult(
    GameDefinition Game,
    string CheckedPath,
    InstallationStatus Status);
