using Nyx.Desktop.Core.Sessions;

namespace Nyx.Desktop.Core.Games;

/// <summary>A user-created game. Paths are retained exactly as selected after validation.</summary>
public sealed record CustomGameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ExecutablePath { get; init; }
    public required string IconPath { get; init; }
    public string? BackgroundPath { get; init; }
    public string? RuntimePath { get; init; }
    public string? RawArguments { get; init; }
    public bool RequestAdministrator { get; init; }
    public long CreationOrder { get; init; }
}

public sealed record CustomGameDraft(
    string Name,
    string ExecutablePath,
    string IconPath,
    string? BackgroundPath = null,
    string? RuntimePath = null,
    string? RawArguments = null,
    bool RequestAdministrator = false,
    string? Id = null,
    long CreationOrder = 0);

public enum CustomGameValidationError
{
    None,
    NameRequired,
    ExecutableRequired,
    IconRequired,
    BackgroundMissing,
    RuntimeMissing,
    ExecutableNotAbsoluteLocalPath,
    IconNotAbsoluteLocalPath,
    BackgroundNotAbsoluteLocalPath,
    RuntimeNotAbsoluteLocalPath,
    ExecutableMissing,
    ExecutableIsDirectory,
    ExecutableNotExe,
    IconMissing,
    IconIsDirectory,
    BackgroundMissingFile,
    BackgroundIsDirectory,
    RuntimeMissingFile,
    RuntimeIsDirectory,
    ReparsePoint,
    PathInspectionFailed,
    DuplicateExecutable,
    UnsafeArguments,
    InvalidId,
}

public sealed record CustomGameValidationResult(
    bool IsValid,
    CustomGameDefinition? Game,
    CustomGameValidationError Error = CustomGameValidationError.None,
    string? Message = null)
{
    public static CustomGameValidationResult Invalid(CustomGameValidationError error, string? message = null) =>
        new(false, null, error, message);
}

public interface ICustomGamePathProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool IsReparsePoint(string path);
    string GetCanonicalPath(string path);
}

/// <summary>The one accepted namespace for user-created game identifiers.</summary>
public static class CustomGameId
{
    public const string Prefix = "custom-";
    public const int MaximumLength = 80;

    public static bool IsValid(string? value) =>
        value is not null
        && value.Length > Prefix.Length
        && value.Length <= MaximumLength
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && !GameCatalog.TryGet(value, out _)
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-');
}

public static class CustomGameValidator
{
    private static readonly char[] ShellSyntax = ['&', '|', '<', '>', ';', '\r', '\n', '`', '$'];

