using System.Runtime.Versioning;
using Microsoft.Win32;
using Nyx.Desktop.Core.Hoyo;

namespace Nyx.Desktop.Infrastructure.Hoyo;

public enum HoyoCurrentGameRecord
{
    HsrGlobal,
    ZzzGlobal,
}

internal sealed record HoyoRegistryCandidate(string? GameInstallPath, string? GameBiz);

internal interface IHoyoCurrentUserRegistryReader
{
    IReadOnlyList<HoyoRegistryCandidate> Read(HoyoCurrentGameRecord record);
}

internal interface IHoyoGameCandidateInspector
{
    HoyoGameInspectionResult Inspect(string gameId, string root);
}

internal sealed class HoyoGameCandidateInspector(HoyoGameIdentityAdapter adapter)
    : IHoyoGameCandidateInspector
{
    private readonly HoyoGameIdentityAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));

    public HoyoGameInspectionResult Inspect(string gameId, string root) =>
        adapter.Inspect(gameId, root);
}

public sealed class HoyoCurrentUserDiscovery
{
    private static readonly IReadOnlyDictionary<HoyoCurrentGameRecord, RecordProfile> Profiles =
        new Dictionary<HoyoCurrentGameRecord, RecordProfile>
        {
            [HoyoCurrentGameRecord.HsrGlobal] = new("hsr", "hkrpg_global"),
            [HoyoCurrentGameRecord.ZzzGlobal] = new("zzz", "nap_global"),
        };

    private readonly IHoyoCurrentUserRegistryReader registryReader;
    private readonly IHoyoGameCandidateInspector inspector;

    [SupportedOSPlatform("windows")]
    public HoyoCurrentUserDiscovery()
        : this(
            new WindowsHoyoCurrentUserRegistryReader(),
            new HoyoGameCandidateInspector(new HoyoGameIdentityAdapter()))
    {
    }

    internal HoyoCurrentUserDiscovery(
        IHoyoCurrentUserRegistryReader registryReader,
        IHoyoGameCandidateInspector inspector)
    {
        this.registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public HoyoGameInspectionResult Discover(HoyoCurrentGameRecord record)
    {
        var profile = Profiles[record];
        var candidates = registryReader.Read(record);
        if (candidates.Count == 0)
        {
            return Review(profile.GameId, HoyoInspectionReason.CurrentRecordMissing);
        }

        if (candidates.Count != 1)
        {
            return Review(profile.GameId, HoyoInspectionReason.AmbiguousCandidates);
        }

        var candidate = candidates[0];
        if (!string.Equals(candidate.GameBiz, profile.GameBiz, StringComparison.Ordinal))
        {
            return Review(profile.GameId, HoyoInspectionReason.CurrentRecordGameBizMismatch);
        }

        if (string.IsNullOrWhiteSpace(candidate.GameInstallPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.CurrentRecordMalformed);
        }

        var result = inspector.Inspect(profile.GameId, candidate.GameInstallPath);
        var candidatesAfterInspection = registryReader.Read(record);
        if (candidatesAfterInspection.Count != 1 || candidatesAfterInspection[0] != candidate)
        {
            return Review(
                profile.GameId,
                HoyoInspectionReason.TargetChangedDuringInspection,
                result.CanonicalRoot);
        }

        return result.Status is HoyoInspectionStatus.NotFound
            ? Review(profile.GameId, HoyoInspectionReason.CurrentRecordStale, result.CanonicalRoot)
            : result;
    }

    private static HoyoGameInspectionResult Review(
        string gameId,
        HoyoInspectionReason reason,
        string? root = null) =>
        new(gameId, HoyoInspectionStatus.NeedsReview, reason, root);

    private sealed record RecordProfile(string GameId, string GameBiz);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsHoyoCurrentUserRegistryReader : IHoyoCurrentUserRegistryReader
{
    private static readonly IReadOnlyDictionary<HoyoCurrentGameRecord, string> Subkeys =
        new Dictionary<HoyoCurrentGameRecord, string>
        {
            [HoyoCurrentGameRecord.HsrGlobal] = @"Software\Cognosphere\HYP\1_0\hkrpg_global",
            [HoyoCurrentGameRecord.ZzzGlobal] = @"Software\Cognosphere\HYP\1_0\nap_global",
        };

    public IReadOnlyList<HoyoRegistryCandidate> Read(HoyoCurrentGameRecord record)
    {
        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = currentUser.OpenSubKey(Subkeys[record], writable: false);
            if (key is null)
            {
                return [];
            }

            var installPath = key.GetValue(
                "GameInstallPath",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var gameBiz = key.GetValue(
                "GameBiz",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return [new(installPath, gameBiz)];
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return [];
        }
    }
}
