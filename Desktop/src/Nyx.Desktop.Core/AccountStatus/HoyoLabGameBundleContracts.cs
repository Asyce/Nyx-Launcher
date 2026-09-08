namespace Nyx.Desktop.Core.AccountStatus;

public sealed record HoyoLabCapabilityConsentSet(
    bool Resources,
    bool Inventory,
    bool Builds,
    bool Achievements,
    bool Exploration,
    bool Endgame,
    bool Events,
    bool Currency)
{
    public override string ToString() => nameof(HoyoLabCapabilityConsentSet);

    public bool IsEnabled(string capability) => capability switch
    {
        HoyoLabGameBundleRules.Resources => Resources,
        HoyoLabGameBundleRules.Inventory => Inventory,
        HoyoLabGameBundleRules.Builds => Builds,
        HoyoLabGameBundleRules.Achievements => Achievements,
        HoyoLabGameBundleRules.Exploration => Exploration,
        HoyoLabGameBundleRules.Endgame => Endgame,
        HoyoLabGameBundleRules.Events => Events,
        HoyoLabGameBundleRules.Currency => Currency,
        _ => false,
    };
}

public sealed record HoyoLabCapabilityObservations(
    DateTimeOffset? Resources,
    DateTimeOffset? Inventory,
    DateTimeOffset? Builds,
    DateTimeOffset? Achievements,
    DateTimeOffset? Exploration,
    DateTimeOffset? Endgame,
    DateTimeOffset? Events,
    DateTimeOffset? Currency)
{
    public override string ToString() => nameof(HoyoLabCapabilityObservations);
}

public sealed record HoyoLabGameBundleRole(
    PublisherRoleRecord Role,
    HoyoLabCapabilityObservations Observations,
    PublisherResourceSnapshot? Resource,
    IReadOnlyList<long>? CompletedHsrAchievementIds,
    HoyoLabGenshinBuildSnapshot? GenshinBuilds = null,
    HoyoLabHsrBuildSnapshot? HsrBuilds = null)
{
    public override string ToString() => nameof(HoyoLabGameBundleRole);
}

public sealed record HoyoLabCapabilityTombstone(
    PublisherRoleBinding Binding,
    string Capability,
    DateTimeOffset DeletedAt)
{
    public override string ToString() => nameof(HoyoLabCapabilityTombstone);
}

public sealed record HoyoLabRoleTombstone(
    PublisherRoleBinding Binding,
    DateTimeOffset DeletedAt)
{
    public override string ToString() => nameof(HoyoLabRoleTombstone);
}

public sealed record HoyoLabGameBundle(
    int SchemaVersion,
    string GameId,
    IReadOnlyList<HoyoLabGameBundleRole> Roles,
    PublisherRoleBinding? SelectedRole,
    HoyoLabCapabilityConsentSet Consents,
    IReadOnlyList<HoyoLabCapabilityTombstone> CapabilityTombstones,
    IReadOnlyList<HoyoLabRoleTombstone> RoleTombstones)
{
    public override string ToString() => nameof(HoyoLabGameBundle);
}

public static class HoyoLabGameBundleRules
{
    public const int SchemaVersion = 2;
    public const int MaximumRoles = 8;
    // ponytail: bounded local history; raise 64 only if real role churn requires it.
    public const int MaximumRoleTombstones = 64;
    public const int MaximumCapabilityTombstones = MaximumRoles * 8;
    public const int MaximumAchievementIds = 10_000;
    public const long MaximumAchievementId = 9_007_199_254_740_991;
    public const string GameId = "hsr";
    public const string GenshinGameId = "gi";
    public const string Resources = "resources";
    public const string Inventory = "inventory";
    public const string Builds = "builds";
    public const string Achievements = "achievements";
    public const string Exploration = "exploration";
    public const string Endgame = "endgame";
    public const string Events = "events";
    public const string Currency = "currency";

    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(
    [
        Resources,
        Inventory,
        Builds,
        Achievements,
        Exploration,
        Endgame,
        Events,
        Currency,
    ]);