    public static CustomGameValidationResult Validate(
        CustomGameDraft draft,
        IEnumerable<CustomGameDefinition>? existing = null,
        ICustomGamePathProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        probe ??= new PhysicalCustomGamePathProbe();
        if (string.IsNullOrWhiteSpace(draft.Name)) return CustomGameValidationResult.Invalid(CustomGameValidationError.NameRequired);
        if (string.IsNullOrWhiteSpace(draft.ExecutablePath)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ExecutableRequired);
        if (string.IsNullOrWhiteSpace(draft.IconPath)) return CustomGameValidationResult.Invalid(CustomGameValidationError.IconRequired);
        if (!IsSafeArguments(draft.RawArguments)
            || !CustomArgumentParser.TryParse(draft.RawArguments, out _))
            return CustomGameValidationResult.Invalid(CustomGameValidationError.UnsafeArguments);
        if (draft.Id is not null && !CustomGameId.IsValid(draft.Id))
            return CustomGameValidationResult.Invalid(CustomGameValidationError.InvalidId);

        try
        {
            var paths = new[]
            {
                (draft.ExecutablePath, CustomGameValidationError.ExecutableNotAbsoluteLocalPath),
                (draft.IconPath, CustomGameValidationError.IconNotAbsoluteLocalPath),
                (draft.BackgroundPath, CustomGameValidationError.BackgroundNotAbsoluteLocalPath),
                (draft.RuntimePath, CustomGameValidationError.RuntimeNotAbsoluteLocalPath),
            };
            foreach (var (path, error) in paths)
            {
                if (path is not null && !IsAbsoluteLocalPath(path))
                    return CustomGameValidationResult.Invalid(error);
            }

            var exe = Canonical(draft.ExecutablePath, probe);
            if (!IsAbsoluteLocalPath(exe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ExecutableNotAbsoluteLocalPath);
            if (!probe.FileExists(exe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ExecutableMissing);
            if (probe.DirectoryExists(exe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ExecutableIsDirectory);
            if (!string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase))
                return CustomGameValidationResult.Invalid(CustomGameValidationError.ExecutableNotExe);
            if (HasReparseComponent(exe, probe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ReparsePoint);

            var icon = Canonical(draft.IconPath, probe);
            if (!IsAbsoluteLocalPath(icon)) return CustomGameValidationResult.Invalid(CustomGameValidationError.IconNotAbsoluteLocalPath);
            if (!probe.FileExists(icon)) return CustomGameValidationResult.Invalid(CustomGameValidationError.IconMissing);
            if (probe.DirectoryExists(icon)) return CustomGameValidationResult.Invalid(CustomGameValidationError.IconIsDirectory);
            if (HasReparseComponent(icon, probe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ReparsePoint);

            string? background = null;
            if (draft.BackgroundPath is not null)
            {
                background = Canonical(draft.BackgroundPath, probe);
                if (!IsAbsoluteLocalPath(background)) return CustomGameValidationResult.Invalid(CustomGameValidationError.BackgroundNotAbsoluteLocalPath);
                if (!probe.FileExists(background)) return CustomGameValidationResult.Invalid(CustomGameValidationError.BackgroundMissingFile);
                if (probe.DirectoryExists(background)) return CustomGameValidationResult.Invalid(CustomGameValidationError.BackgroundIsDirectory);
                if (HasReparseComponent(background, probe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ReparsePoint);
            }

            string? runtime = null;
            if (draft.RuntimePath is not null)
            {
                runtime = Canonical(draft.RuntimePath, probe);
                if (!IsAbsoluteLocalPath(runtime)) return CustomGameValidationResult.Invalid(CustomGameValidationError.RuntimeNotAbsoluteLocalPath);
                if (!probe.FileExists(runtime)) return CustomGameValidationResult.Invalid(CustomGameValidationError.RuntimeMissingFile);
                if (probe.DirectoryExists(runtime)) return CustomGameValidationResult.Invalid(CustomGameValidationError.RuntimeIsDirectory);
                if (HasReparseComponent(runtime, probe)) return CustomGameValidationResult.Invalid(CustomGameValidationError.ReparsePoint);
            }

            if (existing is not null && existing.Any(game => string.Equals(
                Canonical(game.ExecutablePath, probe), exe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(game.Id, draft.Id, StringComparison.Ordinal)))
                return CustomGameValidationResult.Invalid(CustomGameValidationError.DuplicateExecutable);

            var id = draft.Id ?? GenerateId();
            var game = new CustomGameDefinition
            {
                Id = id,
                Name = draft.Name.Trim(),
                ExecutablePath = exe,
                IconPath = icon,
                BackgroundPath = background,
                RuntimePath = runtime,
                RawArguments = draft.RawArguments,
                RequestAdministrator = draft.RequestAdministrator,
                CreationOrder = draft.CreationOrder,
            };
            return new(true, game);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return CustomGameValidationResult.Invalid(CustomGameValidationError.PathInspectionFailed);
        }
    }

    public static CustomGameValidationResult Revalidate(
        CustomGameDefinition game,
        ICustomGamePathProbe? probe = null) =>
        Validate(new CustomGameDraft(
            game.Name,
            game.ExecutablePath,
            game.IconPath,
            game.BackgroundPath,
            game.RuntimePath,
            game.RawArguments,
            game.RequestAdministrator,
            game.Id,
            game.CreationOrder), probe: probe);

    public static string GenerateId() => $"custom-{Guid.NewGuid():N}";

    private static bool IsSafeArguments(string? arguments) =>
        arguments is null || arguments.IndexOfAny(ShellSyntax) < 0;

    private static bool IsAbsoluteLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            return false;
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }

    private static string Canonical(string path, ICustomGamePathProbe probe) =>
        probe.GetCanonicalPath(Path.GetFullPath(path));

    private static bool HasReparseComponent(string path, ICustomGamePathProbe probe)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new IOException("The custom game path has no local root.");
        var current = root;
        if (probe.IsReparsePoint(current)) return true;
        foreach (var segment in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (probe.IsReparsePoint(current)) return true;
        }

        return false;
    }

    internal sealed class PhysicalCustomGamePathProbe : ICustomGamePathProbe
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool IsReparsePoint(string path) =>
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        public string GetCanonicalPath(string path) => Path.GetFullPath(path);
    }
}

public interface ICustomGameProcessInspector
{
    ExactProcessPresence Check(string executablePath);
}

public interface ICustomGameProcessStarter
{
    void Start(string executablePath, IReadOnlyList<string> arguments, bool requestAdministrator);
}

/// <summary>Exact-path custom session boundary. It never matches by process name alone.</summary>
public sealed class CustomGameSessionAdapter : IGameSessionAdapter
{
    private readonly CustomGameDefinition game;
    private readonly ICustomGameProcessInspector inspector;
    private readonly ICustomGameProcessStarter starter;
    private readonly ICustomGamePathProbe pathProbe;

    public CustomGameSessionAdapter(
        CustomGameDefinition game,
        ICustomGameProcessInspector inspector,
        ICustomGameProcessStarter starter,
        ICustomGamePathProbe? pathProbe = null)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        if (!CustomGameId.IsValid(game.Id))
        {
            throw new ArgumentException("The custom game identifier is invalid.", nameof(game));
        }
        this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        this.starter = starter ?? throw new ArgumentNullException(nameof(starter));
        this.pathProbe = pathProbe ?? new CustomGameValidator.PhysicalCustomGamePathProbe();
        GameId = game.Id;
    }

    public string GameId { get; }

    public ValueTask<GameSessionEvidence> ObserveSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = CustomGameValidator.Revalidate(game, pathProbe);
        if (!validation.IsValid || validation.Game is null || !PathsMatch(game, validation.Game))
        {
            return ValueTask.FromResult(new GameSessionEvidence(
                LocalReadinessEvidence.NeedsReview,
                ExactProcessPresence.Absent,
                ExactProcessPresence.Absent));
        }

        try
        {
            var bootstrap = inspector.Check(validation.Game.ExecutablePath);
            var runtime = validation.Game.RuntimePath is null
                ? ExactProcessPresence.Absent
                : inspector.Check(validation.Game.RuntimePath);
            return ValueTask.FromResult(new GameSessionEvidence(LocalReadinessEvidence.Ready, bootstrap, runtime));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return ValueTask.FromResult(new GameSessionEvidence(
                LocalReadinessEvidence.NeedsReview,
                ExactProcessPresence.Uncertain,
                ExactProcessPresence.Uncertain));
        }
    }

    public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observation = ObserveSessionAsync(cancellationToken).Result;
        if (observation.Overall is ExactProcessPresence.Present)
            return ValueTask.FromResult(GameLaunchDispatchResult.Accepted);
        if (observation.Readiness is not LocalReadinessEvidence.Ready)
            return ValueTask.FromResult(GameLaunchDispatchResult.NeedsReview);
        var validation = CustomGameValidator.Revalidate(game, pathProbe);
        if (!validation.IsValid || validation.Game is null || !PathsMatch(game, validation.Game))
            return ValueTask.FromResult(GameLaunchDispatchResult.NeedsReview);
        try
        {
            starter.Start(
                validation.Game.ExecutablePath,
                CustomArgumentParser.Parse(validation.Game.RawArguments),
                validation.Game.RequestAdministrator);
            return ValueTask.FromResult(GameLaunchDispatchResult.Accepted);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return ValueTask.FromResult(GameLaunchDispatchResult.Failed);
        }
    }

