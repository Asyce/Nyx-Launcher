using System.Globalization;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

internal sealed record WuWaPlayerIdentity(string PlayerId, string Region);

internal sealed class WuWaAccountStatusResponseParser
{
    internal bool TryParsePlayerInfo(ReadOnlyMemory<byte> utf8, out WuWaPlayerIdentity? identity)
    {
        identity = null;
        try
        {
            using var document = Parse(utf8);
            if (!TryGetSuccessfulData(document.RootElement, out var data)
                || data.ValueKind is not JsonValueKind.Object) return false;
            var identities = new List<WuWaPlayerIdentity>();
            foreach (var regionProperty in data.EnumerateObject())
            {
                if (!IsSafeIdentifier(regionProperty.Name, 64)
                    || regionProperty.Value.ValueKind is not JsonValueKind.String) continue;
                var nestedJson = regionProperty.Value.GetString();
                if (string.IsNullOrWhiteSpace(nestedJson)
                    || nestedJson.Length > WuWaAccountStatusTransport.MaximumResponseBytes) continue;
                using var regionDocument = JsonDocument.Parse(nestedJson, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
                CollectRoleIds(regionDocument.RootElement, regionProperty.Name, identities, 0);
            }
            var distinct = identities.Distinct().ToArray();
            if (distinct.Length != 1) return false;
            identity = distinct[0];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal bool TryParseRole(
        ReadOnlyMemory<byte> utf8,
        string expectedRegion,
        out WuWaAccountStatusSnapshot? snapshot)
    {
        snapshot = null;
        if (!IsSafeIdentifier(expectedRegion, 64)) return false;
        try
        {
            using var document = Parse(utf8);
            if (!TryGetSuccessfulData(document.RootElement, out var data)
                || data.ValueKind is not JsonValueKind.Object
                || !data.TryGetProperty(expectedRegion, out var nested)
                || nested.ValueKind is not JsonValueKind.String)
                return false;

            var nestedJson = nested.GetString();
            if (string.IsNullOrWhiteSpace(nestedJson)
                || nestedJson.Length > WuWaAccountStatusTransport.MaximumResponseBytes)
                return false;
            using var roleDocument = JsonDocument.Parse(nestedJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var role = roleDocument.RootElement;
            if (role.ValueKind is not JsonValueKind.Object
                || !role.TryGetProperty("Base", out var roleBase)
                || roleBase.ValueKind is not JsonValueKind.Object
                || !TryGetInt(roleBase, "Energy", out var energy)
                || !TryGetInt(roleBase, "MaxEnergy", out var maxEnergy)
                || !TryGetInt(roleBase, "StoreEnergy", out var storeEnergy)
                || !TryGetLong(roleBase, "StoreEnergyRecoverTime", out var storeRecover)
                || !TryGetLong(roleBase, "EnergyRecoverTime", out var recover)
                || !TryGetInt(roleBase, "Liveness", out var liveness)
                || !TryGetInt(roleBase, "LivenessMaxCount", out var livenessMax)
                || energy < 0 || maxEnergy <= 0 || energy > maxEnergy
                || storeEnergy < 0 || liveness < 0 || livenessMax <= 0 || liveness > livenessMax)
                return false;

            if (!WuWaAccountStatusRules.IsValidRecoverySeconds(storeRecover)) storeRecover = 0;
            if (!WuWaAccountStatusRules.IsValidRecoverySeconds(recover)) recover = 0;
            snapshot = new(energy, maxEnergy, storeEnergy, storeRecover, recover, liveness, livenessMax);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal bool IsRejected(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = Parse(utf8);
            return ClassifyResponseCode(document.RootElement) == ResponseCodeKind.Failure;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal bool IsRedisEmpty(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = Parse(utf8);
            var root = document.RootElement;
            return TryReadSoleResponseCode(root, out var name, out var value)
                && name == "code"
                && value == 1005;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> utf8) => JsonDocument.Parse(utf8, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    });

    private static bool TryGetSuccessfulData(JsonElement root, out JsonElement data)
    {
        data = default;
        return root.ValueKind is JsonValueKind.Object
            && ClassifyResponseCode(root) == ResponseCodeKind.Success
            && root.TryGetProperty("data", out data);
    }

    private static ResponseCodeKind ClassifyResponseCode(JsonElement root)
    {
        if (!TryReadSoleResponseCode(root, out _, out var value))
            return ResponseCodeKind.Invalid;
        return value is 0 or 200 ? ResponseCodeKind.Success : ResponseCodeKind.Failure;
    }

    private static bool TryReadSoleResponseCode(
        JsonElement root,
        out string? name,
        out long value)
    {
        name = null;
        value = default;
        if (root.ValueKind is not JsonValueKind.Object) return false;

        JsonElement code = default;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("code") && !property.NameEquals("retcode")) continue;
            if (name is not null) return false;
            name = property.Name;
            code = property.Value;
        }

        return name is not null && TryReadLong(code, out value);
    }

    private enum ResponseCodeKind
    {
        Invalid,
        Success,
        Failure,
    }

    private static void CollectRoleIds(
        JsonElement element,
        string region,
        List<WuWaPlayerIdentity> identities,
        int depth)
    {
        if (depth > 12) return;
        if (element.ValueKind is JsonValueKind.Object)
        {
            if (TryReadIdentifier(element, "roleId", 128, out var roleId))
                identities.Add(new(roleId!, region!));
            foreach (var property in element.EnumerateObject())
                CollectRoleIds(property.Value, region, identities, depth + 1);
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectRoleIds(item, region, identities, depth + 1);
        }
    }

    private static bool TryReadIdentifier(
        JsonElement element,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        var candidate = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
        if (!IsSafeIdentifier(candidate, maximumLength)) return false;
        value = candidate;
        return true;
    }

    private static bool IsSafeIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = default;
        return element.TryGetProperty(name, out var property)
            && TryReadLong(property, out var parsed)
            && parsed is >= int.MinValue and <= int.MaxValue
            && (value = (int)parsed) == parsed;
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) && TryReadLong(property, out value);
    }

    private static bool TryReadLong(JsonElement element, out long value)
    {
        value = default;
        if (element.ValueKind is JsonValueKind.Number) return element.TryGetInt64(out value);
        return element.ValueKind is JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
