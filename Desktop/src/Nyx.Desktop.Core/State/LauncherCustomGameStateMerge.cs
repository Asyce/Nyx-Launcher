using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.State;

/// <summary>
/// Custom-game collection edits that must run inside LauncherStateStore.Update.
/// Executable identity checks are path-only and never touch the file system.
/// </summary>
public static class LauncherCustomGameStateMerge
{
    public static LauncherState Add(LauncherState latest, CustomGameDefinition game)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(game);
        if (latest.CustomGames.Any(existing => string.Equals(existing.Id, game.Id, StringComparison.Ordinal)))
        {
            throw new CustomGameExecutableConflictException();
        }

        EnsureExecutableUnique(latest.CustomGames, game);
        return latest with
        {
            CustomGames = latest.CustomGames.Append(game).ToArray(),
            RailOrder = latest.RailOrder.Append(game.Id).ToArray(),
            SelectedGameId = game.Id,
        };
    }

    internal static void EnsureExecutableUnique(
        IEnumerable<CustomGameDefinition> games,
        CustomGameDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(candidate);
        var candidateIdentity = CanonicalExecutableIdentity(candidate.ExecutablePath);
        foreach (var game in games)
        {
            if (string.Equals(game.Id, candidate.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var existingIdentity = CanonicalExecutableIdentity(game.ExecutablePath);
            if (string.Equals(candidateIdentity, existingIdentity, StringComparison.OrdinalIgnoreCase))
            {
                throw new CustomGameExecutableConflictException();
            }
        }
    }

    internal static string CanonicalExecutableIdentity(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("The executable path is not an absolute local path.", nameof(path));
            }

            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (normalized.Length < 3
                || !char.IsAsciiLetter(normalized[0])
                || normalized[1] != ':'
                || normalized[2] != Path.DirectorySeparatorChar)
            {
                throw new ArgumentException("The executable path is not on a local drive.", nameof(path));
            }

            var segments = normalized[3..]
                .Split(Path.DirectorySeparatorChar)
                .Select(NormalizeWindowsSegment);
            var windowsNormalized = normalized[..3] + string.Join(Path.DirectorySeparatorChar, segments);
            return Path.GetFullPath(windowsNormalized)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            throw new CustomGameExecutableConflictException(exception);
        }
    }

    private static string NormalizeWindowsSegment(string segment)
    {
        var withoutTrailingSpaces = segment.TrimEnd(' ');
        return withoutTrailingSpaces is "." or ".."
            ? withoutTrailingSpaces
            : withoutTrailingSpaces.TrimEnd('.');
    }
}

public sealed class CustomGameExecutableConflictException : InvalidOperationException
{
    public CustomGameExecutableConflictException()
        : base("A custom game already owns that executable identity.")
    {
    }

    internal CustomGameExecutableConflictException(Exception innerException)
        : base("The custom game executable identity could not be validated.", innerException)
    {
    }
}
