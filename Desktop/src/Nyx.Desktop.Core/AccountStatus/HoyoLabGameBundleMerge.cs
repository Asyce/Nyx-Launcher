namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabGameBundleMergeOutcome
{
    Merged,
    Idempotent,
    Conflict,
}

public sealed record HoyoLabGameBundleMergeResult
{
    internal HoyoLabGameBundleMergeResult(
        HoyoLabGameBundleMergeOutcome outcome,
        HoyoLabGameBundle? bundle)
    {
        Outcome = outcome;
        Bundle = bundle;
    }

    public HoyoLabGameBundleMergeOutcome Outcome { get; }
    public HoyoLabGameBundle? Bundle { get; }
}

public static class HoyoLabGameBundleMerge
{
    public static HoyoLabGameBundleMergeResult Merge(
        HoyoLabGameBundle? local,
        HoyoLabGameBundle? remote,
        DateTimeOffset utcNow)
    {
        if (!HoyoLabGameBundleRules.IsValid(local, utcNow)
            || !HoyoLabGameBundleRules.IsValid(remote, utcNow))
            return Conflict();

        local = HoyoLabGameBundleRules.Normalize(local!);
        remote = HoyoLabGameBundleRules.Normalize(remote!);

        var localRoles = local.Roles.ToDictionary(static role => role.Role.Binding);
        var remoteRoles = remote.Roles.ToDictionary(static role => role.Role.Binding);
        var capabilityTombstones = LatestCapabilityTombstones(local, remote);
        var roleTombstones = LatestRoleTombstones(local, remote);
        var bindings = localRoles.Keys
            .Concat(remoteRoles.Keys)
            .Concat(roleTombstones.Keys)
            .Distinct()
            .OrderBy(static binding => binding.Server, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RoleId, StringComparer.Ordinal);
        var roles = new List<HoyoLabGameBundleRole>();

        foreach (var binding in bindings)
        {
            localRoles.TryGetValue(binding, out var localRole);
            remoteRoles.TryGetValue(binding, out var remoteRole);
            if (localRole is not null
                && remoteRole is not null
                && localRole.Role != remoteRole.Role)
                return Conflict();

            if (!TryMergeObservation(
                    localRole?.Observations.Resources,
                    localRole?.Resource,
                    remoteRole?.Observations.Resources,
                    remoteRole?.Resource,
                    static (left, right) => left == right,
                    out var resourcesAt,
                    out var resource)
                || !TryMergeObservation(
                    localRole?.Observations.Achievements,
                    localRole?.CompletedHsrAchievementIds,
                    remoteRole?.Observations.Achievements,
                    remoteRole?.CompletedHsrAchievementIds,
                    static (left, right) => left.SequenceEqual(right),
                    out var achievementsAt,
                    out var achievements))
                return Conflict();

            var newestRoleObservation = Latest(resourcesAt, achievementsAt);
            if (!ApplyCapabilityTombstone(
                    capabilityTombstones,
                    binding,
                    HoyoLabGameBundleRules.Resources,
                    ref resourcesAt,
                    ref resource)
                || !ApplyCapabilityTombstone(
                    capabilityTombstones,
                    binding,
                    HoyoLabGameBundleRules.Achievements,
                    ref achievementsAt,
                    ref achievements))
                return Conflict();

            if (roleTombstones.TryGetValue(binding, out var roleTombstone))
            {
                if (newestRoleObservation is null
                    || roleTombstone.DeletedAt > newestRoleObservation)
                    continue;
                if (roleTombstone.DeletedAt == newestRoleObservation)
                    return Conflict();
                roleTombstones.Remove(binding);
            }

            var role = localRole?.Role ?? remoteRole?.Role;
            if (role is null) continue;
            roles.Add(new(
                role,
                new(
                    resourcesAt,
                    null,
                    null,
                    achievementsAt,
                    null,
                    null,
                    null,
                    null),
                resource,
                achievements?.ToArray()));
        }

        var orderedCapabilityTombstones = capabilityTombstones.Values
            .OrderBy(static tombstone => tombstone.DeletedAt)
            .ThenBy(static tombstone => tombstone.Binding.Server, StringComparer.Ordinal)
            .ThenBy(static tombstone => tombstone.Binding.RoleId, StringComparer.Ordinal)
            .ThenBy(static tombstone => tombstone.Capability, StringComparer.Ordinal)
            .ToArray();
        var orderedRoleTombstones = roleTombstones.Values
            .OrderBy(static tombstone => tombstone.DeletedAt)
            .ThenBy(static tombstone => tombstone.Binding.Server, StringComparer.Ordinal)
            .ThenBy(static tombstone => tombstone.Binding.RoleId, StringComparer.Ordinal)
            .ToArray();
        if (roles.Count > HoyoLabGameBundleRules.MaximumRoles
            || orderedCapabilityTombstones.Length > HoyoLabGameBundleRules.MaximumCapabilityTombstones
            || orderedRoleTombstones.Length > HoyoLabGameBundleRules.MaximumRoleTombstones)
            return Conflict();

        var merged = HoyoLabGameBundleRules.Normalize(new(
            local.SchemaVersion,
            local.GameId,
            roles,
            local.SelectedRole,
            local.Consents,
            orderedCapabilityTombstones,
            orderedRoleTombstones));
        if (!HoyoLabGameBundleRules.IsValid(merged, utcNow)) return Conflict();
        return new(
            BundleEquals(local, merged)
                ? HoyoLabGameBundleMergeOutcome.Idempotent
                : HoyoLabGameBundleMergeOutcome.Merged,
            merged);
    }

