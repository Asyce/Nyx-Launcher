using System.Runtime.Versioning;
using Microsoft.Win32;
using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal interface IWuWaUninstallRegistryReader
{
    IReadOnlyDictionary<string, object?> Read();
}

internal sealed class WuWaMaintenanceCandidateSource(
    IWuWaUninstallRegistryReader registryReader,
    WuWaIdentityAdapter identityAdapter)
{
    private const int MaximumDisplayNameLength = 128;
    private const int MaximumPathLength = 2048;
    private const string ExpectedDisplayName = "Wuthering Waves";
    private readonly IWuWaUninstallRegistryReader registryReader =
        registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    private readonly WuWaIdentityAdapter identityAdapter =
        identityAdapter ?? throw new ArgumentNullException(nameof(identityAdapter));

    public PublisherGameInspectionResult Inspect() =>
        identityAdapter.InspectCandidates(ReadCandidateRoots());

    internal IReadOnlyList<string?> ReadCandidateRoots()
    {
        var values = registryReader.Read();
        if (!TryGetBoundedString(
                values,
                "DisplayName",
                MaximumDisplayNameLength,
                out var displayName)
            || !string.Equals(displayName, ExpectedDisplayName, StringComparison.Ordinal))
        {
            return [];
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetBoundedString(values, "InstallPath", MaximumPathLength, out var installPath)
            && TryNormalizeLocalRoot(installPath, launcherHint: false, out var installRoot))
        {
            roots.Add(installRoot!);
        }

        if (TryGetBoundedString(values, "LauncherPath", MaximumPathLength, out var launcherPath)
            && TryNormalizeLocalRoot(launcherPath, launcherHint: true, out var launcherRoot))
        {
            roots.Add(launcherRoot!);
        }

        return roots.Cast<string?>().ToArray();
    }

    private static bool TryGetBoundedString(
        IReadOnlyDictionary<string, object?> values,
        string name,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(name, out var raw)
            || raw is not string text
            || text.Length == 0
            || text.Length > maximumLength
            || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryNormalizeLocalRoot(
        string value,
        bool launcherHint,
        out string? root)
    {
        root = null;
        try
        {
            if (!Path.IsPathFullyQualified(value)
                || value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.StartsWith(@"\\?\", StringComparison.Ordinal)
                || value.StartsWith(@"\\.\", StringComparison.Ordinal)
                || value.Length < 3
                || !char.IsAsciiLetter(value[0])
                || value[1] != Path.VolumeSeparatorChar
                || value[2] != Path.DirectorySeparatorChar)
            {
                return false;
            }

            var canonical = Path.GetFullPath(value);
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(canonical),
                    Path.TrimEndingDirectorySeparator(value),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (launcherHint && Path.HasExtension(canonical))
            {
                if (!string.Equals(
                        Path.GetFileName(canonical),
                        "launcher.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                canonical = Path.GetDirectoryName(canonical)!;
            }

            root = Path.TrimEndingDirectorySeparator(canonical);
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
internal sealed class WindowsWuWaUninstallRegistryReader : IWuWaUninstallRegistryReader
{
    private const string ExactUninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KRInstall Wuthering Waves Overseas";
    private static readonly string[] ExactValueNames =
        ["DisplayName", "InstallPath", "LauncherPath"];

    public IReadOnlyDictionary<string, object?> Read()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = machine.OpenSubKey(ExactUninstallKey, writable: false);
            if (key is null)
            {
                return values;
            }

            foreach (var valueName in ExactValueNames)
            {
                values[valueName] = key.GetValue(
                    valueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return values;
    }
}

public sealed class WuWaMaintenanceService
{
    private readonly WuWaMaintenanceCandidateSource candidateSource;
    private readonly WuWaOfficialMaintenanceExecutor executor;

    [SupportedOSPlatform("windows")]
    public WuWaMaintenanceService()
        : this(
            new WuWaMaintenanceCandidateSource(
                new WindowsWuWaUninstallRegistryReader(),
                new WuWaIdentityAdapter()),
            new WuWaOfficialMaintenanceExecutor())
    {
    }

    internal WuWaMaintenanceService(
        WuWaMaintenanceCandidateSource candidateSource,
        WuWaOfficialMaintenanceExecutor executor)
    {
        this.candidateSource = candidateSource
            ?? throw new ArgumentNullException(nameof(candidateSource));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public WuWaOfficialMaintenanceResult Check()
    {
        var inspection = candidateSource.Inspect();
        if (inspection.Status is PublisherGameInspectionStatus.NotFound)
        {
            return new(
                WuWaOfficialMaintenanceStatus.NotFound,
                InspectionReason: inspection.Reason);
        }

        if (!inspection.HasFullInstallMaintenanceProof
            || inspection.MaintenanceTarget is null)
        {
            return new(
                WuWaOfficialMaintenanceStatus.NeedsReview,
                InspectionReason: inspection.Reason);
        }

        var request = OfficialMaintenanceHandoffFactory.Create(inspection.MaintenanceTarget);
        return executor.Check(request);
    }

    public WuWaOfficialMaintenanceResult Check(OfficialMaintenanceHandoffRequest request) =>
        executor.Check(request);

    public Task<WuWaOfficialMaintenanceResult> OpenOrObserveCurrentAsync(
        OfficialMaintenanceHandoffRequest request,
        CancellationToken cancellationToken = default) =>
        executor.OpenOrObserveCurrentAsync(request, cancellationToken);
}

/// <summary>
/// Supplies the one bounded install-root hint exposed by the exact public
/// Wuthering Waves uninstall record. It never accepts a registry key, game id,
/// path, or search root from its caller. The returned hint is not launch proof;
/// the direct-launch service revalidates the complete protected installation.
/// </summary>
public sealed class WuWaInstallRootLocator
{
    private readonly WuWaMaintenanceCandidateSource candidateSource;

    [SupportedOSPlatform("windows")]
    public WuWaInstallRootLocator()
        : this(new WuWaMaintenanceCandidateSource(
            new WindowsWuWaUninstallRegistryReader(),
            new WuWaIdentityAdapter()))
    {
    }

    internal WuWaInstallRootLocator(WuWaMaintenanceCandidateSource candidateSource)
    {
        this.candidateSource = candidateSource
            ?? throw new ArgumentNullException(nameof(candidateSource));
    }

    public string? LocateRoot()
    {
        var roots = candidateSource.ReadCandidateRoots();
        return roots.Count == 1 ? roots[0] : null;
    }
}
