using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;

namespace Nyx_Desktop_App;

internal enum EndfieldSiblingDiscoveryStatus
{
    ExistingRoot,
    NoCandidate,
    NoMatch,
    Uncertain,
    Ambiguous,
    Drifted,
    Cancelled,
    SaveFailed,
    Saved,
}

internal sealed record EndfieldSiblingDiscoveryResult(
    EndfieldSiblingDiscoveryStatus Status,
    string? SavedRoot = null);

/// <summary>
/// Derives only the exact GRYPHLINK sibling of already bounded WuWa or Genshin
/// roots. It never searches, enumerates, starts, or accepts an arbitrary suffix.
/// </summary>
internal sealed class EndfieldSiblingDiscoveryPolicy
{
    public EndfieldSiblingDiscoveryResult DiscoverAndSave(
        string? existingEndfieldRoot,
        string? validatedWuWaRoot,
        string? validatedGenshinGameRoot,
        Func<string, PublisherGameDirectLaunchResult> checkEndfield,
        Func<string, bool> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkEndfield);
        ArgumentNullException.ThrowIfNull(save);
        if (!string.IsNullOrWhiteSpace(existingEndfieldRoot))
        {
            return new(EndfieldSiblingDiscoveryStatus.ExistingRoot, existingEndfieldRoot);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new(EndfieldSiblingDiscoveryStatus.Cancelled);
        }

        var candidates = DeriveCandidates(validatedWuWaRoot, validatedGenshinGameRoot);
        if (candidates.Count == 0)
        {
            return new(EndfieldSiblingDiscoveryStatus.NoCandidate);
        }

        var matches = new List<string>();
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new(EndfieldSiblingDiscoveryStatus.Cancelled);
            }

            PublisherGameDirectLaunchResult result;
            try
            {
                result = checkEndfield(candidate);
            }
            catch (Exception exception) when (IsBoundaryFailure(exception))
            {
                return new(EndfieldSiblingDiscoveryStatus.Uncertain);
            }

            if (IsUncertain(result))
            {
                return new(EndfieldSiblingDiscoveryStatus.Uncertain);
            }

            if (result.Status is PublisherGameLaunchStatus.Ready
                or PublisherGameLaunchStatus.Running)
            {
                matches.Add(candidate);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new(EndfieldSiblingDiscoveryStatus.Cancelled);
        }

        if (matches.Count == 0)
        {
            return new(EndfieldSiblingDiscoveryStatus.NoMatch);
        }

        if (matches.Count != 1)
        {
            return new(EndfieldSiblingDiscoveryStatus.Ambiguous);
        }

        var selected = matches[0];
        PublisherGameDirectLaunchResult final;
        try
        {
            final = checkEndfield(selected);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(EndfieldSiblingDiscoveryStatus.Drifted);
        }

        if (final.Status is not PublisherGameLaunchStatus.Ready
            and not PublisherGameLaunchStatus.Running
            || IsUncertain(final))
        {
            return new(EndfieldSiblingDiscoveryStatus.Drifted);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new(EndfieldSiblingDiscoveryStatus.Cancelled);
        }

        try
        {
            return save(selected)
                ? new(EndfieldSiblingDiscoveryStatus.Saved, selected)
                : new(EndfieldSiblingDiscoveryStatus.SaveFailed);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return new(EndfieldSiblingDiscoveryStatus.SaveFailed);
        }
    }

    internal static IReadOnlyList<string> DeriveCandidates(
        string? validatedWuWaRoot,
        string? validatedGenshinGameRoot)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryNormalizeLocalCanonical(validatedWuWaRoot, out var wuwaRoot)
            && string.Equals(
                Path.GetFileName(wuwaRoot),
                "Wuthering Waves",
                StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(wuwaRoot) is { } wuwaLibrary)
        {
            candidates.Add(Path.Combine(wuwaLibrary, "GRYPHLINK"));
        }

        if (TryNormalizeLocalCanonical(validatedGenshinGameRoot, out var genshinGameRoot)
            && string.Equals(
                Path.GetFileName(genshinGameRoot),
                "Genshin Impact Game",
                StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(genshinGameRoot) is { } genshinProductRoot
            && string.Equals(
                Path.GetFileName(genshinProductRoot),
                "Genshin Impact",
                StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(genshinProductRoot) is { } genshinLibrary)
        {
            candidates.Add(Path.Combine(genshinLibrary, "GRYPHLINK"));
        }

        return candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryNormalizeLocalCanonical(string? value, out string root)
    {
        root = string.Empty;
        try
        {
            if (string.IsNullOrEmpty(value)
                || value.Length > 2048
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(value)
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

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!string.Equals(
                    canonical,
                    Path.TrimEndingDirectorySeparator(value),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            root = canonical;
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

    private static bool IsUncertain(PublisherGameDirectLaunchResult result) =>
        result.Bootstrap is RunningProcessStatus.Uncertain
        || result.Runtime is RunningProcessStatus.Uncertain
        || result.InspectionReason is PublisherGameInspectionReason.TargetChangedDuringInspection
            or PublisherGameInspectionReason.InspectionFailed;

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception;
}