    private static bool PathsMatch(CustomGameDefinition stored, CustomGameDefinition validated) =>
        string.Equals(stored.ExecutablePath, validated.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(stored.IconPath, validated.IconPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(stored.BackgroundPath, validated.BackgroundPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(stored.RuntimePath, validated.RuntimePath, StringComparison.OrdinalIgnoreCase);
}

public static class CustomArgumentParser
{
    public const int MaximumRawLength = 2048;
    public const int MaximumArgumentCount = 64;
    public const int MaximumArgumentLength = 1024;

    /// <summary>Splits Windows-style quoted arguments without invoking a shell.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (!TryParse(raw, out var arguments))
            throw new ArgumentException("The launch arguments are malformed or exceed the safe limits.", nameof(raw));
        return arguments;
    }

    public static bool TryParse(string? raw, out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();
        if (raw is null) return true;
        if (raw.Length > MaximumRawLength || raw.Any(char.IsControl)) return false;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '"') { quoted = !quoted; started = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                    if (result.Count > MaximumArgumentCount) return false;
                }
            }
            else
            {
                current.Append(ch);
                started = true;
                if (current.Length > MaximumArgumentLength) return false;
            }
        }
        if (quoted) return false;
        if (started) result.Add(current.ToString());
        if (!IsValid(result)) return false;
        arguments = Array.AsReadOnly(result.ToArray());
        return true;
    }

    public static bool IsValid(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count > MaximumArgumentCount) return false;
        var totalLength = Math.Max(0, arguments.Count - 1);
        foreach (var argument in arguments)
        {
            if (argument is null
                || argument.Length > MaximumArgumentLength
                || argument.Any(char.IsControl)
                || (totalLength += argument.Length) > MaximumRawLength)
                return false;
        }
        return true;
    }

    public static bool TryCombine(
        string? fixedArgument,
        IReadOnlyList<string>? userArguments,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();
        if (!IsValid(userArguments)) return false;
        if (fixedArgument is null)
        {
            arguments = userArguments!.Count == 0
                ? Array.Empty<string>()
                : Array.AsReadOnly(userArguments.ToArray());
            return true;
        }

        var combined = new string[userArguments!.Count + 1];
        combined[0] = fixedArgument;
        for (var i = 0; i < userArguments.Count; i++) combined[i + 1] = userArguments[i];
        arguments = Array.AsReadOnly(combined);
        return true;
    }
}
