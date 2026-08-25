using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.AccountStatus;

public enum PublisherConnectionState
{
    NotConnected,
    Connecting,
    Connected,
    LoginRequired,
    NeedsReview,
}

public enum DailyCheckInState
{
    NotStarted,
    Opening,
    Checking,
    Claiming,
    Claimed,
    AlreadyClaimed,
    LoginNeeded,
    SelectionRequired,
    Unavailable,
    CouldNotCheck,
}

public enum PublisherResourceState
{
    NotStarted,
    Checking,
    Fresh,
    Stale,
    SelectionRequired,
    LoginRequired,
    NeedsReview,
    Unavailable,
}

public sealed record PublisherResourceSnapshot(
    string GameId,
    string ResourceName,
    int Current,
    int Maximum,
    DateTimeOffset ObservedAt,
    bool IsStale = false,
    int RecoverySeconds = 0,
    int? Reserve = null)
{
    public double Fraction => Maximum <= 0 ? 0 : Math.Clamp((double)Current / Maximum, 0, 1);
}

public sealed record DailyCheckInResult(
    string GameId,
    DailyCheckInState State,
    string Message,
    DateTimeOffset ObservedAt);

public sealed record PublisherAccountSummary(
    PublisherConnectionState HoyoLab,
    PublisherConnectionState Skport,
    IReadOnlyDictionary<string, PublisherResourceSnapshot> Resources,
    IReadOnlyDictionary<string, PublisherResourceState> ResourceStates,
    IReadOnlyDictionary<string, PublisherResourceCaptureDiagnostic> ResourceDiagnostics,
    IReadOnlyDictionary<string, DailyCheckInResult> CheckIns)
{
    public static PublisherAccountSummary Empty { get; } = new(
        PublisherConnectionState.NotConnected,
        PublisherConnectionState.NotConnected,
        new ReadOnlyDictionary<string, PublisherResourceSnapshot>(
            new Dictionary<string, PublisherResourceSnapshot>(StringComparer.Ordinal)),
        new ReadOnlyDictionary<string, PublisherResourceState>(
            new Dictionary<string, PublisherResourceState>(StringComparer.Ordinal)),
        new ReadOnlyDictionary<string, PublisherResourceCaptureDiagnostic>(
            new Dictionary<string, PublisherResourceCaptureDiagnostic>(StringComparer.Ordinal)),
        new ReadOnlyDictionary<string, DailyCheckInResult>(
            new Dictionary<string, DailyCheckInResult>(StringComparer.Ordinal)));
}

public sealed record PublisherAccountCatalogEntry(
    string GameId,
    Uri? CheckInUri,
    Uri? ResourceUri,
    string ResourceName)
{
    public string Provider => GameCatalog.GetRequired(GameId).AccountProvider
        ?? throw new InvalidOperationException($"Game '{GameId}' has no account provider.");

    public bool SupportsDailyCheckIn => GameCatalog.GetRequired(GameId).SupportsDailyCheckIn;

    public bool SupportsNumericResource => GameCatalog.GetRequired(GameId).SupportsNumericResource;
}

public sealed record PublisherEndfieldAccountIdentity(string Uid, string Region)
{
    public string DisplayText => string.IsNullOrEmpty(Uid) ? Region : $"{Uid} · {Region}";

    public override string ToString() => nameof(PublisherEndfieldAccountIdentity);
}

public sealed record PublisherEndfieldAccountReviewResult(
    PublisherConnectionState Connection,
    PublisherEndfieldAccountIdentity? Identity);

public static class PublisherEndfieldAccountIdentityParser
{
    public static bool TryCreateRegionOnly(
        string? region,
        out PublisherEndfieldAccountIdentity? identity)
    {
        identity = region switch
        {
            "Asia" or "Americas / Europe" => new(string.Empty, region),
            _ => null,
        };
        return identity is not null;
    }

    public static bool TryParseBindingResponse(
        ReadOnlyMemory<byte> utf8Json,
        out PublisherEndfieldAccountIdentity? identity)
    {
        identity = null;
        if (utf8Json.IsEmpty || utf8Json.Length > PublisherAccountCatalog.MaximumResourceResponseBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 10,
            });
            var root = document.RootElement;
            if (!TryGetUniqueProperty(root, "code", out var codeProperty)
                || codeProperty.ValueKind != JsonValueKind.Number
                || !codeProperty.TryGetInt32(out var code)
                || code != 0
                || !TryGetUniqueProperty(root, "data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                return false;

            PublisherEndfieldAccountIdentity? resolved = null;
            var found = false;
            if (!TryGetServerDefaultRole(data, out var preferredUid, out var preferredRoleId))
                return false;
            if (!TryGetOptionalUniqueProperty(data, "list", out var games, out var hasGames))
                return false;
            if (hasGames)
            {
                if (games.ValueKind != JsonValueKind.Array
                    || games.GetArrayLength() is < 1 or > 8)
                    return false;
                var matches = games.EnumerateArray()
                    .Where(static game => game.ValueKind == JsonValueKind.Object
                        && TryGetUniqueString(game, "appCode", out var appCode)
                        && appCode == "endfield")
                    .ToArray();
                if (matches.Length != 1
                    || !TryParseGameEntry(matches[0], preferredUid, preferredRoleId, out resolved))
                    return false;
                found = true;
            }

            if (!TryGetOptionalUniqueProperty(data, "gameMap", out var gameMap, out var hasGameMap))
                return false;
            if (hasGameMap)
            {
                if (gameMap.ValueKind != JsonValueKind.Object)
                    return false;
                if (!TryGetOptionalUniqueProperty(
                        gameMap,
                        "endfield",
                        out var endfield,
                        out var hasEndfield))
                    return false;
                if (hasEndfield)
                {
                    if (!TryParseGameEntry(endfield, preferredUid, preferredRoleId, out var mapped)
                        || (resolved is not null && resolved != mapped))
                        return false;
                    resolved = mapped;
                    found = true;
                }
            }

            identity = resolved;
            return found && identity is not null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseGameEntry(
        JsonElement game,
        string? preferredUid,
        string? preferredRoleId,
        out PublisherEndfieldAccountIdentity? identity)
    {
        identity = null;
        if (game.ValueKind != JsonValueKind.Object
            || !TryGetUniqueProperty(game, "bindingList", out var bindingsProperty)
            || bindingsProperty.ValueKind != JsonValueKind.Array
            || bindingsProperty.GetArrayLength() is < 1 or > 8)
            return false;
        var bindings = bindingsProperty.EnumerateArray().ToArray();
        var selected = default(JsonElement);
        if (preferredUid is not null)
        {
            var preferredBindings = bindings.Where(binding =>
                binding.ValueKind == JsonValueKind.Object
                && TryGetUniqueString(binding, "uid", out var uid)
                && uid == preferredUid).ToArray();
            if (preferredBindings.Length != 1) return false;
            selected = preferredBindings[0];
        }
        if (selected.ValueKind == JsonValueKind.Undefined)
        {
            selected = bindings[0];
            if (!HasTrue(selected, "isDefault"))
            {
                selected = bindings.FirstOrDefault(static binding => HasTrue(binding, "isDefault"));
                if (selected.ValueKind == JsonValueKind.Undefined)
                    selected = bindings.FirstOrDefault(static binding => HasTrue(binding, "isOfficial"));
                if (selected.ValueKind == JsonValueKind.Undefined)
                    selected = bindings.FirstOrDefault(HasBoundedRoles);
            }
        }
        if (!HasBoundedRoles(selected)
            || !TryGetUniqueProperty(selected, "roles", out var roles))
            return false;
        JsonElement selectedRole;
        if (preferredRoleId is not null)
        {
            var preferredRoles = roles.EnumerateArray().Where(role =>
                role.ValueKind == JsonValueKind.Object
                && TryGetUniqueString(role, "roleId", out var roleId)
                && roleId == preferredRoleId).ToArray();
            if (preferredRoles.Length != 1) return false;
            selectedRole = preferredRoles[0];
        }
        else if (!TryGetUniqueProperty(selected, "defaultRole", out selectedRole)
            || selectedRole.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!TryGetUniqueString(selectedRole, "roleId", out var roleId)
            || !TryGetUniqueString(selectedRole, "serverId", out var serverId)
            || roleId.Length is <= 0 or > 20
            || !roleId.All(char.IsAsciiDigit)
            || !IsAsciiIdentifier(serverId, 32))
            return false;
        var matchingRoles = roles.EnumerateArray().Count(role =>
            role.ValueKind == JsonValueKind.Object
            && TryGetUniqueString(role, "roleId", out var candidateRoleId)
            && TryGetUniqueString(role, "serverId", out var candidateServerId)
            && candidateRoleId == roleId
            && candidateServerId == serverId);
        if (matchingRoles != 1) return false;
        identity = new(roleId, serverId);
        return true;
    }

    private static bool TryGetServerDefaultRole(
        JsonElement data,
        out string? preferredUid,
        out string? preferredRoleId)
    {
        preferredUid = null;
        preferredRoleId = null;
        if (!TryGetOptionalUniqueProperty(
                data,
                "serverDefaultBinding",
                out var defaults,
                out var hasDefaults))
            return false;
        if (!hasDefaults)
            return true;
        JsonElement endfieldDefault = default;
        if (defaults.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetOptionalUniqueProperty(
                    defaults,
                    "3",
                    out endfieldDefault,
                    out _))
                return false;
        }
        else if (defaults.ValueKind == JsonValueKind.Array)
        {
            if (defaults.GetArrayLength() > 8) return false;
            if (defaults.GetArrayLength() > 3) endfieldDefault = defaults[3];
        }
        else
            return false;
        if (endfieldDefault.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (endfieldDefault.ValueKind != JsonValueKind.Object
            || !TryGetUniqueString(endfieldDefault, "uid", out var uid)
            || !TryGetUniqueString(endfieldDefault, "roleId", out var roleId)
            || uid.Length > 64
            || !uid.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_')
            || roleId.Length > 20
            || !roleId.All(char.IsAsciiDigit))
            return false;
        preferredUid = uid;
        preferredRoleId = roleId;
        return true;
    }

    private static bool HasBoundedRoles(JsonElement binding) =>
        binding.ValueKind == JsonValueKind.Object
        && TryGetUniqueProperty(binding, "roles", out var roles)
        && roles.ValueKind == JsonValueKind.Array
        && roles.GetArrayLength() is > 0 and <= 8;

    private static bool HasTrue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && TryGetUniqueProperty(element, name, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static bool TryGetUniqueProperty(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName)) continue;
            if (++count > 1) return false;
            value = property.Value;
        }
        return count == 1;
    }

    private static bool TryGetOptionalUniqueProperty(
        JsonElement parent,
        string propertyName,
        out JsonElement value,
        out bool present)
    {
        value = default;
        present = false;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName)) continue;
            if (present) return false;
            present = true;
            value = property.Value;
        }
        return true;
    }

    private static bool TryGetUniqueString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetUniqueProperty(element, name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsAsciiIdentifier(string value, int maximumLength) =>
        value.Length is > 0
        && value.Length <= maximumLength
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');
}

public static class PublisherResourceRefreshPolicy
{
    public static readonly TimeSpan SelectedInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackgroundInterval = SelectedInterval;

    public static bool IsFresh(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt <= now && now - observedAt < SelectedInterval;

    public static bool IsDue(
        DateTimeOffset? lastAttempt,
        DateTimeOffset now,
        bool selected) =>
        lastAttempt is null
        || now - lastAttempt >= (selected ? SelectedInterval : BackgroundInterval);
}

/// <summary>
/// A second, non-UI authorization boundary for publisher-account work. Unknown
/// providers always fail closed and revocation is visible to concurrent callers
/// before any cleanup begins.
/// </summary>
public sealed class PublisherAccountConsentGate(bool hoyoLabEnabled = false, bool skportEnabled = false)
{
    private int hoyoLab = hoyoLabEnabled ? 1 : 0;
    private int skport = skportEnabled ? 1 : 0;

    public bool IsEnabled(string provider) => provider switch
    {
        "HoYoLAB" => Volatile.Read(ref hoyoLab) == 1,
        "SKPORT" => Volatile.Read(ref skport) == 1,
        _ => false,
    };

    public bool Set(string provider, bool enabled)
    {
        var value = enabled ? 1 : 0;
        switch (provider)
        {
            case "HoYoLAB":
                Interlocked.Exchange(ref hoyoLab, value);
                return true;
            case "SKPORT":
                Interlocked.Exchange(ref skport, value);
                return true;
            default:
                return false;
        }
    }
}

public sealed record PublisherCheckInDomContract(
    string ReadySelector,
    string? ReceivedSelector = null);

public enum PublisherCheckInProof
{
    Invalid,
    LoginNeeded,
    Ready,
    Claimed,
    ClaimAccepted,
}

public enum PublisherCheckInCaptureDiagnostic
{
    None,
    TimedOutWithoutEndpoint,
    EndpointQueryRejected,
    InvalidStatusOrType,
    InvalidBody,
}

public sealed class PublisherCheckInCaptureDiagnosticGate
{
    private int selected;
    private int diagnostic;

    public PublisherCheckInCaptureDiagnostic Current =>
        (PublisherCheckInCaptureDiagnostic)Volatile.Read(ref diagnostic);

    public bool TryBeginSelectedResponse()
    {
        if (Interlocked.CompareExchange(ref selected, 1, 0) != 0)
            return false;
        Interlocked.Exchange(ref diagnostic, (int)PublisherCheckInCaptureDiagnostic.None);
        return true;
    }

    public void MarkCandidate(PublisherCheckInCaptureDiagnostic value)
    {
        if (value == PublisherCheckInCaptureDiagnostic.None || Volatile.Read(ref selected) != 0)
            return;
        Interlocked.Exchange(ref diagnostic, (int)value);
    }

