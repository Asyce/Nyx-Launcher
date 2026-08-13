using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nyx.Desktop.Core.AccountStatus;

public sealed record HoyoLabAccountSlot(
    string Id,
    string Label,
    bool IsLegacy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool RemovalPending)
{
    public override string ToString() => nameof(HoyoLabAccountSlot);
}

public sealed record HoyoLabAccountSlotIndex(
    int SchemaVersion,
    string? ActiveSlotId,
    IReadOnlyList<HoyoLabAccountSlot> Slots,
    bool LegacyFallback)
{
    public override string ToString() => nameof(HoyoLabAccountSlotIndex);
}

public enum HoyoLabAccountSlotInitializationState
{
    Ready,
    LegacyCompatibility,
    Unavailable,
}

public sealed record HoyoLabAccountSlotInitializationResult(
    HoyoLabAccountSlotInitializationState State,
    HoyoLabAccountSlotIndex? Index)
{
    public bool IsReady => State == HoyoLabAccountSlotInitializationState.Ready
        && Index is not null;

    public override string ToString() => nameof(HoyoLabAccountSlotInitializationResult);
}

public sealed record HoyoLabAccountSlotManagerState(
    bool Available,
    string? ActiveSlotId,
    IReadOnlyList<HoyoLabAccountSlot> Slots)
{
    public override string ToString() => nameof(HoyoLabAccountSlotManagerState);
}

public sealed record HoyoLabAccountIdentity(
    string SlotId,
    string LocalLabel,
    string? Nickname,
    string? FullUid,
    string? ReadableRegion)
{
    public bool IsBound => FullUid is not null && ReadableRegion is not null;

    public string DisplayName => Nickname ?? LocalLabel;

    public string CharacterSummary => IsBound
        ? $"{DisplayName} · {FullUid} · {ReadableRegion}"
        : $"{DisplayName} · Choose Region";

    public static HoyoLabAccountIdentity Create(
        string gameId,
        HoyoLabAccountSlot slot,
        PublisherRoleRecord? record)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slot.Id)
            || !HoyoLabAccountSlotRules.TryNormalizeLabel(slot.Label, out var label)
            || !string.Equals(label, slot.Label, StringComparison.Ordinal)
            || slot.RemovalPending
            || (record is not null && !PublisherRoleRecordRules.IsValid(gameId, record)))
            throw new ArgumentException("The HoYoLAB account identity is invalid.");

        return new(
            slot.Id,
            label,
            record?.Nickname,
            record?.Binding.RoleId,
            record?.ReadableRegion);
    }

    public override string ToString() => nameof(HoyoLabAccountIdentity);
}

public static class HoyoLabAccountSlotRules
{
    public const int SchemaVersion = 1;
    public const int MaximumSlots = 8;
    public const int MaximumLabelScalars = 48;
    public const int MaximumLabelUtf8Bytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string CreateSlotId()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return Convert.ToHexStringLower(random);
    }

    public static bool IsValidSlotId(string? value)
    {
        if (value is null || value.Length != 32) return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    public static bool TryNormalizeLabel(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null) return false;
        if (value.Any(char.IsControl)) return false;
        var candidate = value.Trim();
        if (candidate.Length == 0 || candidate.Contains('\r') || candidate.Contains('\n'))
            return false;

        int utf8Bytes;
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(candidate);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        if (utf8Bytes is <= 0 or > MaximumLabelUtf8Bytes) return false;

        var scalars = 0;
        var remaining = candidate.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed)
                != OperationStatus.Done)
                return false;
            if (++scalars > MaximumLabelScalars) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
                return false;
            remaining = remaining[consumed..];
        }

        normalized = candidate;
        return true;
    }

    public static bool IsValidIndex(HoyoLabAccountSlotIndex? index)
    {
        if (index is null
            || index.SchemaVersion != SchemaVersion
            || index.Slots is null
            || index.Slots.Count > MaximumSlots)
            return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var legacyCount = 0;
        foreach (var slot in index.Slots)
        {
            if (slot is null
                || !IsValidSlotId(slot.Id)
                || !ids.Add(slot.Id)
                || !TryNormalizeLabel(slot.Label, out var normalized)
                || !string.Equals(slot.Label, normalized, StringComparison.Ordinal)
                || slot.CreatedAt.Offset != TimeSpan.Zero
                || slot.UpdatedAt.Offset != TimeSpan.Zero
                || slot.CreatedAt > slot.UpdatedAt)
                return false;
            if (slot.IsLegacy && ++legacyCount > 1) return false;
        }

        if (legacyCount == 1 && !index.LegacyFallback) return false;
        if (index.ActiveSlotId is null) return true;
        return IsValidSlotId(index.ActiveSlotId)
            && index.Slots.Any(slot =>
                string.Equals(slot.Id, index.ActiveSlotId, StringComparison.Ordinal)
                && !slot.RemovalPending);
    }
}

public static class HoyoLabPasswordCleanupRules
{
    public static bool RequiresCleanup(
        bool targetsValidated,
        IReadOnlyCollection<bool> profileEntriesExist) =>
        !targetsValidated || profileEntriesExist.Any(exists => exists);
}

public sealed record PublisherRoleRecord(
    PublisherRoleBinding Binding,
    string? Nickname,
    string ReadableRegion)
{
    public override string ToString() => nameof(PublisherRoleRecord);
}

public static class PublisherRoleRecordRules
{
    public static string CanonicalRegionLabel(string server) => server switch
    {
        "os_usa" or "prod_official_usa" or "prod_gf_us" => "Americas",
        "os_euro" or "prod_official_eur" or "prod_gf_eu" => "Europe",
        "os_asia" or "prod_official_asia" or "prod_gf_jp" or "prod_gf_sg" => "Asia",
        "os_cht" or "prod_official_cht" => "TW/HK/MO",
        _ => "Official region",
    };

    public static bool IsValid(string gameId, PublisherRoleRecord? record) =>
        record is not null
        && record.Binding is not null
        && PublisherAccountCatalog.IsValidRoleBinding(gameId, record.Binding)
        && (record.Nickname is null
            || PublisherResourceTriggerResultParser.IsValidNickname(record.Nickname))
        && string.Equals(
            record.ReadableRegion,
            CanonicalRegionLabel(record.Binding.Server),
            StringComparison.Ordinal);
}