    private static bool TryMergeObservation<T>(
        DateTimeOffset? localAt,
        T? localValue,
        DateTimeOffset? remoteAt,
        T? remoteValue,
        Func<T, T, bool> valuesEqual,
        out DateTimeOffset? mergedAt,
        out T? mergedValue)
        where T : class
    {
        if (localAt is null)
        {
            mergedAt = remoteAt;
            mergedValue = remoteValue;
            return true;
        }
        if (remoteAt is null || localAt > remoteAt)
        {
            mergedAt = localAt;
            mergedValue = localValue;
            return true;
        }
        if (remoteAt > localAt)
        {
            mergedAt = remoteAt;
            mergedValue = remoteValue;
            return true;
        }

        mergedAt = localAt;
        mergedValue = localValue;
        return valuesEqual(localValue!, remoteValue!);
    }

    private static bool ApplyCapabilityTombstone<T>(
        IDictionary<(PublisherRoleBinding Binding, string Capability), HoyoLabCapabilityTombstone> tombstones,
        PublisherRoleBinding binding,
        string capability,
        ref DateTimeOffset? observedAt,
        ref T? value)
        where T : class
    {
        var identity = (binding, capability);
        if (!tombstones.TryGetValue(identity, out var tombstone)) return true;
        if (observedAt is null || tombstone.DeletedAt > observedAt)
        {
            observedAt = null;
            value = null;
            return true;
        }
        if (tombstone.DeletedAt == observedAt) return false;
        tombstones.Remove(identity);
        return true;
    }

    private static Dictionary<(PublisherRoleBinding Binding, string Capability), HoyoLabCapabilityTombstone>
        LatestCapabilityTombstones(HoyoLabGameBundle local, HoyoLabGameBundle remote)
    {
        var latest = new Dictionary<
            (PublisherRoleBinding Binding, string Capability),
            HoyoLabCapabilityTombstone>();
        foreach (var tombstone in local.CapabilityTombstones.Concat(remote.CapabilityTombstones))
        {
            var identity = (tombstone.Binding, tombstone.Capability);
            if (!latest.TryGetValue(identity, out var existing)
                || tombstone.DeletedAt > existing.DeletedAt)
                latest[identity] = tombstone;
        }
        return latest;
    }

    private static Dictionary<PublisherRoleBinding, HoyoLabRoleTombstone> LatestRoleTombstones(
        HoyoLabGameBundle local,
        HoyoLabGameBundle remote)
    {
        var latest = new Dictionary<PublisherRoleBinding, HoyoLabRoleTombstone>();
        foreach (var tombstone in local.RoleTombstones.Concat(remote.RoleTombstones))
        {
            if (!latest.TryGetValue(tombstone.Binding, out var existing)
                || tombstone.DeletedAt > existing.DeletedAt)
                latest[tombstone.Binding] = tombstone;
        }
        return latest;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left > right ? left : right;
    }

    private static bool BundleEquals(HoyoLabGameBundle left, HoyoLabGameBundle right) =>
        left.SchemaVersion == right.SchemaVersion
        && string.Equals(left.GameId, right.GameId, StringComparison.Ordinal)
        && left.SelectedRole == right.SelectedRole
        && left.Consents == right.Consents
        && left.Roles.Count == right.Roles.Count
        && left.Roles.Zip(right.Roles).All(static pair => RoleEquals(pair.First, pair.Second))
        && left.CapabilityTombstones.SequenceEqual(right.CapabilityTombstones)
        && left.RoleTombstones.SequenceEqual(right.RoleTombstones);

    private static bool RoleEquals(HoyoLabGameBundleRole left, HoyoLabGameBundleRole right) =>
        left.Role == right.Role
        && left.Observations == right.Observations
        && left.Resource == right.Resource
        && (left.CompletedHsrAchievementIds is null
            ? right.CompletedHsrAchievementIds is null
            : right.CompletedHsrAchievementIds is not null
                && left.CompletedHsrAchievementIds.SequenceEqual(right.CompletedHsrAchievementIds));

    private static HoyoLabGameBundleMergeResult Conflict() =>
        new(HoyoLabGameBundleMergeOutcome.Conflict, null);
}