    public void MarkSelectedResponse(PublisherCheckInCaptureDiagnostic value)
    {
        if (value == PublisherCheckInCaptureDiagnostic.None || Volatile.Read(ref selected) == 0)
            return;
        Interlocked.Exchange(ref diagnostic, (int)value);
    }
}

public enum PublisherResourceProof
{
    Invalid,
    LoginNeeded,
    Valid,
}

public enum PublisherSessionPurpose
{
    Connect,
    ConnectionProbe,
    CheckIn,
    Resource,
    Achievements,
}

public enum PublisherResourceReadOutcome
{
    Valid,
    SelectionRequired,
    LoginRequired,
    NeedsReview,
}

public enum PublisherResourceCaptureDiagnostic
{
    NotAvailable,
    NoAcceptedRequest,
    ResponseRejected,
    ResponseIncomplete,
    RequestRejected,
    PublisherResultRejected,
    EnvelopeRejected,
    DataRejected,
    CoreFieldsRejected,
    TimeFieldsRejected,
    ReserveRejected,
    BoundsRejected,
    SignatureRejected,
    BrowserRequestBlocked,
    OperationTimedOut,
    BrowserSessionUnavailable,
    LoginRequired,
    Valid,
    SelectionRequired,
}

public enum PublisherSessionProof
{
    Authenticated,
    LoginRequired,
    NeedsReview,
}

public enum PublisherWebResourceContext
{
    Document,
    Stylesheet,
    Image,
    Media,
    Font,
    Script,
    XmlHttpRequest,
    Fetch,
    Other,
}

public sealed record PublisherRoleBinding(string RoleId, string Server)
{
    // A debugger or accidental interpolation must not reveal the UID.
    public override string ToString() => nameof(PublisherRoleBinding);
}

public sealed record PublisherResourceFetchContract(
    string GameBusiness,
    Uri RoleDiscoveryEndpoint,
    Uri NoteEndpoint,
    IReadOnlyList<string> Regions)
{
    public override string ToString() => nameof(PublisherResourceFetchContract);
}

public sealed record PublisherRoleChoice(PublisherRoleBinding Binding, string DisplayText)
{
    public override string ToString() => nameof(PublisherRoleChoice);
}

// Nickname is a transient chooser hint. Persistence boundaries accept only
// PublisherRoleBinding, so this value never reaches settings or disk.
public sealed record PublisherResourceCandidate(
    PublisherRoleBinding Binding,
    PublisherResourceSnapshot? Snapshot,
    string? Nickname = null)
{
    public override string ToString() => nameof(PublisherResourceCandidate);
}

public sealed record PublisherResourceRoleIdentity(
    PublisherRoleBinding Binding,
    string? Nickname)
{
    public override string ToString() => nameof(PublisherResourceRoleIdentity);
}

public sealed record PublisherResourceTriggerResult(
    string State,
    IReadOnlyList<PublisherResourceRoleIdentity> Roles)
{
    public override string ToString() => nameof(PublisherResourceTriggerResult);
}

public static class PublisherResourceTriggerResultParser
{
    public const int MaximumPayloadCharacters = 4096;
    public const int MaximumNicknameUtf8Bytes = 64;
    private const int MaximumNicknameScalars = 32;
    private const int MaximumRoles = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryParse(
        string gameId,
        string raw,
        out PublisherResourceTriggerResult? result)
    {
        // This is the only host boundary for role identity data. The WebView
        // projects a small exact object; raw publisher envelopes never cross it.
        result = null;
        if (string.IsNullOrEmpty(raw)
            || raw.Length > MaximumPayloadCharacters)
            return false;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 5,
            });
            var root = document.RootElement;
            if (!HasExactProperties(root, "state", "roles")
                || !TryGetUniqueProperty(root, "state", out var stateProperty)
                || stateProperty.ValueKind != JsonValueKind.String)
                return false;
            var state = stateProperty.GetString();
            if (state is not ("running"
                or "done"
                or "login"
                or "invalid"
                or "no-roles"
                or "canceled"
                or "missing"
                or "signature-rejected"
                or "request-blocked"
                or "timed-out")
                || !TryGetUniqueProperty(root, "roles", out var rolesProperty)
                || rolesProperty.ValueKind != JsonValueKind.Array
                || rolesProperty.GetArrayLength() > MaximumRoles
                || (state != "done" && rolesProperty.GetArrayLength() != 0))
                return false;
            if ((state == "signature-rejected" && gameId != "hsr")
                || (state is "request-blocked" or "timed-out"
                    && gameId is not ("hsr" or "zzz")))
                return false;

            var roles = new List<PublisherResourceRoleIdentity>(
                rolesProperty.GetArrayLength());
            var seen = new HashSet<PublisherRoleBinding>();
            foreach (var item in rolesProperty.EnumerateArray())
            {
                if (!HasExactProperties(item, "region", "uid", "nickname")
                    || !TryGetUniqueString(item, "region", out var region)
                    || !TryGetUniqueString(item, "uid", out var uid)
                    || !TryGetUniqueProperty(item, "nickname", out var nicknameProperty))
                    return false;
                string? nickname = null;
                if (nicknameProperty.ValueKind == JsonValueKind.String)
                {
                    nickname = nicknameProperty.GetString();
                    if (!IsValidNickname(nickname)) return false;
                }
                else if (nicknameProperty.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }

                var binding = new PublisherRoleBinding(uid, region);
                if (!PublisherAccountCatalog.IsValidRoleBinding(gameId, binding)
                    || !seen.Add(binding))
                    return false;
                roles.Add(new(binding, nickname));
            }
            result = new(state, roles);
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or EncoderFallbackException
            or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValidNickname(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        int utf8Bytes;
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        if (utf8Bytes > MaximumNicknameUtf8Bytes) return false;

        var scalars = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed)
                != OperationStatus.Done)
                return false;
            scalars++;
            if (scalars > MaximumNicknameScalars) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(expected, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (!names.Remove(property.Name)) return false;
        }
        return count == expected.Length && names.Count == 0;
    }

    private static bool TryGetUniqueString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!TryGetUniqueProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetUniqueProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                continue;
            if (found) return false;
            found = true;
            value = property.Value;
        }
        return found;
    }
}

public sealed record PublisherResourceReadResult(
    PublisherResourceSnapshot? Snapshot,
    PublisherResourceReadOutcome Outcome,
    IReadOnlyList<PublisherResourceCandidate>? Candidates = null,
    PublisherResourceCaptureDiagnostic Diagnostic = PublisherResourceCaptureDiagnostic.NotAvailable)
{
    public bool LoginRequired => Outcome == PublisherResourceReadOutcome.LoginRequired;
    public bool NeedsReview => Outcome == PublisherResourceReadOutcome.NeedsReview;
}

public enum PublisherDailyRoleResolutionState
{
    Resolved,
    SelectionRequired,
    LoginRequired,
    NeedsReview,
}

public sealed record PublisherDailyRoleResolution(
    PublisherDailyRoleResolutionState State,
    PublisherRoleBinding? Binding,
    IReadOnlyList<PublisherRoleChoice> Choices,
    bool AccountWideStatusAllowed,
    bool StoredBindingStillMatches,
    bool StoredBindingWasProvenMissing)
{
    public override string ToString() => nameof(PublisherDailyRoleResolution);
}

public static class PublisherDailyRolePolicy
{
    public static PublisherDailyRoleResolution Resolve(
        string gameId,
        PublisherResourceReadResult resourceRead,
        PublisherRoleBinding? storedBinding,
        PublisherRoleBinding? explicitSelection = null)
    {
        ArgumentNullException.ThrowIfNull(resourceRead);
        if (resourceRead.Outcome == PublisherResourceReadOutcome.LoginRequired)
            return Result(PublisherDailyRoleResolutionState.LoginRequired);
        if (resourceRead.Outcome == PublisherResourceReadOutcome.NeedsReview)
            return Result(PublisherDailyRoleResolutionState.NeedsReview);

        var candidates = (resourceRead.Candidates ?? Array.Empty<PublisherResourceCandidate>())
            .Select(static candidate => candidate.Binding)
            .Where(binding => PublisherAccountCatalog.IsValidRoleBinding(gameId, binding))
            .Distinct()
            .OrderBy(static binding => binding.Server, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RoleId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length is < 1 or > 8)
            return Result(PublisherDailyRoleResolutionState.NeedsReview);

        var storedMatches = storedBinding is not null && candidates.Contains(storedBinding);
        var selectedMatches = explicitSelection is not null && candidates.Contains(explicitSelection);
        var binding = storedMatches
            ? storedBinding
            : candidates.Length == 1
                ? candidates[0]
                : selectedMatches
                    ? explicitSelection
                    : null;
        var choices = candidates.Length > 1
            ? PublisherAccountCatalog.CreateRoleChoices(
                gameId,
                resourceRead.Candidates ?? Array.Empty<PublisherResourceCandidate>())
            : Array.Empty<PublisherRoleChoice>();
        return binding is null
            ? new(
                PublisherDailyRoleResolutionState.SelectionRequired,
                null,
                choices,
                AccountWideStatusAllowed: false,
                StoredBindingStillMatches: storedMatches,
                StoredBindingWasProvenMissing: storedBinding is not null && !storedMatches)
            : new(
                PublisherDailyRoleResolutionState.Resolved,
                binding,
                choices,
                AccountWideStatusAllowed: candidates.Length == 1,
                StoredBindingStillMatches: storedMatches,
                StoredBindingWasProvenMissing: storedBinding is not null && !storedMatches);
    }

    public static PublisherResourceCaptureDiagnostic FinalDiagnostic(
        PublisherResourceReadResult resourceRead,
        PublisherDailyRoleResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resourceRead);
        ArgumentNullException.ThrowIfNull(resolution);
        return resolution.State == PublisherDailyRoleResolutionState.Resolved
            && resolution.Binding is not null
                ? PublisherResourceCaptureDiagnostic.Valid
                : resourceRead.Diagnostic;
    }

    private static PublisherDailyRoleResolution Result(
        PublisherDailyRoleResolutionState state) =>
        new(
            state,
            null,
            Array.Empty<PublisherRoleChoice>(),
            AccountWideStatusAllowed: false,
            StoredBindingStillMatches: false,
            StoredBindingWasProvenMissing: false);
}

public sealed class PublisherClaimWriteAuthority
{
    private readonly object sync = new();
    private long generation;
    private string? armedGameId;
    private bool scopeActive;

    public IDisposable Arm(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (sync)
        {
            if (scopeActive)
                throw new InvalidOperationException("A publisher claim write is already armed.");
            scopeActive = true;
            armedGameId = gameId;
            return new Scope(this, ++generation);
        }
    }

    public bool TryConsume(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (sync)
        {
            if (!string.Equals(armedGameId, gameId, StringComparison.Ordinal)) return false;
            armedGameId = null;
            return true;
        }
    }

    private void Revoke(long scopeGeneration)
    {
        lock (sync)
        {
            if (generation == scopeGeneration)
            {
                armedGameId = null;
                scopeActive = false;
            }
        }
    }

    private sealed class Scope(
        PublisherClaimWriteAuthority owner,
        long generation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Revoke(generation);
        }
    }
}

public sealed class PublisherResourceCaptureAuthority(
    string gameId,
    long generation,
    PublisherRoleBinding? expectedBinding = null)
{
    private const int MaximumRequests = 8;
    private readonly object sync = new();
    private readonly Dictionary<PublisherRoleBinding, int> pending = [];
    private readonly Dictionary<PublisherRoleBinding, int> processing = [];
    private readonly HashSet<PublisherRoleBinding> bindings = [];
    private readonly List<PublisherResourceCandidate> candidates = [];
    private int observedRequests;
    private int reserved;
    private int completed;
    private bool accepting;
    private bool sealedCapture;
    private bool overflow;
    private bool invalidProof;
    private PublisherResourceCaptureDiagnostic invalidDiagnostic =
        PublisherResourceCaptureDiagnostic.NotAvailable;
    private bool loginRequired;
    private bool roleDiscoveryLoginRequired;
    private bool canceled;

    public string GameId { get; } = gameId;
    public long Generation { get; } = generation;
    public bool HasExpectedBinding => expectedBinding is not null;
    public bool AllResponsesCompleted
    {
        get
        {
            lock (sync)
            {
                return reserved > 0
                    && pending.Count == 0
                    && processing.Count == 0
                    && completed == reserved;
            }
        }
    }

    public bool Open(long requestGeneration)
    {
        lock (sync)
        {
            if (requestGeneration != Generation || sealedCapture) return false;
            accepting = true;
            return true;
        }
    }

    public bool TryReserve(
        long requestGeneration,
        string requestGameId,
        PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (sync)
        {
            if (!accepting
                || sealedCapture
                || requestGeneration != Generation
                || !string.Equals(requestGameId, GameId, StringComparison.Ordinal))
                return false;

            observedRequests++;
            if (observedRequests > MaximumRequests)
            {
                overflow = true;
                return false;
            }
            bindings.Add(binding);
            if (expectedBinding is not null && binding != expectedBinding) return false;

            reserved++;
            pending[binding] = pending.GetValueOrDefault(binding) + 1;
            return true;
        }
    }

    public bool TryBeginResponse(long responseGeneration, PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (sync)
        {
            if (sealedCapture
                || responseGeneration != Generation
                || !pending.TryGetValue(binding, out var count)
                || count <= 0)
                return false;

            if (count == 1) pending.Remove(binding);
            else pending[binding] = count - 1;
            processing[binding] = processing.GetValueOrDefault(binding) + 1;
            return true;
        }
    }

    public bool CompleteResponse(
        long responseGeneration,
        PublisherRoleBinding binding,
        PublisherResourceProof proof,
        PublisherResourceSnapshot? snapshot,
        PublisherResourceCaptureDiagnostic failureDiagnostic =
            PublisherResourceCaptureDiagnostic.ResponseRejected)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (sync)
        {
            if (sealedCapture
                || responseGeneration != Generation
                || !processing.TryGetValue(binding, out var count)
                || count <= 0)
                return false;

            if (count == 1) processing.Remove(binding);
            else processing[binding] = count - 1;
            completed++;
            switch (proof)
            {
                case PublisherResourceProof.LoginNeeded:
                    loginRequired = true;
                    break;
                case PublisherResourceProof.Valid when snapshot is not null:
                    candidates.Add(new(binding, snapshot));
                    break;
                default:
                    invalidProof = true;
                    var fixedDiagnostic = IsFixedResponseFailure(failureDiagnostic)
                        ? failureDiagnostic
                        : PublisherResourceCaptureDiagnostic.ResponseRejected;
                    invalidDiagnostic = invalidDiagnostic
                        == PublisherResourceCaptureDiagnostic.NotAvailable
                            ? fixedDiagnostic
                            : invalidDiagnostic == fixedDiagnostic
                                ? invalidDiagnostic
                                : PublisherResourceCaptureDiagnostic.ResponseRejected;
                    break;
            }
            return true;
        }
    }

    public bool MarkRoleDiscoveryLoginRequired(long requestGeneration)
    {
        lock (sync)
        {
            if (!accepting
                || sealedCapture
                || requestGeneration != Generation)
                return false;
            roleDiscoveryLoginRequired = true;
            return true;
        }
    }

    public PublisherResourceReadResult SealTriggerFailure(
        long requestGeneration,
        PublisherResourceCaptureDiagnostic diagnostic)
    {
        lock (sync)
        {
            var bindingConflict = expectedBinding is not null
                && bindings.Any(binding => binding != expectedBinding);
            var gameAllowsDiagnostic = diagnostic switch
            {
                PublisherResourceCaptureDiagnostic.SignatureRejected =>
                    GameId == "hsr",
                PublisherResourceCaptureDiagnostic.BrowserRequestBlocked
                    or PublisherResourceCaptureDiagnostic.OperationTimedOut =>
                    GameId is "hsr" or "zzz",
                _ => false,
            };
            var accepted = IsFixedTriggerFailure(diagnostic)
                && gameAllowsDiagnostic
                && accepting
                && !sealedCapture
                && !canceled
                && requestGeneration == Generation
                && !overflow
                && !bindingConflict
                && !invalidProof
                && !loginRequired
                && !roleDiscoveryLoginRequired
                && completed == 0
                && processing.Count == 0
                && candidates.Count == 0;
            accepting = false;
            sealedCapture = true;
            return new(
                null,
                PublisherResourceReadOutcome.NeedsReview,
                Diagnostic: accepted
                    ? diagnostic
                    : PublisherResourceCaptureDiagnostic.ResponseRejected);
        }
    }

    public PublisherResourceReadResult Seal(long requestGeneration)
    {
        lock (sync)
        {
            accepting = false;
            sealedCapture = true;
            if (canceled
                || requestGeneration != Generation
                || overflow
                || (loginRequired && candidates.Count != 0))
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.NeedsReview,
                    Diagnostic: PublisherResourceCaptureDiagnostic.ResponseRejected);
            }
            if (invalidProof)
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.NeedsReview,
                    Diagnostic: loginRequired
                        || roleDiscoveryLoginRequired
                        || candidates.Count != 0
                        ? PublisherResourceCaptureDiagnostic.ResponseRejected
                        : invalidDiagnostic);
            }
            if (roleDiscoveryLoginRequired)
            {
                if (reserved != 0 || candidates.Count != 0)
                {
                    return new(
                        null,
                        PublisherResourceReadOutcome.NeedsReview,
                        Diagnostic: PublisherResourceCaptureDiagnostic.ResponseRejected);
                }
                return new(
                    null,
                    PublisherResourceReadOutcome.LoginRequired,
                    Diagnostic: PublisherResourceCaptureDiagnostic.LoginRequired);
            }
            if (reserved == 0)
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.NeedsReview,
                    Diagnostic: PublisherResourceCaptureDiagnostic.NoAcceptedRequest);
            }
            if (pending.Count != 0
                || processing.Count != 0
                || completed != reserved)
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.NeedsReview,
                    Diagnostic: PublisherResourceCaptureDiagnostic.ResponseIncomplete);
            }
            if (loginRequired)
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.LoginRequired,
                    Diagnostic: PublisherResourceCaptureDiagnostic.LoginRequired);
            }

            var snapshot = PublisherAccountCatalog.SelectUnambiguousResource(candidates);
            var captured = candidates
                .GroupBy(static candidate => candidate.Binding)
                .Select(static group => group.MaxBy(static candidate => candidate.Snapshot!.ObservedAt)!)
                .OrderBy(static candidate => candidate.Binding.Server, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.Binding.RoleId, StringComparer.Ordinal)
                .ToArray();
            if (snapshot is null && expectedBinding is null && captured.Length > 1)
            {
                return new(
                    null,
                    PublisherResourceReadOutcome.SelectionRequired,
                    captured,
                    PublisherResourceCaptureDiagnostic.SelectionRequired);
            }
            return snapshot is null
                ? new(
                    null,
                    PublisherResourceReadOutcome.NeedsReview,
                    Diagnostic: PublisherResourceCaptureDiagnostic.ResponseRejected)
                : new(
                    snapshot,
                    PublisherResourceReadOutcome.Valid,
                    captured,
                    PublisherResourceCaptureDiagnostic.Valid);
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            accepting = false;
            sealedCapture = true;
            canceled = true;
        }
    }

    private static bool IsFixedResponseFailure(
        PublisherResourceCaptureDiagnostic diagnostic) => diagnostic is
        PublisherResourceCaptureDiagnostic.RequestRejected
        or PublisherResourceCaptureDiagnostic.PublisherResultRejected
        or PublisherResourceCaptureDiagnostic.EnvelopeRejected
        or PublisherResourceCaptureDiagnostic.DataRejected
        or PublisherResourceCaptureDiagnostic.CoreFieldsRejected
        or PublisherResourceCaptureDiagnostic.TimeFieldsRejected
        or PublisherResourceCaptureDiagnostic.ReserveRejected
        or PublisherResourceCaptureDiagnostic.BoundsRejected
        or PublisherResourceCaptureDiagnostic.ResponseRejected;

    private static bool IsFixedTriggerFailure(
        PublisherResourceCaptureDiagnostic diagnostic) => diagnostic is
        PublisherResourceCaptureDiagnostic.SignatureRejected
        or PublisherResourceCaptureDiagnostic.BrowserRequestBlocked
        or PublisherResourceCaptureDiagnostic.OperationTimedOut;
}

