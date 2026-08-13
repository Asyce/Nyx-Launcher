using Microsoft.Win32;
using Nyx.Desktop.Core.Genshin;
using System.Runtime.Versioning;

namespace Nyx.Desktop.Infrastructure.Genshin;

public enum GenshinRegistryRecord
{
    GenshinImpact,
    HoYoPlayGlobal,
}

public interface IGenshinRegistryReader
{
    IReadOnlyDictionary<string, string?> Read(GenshinRegistryRecord record);
}

public interface IGenshinCandidateInspector
{
    GenshinInspectionResult InspectGame(string root);

    GenshinInspectionResult InspectUpdater(string root);
}

public sealed record GenshinDiscoveryResult(string? GameRoot, string? UpdaterRoot);

public sealed class GenshinInspectionCandidateInspector(GenshinInspectionAdapter adapter)
    : IGenshinCandidateInspector
{
    private readonly GenshinInspectionAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));

    public GenshinInspectionResult InspectGame(string root) => adapter.InspectGame(root);

    public GenshinInspectionResult InspectUpdater(string root) => adapter.InspectUpdater(root);
}

public sealed class WindowsGenshinCandidateDiscovery(
    IGenshinRegistryReader registryReader,
    IGenshinCandidateInspector inspector)
{
    private readonly IGenshinRegistryReader registryReader =
        registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    private readonly IGenshinCandidateInspector inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    public GenshinDiscoveryResult Discover()
    {
        var gameRoot = DiscoverGame();
        var updaterRoot = DiscoverUpdater();
        return new(gameRoot, updaterRoot);
    }

    private string? DiscoverGame()
    {
        var values = registryReader.Read(GenshinRegistryRecord.GenshinImpact);
        if (!Matches(values, "GameBiz", "hk4e_global")
            || !Matches(values, "Channel", "1_0")
            || !Matches(values, "HoYoPlay", "V2")
            || !TryGetCanonicalLocalPath(values, "InstallPath", out var parentRoot))
        {
            return null;
        }

        var candidate = Path.Combine(parentRoot!, "Genshin Impact Game");
        var inspection = inspector.InspectGame(candidate);
        return inspection.Status is GenshinInspectionStatus.Ready
            && string.Equals(inspection.CanonicalRoot, candidate, StringComparison.OrdinalIgnoreCase)
            ? inspection.CanonicalRoot
            : null;
    }

    private string? DiscoverUpdater()
    {
        var values = registryReader.Read(GenshinRegistryRecord.HoYoPlayGlobal);
        if (!Matches(values, "GameBiz", "hk4e_global")
            || !Matches(values, "Region", "global")
            || !Matches(values, "ExeName", "launcher.exe")
            || !TryGetCanonicalLocalPath(values, "InstallPath", out var candidate))
        {
            return null;
        }

        var inspection = inspector.InspectUpdater(candidate!);
        return inspection.Status is GenshinInspectionStatus.Ready
            && string.Equals(inspection.CanonicalRoot, candidate, StringComparison.OrdinalIgnoreCase)
            ? inspection.CanonicalRoot
            : null;
    }

    private static bool Matches(
        IReadOnlyDictionary<string, string?> values,
        string name,
        string expected) =>
        values.TryGetValue(name, out var value)
        && string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetCanonicalLocalPath(
        IReadOnlyDictionary<string, string?> values,
        string name,
        out string? path)
    {
        path = null;
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(value)
                || value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.Length < 3
                || !char.IsAsciiLetter(value[0])
                || value[1] != Path.VolumeSeparatorChar
                || value[2] != Path.DirectorySeparatorChar)
            {
                return false;
            }

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!string.Equals(canonical, Path.TrimEndingDirectorySeparator(value), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = canonical;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMachineGenshinRegistryReader : IGenshinRegistryReader
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly IReadOnlyDictionary<GenshinRegistryRecord, RegistryContract> Contracts =
        new Dictionary<GenshinRegistryRecord, RegistryContract>
        {
            [GenshinRegistryRecord.GenshinImpact] = new(
                "Genshin Impact",
                ["InstallPath", "GameBiz", "Channel", "HoYoPlay"]),
            [GenshinRegistryRecord.HoYoPlayGlobal] = new(
                "HYP_1_0_global",
                ["InstallPath", "ExeName", "Region", "GameBiz"]),
        };

    public IReadOnlyDictionary<string, string?> Read(GenshinRegistryRecord record)
    {
        var contract = Contracts[record];
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey($@"{UninstallRoot}\{contract.SubkeyName}", writable: false);
            if (key is null)
            {
                return values;
            }

            foreach (var valueName in contract.ValueNames)
            {
                values[valueName] = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    as string;
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return values;
    }

    private sealed record RegistryContract(string SubkeyName, IReadOnlyList<string> ValueNames);
}