    public static bool IsValid(HoyoLabGameBundle? bundle, DateTimeOffset utcNow)
    {
        if (bundle is null
            || bundle.SchemaVersion != SchemaVersion
            || !IsSupportedGame(bundle.GameId)
            || bundle.Roles is null
            || bundle.Roles.Count > MaximumRoles
            || bundle.Consents is null
            || bundle.CapabilityTombstones is null
            || bundle.RoleTombstones is null
            || bundle.CapabilityTombstones.Count > MaximumCapabilityTombstones
            || bundle.RoleTombstones.Count > MaximumRoleTombstones
            || bundle.Consents.Inventory
            || bundle.Consents.Exploration
            || bundle.Consents.Endgame
            || bundle.Consents.Events
            || bundle.Consents.Currency)
            return false;
        if (bundle.GameId == GenshinGameId && bundle.Consents.Achievements)
            return false;

        var active = new HashSet<PublisherRoleBinding>();
        foreach (var role in bundle.Roles)
        {
            if (!IsValidRole(bundle.GameId, role, bundle.Consents, utcNow)
                || !active.Add(role.Role.Binding))
                return false;
        }
        if (active.Count == 0)
        {
            if (bundle.SelectedRole is not null || bundle.RoleTombstones.Count == 0) return false;
        }
        else if (bundle.SelectedRole is null || !active.Contains(bundle.SelectedRole))
        {
            return false;
        }

        var capabilityIdentities = new HashSet<(PublisherRoleBinding, string)>();
        HoyoLabCapabilityTombstone? previousCapability = null;
        foreach (var tombstone in bundle.CapabilityTombstones)
        {
            if (tombstone is null
                || tombstone.Binding is null
                || !PublisherAccountCatalog.IsValidRoleBinding(bundle.GameId, tombstone.Binding)
                || !IsValidTombstoneCapability(bundle.GameId, tombstone.Capability)
                || !capabilityIdentities.Add((tombstone.Binding, tombstone.Capability))
                || !IsValidTimestamp(tombstone.DeletedAt, utcNow)
                || (previousCapability is not null
                    && Compare(previousCapability, tombstone) >= 0))
                return false;
            previousCapability = tombstone;
        }

        var roleTombstoneIdentities = new HashSet<PublisherRoleBinding>();
        HoyoLabRoleTombstone? previousRole = null;
        foreach (var tombstone in bundle.RoleTombstones)
        {
            if (tombstone is null
                || tombstone.Binding is null
                || !PublisherAccountCatalog.IsValidRoleBinding(bundle.GameId, tombstone.Binding)
                || active.Contains(tombstone.Binding)
                || !roleTombstoneIdentities.Add(tombstone.Binding)
                || !IsValidTimestamp(tombstone.DeletedAt, utcNow)
                || (previousRole is not null && Compare(previousRole, tombstone) >= 0))
                return false;
            previousRole = tombstone;
        }

        foreach (var tombstone in bundle.CapabilityTombstones)
        {
            if (!active.Contains(tombstone.Binding)
                && !roleTombstoneIdentities.Contains(tombstone.Binding))
                return false;
            var activeRole = bundle.Roles.FirstOrDefault(role => role.Role.Binding == tombstone.Binding);
            if (activeRole is null) continue;
            if (tombstone.Capability == Resources
                && (activeRole.Resource is not null
                    || activeRole.Observations.Resources is not null))
                return false;
            if (tombstone.Capability == Achievements
                && (activeRole.CompletedHsrAchievementIds is not null
                    || activeRole.Observations.Achievements is not null))
                return false;
            if (tombstone.Capability == Builds
                && (activeRole.GenshinBuilds is not null
                    || activeRole.HsrBuilds is not null
                    || activeRole.Observations.Builds is not null))
                return false;
        }
        return true;
    }

    public static HoyoLabGameBundle Normalize(HoyoLabGameBundle bundle) => bundle with
    {
        Roles = bundle.Roles.Select(static role => role with
        {
            Resource = role.Resource is null ? null : role.Resource with { IsStale = true },
            CompletedHsrAchievementIds = role.CompletedHsrAchievementIds?.ToArray(),
            GenshinBuilds = role.GenshinBuilds is null
                ? null
                : HoyoLabGenshinBuildRules.Normalize(role.GenshinBuilds),
            HsrBuilds = role.HsrBuilds is null
                ? null
                : HoyoLabHsrBuildRules.Normalize(role.HsrBuilds),
        }).ToArray(),
        CapabilityTombstones = bundle.CapabilityTombstones.ToArray(),
        RoleTombstones = bundle.RoleTombstones.ToArray(),
    };

    public static bool IsSupportedGame(string? gameId) => gameId is GameId or GenshinGameId;

    public static IReadOnlyList<string> SupportedGames { get; } = Array.AsReadOnly<string>([GameId, GenshinGameId]);

    public static bool SupportsLocalCapability(string gameId, string capability) =>
        IsSupportedGame(gameId)
        && (capability == Resources
            || capability == Builds
            || (gameId == GameId && capability == Achievements));