public static class PublisherResourceTriggerPolicy
{
    public static PublisherResourceReadResult Seal(
        PublisherResourceCaptureAuthority authority,
        long generation,
        string? triggerState)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var triggerDiagnostic = (authority.GameId, triggerState) switch
        {
            ("hsr", "signature-rejected") =>
                PublisherResourceCaptureDiagnostic.SignatureRejected,
            ("hsr" or "zzz", "request-blocked") =>
                PublisherResourceCaptureDiagnostic.BrowserRequestBlocked,
            ("hsr" or "zzz", "timed-out") =>
                PublisherResourceCaptureDiagnostic.OperationTimedOut,
            _ => PublisherResourceCaptureDiagnostic.NotAvailable,
        };
        if (triggerDiagnostic != PublisherResourceCaptureDiagnostic.NotAvailable)
            return authority.SealTriggerFailure(generation, triggerDiagnostic);
        if (string.Equals(triggerState, "login", StringComparison.Ordinal))
            authority.MarkRoleDiscoveryLoginRequired(generation);
        else if (!string.Equals(triggerState, "done", StringComparison.Ordinal))
            authority.Cancel();
        return authority.Seal(generation);
    }

    public static PublisherResourceReadResult Seal(
        PublisherResourceCaptureAuthority authority,
        long generation,
        PublisherResourceTriggerResult? trigger)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (trigger is not null
            && trigger.State != "done"
            && trigger.Roles.Count != 0)
        {
            authority.Cancel();
            return authority.Seal(generation);
        }
        var result = Seal(authority, generation, trigger?.State);
        if (trigger?.State != "done"
            || result.Outcome is not (PublisherResourceReadOutcome.Valid
                or PublisherResourceReadOutcome.SelectionRequired))
            return result;

        if (authority.HasExpectedBinding)
            return trigger.Roles.Count == 0
                ? result
                : Rejected();
        if (trigger.Roles.Count is < 1 or > 8
            || result.Candidates is null)
            return Rejected();

        var seenIdentities = new HashSet<PublisherRoleBinding>();
        if (trigger.Roles.Any(role =>
            !PublisherAccountCatalog.IsValidRoleBinding(authority.GameId, role.Binding)
            || (role.Nickname is not null
                && !PublisherResourceTriggerResultParser.IsValidNickname(role.Nickname))
            || !seenIdentities.Add(role.Binding)))
            return Rejected();
        var identities = trigger.Roles.ToDictionary(
            static role => role.Binding,
            static role => role.Nickname);
        var capturedBindings = result.Candidates
            .Select(static candidate => candidate.Binding)
            .Distinct()
            .ToHashSet();
        if (identities.Count != capturedBindings.Count
            || identities.Keys.Any(binding => !capturedBindings.Contains(binding)))
            return Rejected();

        return result with
        {
            Candidates = result.Candidates
                .Select(candidate => candidate with
                {
                    Nickname = identities[candidate.Binding],
                })
                .ToArray(),
        };
    }

    private static PublisherResourceReadResult Rejected() =>
        new(
            null,
            PublisherResourceReadOutcome.NeedsReview,
            Diagnostic: PublisherResourceCaptureDiagnostic.ResponseRejected);
}

public enum PublisherProfileMutationCommitPoint
{
    Unchanged,
    MayHaveChanged,
    Deleted,
}

public readonly record struct PublisherProfileMutationSnapshot(
    long Revision,
    PublisherProfileMutationCommitPoint CommitPoint);

public sealed class PublisherProfileMutationJournal
{
    private readonly object sync = new();
    private long revision;
    private PublisherProfileMutationCommitPoint commitPoint;

    public PublisherProfileMutationSnapshot Capture()
    {
        lock (sync)
            return new(revision, commitPoint);
    }

    public void MarkMayHaveChanged()
    {
        lock (sync)
        {
            revision++;
            commitPoint = PublisherProfileMutationCommitPoint.MayHaveChanged;
        }
    }

    public void MarkDeleted()
    {
        lock (sync)
        {
            revision++;
            commitPoint = PublisherProfileMutationCommitPoint.Deleted;
        }
    }
}

public static class PublisherProfileCommitPolicy
{
    public static PublisherConnectionState ForCanceledConnect(
        PublisherConnectionState previousState,
        PublisherProfileMutationSnapshot initialProfile,
        PublisherProfileMutationSnapshot currentProfile)
    {
        if (currentProfile.Revision != initialProfile.Revision)
            return currentProfile.CommitPoint == PublisherProfileMutationCommitPoint.Deleted
                ? PublisherConnectionState.NotConnected
                : PublisherConnectionState.NeedsReview;
        return previousState == PublisherConnectionState.Connecting
            ? PublisherConnectionState.NotConnected
            : previousState;
    }

    public static bool MustCommitDeletedProfile(
        PublisherProfileMutationSnapshot initialProfile,
        PublisherProfileMutationSnapshot currentProfile) =>
        currentProfile.Revision != initialProfile.Revision
        && currentProfile.CommitPoint == PublisherProfileMutationCommitPoint.Deleted;

    public static bool TryGetInterruptedDisconnectState(
        PublisherProfileMutationSnapshot initialProfile,
        PublisherProfileMutationSnapshot currentProfile,
        out PublisherConnectionState terminalState)
    {
        terminalState = currentProfile.CommitPoint == PublisherProfileMutationCommitPoint.Deleted
            ? PublisherConnectionState.NotConnected
            : PublisherConnectionState.NeedsReview;
        return currentProfile.Revision != initialProfile.Revision;
    }
}

public sealed class PublisherConnectCancellationAuthority(
    long generation,
    PublisherConnectionState previousState,
    PublisherProfileMutationSnapshot initialProfile)
{
    private int available = 1;

    public long Generation { get; } = generation;

    public bool TryConsume(
        long currentGeneration,
        PublisherProfileMutationSnapshot currentProfile,
        out PublisherConnectionState terminalState)
    {
        terminalState = PublisherProfileCommitPolicy.ForCanceledConnect(
            previousState,
            initialProfile,
            currentProfile);
        return currentGeneration == Generation
            && Interlocked.Exchange(ref available, 0) == 1;
    }
}

public static class PublisherAccountStatePolicy
{
    public static PublisherConnectionState ForSessionProof(PublisherSessionProof proof) => proof switch
    {
        PublisherSessionProof.Authenticated => PublisherConnectionState.Connected,
        PublisherSessionProof.LoginRequired => PublisherConnectionState.LoginRequired,
        _ => PublisherConnectionState.NeedsReview,
    };

    public static PublisherConnectionState ForResourceRead(PublisherResourceReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            PublisherResourceReadOutcome.Valid when result.Snapshot is not null => PublisherConnectionState.Connected,
            PublisherResourceReadOutcome.SelectionRequired => PublisherConnectionState.Connected,
            PublisherResourceReadOutcome.LoginRequired => PublisherConnectionState.LoginRequired,
            _ => PublisherConnectionState.NeedsReview,
        };
    }

    public static PublisherConnectionState ForAuthenticatedResourceRead(
        PublisherResourceReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // The caller has already proved the shared publisher session. A
        // changed or blocked game-record page is a resource failure, not proof
        // that the account itself needs review.
        return result.Outcome == PublisherResourceReadOutcome.LoginRequired
            ? PublisherConnectionState.LoginRequired
            : PublisherConnectionState.Connected;
    }

    public static PublisherConnectionState? ForCheckIn(DailyCheckInState state) => state switch
    {
        DailyCheckInState.Claimed or DailyCheckInState.AlreadyClaimed => PublisherConnectionState.Connected,
        DailyCheckInState.LoginNeeded => PublisherConnectionState.LoginRequired,
        // A single game's page or claim flow can change independently of the
        // shared publisher login. Keep that failure on the game result so the
        // remaining games still run and Review is not shown unnecessarily.
        DailyCheckInState.CouldNotCheck => null,
        _ => null,
    };
}

public static class PublisherAccountPresentation
{
    public static string? ResourceCaptureGuidance(
        PublisherResourceCaptureDiagnostic diagnostic) => diagnostic switch
        {
            PublisherResourceCaptureDiagnostic.NoAcceptedRequest =>
                "OFFICIAL REQUEST NOT SEEN · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.ResponseRejected =>
                "RESPONSE NOT ACCEPTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.ResponseIncomplete =>
                "RESPONSE INCOMPLETE · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.RequestRejected =>
                "REQUEST REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.PublisherResultRejected =>
                "PUBLISHER RESULT REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.EnvelopeRejected =>
                "RESPONSE ENVELOPE REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.DataRejected =>
                "RESPONSE DATA REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.CoreFieldsRejected =>
                "RESOURCE FIELDS REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.TimeFieldsRejected =>
                "RECOVERY FIELDS REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.ReserveRejected =>
                "RESERVE FIELD REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.BoundsRejected =>
                "VALUE BOUNDS REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.SignatureRejected =>
                "SIGNATURE REJECTED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.BrowserRequestBlocked =>
                "BROWSER REQUEST BLOCKED · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.OperationTimedOut =>
                "OPERATION TIMED OUT · TRY AGAIN",
            PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable =>
                "BROWSER CLOSED · RESTART NYX",
            PublisherResourceCaptureDiagnostic.LoginRequired => "SIGN IN AGAIN",
            PublisherResourceCaptureDiagnostic.SelectionRequired => "CHOOSE REGION",
            _ => null,
        };

    public static bool IsCurrentDayCheckIn(DailyCheckInResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ObservedAt.ToOffset(now.Offset).Date == now.Date;
    }
}

public static class PublisherResourceTeardownDiagnosticPolicy
{
    public static PublisherResourceCaptureDiagnostic ForQuarantine(
        string gameId,
        PublisherResourceCaptureDiagnostic priorDiagnostic,
        bool preservePriorEvidence)
    {
        if (gameId is not ("hsr" or "zzz"))
            return PublisherResourceCaptureDiagnostic.NotAvailable;
        if (!preservePriorEvidence)
            return PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable;
        return priorDiagnostic switch
        {
            PublisherResourceCaptureDiagnostic.NoAcceptedRequest
                or PublisherResourceCaptureDiagnostic.ResponseRejected
                or PublisherResourceCaptureDiagnostic.ResponseIncomplete
                or PublisherResourceCaptureDiagnostic.RequestRejected
                or PublisherResourceCaptureDiagnostic.PublisherResultRejected
                or PublisherResourceCaptureDiagnostic.EnvelopeRejected
                or PublisherResourceCaptureDiagnostic.DataRejected
                or PublisherResourceCaptureDiagnostic.CoreFieldsRejected
                or PublisherResourceCaptureDiagnostic.TimeFieldsRejected
                or PublisherResourceCaptureDiagnostic.ReserveRejected
                or PublisherResourceCaptureDiagnostic.BoundsRejected
                or PublisherResourceCaptureDiagnostic.SignatureRejected
                or PublisherResourceCaptureDiagnostic.BrowserRequestBlocked
                or PublisherResourceCaptureDiagnostic.OperationTimedOut
                or PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable
                or PublisherResourceCaptureDiagnostic.LoginRequired =>
                    priorDiagnostic,
            _ => PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable,
        };
    }
}

public sealed class PublisherSingleFlight<T>
{
    private readonly object sync = new();
    private Task<T>? current;

