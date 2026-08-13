using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Core.Exports;

public sealed record AchievementAccountBinding(string Scheme, string Value, string Region)
{
    public const string CurrentScheme = "pengo-install-hmac-v1";

    public override string ToString() => nameof(AchievementAccountBinding);
}

public sealed record HoyoLabHsrAchievementResult(
    IReadOnlyList<long> AchievementIds,
    PublisherRoleBinding Role)
{
    public override string ToString() => nameof(HoyoLabHsrAchievementResult);
}

public static class HoyoLabHsrAchievementResultParser
{
    public const int MaximumAchievementCount = 10_000;
    public const int MaximumScriptResultCharacters = 1_048_576;
    public const long MaximumAchievementId = 9_007_199_254_740_991;

    public static HoyoLabHsrAchievementResult Parse(
        string scriptResult,
        IReadOnlySet<long> currentCatalogIds,
        PublisherRoleBinding? expectedRole = null)
    {
        ArgumentNullException.ThrowIfNull(currentCatalogIds);
        if (string.IsNullOrWhiteSpace(scriptResult)
            || scriptResult.Length > MaximumScriptResultCharacters)
            throw new ExportProviderException("hoyolab-script-result-invalid");

        JsonDocument outer;
        try
        {
            outer = JsonDocument.Parse(scriptResult, StrictOptions);
        }
        catch (JsonException)
        {
            throw new ExportProviderException("hoyolab-script-result-json-invalid");
        }
        using (outer)
        {
            if (outer.RootElement.ValueKind != JsonValueKind.String)
                throw new ExportProviderException("hoyolab-script-result-not-string");
            var payload = outer.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(payload)
                || payload.Length > MaximumScriptResultCharacters)
                throw new ExportProviderException("hoyolab-script-payload-invalid");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload, StrictOptions);
            }
            catch (JsonException)
            {
                throw new ExportProviderException("hoyolab-script-payload-json-invalid");
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(root, "state", "ids", "region", "uid")
                    || !root.TryGetProperty("state", out var state)
                    || state.ValueKind != JsonValueKind.String)
                    throw new ExportProviderException("hoyolab-script-payload-shape-invalid");

                var stateValue = state.GetString();
                if (!string.Equals(stateValue, "ok", StringComparison.Ordinal))
                {
                    if (TryParseRetcodeState(stateValue, "login-retcode:", out var loginRetcode))
                        throw new ExportProviderException($"hoyolab-login-retcode-{loginRetcode}");
                    if (TryParseRetcodeState(stateValue, "list-retcode:", out var retcode))
                        throw new ExportProviderException($"hoyolab-list-retcode-{retcode}");
                    throw new ExportProviderException(stateValue switch
                    {
                        "login-required" => "hoyolab-login-required",
                        "timed-out" => "timed-out",
                        "login-request" => "hoyolab-login-request-failed",
                        "login-processing" => "hoyolab-login-processing-failed",
                        "login-response" => "hoyolab-login-response-invalid",
                        "login-envelope" => "hoyolab-login-envelope-invalid",
                        "login-retcode" => "hoyolab-login-retcode-failed",
                        "login-data" => "hoyolab-login-data-invalid",
                        "login-binding" => "hoyolab-login-binding-invalid",
                        "role-request" => "hoyolab-role-request-failed",
                        "role-processing" => "hoyolab-role-processing-failed",
                        "role-response" => "hoyolab-role-response-invalid",
                        "role-envelope" => "hoyolab-role-envelope-invalid",
                        "role-retcode" => "hoyolab-role-retcode-failed",
                        "role-data" => "hoyolab-role-data-invalid",
                        "role-shape" => "hoyolab-role-shape-invalid",
                        "role-row" => "hoyolab-role-row-invalid",
                        "role-duplicate" => "hoyolab-role-duplicate-invalid",
                        "role-none" => "hoyolab-role-none",
                        "role-multiple" => "hoyolab-role-selection-required",
                        "role-changed" => "hoyolab-role-selection-required",
                        "session-chunks" => "hoyolab-session-chunks-unavailable",
                        "session-require" => "hoyolab-session-require-unavailable",
                        "session-vue" => "hoyolab-session-vue-unavailable",
                        "session-missing" => "hoyolab-session-client-unavailable",
                        "session-account" => "hoyolab-session-account-unavailable",
                        "session-role" => "hoyolab-session-role-unavailable",
                        "session-role-setter" => "hoyolab-session-role-setter-unavailable",
                        "session-role-bind" => "hoyolab-session-role-bind-failed",
                        "session-role-region" => "hoyolab-session-role-region-mismatch",
                        "session-role-uid" => "hoyolab-session-role-uid-mismatch",
                        "list-request" => "hoyolab-list-request-failed",
                        "list-client" => "hoyolab-list-client-unavailable",
                        "list-processing" => "hoyolab-list-processing-failed",
                        "list-response" => "hoyolab-list-response-invalid",
                        "list-envelope" => "hoyolab-list-envelope-invalid",
                        "list-retcode" => "hoyolab-list-retcode-failed",
                        "list-data" => "hoyolab-list-data-invalid",
                        "list-shape" => "hoyolab-list-shape-invalid",
                        "list-row" => "hoyolab-list-row-invalid",
                        "list-duplicate" => "hoyolab-list-duplicate-invalid",
                        _ => "hoyolab-response-invalid",
                    });
                }

                if (!root.TryGetProperty("region", out var region)
                    || region.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("uid", out var uid)
                    || uid.ValueKind != JsonValueKind.String)
                    throw new ExportProviderException("hoyolab-response-invalid");
                var role = new PublisherRoleBinding(
                    uid.GetString() ?? string.Empty,
                    region.GetString() ?? string.Empty);
                if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", role))
                    throw new ExportProviderException("hoyolab-response-invalid");
                if (expectedRole is not null
                    && (!string.Equals(role.RoleId, expectedRole.RoleId, StringComparison.Ordinal)
                        || !string.Equals(role.Server, expectedRole.Server, StringComparison.Ordinal)))
                    throw new ExportProviderException("hoyolab-role-selection-required");

                if (!root.TryGetProperty("ids", out var ids)
                    || ids.ValueKind != JsonValueKind.Array
                    || ids.GetArrayLength() > MaximumAchievementCount)
                    throw new ExportProviderException("hoyolab-response-invalid");

                var result = new List<long>(ids.GetArrayLength());
                long previous = 0;
                foreach (var item in ids.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number
                        || !item.TryGetInt64(out var id)
                        || id <= previous
                        || id > MaximumAchievementId
                        || !currentCatalogIds.Contains(id))
                        throw new ExportProviderException("hoyolab-response-invalid");
                    result.Add(id);
                    previous = id;
                }
                return new(result.AsReadOnly(), role);
            }
        }
    }

    private static readonly JsonDocumentOptions StrictOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private static bool HasExactProperties(JsonElement root, params string[] names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }
        return seen.SetEquals(expected);
    }

    private static bool TryParseRetcodeState(string? state, string prefix, out int retcode)
    {
        retcode = 0;
        return state is not null
            && state.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                state.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out retcode)
            && retcode is >= -9_999_999 and <= 9_999_999;
    }
}

public sealed class RoutedAchievementExportProvider(
    IAchievementExportProvider game,
    IAchievementExportProvider hoyoLab,
    Func<string, string?>? sourceResolver = null) : IAchievementExportProvider
{
    private readonly IAchievementExportProvider game =
        game ?? throw new ArgumentNullException(nameof(game));
    private readonly IAchievementExportProvider hoyoLab =
        hoyoLab ?? throw new ArgumentNullException(nameof(hoyoLab));
    private readonly Func<string, string?> sourceResolver =
        sourceResolver ?? (static _ => null);

    public ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken) => gameId switch
        {
            "gi" => game.StartAsync(gameId, outputPath, cancellationToken),
            "hsr" when AchievementExportSources.Normalize(
                gameId,
                sourceResolver(gameId)) == AchievementExportSources.Game =>
                    game.StartAsync(gameId, outputPath, cancellationToken),
            "hsr" => hoyoLab.StartAsync(gameId, outputPath, cancellationToken),
            _ => ValueTask.FromException<IAchievementExportSession>(
                new ExportProviderException("achievement-export-unsupported")),
        };
}