    public static string ResourceName(string gameId) => gameId switch
    {
        GameId => "Trailblaze Power",
        GenshinGameId => "Original Resin",
        _ => string.Empty,
    };

    private static bool IsValidRole(
        string gameId,
        HoyoLabGameBundleRole? role,
        HoyoLabCapabilityConsentSet consents,
        DateTimeOffset utcNow)
    {
        if (role is null
            || role.Role is null
            || role.Observations is null
            || !PublisherRoleRecordRules.IsValid(gameId, role.Role)
            || !IsValidTimestamp(role.Observations.Resources, utcNow)
            || !IsValidTimestamp(role.Observations.Builds, utcNow)
            || !IsValidTimestamp(role.Observations.Achievements, utcNow)
            || role.Observations.Inventory is not null
            || role.Observations.Exploration is not null
            || role.Observations.Endgame is not null
            || role.Observations.Events is not null
            || role.Observations.Currency is not null)
            return false;

        if (role.Resource is { } resource
            && (!consents.Resources
                || resource.GameId != gameId
                || resource.ResourceName != ResourceName(gameId)
                || resource.Current is < 0 or > 10_000
                || resource.Maximum is <= 0 or > 10_000
                || resource.Current > resource.Maximum
                || resource.RecoverySeconds is < 0 or > 14 * 24 * 60 * 60
                || resource.Reserve is < 0 or > 10_000
                || !IsValidTimestamp(resource.ObservedAt, utcNow)
                || resource.ObservedAt != role.Observations.Resources))
            return false;
        if (role.Resource is null && role.Observations.Resources is not null)
            return false;

        if (role.Observations.Builds is not null)
        {
            if (!consents.Builds
                || gameId == GenshinGameId
                    && (role.HsrBuilds is not null
                        || !HoyoLabGenshinBuildRules.IsValid(role.GenshinBuilds))
                || gameId == GameId
                    && (role.GenshinBuilds is not null
                        || !HoyoLabHsrBuildRules.IsValid(role.HsrBuilds)))
                return false;
        }
        else if (role.GenshinBuilds is not null || role.HsrBuilds is not null)
        {
            return false;
        }

        if (role.CompletedHsrAchievementIds is { } ids)
        {
            if (gameId != GameId
                || !consents.Achievements
                || role.Observations.Achievements is null
                || ids.Count > MaximumAchievementIds)
                return false;
            long previous = 0;
            foreach (var id in ids)
            {
                if (id <= previous || id > MaximumAchievementId) return false;
                previous = id;
            }
        }
        else if (role.Observations.Achievements is not null)
        {
            return false;
        }
        return true;
    }

    private static bool IsValidTombstoneCapability(string gameId, string capability) =>
        gameId == GameId
            ? Capabilities.Contains(capability, StringComparer.Ordinal)
            : capability is Resources or Builds;

    private static bool IsValidTimestamp(DateTimeOffset? value, DateTimeOffset utcNow) =>
        value is null || IsValidTimestamp(value.Value, utcNow);

    private static bool IsValidTimestamp(DateTimeOffset value, DateTimeOffset utcNow)
    {
        var now = utcNow.ToUniversalTime();
        var maximum = now > DateTimeOffset.MaxValue.AddMinutes(-5)
            ? DateTimeOffset.MaxValue
            : now.AddMinutes(5);
        return value.Offset == TimeSpan.Zero
            && value >= DateTimeOffset.UnixEpoch
            && value <= maximum
            && value.Ticks % TimeSpan.TicksPerSecond == 0;
    }

    private static int Compare(
        HoyoLabCapabilityTombstone left,
        HoyoLabCapabilityTombstone right)
    {
        var timestamp = left.DeletedAt.CompareTo(right.DeletedAt);
        if (timestamp != 0) return timestamp;
        var server = string.CompareOrdinal(left.Binding.Server, right.Binding.Server);
        if (server != 0) return server;
        var roleId = string.CompareOrdinal(left.Binding.RoleId, right.Binding.RoleId);
        return roleId != 0
            ? roleId
            : string.CompareOrdinal(left.Capability, right.Capability);
    }

    private static int Compare(HoyoLabRoleTombstone left, HoyoLabRoleTombstone right)
    {
        var timestamp = left.DeletedAt.CompareTo(right.DeletedAt);
        if (timestamp != 0) return timestamp;
        var server = string.CompareOrdinal(left.Binding.Server, right.Binding.Server);
        return server != 0
            ? server
            : string.CompareOrdinal(left.Binding.RoleId, right.Binding.RoleId);
    }
}