    public Task<T> RunAsync(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken operationCancellation,
        CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task<T> shared;
        lock (sync)
        {
            if (current is null || current.IsCompleted)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                current = completion.Task;
                _ = CompleteAsync(operation, operationCancellation, completion);
            }
            shared = current;
        }
        return observerCancellation.CanBeCanceled
            ? shared.WaitAsync(observerCancellation)
            : shared;
    }

    private static async Task CompleteAsync(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken operationCancellation,
        TaskCompletionSource<T> completion)
    {
        try
        {
            completion.TrySetResult(await operation(operationCancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}

public sealed class PublisherGeneration
{
    private long value;

    public long Current => Interlocked.Read(ref value);

    public long Advance() => Interlocked.Increment(ref value);

    public bool IsCurrent(long generation) => generation == Current;

    public bool CanPublish(long generation, CancellationToken cancellationToken = default) =>
        !cancellationToken.IsCancellationRequested && IsCurrent(generation);
}

public static class PublisherAccountCatalog
{
    public const int MaximumResourceResponseBytes = 64 * 1024;
    public const int MaximumConnectRequestBodyBytes = 16 * 1024;
    private static readonly TimeSpan EndfieldMaximumPastSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EndfieldMaximumFutureSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EndfieldAsiaServerOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan EndfieldAmericasEuropeServerOffset = TimeSpan.FromHours(-5);
    private static readonly Uri SkportSessionProbeUri =
        new("https://web-api.skport.com/cookie_store/account_token");
    private static readonly Uri EndfieldAccountIdentityUri =
        new("https://zonai.skport.com/api/v1/game/player/binding");

    private sealed record CheckInResponseEndpoint(
        Uri InfoUri,
        Uri ClaimUri,
        string? ActId,
        IReadOnlySet<string>? Servers);

    private static readonly IReadOnlyDictionary<string, CheckInResponseEndpoint> CheckInResponseEndpoints =
        new ReadOnlyDictionary<string, CheckInResponseEndpoint>(
            new Dictionary<string, CheckInResponseEndpoint>(StringComparer.Ordinal)
            {
                // Reviewed from the official production Genshin sign-in bundle
                // on 2026-08-02. Keep the retired sg-hk4e API host denied.
                ["gi"] = new(
                    new("https://sg-act-public-api.hoyolab.com/event/sol/info"),
                    new("https://sg-act-public-api.hoyolab.com/event/sol/sign"),
                    "e202102251931481",
                    new HashSet<string>(["os_usa", "os_euro", "os_asia", "os_cht"], StringComparer.Ordinal)),
                ["hsr"] = new(
                    new("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"),
                    new("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/sign"),
                    "e202303301540311",
                    new HashSet<string>(["prod_official_usa", "prod_official_eur", "prod_official_asia", "prod_official_cht"], StringComparer.Ordinal)),
                ["zzz"] = new(
                    new("https://sg-act-public-api.hoyolab.com/event/luna/zzz/os/info"),
                    new("https://sg-act-public-api.hoyolab.com/event/luna/zzz/os/sign"),
                    "e202406031448091",
                    new HashSet<string>(["prod_gf_us", "prod_gf_eu", "prod_gf_jp", "prod_gf_sg"], StringComparer.Ordinal)),
                ["ae"] = new(
                    new("https://zonai.skport.com/web/v1/game/endfield/attendance"),
                    new("https://zonai.skport.com/web/v1/game/endfield/attendance"),
                    null,
                    null),
            });

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Uri>> RetiredCheckInResponseEndpoints =
        new ReadOnlyDictionary<string, IReadOnlyList<Uri>>(
            new Dictionary<string, IReadOnlyList<Uri>>(StringComparer.Ordinal)
            {
                ["gi"] =
                [
                    new("https://sg-hk4e-api.hoyolab.com/event/sol/info"),
                    new("https://sg-hk4e-api.hoyolab.com/event/sol/sign"),
                ],
            });

    private static readonly IReadOnlyDictionary<string, Uri> ResourceResponseEndpoints =
        new ReadOnlyDictionary<string, Uri>(
            new Dictionary<string, Uri>(StringComparer.Ordinal)
            {
                ["gi"] = new("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote"),
                ["hsr"] = new("https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note"),
                ["zzz"] = new("https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note"),
            });

    private static readonly Uri HsrAchievementPageUri =
        new("https://act.hoyolab.com/sr/event/cultivation-tool/index.html?game_biz=hkrpg_global&hyl_auth_required=true#/tools/achievement");

    private static readonly Uri HsrRoleDiscoveryEndpoint =
        new("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken");

    private static readonly Uri HsrRetiredRoleDiscoveryEndpoint =
        new("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken");

    private static readonly IReadOnlyDictionary<string, string> ResourceGameBusinesses =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gi"] = "hk4e_global",
                ["hsr"] = "hkrpg_global",
                ["zzz"] = "nap_global",
            });

    private static readonly Uri HsrAchievementLoginEndpoint =
        new("https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account");

    private static readonly Uri HsrAchievementFallbackLoginEndpoint =
        new("https://sg-public-api.hoyolab.com/common/badge/v1/login/info");

    private static readonly Uri HsrAchievementListEndpoint =
        new("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list");

    private static readonly Uri HsrAchievementFallbackListEndpoint =
        new("https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list");

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ResourceServers =
        new ReadOnlyDictionary<string, IReadOnlySet<string>>(
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["gi"] = new HashSet<string>(["os_usa", "os_euro", "os_asia", "os_cht"], StringComparer.Ordinal),
                ["hsr"] = new HashSet<string>(["prod_official_usa", "prod_official_eur", "prod_official_asia", "prod_official_cht"], StringComparer.Ordinal),
                ["zzz"] = new HashSet<string>(["prod_gf_us", "prod_gf_eu", "prod_gf_jp", "prod_gf_sg"], StringComparer.Ordinal),
            });

    // These selectors are compiled from the reviewed official pages. If a
    // publisher changes its markup, Nyx stops instead of guessing another
    // clickable element. Updating them requires a source review and a rebuild.
    private static readonly IReadOnlyDictionary<string, PublisherCheckInDomContract> CheckInDomContracts =
        new ReadOnlyDictionary<string, PublisherCheckInDomContract>(
            new Dictionary<string, PublisherCheckInDomContract>(StringComparer.Ordinal)
            {
                ["gi"] = new(
                    ".components-home-assets-__sign-content_---sign-item---k8WFIr.components-home-assets-__sign-content_---sign-wrapper---38rWqB:not(.components-home-assets-__sign-content_---has-signed---2brETR)"
                    + ",.components-home-assets-__sign-content-test_---sign-item---3gtMqV.components-home-assets-__sign-content-test_---sign-wrapper---22GpLY:not(.components-home-assets-__sign-content-test_---has-signed---1--Ffl)"
                    + ",.components-m-assets-__index_---sign-item---2jh3xA.components-m-assets-__index_---sign-wrapper---3WcYRI:not(.components-m-assets-__index_---has-signed---ALNJsm)"),
                ["hsr"] = new(
                    ".components-pc-assets-__prize-list_---item---F852VZ",
                    ".components-pc-assets-__prize-list_---received---tOZ4Gy"),
                ["zzz"] = new(
                    ".components-pc-assets-__prize-list_---item---F852VZ",
                    ".components-pc-assets-__prize-list_---received---tOZ4Gy"),
            });

    private static readonly IReadOnlyDictionary<string, PublisherAccountCatalogEntry> Entries =
        new ReadOnlyDictionary<string, PublisherAccountCatalogEntry>(
            new Dictionary<string, PublisherAccountCatalogEntry>(StringComparer.Ordinal)
            {
                ["gi"] = new("gi",
                    new Uri("https://act.hoyolab.com/ys/event/signin-sea-v3/index.html?act_id=e202102251931481"),
                    new Uri("https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys/realtime"),
                    "Original Resin"),
                ["hsr"] = new("hsr",
                    new Uri("https://act.hoyolab.com/bbs/event/signin/hkrpg/e202303301540311.html?act_id=e202303301540311&lang=en-us"),
                    new Uri("https://act.hoyolab.com/app/community-game-records-sea/rpg/index.html#/hsr"),
                    "Trailblaze Power"),
                ["zzz"] = new("zzz",
                    new Uri("https://act.hoyolab.com/bbs/event/signin/zzz/e202406031448091.html?act_id=e202406031448091&lang=en-us"),
                    new Uri("https://act.hoyolab.com/app/zzz-game-record/index.html#/zzz"),
                    "Battery Charge"),
                ["wuwa"] = new("wuwa", null, null, "Waveplates"),
                ["ae"] = new("ae",
                    new Uri("https://game.skport.com/endfield/sign-in"),
                    new Uri("https://game.skport.com/endfield/game-data?header=0"),
                    "Sanity"),
            });

    public static IReadOnlyCollection<PublisherAccountCatalogEntry> All => Entries.Values.ToArray();

    public static PublisherAccountCatalogEntry Get(string gameId) =>
        Entries.TryGetValue(gameId, out var entry)
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(gameId));

    public static bool IsExactCheckInUri(string gameId, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var expected = Get(gameId).CheckInUri;
        return expected is not null
            && uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.Equals(NormalizeTopLevelUri(uri), NormalizeTopLevelUri(expected), StringComparison.Ordinal);
    }

    public static bool IsExactResourcePageUri(string gameId, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var expected = Get(gameId).ResourceUri;
        return expected is not null
            && uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeTopLevelUri(uri), NormalizeTopLevelUri(expected), StringComparison.Ordinal);
    }

    public static string NormalizeTopLevelUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) throw new ArgumentException("An absolute URI is required.", nameof(uri));
        return uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery | UriComponents.Fragment,
            UriFormat.UriEscaped);
    }

    public static PublisherCheckInDomContract GetCheckInDomContract(string gameId) =>
        CheckInDomContracts.TryGetValue(gameId, out var contract)
            ? contract
            : throw new ArgumentOutOfRangeException(nameof(gameId));

    public static bool IsExactEndfieldAccountIdentityRequest(Uri uri, string method)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (method != "GET"
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, EndfieldAccountIdentityUri.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, EndfieldAccountIdentityUri.AbsolutePath, StringComparison.Ordinal))
            return false;
        var query = ParseBoundedQuery(uri.Query, "uid");
        return query is not null
            && query.Count == 1
            && query.TryGetValue("uid", out var uid)
            && uid.Length is > 0 and <= 64
            && uid.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_');
    }

    public static Uri GetCheckInClaimRequestUri(string gameId) =>
        CheckInResponseEndpoints.TryGetValue(gameId, out var endpoint)
            ? endpoint.ClaimUri
            : throw new ArgumentOutOfRangeException(nameof(gameId));

    public static IReadOnlyList<string> GetCheckInWebResourceFilterPatterns(string gameId)
    {
        var claim = GetCheckInClaimRequestUri(gameId).AbsoluteUri;
        var retired = RetiredCheckInResponseEndpoints.TryGetValue(gameId, out var endpoints)
            ? endpoints
            : [];
        return new[] { claim }
            .Concat(retired.Select(static endpoint => endpoint.AbsoluteUri))
            .SelectMany(static endpoint => new[] { endpoint, endpoint + "?*" })
            .ToArray();
    }

    public static IReadOnlyList<string> GetAchievementWebResourceFilterPatterns(string gameId)
    {
        if (gameId != "hsr") throw new ArgumentOutOfRangeException(nameof(gameId));
        return new[]
            {
                HsrRoleDiscoveryEndpoint,
                HsrRetiredRoleDiscoveryEndpoint,
                HsrAchievementLoginEndpoint,
                HsrAchievementFallbackLoginEndpoint,
                HsrAchievementListEndpoint,
                HsrAchievementFallbackListEndpoint,
            }
            .SelectMany(static endpoint => new[] { endpoint.AbsoluteUri, endpoint.AbsoluteUri + "?*" })
            .ToArray();
    }

    public static Uri GetAchievementPageUri(string gameId) =>
        gameId == "hsr"
            ? HsrAchievementPageUri
            : throw new ArgumentOutOfRangeException(nameof(gameId));

    public static bool IsExactAchievementPageUri(string gameId, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (gameId != "hsr") return false;
        return uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeTopLevelUri(uri),
                NormalizeTopLevelUri(HsrAchievementPageUri),
                StringComparison.Ordinal);
    }

    public static bool IsAllowedWebResourceRequest(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        Uri uri,
        string method,
        PublisherWebResourceContext context,
        PublisherClaimWriteAuthority? claimWriteAuthority = null,
        ReadOnlyMemory<byte>? requestBody = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Entries.TryGetValue(gameId, out var entry)
            || !string.Equals(entry.Provider, provider, StringComparison.Ordinal)
            || method is not ("GET" or "HEAD" or "POST" or "DELETE" or "OPTIONS")
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Query.Length > 2048
                && !IsBoundedHsrGeetestApiRequest(
                    provider,
                    purpose,
                    gameId,
                    uri,
                    method,
                    context))
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (purpose == PublisherSessionPurpose.Connect
            && method != "POST"
            && !IsNoRequestBody(requestBody))
            return false;

        if (context == PublisherWebResourceContext.Document)
        {
            if (method != "GET") return false;
            if (IsAllowedPurposeDocumentRequest(entry, purpose, uri)) return true;
            return purpose == PublisherSessionPurpose.Connect
                && provider == "HoYoLAB"
                && IsReviewedHoyoAccountDocument(uri);
        }

        // Cross-origin claim APIs can issue a non-mutating CORS preflight.
        // Keep that handshake exact-game and exact-endpoint without spending
        // the one authorization reserved for the actual POST.
        if (purpose == PublisherSessionPurpose.CheckIn
            && method == "OPTIONS"
            && context is PublisherWebResourceContext.XmlHttpRequest
                or PublisherWebResourceContext.Fetch
                or PublisherWebResourceContext.Other
            && IsExactCheckInResponseUri(gameId, uri, "POST"))
            return true;

        // The active resource reader uses simple credentialed GETs, but the
        // browser may still classify a CORS handshake as Other. Keep that
        // handshake on the same exact read-only role/note contracts.
        if (purpose == PublisherSessionPurpose.Resource
            && method == "OPTIONS"
            && context is PublisherWebResourceContext.XmlHttpRequest
                or PublisherWebResourceContext.Fetch
                or PublisherWebResourceContext.Other
            && IsNoRequestBody(requestBody)
            && IsExactResourceRoleDiscoveryRequest(gameId, uri, method))
            return true;

        // HSR's signed note GET carries non-safelisted DS/x-rpc headers, so the
        // browser must complete this read-only preflight before issuing the
        // already-bounded GET. Other games use simple note GETs.
        if (purpose == PublisherSessionPurpose.Resource
            && gameId == "hsr"
            && method == "OPTIONS"
            && context is PublisherWebResourceContext.XmlHttpRequest
                or PublisherWebResourceContext.Fetch
                or PublisherWebResourceContext.Other
            && IsNoRequestBody(requestBody)
            && IsExactResourceResponseUri(gameId, uri))
            return true;

        if (context is PublisherWebResourceContext.XmlHttpRequest or PublisherWebResourceContext.Fetch)
        {
            if (provider == "HoYoLAB"
                && IsAllowedHoyoApiRequest(
                    gameId,
                    purpose,
                    uri,
                    method,
                    claimWriteAuthority,
                    requestBody,
                    contentType))
                return true;
            if (provider == "SKPORT"
                && IsAllowedSkportApiRequest(
                    gameId,
                    purpose,
                    uri,
                    method,
                    claimWriteAuthority,
                    requestBody,
                    contentType))
                return true;
        }

        return method is "GET" or "HEAD"
            && (provider == "HoYoLAB"
                ? IsAllowedHoyoAsset(
                    gameId,
                    uri,
                    method,
                    context,
                    purpose == PublisherSessionPurpose.Connect)
                : IsAllowedSkportAsset(uri, context, purpose == PublisherSessionPurpose.Connect));
    }

    public static bool IsAllowedTopLevelNavigation(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Entries.TryGetValue(gameId, out var entry)
            || !string.Equals(entry.Provider, provider, StringComparison.Ordinal)
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || uri.Query.Length > 2048
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var selectedPage = purpose switch
        {
            PublisherSessionPurpose.CheckIn => entry.CheckInUri is not null && IsExactCheckInUri(gameId, uri),
            PublisherSessionPurpose.Resource => entry.ResourceUri is not null && IsExactResourcePageUri(gameId, uri),
            PublisherSessionPurpose.Achievements => IsExactAchievementPageUri(gameId, uri),
            PublisherSessionPurpose.Connect =>
                (entry.CheckInUri is not null && IsExactCheckInUri(gameId, uri))
                || (entry.ResourceUri is not null && IsExactResourcePageUri(gameId, uri))
                || (gameId == "hsr" && IsExactAchievementPageUri(gameId, uri)),
            PublisherSessionPurpose.ConnectionProbe =>
                (entry.CheckInUri is not null && IsExactCheckInUri(gameId, uri))
                || (entry.ResourceUri is not null && IsExactResourcePageUri(gameId, uri)),
            _ => false,
        };
        if (selectedPage) return true;
        return purpose == PublisherSessionPurpose.Connect
            && provider == "HoYoLAB"
            && string.IsNullOrEmpty(uri.Fragment)
            && IsReviewedHoyoAccountDocument(uri);
    }

    private static bool IsReviewedHoyoAccountDocument(Uri uri) =>
        uri.Query.Length <= 2048
        && (string.Equals(uri.Host, "account.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath is "/passport/index.html"
                or "/login-platform"
                or "/login-platform/index.html"
                or "/single-page"
                or "/single-page/index.html"
                or "/ue/login-platform"
                or "/ue/single-page"
            : string.Equals(uri.Host, "account.hoyolab.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath is "/login-platform" or "/login-platform/index.html");

    private static bool IsAllowedPurposeDocumentRequest(
        PublisherAccountCatalogEntry entry,
        PublisherSessionPurpose purpose,
        Uri uri) => purpose switch
        {
            PublisherSessionPurpose.CheckIn =>
                entry.CheckInUri is not null && IsExactCheckInUri(entry.GameId, uri),
            PublisherSessionPurpose.Resource =>
                entry.ResourceUri is not null && IsExactResourceDocumentRequest(entry.GameId, uri),
            PublisherSessionPurpose.Achievements =>
                IsExactAchievementDocumentRequest(entry.GameId, uri),
            PublisherSessionPurpose.Connect =>
                (entry.CheckInUri is not null && IsExactCheckInUri(entry.GameId, uri))
                || (entry.ResourceUri is not null && IsExactResourceDocumentRequest(entry.GameId, uri))
                || (entry.GameId == "hsr" && IsExactAchievementDocumentRequest(entry.GameId, uri)),
            PublisherSessionPurpose.ConnectionProbe =>
                (entry.CheckInUri is not null && IsExactCheckInUri(entry.GameId, uri))
                || (entry.ResourceUri is not null && IsExactResourceDocumentRequest(entry.GameId, uri)),
            _ => false,
        };

    private static bool IsExactAchievementDocumentRequest(string gameId, Uri uri)
    {
        if (gameId != "hsr") return false;
        return uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            // The fixed fragment selects the official achievement tool but is
            // never sent in the HTTP document request. Keep its exact query.
            && string.Equals(
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                HsrAchievementPageUri.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped),
                StringComparison.Ordinal);
    }

    private static bool IsExactResourceDocumentRequest(string gameId, Uri uri)
    {
        var expected = Get(gameId).ResourceUri;
        return expected is not null
            && uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            // URI fragments select the in-page route but are never sent in an
            // HTTP request. Compare the exact network address beneath it.
            && string.Equals(
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                expected.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                StringComparison.Ordinal);
    }

    private static bool IsAllowedHoyoApiRequest(
        string gameId,
        PublisherSessionPurpose purpose,
        Uri uri,
        string method,
        PublisherClaimWriteAuthority? claimWriteAuthority,
        ReadOnlyMemory<byte>? requestBody,
        string? contentType)
    {
        if ((purpose == PublisherSessionPurpose.Connect
                || (purpose == PublisherSessionPurpose.Achievements && gameId == "hsr"))
            && method == "GET"
            && IsExactHoyoLocalizationRequest(uri, requestBody))
            return true;
        if (method == "GET"
            && purpose is PublisherSessionPurpose.Connect
                or PublisherSessionPurpose.ConnectionProbe
                or PublisherSessionPurpose.CheckIn
            && IsExactCheckInResponseUri(gameId, uri, method))
            return true;
        if (method == "POST"
            && purpose == PublisherSessionPurpose.CheckIn
            && IsExactCheckInResponseUri(gameId, uri, method))
            // Deliberate publisher-page trust boundary: Nyx authorizes one
            // exact game's exact sign endpoint after one explicit Daily click,
            // but does not copy the official page's request body out of the
            // isolated browser. The one-shot permit and before/after response
            // proofs bound the operation; body validation is not claimed.
            return claimWriteAuthority?.TryConsume(gameId) == true;
        if (method == "GET"
            && purpose is PublisherSessionPurpose.Connect
                or PublisherSessionPurpose.ConnectionProbe
                or PublisherSessionPurpose.Resource
            && IsNoRequestBody(requestBody)
            && IsExactResourceResponseUri(gameId, uri))
            return true;
        if (purpose == PublisherSessionPurpose.Resource
            && IsNoRequestBody(requestBody)
            && IsExactResourceRoleDiscoveryRequest(gameId, uri, method))
            return true;
        if (gameId == "hsr"
            && purpose == PublisherSessionPurpose.Achievements
            && IsExactHsrAchievementApiRequest(uri, method))
            return true;
        if (gameId == "hsr"
            && purpose is PublisherSessionPurpose.Connect
                or PublisherSessionPurpose.Achievements
            && IsReviewedCurrentHsrConnectSupportRequest(
                uri,
                method,
                requestBody,
                contentType))
            return true;
        if (purpose == PublisherSessionPurpose.Achievements)
            return false;

        if (purpose == PublisherSessionPurpose.Connect
            && string.Equals(uri.Host, "bbs-api-os.hoyolab.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                uri.AbsolutePath,
                "/community/user/wapi/getUserFullInfo",
                StringComparison.Ordinal)
            && method == "GET"
            && IsNoRequestBody(requestBody))
            return true;

        if (purpose != PublisherSessionPurpose.Connect
            && string.Equals(uri.Host, "bbs-api-os.hoyolab.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/community/", StringComparison.Ordinal)
            && method == "GET")
            return true;

        return purpose == PublisherSessionPurpose.Connect
            && IsReviewedHoyoConnectRequest(gameId, uri, method, requestBody, contentType);
    }

    private static bool IsAllowedSkportApiRequest(
        string gameId,
        PublisherSessionPurpose purpose,
        Uri uri,
        string method,
        PublisherClaimWriteAuthority? claimWriteAuthority,
        ReadOnlyMemory<byte>? requestBody,
        string? contentType)
    {
        if (IsExactSkportSessionProbeUri(uri, method)) return true;
        if (gameId == "ae"
            && method == "GET"
            && purpose is PublisherSessionPurpose.Connect
                or PublisherSessionPurpose.ConnectionProbe
                or PublisherSessionPurpose.CheckIn
            && IsExactCheckInResponseUri(gameId, uri, method))
            return true;
        if (gameId == "ae"
            && method == "POST"
            && purpose == PublisherSessionPurpose.CheckIn
            && IsExactCheckInResponseUri(gameId, uri, method))
            return claimWriteAuthority?.TryConsume(gameId) == true;

        if (IsExactEndfieldAccountIdentityRequest(uri, method))
            return true;

        if (IsReviewedSkportBindingListRequest(uri, method))
            return true;

        return purpose == PublisherSessionPurpose.Connect
            && IsReviewedSkportConnectRequest(uri, method, requestBody, contentType);
    }

    // Reviewed from the official production login clients on 2026-07-21.
    // HoYo: account.hoyoverse.com/login-platform/chunk-common.8caf3da0.js.
    // SKPORT: WEB-SDK 1.14.0, chunk 988.b35e1f61131197f9cb91.js, plus
    // game.skport.com/skport-fe-static/skport-game-tools/9773.b689537c.js.
    // Only the existing-account password path and its required session/OAuth
    // hand-off are authorized. Registration, recovery, binding, unbinding,
    // account deletion, and future endpoints stay blocked.
    private static bool IsReviewedCurrentHsrConnectSupportRequest(
        Uri uri,
        string method,
        ReadOnlyMemory<byte>? requestBody,
        string? contentType)
    {
        var path = uri.AbsolutePath;
        if (string.Equals(
                uri.Host,
                "sg-public-data-api.hoyoverse.com",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/device-fp/api/getFp", StringComparison.Ordinal))
        {
            if (method == "OPTIONS")
                return string.IsNullOrEmpty(uri.Query) && IsNoRequestBody(requestBody);
            if (method != "POST"
                || !string.IsNullOrEmpty(uri.Query))
                return false;
            return IsExactJsonObject(requestBody, contentType, static root =>
                HasExactProperties(
                    root,
                    "app_name",
                    "device_fp",
                    "device_id",
                    "ext_fields",
                    "platform",
                    "seed_id",
                    "seed_time")
                && HasJsonString(root, "app_name")
                && HasJsonString(root, "device_fp")
                && HasJsonString(root, "device_id")
                && HasJsonString(root, "ext_fields")
                && HasJsonString(root, "platform")
                && HasJsonString(root, "seed_id")
                && HasJsonString(root, "seed_time"));
        }

        if (method == "GET")
        {
            if (!IsNoRequestBody(requestBody)) return false;
            if (IsHoyoSwitchApiHost(uri.Host)
                && string.Equals(
                    path,
                    "/account/ma-passport/api/getSwitchStatus",
                    StringComparison.Ordinal))
                return IsExactHoyoSwitchQuery("hsr", uri.Host, uri.Query);
            if (string.Equals(uri.Host, "bbs-api-os.hoyolab.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/community/misc/wapi/langs", StringComparison.Ordinal))
                return IsExactQuery(uri.Query, ("lang2022", "true"));
            if (string.Equals(uri.Host, "sdk-os-static.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/combo/box/api/config/porte-fe-os/config", StringComparison.Ordinal))
                return IsExactQuery(uri.Query, ("type", "common"));
            return string.Equals(
                    uri.Host,
                    "sg-public-data-api.hoyoverse.com",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/device-fp/api/getExtList", StringComparison.Ordinal)
                && IsExactQuery(
                    uri.Query,
                    ("platform", "4"),
                    ("app_name", "hkrpg_global"));
        }

        return false;
    }

    private static bool IsReviewedHoyoConnectRequest(
        string gameId,
        Uri uri,
        string method,
        ReadOnlyMemory<byte>? requestBody,
        string? contentType)
    {
        var path = uri.AbsolutePath;
        if (string.Equals(
                uri.Host,
                "passport-api-sg.hoyolab.com",
                StringComparison.OrdinalIgnoreCase)
            && gameId == "hsr"
            && path is
                "/account/ma-aigis/api/createBySmartCaptchaTicket"
                or "/account/ma-aigis/api/checkSmartCaptcha")
        {
            if (method == "OPTIONS")
                return string.IsNullOrEmpty(uri.Query) && IsNoRequestBody(requestBody);
            if (method != "POST" || !string.IsNullOrEmpty(uri.Query)) return false;
            return IsExactJsonObject(
                requestBody,
                contentType,
                static root => HasUniqueProperties(root));
        }

        if (method == "GET")
            return IsHoyoSwitchApiHost(uri.Host)
                && IsNoRequestBody(requestBody)
                && string.Equals(path, "/account/ma-passport/api/getSwitchStatus", StringComparison.Ordinal)
                && IsExactHoyoSwitchQuery(gameId, uri.Host, uri.Query);

        var isLegacyPassportHost = IsLegacyHoyoPassportApiHost(uri.Host);
        var isCurrentPasswordHost = string.Equals(
            uri.Host,
            "passport-api-sg.hoyolab.com",
            StringComparison.OrdinalIgnoreCase);
        if (!isLegacyPassportHost && !isCurrentPasswordHost) return false;

        var exactPost = isLegacyPassportHost
            ? path is
                "/account/ma-passport/api/getConfig"
                or "/account/ma-passport/api/getAreaCode"
                or "/account/ma-passport/api/webLoginByPassword"
                or "/account/ma-passport/token/verifyCookieToken"
                or "/account/ma-passport/token/verifyLToken"
            : string.Equals(
                path,
                "/account/ma-passport/api/webLoginByPassword",
                StringComparison.Ordinal);
        if (method == "OPTIONS")
            return IsNoRequestBody(requestBody)
                && exactPost
                && string.IsNullOrEmpty(uri.Query);
        if (method != "POST" || !exactPost || !string.IsNullOrEmpty(uri.Query)) return false;

        var expectedPasswordTokenType = isLegacyPassportHost ? 2 : 6;
        return string.Equals(path, "/account/ma-passport/api/webLoginByPassword", StringComparison.Ordinal)
            ? IsExactJsonObject(requestBody, contentType, root =>
                HasExactProperties(root, "account", "password", "token_type")
                && HasNonEmptyJsonString(root, "account")
                && HasNonEmptyJsonString(root, "password")
                && TryGetExactInt32(root, "token_type", expectedPasswordTokenType))
            : IsExactJsonObject(requestBody, contentType, static root =>
                HasExactProperties(root));
    }

    private static bool IsReviewedSkportConnectRequest(
        Uri uri,
        string method,
        ReadOnlyMemory<byte>? requestBody,
        string? contentType)
    {
        var path = uri.AbsolutePath;
        if (method == "GET")
        {
            if (!IsNoRequestBody(requestBody)) return false;
            if (string.Equals(uri.Host, "as.gryphline.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/user/info/v1/basic", StringComparison.Ordinal))
                return IsExactOpaqueTokenQuery(uri.Query);
            return string.Equals(uri.Host, "zonai.skport.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, "/web/v1/user/check", StringComparison.Ordinal)
                && string.IsNullOrEmpty(uri.Query);
        }

        var isPasswordLogin = string.Equals(uri.Host, "as.gryphline.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/user/auth/v1/token_by_email_password", StringComparison.Ordinal);
        var isOauthGrant = string.Equals(uri.Host, "as.gryphline.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/user/oauth2/v2/grant", StringComparison.Ordinal);
        var isTokenStore = string.Equals(uri.Host, "web-api.skport.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/cookie_store/account_token", StringComparison.Ordinal);
        var isCredExchange = string.Equals(uri.Host, "zonai.skport.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/web/v1/user/auth/generate_cred_by_code", StringComparison.Ordinal);
        var exactPost = isPasswordLogin || isOauthGrant || isTokenStore || isCredExchange;

        if (method == "OPTIONS")
            return IsNoRequestBody(requestBody)
                && string.IsNullOrEmpty(uri.Query)
                && (exactPost
                    || (string.Equals(uri.Host, "zonai.skport.com", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(path, "/web/v1/user/check", StringComparison.Ordinal)));
        if (method != "POST" || !exactPost || !string.IsNullOrEmpty(uri.Query)) return false;

        if (isPasswordLogin)
            return IsExactJsonObject(requestBody, contentType, static root =>
                HasExactProperties(root, "email", "password")
                && HasNonEmptyJsonString(root, "email")
                && HasNonEmptyJsonString(root, "password"));
        if (isOauthGrant)
            return IsExactJsonObject(requestBody, contentType, static root =>
                HasExactProperties(root, "token", "appCode", "type")
                && HasNonEmptyJsonString(root, "token")
                && (HasExactJsonString(root, "appCode", "endfield")
                    || HasExactJsonString(root, "appCode", "4ca99fa6b56cc2ba"))
                && TryGetExactInt32(root, "type", 1));
        if (isTokenStore)
            return IsExactJsonObject(requestBody, contentType, static root =>
                HasExactProperties(root, "content")
                && HasNonEmptyJsonString(root, "content"));
        return IsExactJsonObject(requestBody, contentType, static root =>
            HasExactProperties(root, "kind", "code")
            && TryGetExactInt32(root, "kind", 1)
            && HasNonEmptyJsonString(root, "code"));
    }

    private static bool IsReviewedSkportBindingListRequest(Uri uri, string method)
    {
        if (!string.Equals(uri.Host, "binding-api-account-prod.gryphline.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, "/account/binding/v1/binding_list", StringComparison.Ordinal)
            || method is not ("GET" or "OPTIONS"))
            return false;
        return IsExactSkportBindingQuery(uri.Query);
    }

    private static bool IsLegacyHoyoPassportApiHost(string host) =>
        string.Equals(host, "passport-api-sg.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "passport-api-eu.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "passport-api-us.hoyoverse.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsHoyoSwitchApiHost(string host) =>
        IsLegacyHoyoPassportApiHost(host)
        || string.Equals(host, "sg-public-api-static.hoyolab.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactHsrAchievementApiRequest(Uri uri, string method)
    {
        if (string.Equals(uri.Host, HsrRoleDiscoveryEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                uri.AbsolutePath,
                HsrRoleDiscoveryEndpoint.AbsolutePath,
                StringComparison.Ordinal))
        {
            if (method is not ("GET" or "OPTIONS")) return false;
            var query = ParseBoundedQuery(uri.Query, "game_biz", "region");
            return query is not null
                && query.Count == 2
                && query.TryGetValue("game_biz", out var gameBiz)
                && string.Equals(gameBiz, "hkrpg_global", StringComparison.Ordinal)
                && query.TryGetValue("region", out var roleRegion)
                && ResourceServers["hsr"].Contains(roleRegion);
        }

        if (string.Equals(uri.Host, HsrAchievementLoginEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                uri.AbsolutePath,
                HsrAchievementLoginEndpoint.AbsolutePath,
                StringComparison.Ordinal))
            return method is "POST" or "OPTIONS"
                && string.IsNullOrEmpty(uri.Query);

        if (string.Equals(
                uri.Host,
                HsrAchievementFallbackLoginEndpoint.Host,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                uri.AbsolutePath,
                HsrAchievementFallbackLoginEndpoint.AbsolutePath,
                StringComparison.Ordinal))
        {
            if (method is not ("GET" or "OPTIONS")) return false;
            var loginQuery = ParseBoundedQuery(uri.Query, "game_biz", "lang", "ts");
            return loginQuery is not null
                && loginQuery.Count == 3
                && loginQuery.TryGetValue("game_biz", out var loginGameBiz)
                && string.Equals(loginGameBiz, "hkrpg_global", StringComparison.Ordinal)
                && loginQuery.TryGetValue("lang", out var language)
                && string.Equals(language, "en-us", StringComparison.Ordinal)
                && loginQuery.TryGetValue("ts", out var timestamp)
                && timestamp.Length is >= 10 and <= 16
                && timestamp.All(char.IsAsciiDigit);
        }

        if (!IsExactHsrAchievementListEndpoint(uri)
            || method is not ("GET" or "OPTIONS"))
            return false;
        var listQuery = ParseBoundedQuery(
            uri.Query,
            "game_biz",
            "badge_region",
            "badge_uid",
            "show_hide",
            "need_all",
            "game",
            "t",
            "noSessionRetry");
        return listQuery is not null
            && listQuery.Count is >= 5 and <= 8
            && listQuery.TryGetValue("game_biz", out var listGameBiz)
            && string.Equals(listGameBiz, "hkrpg_global", StringComparison.Ordinal)
            && listQuery.TryGetValue("badge_region", out var region)
            && ResourceServers["hsr"].Contains(region)
            && listQuery.TryGetValue("badge_uid", out var uid)
            && uid.Length is >= 1 and <= 20
            && uid[0] != '0'
            && uid.All(char.IsAsciiDigit)
            && listQuery.TryGetValue("show_hide", out var showHide)
            && string.Equals(showHide, "false", StringComparison.Ordinal)
            && listQuery.TryGetValue("need_all", out var needAll)
            && string.Equals(needAll, "true", StringComparison.Ordinal)
            && (!listQuery.TryGetValue("game", out var game)
                || string.Equals(game, "hkrpg", StringComparison.Ordinal))
            && (!listQuery.TryGetValue("t", out var listTimestamp)
                || (listTimestamp.Length is >= 10 and <= 16
                    && listTimestamp.All(char.IsAsciiDigit)))
            && (!listQuery.TryGetValue("noSessionRetry", out var noSessionRetry)
                || string.Equals(noSessionRetry, "true", StringComparison.Ordinal));
    }

    public static bool IsExactHsrAchievementListRequestForRole(
        Uri uri,
        string method,
        PublisherRoleBinding role)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(role);
        if (!IsValidRoleBinding("hsr", role)
            || !IsExactHsrAchievementApiRequest(uri, method)
            || !IsExactHsrAchievementListEndpoint(uri))
            return false;

        var query = ParseBoundedQuery(
            uri.Query,
            "game_biz",
            "badge_region",
            "badge_uid",
            "show_hide",
            "need_all",
            "game",
            "t",
            "noSessionRetry");
        return query is not null
            && query.TryGetValue("badge_region", out var region)
            && string.Equals(region, role.Server, StringComparison.Ordinal)
            && query.TryGetValue("badge_uid", out var uid)
            && string.Equals(uid, role.RoleId, StringComparison.Ordinal);
    }

    private static bool IsExactHsrAchievementListEndpoint(Uri uri) =>
        (string.Equals(
                uri.Host,
                HsrAchievementListEndpoint.Host,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                uri.Host,
                HsrAchievementFallbackListEndpoint.Host,
                StringComparison.OrdinalIgnoreCase))
        && string.Equals(
            uri.AbsolutePath,
            HsrAchievementListEndpoint.AbsolutePath,
            StringComparison.Ordinal);

    private static bool IsExactHoyoSwitchQuery(string gameId, string host, string query)
    {
        var expectedAppId = string.Equals(
            host,
            "sg-public-api-static.hoyolab.com",
            StringComparison.OrdinalIgnoreCase)
            ? gameId is "gi" or "hsr" or "zzz"
                ? "c9oqaq3s3gu8"
                : null
            : gameId switch
            {
                "gi" => "c9oqaq3s3gu8",
                "hsr" => "ciebhwzprpq8",
                "zzz" => "cieaz4epd5vk",
                _ => null,
            };
        var parsed = ParseBoundedQuery(query, "app_id", "platform");
        return expectedAppId is not null
            && parsed is not null
            && parsed.Count == 2
            && parsed.TryGetValue("app_id", out var appId)
            && string.Equals(appId, expectedAppId, StringComparison.Ordinal)
            && parsed.TryGetValue("platform", out var platform)
            && string.Equals(platform, "4", StringComparison.Ordinal);
    }

    private static bool IsExactOpaqueTokenQuery(string query)
    {
        var parsed = ParseConnectQuery(query, "token");
        return parsed is not null
            && parsed.Count == 1
            && parsed.TryGetValue("token", out var token)
            && IsBoundedOpaqueValue(token, 4096);
    }

    private static bool IsExactSkportBindingQuery(string query)
    {
        var parsed = ParseConnectQuery(query, "token", "appCode", "serverId");
        if (parsed is null
            || parsed.Count is < 2 or > 3
            || !parsed.TryGetValue("token", out var token)
            || !IsBoundedOpaqueValue(token, 4096)
            || !parsed.TryGetValue("appCode", out var appCode)
            || !string.Equals(appCode, "endfield", StringComparison.Ordinal))
            return false;
        return !parsed.TryGetValue("serverId", out var serverId)
            || (serverId.Length is > 0 and <= 64
                && serverId.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    private static bool IsNoRequestBody(ReadOnlyMemory<byte>? requestBody) =>
        requestBody is null || requestBody.Value.IsEmpty;

    private static bool IsExactJsonObject(
        ReadOnlyMemory<byte>? requestBody,
        string? contentType,
        Func<JsonElement, bool> predicate)
    {
        if (requestBody is null
            || requestBody.Value.IsEmpty
            || requestBody.Value.Length > MaximumConnectRequestBodyBytes
            || !IsJsonContentType(contentType))
            return false;
        try
        {
            using var document = JsonDocument.Parse(requestBody.Value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && predicate(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement root, params string[] propertyNames)
    {
        var expected = propertyNames.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !found.Add(property.Name)) return false;
        }
        return found.SetEquals(expected);
    }

    private static bool HasUniqueProperties(JsonElement root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!found.Add(property.Name)) return false;
        }
        return true;
    }

    // Keep credentials and tokens in the bounded UTF-8 request buffer. ValueEquals
    // validates string contents without creating an immutable secret-bearing string.
    private static bool HasNonEmptyJsonString(JsonElement root, string propertyName) =>
        TryGetUniqueProperty(root, propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && !property.ValueEquals(ReadOnlySpan<byte>.Empty);

    private static bool HasJsonString(JsonElement root, string propertyName) =>
        TryGetUniqueProperty(root, propertyName, out var property)
        && property.ValueKind == JsonValueKind.String;

    private static bool HasExactJsonString(
        JsonElement root,
        string propertyName,
        string expected) =>
        TryGetUniqueProperty(root, propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && property.ValueEquals(expected);

    private static bool TryGetExactInt32(JsonElement root, string propertyName, int expected) =>
        TryGetUniqueProperty(root, propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
        && value == expected;

    private static bool IsExactHoyoLocalizationRequest(
        Uri uri,
        ReadOnlyMemory<byte>? requestBody)
    {
        if (!string.Equals(uri.Host, "webstatic.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !IsNoRequestBody(requestBody))
            return false;

        var segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 5
            || !string.Equals(segments[0], "admin", StringComparison.Ordinal)
            || !string.Equals(segments[1], "mi18n", StringComparison.Ordinal)
            || !IsHoyoLocalizationSegment(segments[2])
            || !IsHoyoLocalizationSegment(segments[3]))
            return false;

        return string.Equals(
            segments[4],
            $"{segments[3]}-en-us.json",
            StringComparison.Ordinal);
    }

    private static bool IsHoyoLocalizationSegment(string value) =>
        value.Length is > 0 and <= 64
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsReviewedHoyoAccountHost(string host) =>
        string.Equals(host, "account.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "account.hoyolab.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactCurrentHoyoLoginAsset(string path) =>
        path is
            "/login-platform/chunk-vendors.8caf3da0.js"
            or "/login-platform/chunk-common.8caf3da0.js"
            or "/login-platform/web.8caf3da0.js"
            or "/login-platform/password-login-web.8caf3da0.js"
            or "/login-platform/chunk-vendors.8caf3da0.css"
            or "/login-platform/chunk-common.8caf3da0.css"
            or "/login-platform/web.8caf3da0.css"
            or "/login-platform/password-login-web.8caf3da0.css";

    private static bool IsBoundedOpaqueValue(string value, int maximumLength) =>
        value.Length is > 0
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool IsAllowedHoyoAsset(
        string gameId,
        Uri uri,
        string method,
        PublisherWebResourceContext context,
        bool connectMode)
    {
        if (connectMode
            && gameId == "hsr"
            && IsReviewedGeetestApiHost(uri.Host))
            return method == "GET"
                && context == PublisherWebResourceContext.Script
                && (string.Equals(uri.AbsolutePath, "/load", StringComparison.Ordinal)
                    ? IsBoundedGeetestQuery(uri.Query, 8192)
                    : string.Equals(uri.AbsolutePath, "/verify", StringComparison.Ordinal)
                        && IsBoundedGeetestQuery(uri.Query, 65536));
        if (connectMode
            && gameId == "hsr"
            && IsReviewedGeetestStaticHost(uri.Host))
            return (uri.AbsolutePath.StartsWith("/v4/", StringComparison.Ordinal)
                    && context is
                        PublisherWebResourceContext.Script
                        or PublisherWebResourceContext.Stylesheet
                        or PublisherWebResourceContext.Image
                        or PublisherWebResourceContext.Font
                        or PublisherWebResourceContext.XmlHttpRequest
                        or PublisherWebResourceContext.Fetch)
                || (uri.AbsolutePath.StartsWith("/captcha_v4/", StringComparison.Ordinal)
                    && context == PublisherWebResourceContext.Image);
        if (!IsAssetContext(context, allowDataFetch: !connectMode)) return false;
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (string.Equals(host, "act.hoyolab.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/ys/event/signin-sea-v3/", StringComparison.Ordinal)
                || path.StartsWith("/bbs/event/signin/hkrpg/", StringComparison.Ordinal)
                || path.StartsWith("/bbs/event/signin/zzz/", StringComparison.Ordinal)
                || path.StartsWith("/sr/event/cultivation-tool/", StringComparison.Ordinal)
                || path.StartsWith("/app/community-game-records-sea/", StringComparison.Ordinal)
                || path.StartsWith("/app/zzz-game-record/", StringComparison.Ordinal);
        if (string.Equals(host, "account.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return connectMode
                && (path.StartsWith("/passport/", StringComparison.Ordinal)
                    || path.StartsWith("/login-platform/", StringComparison.Ordinal)
                    || path.StartsWith("/single-page/", StringComparison.Ordinal)
                    || path is "/chunk-vendors.8caf3da0.js"
                        or "/chunk-common.8caf3da0.js"
                        or "/web.8caf3da0.js"
                        or "/chunk-vendors.8caf3da0.css"
                        or "/chunk-common.8caf3da0.css"
                        or "/web.8caf3da0.css"
                        or "/favicon.ico");
        if (string.Equals(host, "account.hoyolab.com", StringComparison.OrdinalIgnoreCase))
            return connectMode
                && string.IsNullOrEmpty(uri.Query)
                && IsExactCurrentHoyoLoginAsset(path);
        if (string.Equals(host, "webstatic.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/dora/", StringComparison.Ordinal);
        if (string.Equals(host, "act.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/common/event/", StringComparison.Ordinal)
                || (context == PublisherWebResourceContext.Image
                    && path.StartsWith("/event-static/", StringComparison.Ordinal));
        if (string.Equals(host, "fastcdn.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return context == PublisherWebResourceContext.Image
                && path.StartsWith("/static-resource-v2/", StringComparison.Ordinal);
        if (string.Equals(host, "upload-static.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return context == PublisherWebResourceContext.Image
                && path.StartsWith("/event/", StringComparison.Ordinal);
        if (string.Equals(host, "act-webstatic.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return context == PublisherWebResourceContext.Image
                && path.StartsWith("/event-static/", StringComparison.Ordinal);
        if (string.Equals(host, "sdk-os-static.hoyoverse.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                path,
                "/combo/box/api/config/porte-fe-os/config",
                StringComparison.Ordinal))
            return false;
        if (string.Equals(host, "img-os-static.hoyolab.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "sdk-os-static.hoyoverse.com", StringComparison.OrdinalIgnoreCase))
            return context is PublisherWebResourceContext.Image
                or PublisherWebResourceContext.Font
                or PublisherWebResourceContext.Script
                or PublisherWebResourceContext.Stylesheet;
        return false;
    }

    private static bool IsBoundedHsrGeetestApiRequest(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        Uri uri,
        string method,
        PublisherWebResourceContext context) =>
        provider == "HoYoLAB"
        && purpose == PublisherSessionPurpose.Connect
        && gameId == "hsr"
        && method == "GET"
        && context == PublisherWebResourceContext.Script
        && IsReviewedGeetestApiHost(uri.Host)
        && (string.Equals(uri.AbsolutePath, "/load", StringComparison.Ordinal)
            ? IsBoundedGeetestQuery(uri.Query, 8192)
            : string.Equals(uri.AbsolutePath, "/verify", StringComparison.Ordinal)
                && IsBoundedGeetestQuery(uri.Query, 65536));

    private static bool IsReviewedGeetestApiHost(string host) =>
        string.Equals(host, "gcaptcha4.geetest.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "gcaptcha4.geevisit.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "gcaptcha4.gsensebot.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewedGeetestStaticHost(string host) =>
        string.Equals(host, "static.geetest.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "static.geevisit.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedGeetestQuery(string query, int maximumLength) =>
        query.Length is > 1
        && query.Length <= maximumLength
        && query[0] == '?';

    private static bool IsAllowedSkportAsset(
        Uri uri,
        PublisherWebResourceContext context,
        bool connectMode)
    {
        if (!IsAssetContext(context, allowDataFetch: !connectMode)) return false;
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (string.Equals(host, "static.skport.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/skport-fe-static/skport-game-tools/", StringComparison.Ordinal)
                || (context == PublisherWebResourceContext.Image
                    && (path.StartsWith("/asset/endfield_attendance/", StringComparison.Ordinal)
                        || path.StartsWith("/image/", StringComparison.Ordinal)));
        if (string.Equals(host, "assets.skport.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/assets/", StringComparison.Ordinal);
        if (string.Equals(host, "web-api.gryphline.com", StringComparison.OrdinalIgnoreCase))
            return context == PublisherWebResourceContext.Script
                && path.StartsWith("/static/gl_web_sdk/", StringComparison.Ordinal);
        if (string.Equals(host, "web-static.hg-cdn.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/gl_web_sdk/", StringComparison.Ordinal);
        return string.Equals(host, "o.alicdn.com", StringComparison.OrdinalIgnoreCase)
            && context == PublisherWebResourceContext.Script
            && string.Equals(path, "/frontend-lib/common-lib/jquery.min.js", StringComparison.Ordinal);
    }

    private static bool IsAssetContext(PublisherWebResourceContext context, bool allowDataFetch) =>
        context is PublisherWebResourceContext.Stylesheet
            or PublisherWebResourceContext.Image
            or PublisherWebResourceContext.Media
            or PublisherWebResourceContext.Font
            or PublisherWebResourceContext.Script
        || (allowDataFetch
            && context is PublisherWebResourceContext.XmlHttpRequest or PublisherWebResourceContext.Fetch);

    public static bool IsExactSkportSessionProbeUri(Uri uri, string method)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return method == "GET"
            && uri.IsAbsoluteUri
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, SkportSessionProbeUri.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, SkportSessionProbeUri.AbsolutePath, StringComparison.Ordinal);
    }

    public static bool IsAuthenticatedSkportSessionResponse(
        int statusCode,
        string? contentType,
        ReadOnlyMemory<byte> utf8Json)
    {
        if (statusCode != 200
            || !IsJsonContentType(contentType)
            || utf8Json.IsEmpty
            || utf8Json.Length > MaximumResourceResponseBytes)
            return false;

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && TryGetUniqueProperty(root, "code", out var codeProperty)
                && codeProperty.ValueKind == JsonValueKind.Number
                && codeProperty.TryGetInt32(out var code)
                && code == 0
                && TryGetUniqueProperty(root, "data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && TryGetUniqueProperty(data, "content", out var content)
                && content.ValueKind == JsonValueKind.String
                // The official SDK treats data.content as the account token.
                // ValueEquals avoids materializing or retaining that secret.
                && !content.ValueEquals(ReadOnlySpan<byte>.Empty);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static PublisherSessionProof ClassifySkportSessionResponse(
        int statusCode,
        string? contentType,
        ReadOnlyMemory<byte> utf8Json)
    {
        if (statusCode is 401 or 403) return PublisherSessionProof.LoginRequired;
        return IsAuthenticatedSkportSessionResponse(statusCode, contentType, utf8Json)
            ? PublisherSessionProof.Authenticated
            : PublisherSessionProof.NeedsReview;
    }

    public static bool IsExactCheckInResponseUri(string gameId, Uri uri, string method) =>
        IsExactCheckInResponseUri(
            gameId,
            uri,
            method,
            expectedBinding: null,
            allowAccountWideStatus: true);

    public static bool IsExactCheckInResponseUri(
        string gameId,
        Uri uri,
        string method,
        PublisherRoleBinding? expectedBinding,
        bool allowAccountWideStatus)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsCheckInResponseEndpoint(gameId, uri, method)
            || !CheckInResponseEndpoints.TryGetValue(gameId, out var endpoint))
            return false;

        if (gameId == "ae") return string.IsNullOrEmpty(uri.Query);
        if (method == "POST")
        {
            // The reviewed 2026-08-02 Genshin production page sends its
            // language as the claim URL's sole query field and keeps act_id in
            // the JSON body. HSR and ZZZ still use the query-free claim URL.
            if (gameId != "gi") return string.IsNullOrEmpty(uri.Query);
            var claimQuery = ParseBoundedQuery(uri.Query, "lang");
            return claimQuery is not null
                && claimQuery.Count == 1
                && claimQuery.TryGetValue("lang", out var claimLanguage)
                && IsBoundedLanguage(claimLanguage);
        }
        if (expectedBinding is not null
            && !IsValidRoleBinding(gameId, expectedBinding))
            return false;

        var query = ParseBoundedStatusQuery(uri.Query);
        if (query is null
            || !query.TryGetValue("act_id", out var actId)
            || !string.Equals(actId, endpoint.ActId, StringComparison.Ordinal)
            || (query.TryGetValue("lang", out var language)
                && !IsBoundedLanguage(language))
            || query.Any(static pair =>
                pair.Value.Length == 0
                && pair.Key is not ("region" or "uid")))
            return false;

        // The publisher may add bounded metadata to this read-only request.
        // Its Daily status API is sometimes account-scoped and omits role
        // fields even after Nyx independently proved a selected role from the
        // same isolated profile. A supplied role pair must still match exactly.
        var hasUid = query.TryGetValue("uid", out var uid);
        var hasRegion = query.TryGetValue("region", out var region);
        if (hasUid != hasRegion)
            return false;
        if (!hasUid || (uid!.Length == 0 && region!.Length == 0))
            return allowAccountWideStatus || expectedBinding is not null;
        if (uid!.Length == 0
            || region!.Length == 0
            || uid.Length > 20
            || !uid.All(char.IsAsciiDigit)
            || !endpoint.Servers!.Contains(region))
            return false;
        return expectedBinding is null
            || (string.Equals(uid, expectedBinding.RoleId, StringComparison.Ordinal)
                && string.Equals(region, expectedBinding.Server, StringComparison.Ordinal));
    }

    public static bool IsCheckInResponseEndpoint(string gameId, Uri uri, string method)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!CheckInResponseEndpoints.TryGetValue(gameId, out var endpoint)
            || method is not ("GET" or "POST")
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = method == "GET" ? endpoint.InfoUri : endpoint.ClaimUri;
        return string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal);
    }

    public static PublisherCheckInProof ParseCheckInResponse(
        string gameId,
        string method,
        ReadOnlyMemory<byte> utf8Json,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant)
    {
        if (!CheckInResponseEndpoints.ContainsKey(gameId)
            || method is not ("GET" or "POST")
            || utf8Json.IsEmpty
            || utf8Json.Length > MaximumResourceResponseBytes)
            return PublisherCheckInProof.Invalid;

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            return gameId == "ae"
                ? ParseEndfieldCheckInResponse(method, document.RootElement, expectedInstant)
                : ParseHoyoCheckInResponse(
                    method,
                    document.RootElement,
                    expectedDate,
                    expectedInstant);
        }
        catch (JsonException)
        {
            return PublisherCheckInProof.Invalid;
        }
    }

    public static PublisherCheckInProof ClassifyCheckInResponse(
        int statusCode,
        string? contentType,
        string gameId,
        string method,
        ReadOnlyMemory<byte> utf8Json,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant)
    {
        if (statusCode is 401 or 403) return PublisherCheckInProof.LoginNeeded;
        if (statusCode != 200 || !IsJsonContentType(contentType))
            return PublisherCheckInProof.Invalid;
        return ParseCheckInResponse(
            gameId,
            method,
            utf8Json,
            expectedDate,
            expectedInstant);
    }

    public static bool IsExactResourceResponseUri(string gameId, Uri uri)
        => TryGetResourceBinding(gameId, uri, out _);

    public static PublisherResourceFetchContract GetResourceFetchContract(string gameId)
    {
        if (!ResourceGameBusinesses.TryGetValue(gameId, out var gameBusiness)
            || !ResourceResponseEndpoints.TryGetValue(gameId, out var noteEndpoint))
            throw new ArgumentOutOfRangeException(nameof(gameId));
        IReadOnlyList<string> regions = gameId switch
        {
            "gi" => ["os_usa", "os_euro", "os_asia", "os_cht"],
            "hsr" =>
            [
                "prod_official_usa",
                "prod_official_eur",
                "prod_official_asia",
                "prod_official_cht",
            ],
            "zzz" => ["prod_gf_us", "prod_gf_eu", "prod_gf_jp", "prod_gf_sg"],
            _ => throw new ArgumentOutOfRangeException(nameof(gameId)),
        };
        return new(
            gameBusiness,
            HsrRoleDiscoveryEndpoint,
            noteEndpoint,
            regions);
    }

    public static bool IsExactResourceRoleDiscoveryRequest(
        string gameId,
        Uri uri,
        string method)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method is not ("GET" or "OPTIONS")
            || !ResourceGameBusinesses.TryGetValue(gameId, out var expectedGameBiz)
            || !ResourceServers.TryGetValue(gameId, out var servers)
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                HsrRoleDiscoveryEndpoint.Host,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath,
                HsrRoleDiscoveryEndpoint.AbsolutePath,
                StringComparison.Ordinal))
            return false;

        var query = ParseBoundedQuery(uri.Query, "game_biz", "region");
        return query is not null
            && query.Count == 2
            && query.TryGetValue("game_biz", out var gameBiz)
            && string.Equals(gameBiz, expectedGameBiz, StringComparison.Ordinal)
            && query.TryGetValue("region", out var region)
            && servers.Contains(region);
    }

    public static bool TryGetResourceBinding(
        string gameId,
        Uri uri,
        out PublisherRoleBinding? binding)
    {
        binding = null;
        ArgumentNullException.ThrowIfNull(uri);
        if (!ResourceResponseEndpoints.TryGetValue(gameId, out var expected)
            || !ResourceServers.TryGetValue(gameId, out var servers)
            || !uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal))
            return false;

        var query = ParseBoundedQuery(uri.Query, "role_id", "server");
        if (query is null
            || query.Count != 2
            || !query.TryGetValue("role_id", out var roleId)
            || roleId.Length is <= 0 or > 20
            || !roleId.All(char.IsAsciiDigit)
            || !query.TryGetValue("server", out var server)
            || !servers.Contains(server))
            return false;
        binding = new(roleId, server);
        return true;
    }

    public static bool IsValidRoleBinding(string gameId, PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return ResourceServers.TryGetValue(gameId, out var servers)
            && binding.RoleId.Length is > 0 and <= 20
            && binding.RoleId.All(char.IsAsciiDigit)
            && servers.Contains(binding.Server);
    }

    public static IReadOnlyList<PublisherRoleChoice> CreateRoleChoices(
        string gameId,
        IReadOnlyCollection<PublisherResourceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 2 or > 8) return Array.Empty<PublisherRoleChoice>();
        if (candidates.Any(candidate => !IsValidRoleBinding(gameId, candidate.Binding)
            || (candidate.Nickname is not null
                && !PublisherResourceTriggerResultParser.IsValidNickname(candidate.Nickname))))
            return Array.Empty<PublisherRoleChoice>();

        var grouped = candidates
            .GroupBy(static candidate => candidate.Binding)
            .ToArray();
        if (grouped.Length is < 2 or > 8) return Array.Empty<PublisherRoleChoice>();
        var choices = new List<PublisherRoleChoice>(grouped.Length);
        foreach (var group in grouped
            .OrderBy(static group => group.Key.Server, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.RoleId, StringComparer.Ordinal))
        {
            var nicknames = group
                .Select(static candidate => candidate.Nickname)
                .Where(static nickname => nickname is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (nicknames.Length > 1) return Array.Empty<PublisherRoleChoice>();
            var nickname = nicknames.SingleOrDefault();
            var identity = $"UID {group.Key.RoleId} · {RegionLabel(group.Key.Server)}";
            choices.Add(new(
                group.Key,
                nickname is null
                    ? identity
                    : $"{nickname} · {identity}"));
        }
        return choices;
    }

    public static PublisherResourceSnapshot? SelectResourceForBinding(
        IReadOnlyCollection<PublisherResourceCandidate> candidates,
        PublisherRoleBinding binding)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(binding);
        return SelectUnambiguousResource(
            candidates.Where(candidate => candidate.Binding == binding).ToArray());
    }

    private static string RegionLabel(string server) => server switch
    {
        "os_usa" or "prod_official_usa" or "prod_gf_us" => "Americas",
        "os_euro" or "prod_official_eur" or "prod_gf_eu" => "Europe",
        "os_asia" or "prod_official_asia" or "prod_gf_jp" or "prod_gf_sg" => "Asia",
        "os_cht" or "prod_official_cht" => "TW/HK/MO",
        _ => "Official region",
    };

    public static PublisherResourceSnapshot? SelectUnambiguousResource(
        IReadOnlyCollection<PublisherResourceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > 8 || candidates.Any(static candidate => candidate.Snapshot is null))
            return null;

        var first = candidates.First();
        if (candidates.Any(candidate => candidate.Binding != first.Binding))
            return null;
        var snapshots = candidates.Select(static candidate => candidate.Snapshot!).ToArray();
        if (snapshots.Any(snapshot => !SameResourceValue(snapshot, snapshots[0])))
            return null;
        return snapshots.MaxBy(static snapshot => snapshot.ObservedAt);
    }

    public static PublisherResourceProof ParseResourceResponse(
        string gameId,
        ReadOnlyMemory<byte> utf8Json,
        DateTimeOffset observedAt,
        out PublisherResourceSnapshot? snapshot) =>
        ParseResourceResponse(
            gameId,
            utf8Json,
            observedAt,
            out snapshot,
            out _);

    public static PublisherResourceProof ParseResourceResponse(
        string gameId,
        ReadOnlyMemory<byte> utf8Json,
        DateTimeOffset observedAt,
        out PublisherResourceSnapshot? snapshot,
        out PublisherResourceCaptureDiagnostic diagnostic)
    {
        snapshot = null;
        diagnostic = PublisherResourceCaptureDiagnostic.EnvelopeRejected;
        if (!ResourceResponseEndpoints.ContainsKey(gameId) || utf8Json.IsEmpty)
            return PublisherResourceProof.Invalid;
        if (utf8Json.Length > MaximumResourceResponseBytes)
        {
            diagnostic = PublisherResourceCaptureDiagnostic.BoundsRejected;
            return PublisherResourceProof.Invalid;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt32(root, "retcode", out var retcode))
                return PublisherResourceProof.Invalid;
            if (retcode == -100)
            {
                diagnostic = PublisherResourceCaptureDiagnostic.LoginRequired;
                return PublisherResourceProof.LoginNeeded;
            }
            if (retcode != 0)
            {
                diagnostic = PublisherResourceCaptureDiagnostic.PublisherResultRejected;
                return PublisherResourceProof.Invalid;
            }
            if (!TryGetUniqueProperty(root, "data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                diagnostic = PublisherResourceCaptureDiagnostic.DataRejected;
                return PublisherResourceProof.Invalid;
            }

            int current;
            int maximum;
            int recoverySeconds;
            int? reserve = null;
            var resourceValuesAreIndependent = false;
            switch (gameId)
            {
                case "gi":
                    if (!TryGetInt32(data, "current_resin", out current)
                        || !TryGetInt32(data, "max_resin", out maximum))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.CoreFieldsRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (!TryGetInt32(data, "resin_recovery_time", out recoverySeconds))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.TimeFieldsRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    break;
                case "hsr":
                    if (!TryGetInt32(data, "current_stamina", out current)
                        || !TryGetInt32(data, "max_stamina", out maximum))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.CoreFieldsRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (!TryGetInt32(data, "stamina_recover_time", out recoverySeconds))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.TimeFieldsRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (!TryGetInt32(data, "current_reserve_stamina", out var parsedReserve))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.ReserveRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (parsedReserve is < 0 or > 10000 || recoverySeconds < -604800)
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.BoundsRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (recoverySeconds < 1) recoverySeconds = 0;
                    // The reviewed HSR UI does not infer relationships between
                    // current, maximum, and recovery; it bounds each independently.
                    resourceValuesAreIndependent = true;
                    reserve = parsedReserve;
                    break;
                case "zzz":
                    if (!TryGetUniqueProperty(data, "energy", out var energy)
                        || energy.ValueKind != JsonValueKind.Object
                        || !TryGetUniqueProperty(energy, "progress", out var progress)
                        || progress.ValueKind != JsonValueKind.Object)
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.DataRejected;
                        return PublisherResourceProof.Invalid;
                    }
                    if (!TryGetInt32(progress, "current", out current)
                        || !TryGetInt32(progress, "max", out maximum))
                    {
                        diagnostic = PublisherResourceCaptureDiagnostic.CoreFieldsRejected;
                        return PublisherResourceProof.Invalid;
                    }

                    var currentTimeFieldCount = 0;
                    var restoreFieldCount = 0;
                    foreach (var property in energy.EnumerateObject())
                    {
                        if (property.NameEquals("day_type")
                            || property.NameEquals("hour")
                            || property.NameEquals("minute"))
                            currentTimeFieldCount++;
                        else if (property.NameEquals("restore"))
                            restoreFieldCount++;
                    }

                    if (currentTimeFieldCount == 0)
                    {
                        if (restoreFieldCount != 1
                            || !TryGetInt32(energy, "restore", out recoverySeconds))
                        {
                            diagnostic = PublisherResourceCaptureDiagnostic.TimeFieldsRejected;
                            return PublisherResourceProof.Invalid;
                        }
                    }
                    else
                    {
                        if (currentTimeFieldCount != 3
                            || restoreFieldCount > 1
                            || !TryGetInt32(energy, "day_type", out var dayType)
                            || dayType is not (1 or 2)
                            || !TryGetInt32(energy, "hour", out var hour)
                            || !TryGetInt32(energy, "minute", out var minute))
                        {
                            diagnostic = PublisherResourceCaptureDiagnostic.TimeFieldsRejected;
                            return PublisherResourceProof.Invalid;
                        }
                        if (hour is < 0 or > 23 || minute is < 0 or > 59)
                        {
                            diagnostic = PublisherResourceCaptureDiagnostic.BoundsRejected;
                            return PublisherResourceProof.Invalid;
                        }

                        // The live contract can expose both the bounded recovery
                        // duration and a complete target clock. Validate the clock
                        // without deriving a local-time countdown; restore remains
                        // the authoritative duration when it is present.
                        if (restoreFieldCount == 0)
                        {
                            recoverySeconds = 0;
                        }
                        else if (!TryGetInt32(energy, "restore", out recoverySeconds))
                        {
                            diagnostic = PublisherResourceCaptureDiagnostic.TimeFieldsRejected;
                            return PublisherResourceProof.Invalid;
                        }
                    }
                    break;
                default:
                    return PublisherResourceProof.Invalid;
            }

            if (current < 0
                || current > 10000
                || maximum is <= 0 or > 10000
                || (current > maximum && !resourceValuesAreIndependent)
                || recoverySeconds is < 0 or > 604800
                || (current == maximum
                    && recoverySeconds != 0
                    && !resourceValuesAreIndependent))
            {
                diagnostic = PublisherResourceCaptureDiagnostic.BoundsRejected;
                return PublisherResourceProof.Invalid;
            }

            snapshot = new(
                gameId,
                Get(gameId).ResourceName,
                current,
                maximum,
                observedAt,
                RecoverySeconds: recoverySeconds,
                Reserve: reserve);
            diagnostic = PublisherResourceCaptureDiagnostic.Valid;
            return PublisherResourceProof.Valid;
        }
        catch (JsonException)
        {
            diagnostic = PublisherResourceCaptureDiagnostic.EnvelopeRejected;
            return PublisherResourceProof.Invalid;
        }
    }

    public static bool TryParseResourceResponse(
        string gameId,
        ReadOnlyMemory<byte> utf8Json,
        DateTimeOffset observedAt,
        out PublisherResourceSnapshot? snapshot) =>
        ParseResourceResponse(gameId, utf8Json, observedAt, out snapshot) == PublisherResourceProof.Valid;

    private static bool SameResourceValue(
        PublisherResourceSnapshot left,
        PublisherResourceSnapshot right) =>
        string.Equals(left.GameId, right.GameId, StringComparison.Ordinal)
        && string.Equals(left.ResourceName, right.ResourceName, StringComparison.Ordinal)
        && left.Current == right.Current
        && left.Maximum == right.Maximum
        && left.IsStale == right.IsStale
        && left.RecoverySeconds == right.RecoverySeconds
        && left.Reserve == right.Reserve;

    private static PublisherCheckInProof ParseHoyoCheckInResponse(
        string method,
        JsonElement root,
        DateOnly expectedDate,
        DateTimeOffset expectedInstant)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetInt32(root, "retcode", out var retcode))
            return PublisherCheckInProof.Invalid;
        if (retcode == -100) return PublisherCheckInProof.LoginNeeded;
        if (retcode != 0
            || !TryGetUniqueProperty(root, "data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return PublisherCheckInProof.Invalid;
        if (method == "POST") return PublisherCheckInProof.ClaimAccepted;

        if (!TryGetUniqueProperty(data, "is_sign", out var isSign)
            || isSign.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetInt32(data, "total_sign_day", out var totalSignDay)
            || totalSignDay is < 0 or > 366)
            return PublisherCheckInProof.Invalid;

        // The current official HSR/ZZZ client only consumes is_sign and
        // total_sign_day. Some responses still include today, while others do
        // not. When present, keep it unique and accept either the user's local
        // date or HoYoLAB's UTC+8 service date around the reset boundary.
        var todayCount = 0;
        JsonElement today = default;
        foreach (var property in data.EnumerateObject())
        {
            if (!property.NameEquals("today")) continue;
            today = property.Value;
            if (++todayCount > 1) return PublisherCheckInProof.Invalid;
        }
        if (todayCount == 1)
        {
            var serviceDate = DateOnly.FromDateTime(
                expectedInstant.ToOffset(TimeSpan.FromHours(8)).DateTime);
            if (today.ValueKind != JsonValueKind.String
                || today.GetString() is not { Length: 10 } todayText
                || !DateOnly.TryParseExact(
                    todayText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var responseDate)
                || (responseDate != expectedDate && responseDate != serviceDate))
                return PublisherCheckInProof.Invalid;
        }
        return isSign.GetBoolean()
            ? PublisherCheckInProof.Claimed
            : PublisherCheckInProof.Ready;
    }

    private static PublisherCheckInProof ParseEndfieldCheckInResponse(
        string method,
        JsonElement root,
        DateTimeOffset expectedInstant)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetInt32(root, "code", out var code)
            || code != 0
            || !TryGetUniqueProperty(root, "data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return PublisherCheckInProof.Invalid;

        if (method == "POST")
        {
            if (!TryGetFreshEndfieldTimestamp(data, "ts", expectedInstant)
                || !TryGetUniqueProperty(data, "awardIds", out var awards)
                || !IsBoundedEndfieldAwardArray(awards, requireNonEmpty: true)
                || !TryGetUniqueProperty(data, "tomorrowAwardIds", out var tomorrowAwards)
                || !IsBoundedEndfieldAwardArray(tomorrowAwards, requireNonEmpty: false)
                || !TryGetUniqueProperty(data, "resourceInfoMap", out var resources)
                || resources.ValueKind != JsonValueKind.Object)
                return PublisherCheckInProof.Invalid;
            return PublisherCheckInProof.ClaimAccepted;
        }

        if (!TryGetFreshEndfieldTimestamp(data, "currentTs", expectedInstant)
            || !TryGetUniqueProperty(data, "hasToday", out var hasToday)
            || hasToday.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetUniqueProperty(data, "calendar", out var calendar)
            || calendar.ValueKind != JsonValueKind.Array
            || calendar.GetArrayLength() is < 1 or > 62
            || !TryGetUniqueProperty(data, "first", out var first)
            || first.ValueKind != JsonValueKind.Array
            || first.GetArrayLength() > 16
            || !TryGetUniqueProperty(data, "resourceInfoMap", out var resourceInfoMap)
            || resourceInfoMap.ValueKind != JsonValueKind.Object)
            return PublisherCheckInProof.Invalid;

        var availableCount = 0;
        var doneCount = 0;
        var awardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in calendar.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(item, "awardId", out var awardId)
                || awardId.ValueKind != JsonValueKind.String
                || awardId.GetString() is not { Length: > 0 and <= 64 } awardIdText
                || !awardIds.Add(awardIdText)
                || !TryGetUniqueProperty(item, "available", out var available)
                || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !TryGetUniqueProperty(item, "done", out var done)
                || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return PublisherCheckInProof.Invalid;
            if (done.GetBoolean()) doneCount++;
            if (!available.GetBoolean()) continue;
            if (done.GetBoolean() || ++availableCount > 1) return PublisherCheckInProof.Invalid;
        }
        // The current official UI uses hasToday to keep the selected day on
        // the last completed reward. A response cannot therefore be both
        // checked today and expose a claimable reward (or have no completed
        // calendar reward at all). `available` remains the primary proof.
        if (hasToday.GetBoolean() && (availableCount != 0 || doneCount == 0))
            return PublisherCheckInProof.Invalid;
        if (availableCount == 0 && doneCount == 0)
            return PublisherCheckInProof.Invalid;
        return availableCount == 1
            ? PublisherCheckInProof.Ready
            : PublisherCheckInProof.Claimed;
    }

    private static bool IsBoundedEndfieldAwardArray(JsonElement array, bool requireNonEmpty)
    {
        if (array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() > 16
            || (requireNonEmpty && array.GetArrayLength() == 0))
            return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var award in array.EnumerateArray())
        {
            if (award.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(award, "id", out var id)
                || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 and <= 64 } idText
                || !ids.Add(idText)
                || !TryGetInt32(award, "type", out var type)
                || type is < 1 or > 3)
                return false;
        }
        return true;
    }

    private static bool TryGetFreshEndfieldTimestamp(
        JsonElement parent,
        string name,
        DateTimeOffset expectedInstant)
    {
        if (!TryGetUniqueProperty(parent, name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        var text = property.GetString();
        if (text is not { Length: 10 }
            || !text.All(char.IsAsciiDigit)
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        DateTimeOffset serverInstant;
        try
        {
            // The reviewed anonymous endpoint advances with Unix seconds. The
            // official game notices define reset at 04:00 in UTC+8 (Asia) or
            // UTC-5 (Americas/Europe). The response does not prove its region,
            // so require agreement under both possible server calendars.
            serverInstant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var expectedUtc = expectedInstant.ToUniversalTime();
        if (serverInstant < expectedUtc - EndfieldMaximumPastSkew
            || serverInstant > expectedUtc + EndfieldMaximumFutureSkew)
            return false;
        return IsSameEndfieldResetDay(serverInstant, expectedUtc, EndfieldAsiaServerOffset)
            && IsSameEndfieldResetDay(serverInstant, expectedUtc, EndfieldAmericasEuropeServerOffset);
    }

    private static bool IsSameEndfieldResetDay(
        DateTimeOffset serverInstant,
        DateTimeOffset expectedInstant,
        TimeSpan serverOffset)
    {
        static DateOnly ResetDay(DateTimeOffset instant, TimeSpan offset) =>
            DateOnly.FromDateTime(instant.ToOffset(offset).AddHours(-4).DateTime);

        return ResetDay(serverInstant, serverOffset) == ResetDay(expectedInstant, serverOffset);
    }

    private static bool IsBoundedLanguage(string language) =>
        language.Length is >= 2 and <= 16
        && language.All(static character => char.IsAsciiLetterLower(character) || character == '-');

    private static bool IsExactQuery(
        string query,
        params (string Key, string Value)[] expectedPairs)
    {
        var parsed = ParseBoundedQuery(
            query,
            expectedPairs.Select(static pair => pair.Key).ToArray());
        return parsed is not null
            && parsed.Count == expectedPairs.Length
            && expectedPairs.All(pair =>
                parsed.TryGetValue(pair.Key, out var value)
                && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetUniqueProperty(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName)) continue;
            if (++count > 1) return false;
            value = property.Value;
        }
        return count == 1;
    }

    private static Dictionary<string, string>? ParseBoundedQuery(string query, params string[] allowedKeys)
    {
        if (query.Length is <= 1 or > 256 || query[0] != '?') return null;
        var allowed = allowedKeys.ToHashSet(StringComparer.Ordinal);
        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query[1..].Split('&', StringSplitOptions.None))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || separator == pair.Length - 1) return null;
                var key = pair[..separator];
                var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
                if (!allowed.Contains(key)
                    || value.Length > 64
                    || value.Any(char.IsControl)
                    || !result.TryAdd(key, value))
                    return null;
            }
            return result;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? ParseBoundedStatusQuery(string query)
    {
        if (query.Length is <= 1 or > 256 || query[0] != '?') return null;
        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query[1..].Split('&', StringSplitOptions.None))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0) return null;
                var encodedKey = pair[..separator];
                var encodedValue = pair[(separator + 1)..];
                if (!HasValidPercentEncoding(encodedKey)
                    || !HasValidPercentEncoding(encodedValue))
                    return null;
                var key = Uri.UnescapeDataString(encodedKey);
                var value = Uri.UnescapeDataString(encodedValue);
                if (key.Length is <= 0 or > 64
                    || value.Length > 64
                    || key.Any(char.IsControl)
                    || value.Any(char.IsControl)
                    || !result.TryAdd(key, value))
                    return null;
            }
            return result;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }
        return true;
    }

    private static Dictionary<string, string>? ParseConnectQuery(string query, params string[] allowedKeys)
    {
        if (query.Length is <= 1 or > 2048 || query[0] != '?') return null;
        var allowed = allowedKeys.ToHashSet(StringComparer.Ordinal);
        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query[1..].Split('&', StringSplitOptions.None))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || separator == pair.Length - 1) return null;
                var key = pair[..separator];
                var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
                if (!allowed.Contains(key)
                    || value.Length > 4096
                    || value.Any(char.IsControl)
                    || !result.TryAdd(key, value))
                    return null;
            }
            return result;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool TryGetInt32(JsonElement parent, string propertyName, out int value)
    {
        value = default;
        if (!TryGetUniqueProperty(parent, propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out value);
        if (property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        return text is { Length: > 0 and <= 10 }
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
