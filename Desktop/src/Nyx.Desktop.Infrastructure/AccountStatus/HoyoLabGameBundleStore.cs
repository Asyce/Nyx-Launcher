using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

/// <summary>
/// Stores one bounded HSR data bundle below a HoYoLAB slot's protected root.
/// Publisher credentials and raw responses never enter this store.
/// </summary>
public sealed class HoyoLabGameBundleStore
{
    internal const int MaximumPlaintextBytes = 3 * 1024 * 1024;
    internal const int MaximumCiphertextBytes = 3 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string protectedSlotRoot;
    private readonly string root;
    private readonly string path;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IPublisherRoleBindingFileBoundary files;
    private readonly TimeProvider clock;
    private readonly object mutationSync = new();
    private readonly string mutationMutexName;

    internal string MutationMutexName => mutationMutexName;

    public HoyoLabGameBundleStore(string protectedSlotRoot)
        : this(
            protectedSlotRoot,
            new WindowsCurrentUserRoleBindingProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            TimeProvider.System)
    {
    }

    internal HoyoLabGameBundleStore(
        string protectedSlotRoot,
        IPublisherRoleBindingProtector protector,
        IPublisherRoleBindingFileBoundary files,
        TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSlotRoot);
        this.protectedSlotRoot = Path.GetFullPath(protectedSlotRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        root = Path.GetFullPath(Path.Combine(
            this.protectedSlotRoot,
            ".protected-hoyolab-game-bundles"));
        path = Path.Combine(root, "hsr-v2.bin");
        if (!IsContained(root) || !IsContained(path))
            throw new ArgumentException("Protected game bundle escaped its configured root.", nameof(protectedSlotRoot));
        mutationMutexName = "Local\\Pengo.Nyx.Desktop.HoyoLabGameBundle."
            + Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(
                root.ToUpperInvariant())));
    }

    public HoyoLabGameBundle? TryLoad() => SerializeMutation<HoyoLabGameBundle?>(
        () =>
        {
            try
            {
                if (!ValidateExistingComponents(protectedSlotRoot)
                    || !ValidateExistingComponents(root)
                    || !ValidateExistingComponents(path))
                    return null;
                return ReadBundle(path);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                return null;
            }
        },
        null);

    public bool TrySave(HoyoLabGameBundle bundle) => SerializeMutation(
        () => TryWrite(bundle, requireMissing: false),
        false);

    public bool TryMigrateFromV1(
        PublisherRoleRecord role,
        PublisherResourceSnapshot? resource = null,
        PublisherRoleBinding? resourceBinding = null) => SerializeMutation(
        () => TryMigrateFromV1Core(role, resource, resourceBinding),
        false);

    private bool TryMigrateFromV1Core(
        PublisherRoleRecord role,
        PublisherResourceSnapshot? resource,
        PublisherRoleBinding? resourceBinding)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!PublisherRoleRecordRules.IsValid(HoyoLabGameBundleRules.GameId, role)
            || (resource is null) != (resourceBinding is null)
            || (resourceBinding is not null && resourceBinding != role.Binding))
            return false;

        var observedAt = resource?.ObservedAt;
        var migrated = new HoyoLabGameBundle(
            HoyoLabGameBundleRules.SchemaVersion,
            HoyoLabGameBundleRules.GameId,
            [
                new(
                    role,
                    new(
                        observedAt,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                    resource,
                    null),
            ],
            role.Binding,
            new(
                Resources: resource is not null,
                Inventory: false,
                Builds: false,
                Achievements: false,
                Exploration: false,
                Endgame: false,
                Events: false,
                Currency: false),
            Array.Empty<HoyoLabCapabilityTombstone>(),
            Array.Empty<HoyoLabRoleTombstone>());
        return TryWrite(migrated, requireMissing: true);
    }

    private bool TryWrite(HoyoLabGameBundle bundle, bool requireMissing)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var now = clock.GetUtcNow().ToUniversalTime();
        if (!HoyoLabGameBundleRules.IsValid(bundle, now)) return false;
        var normalized = HoyoLabGameBundleRules.Normalize(bundle);
        if (!HoyoLabGameBundleRules.IsValid(normalized, now)) return false;

        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            EnsureRoot();
            var entryExists = files.EntryExists(path);
            var fileExists = files.Exists(path);
            if (entryExists != fileExists
                || !ValidateExistingComponents(path)
                || (fileExists
                    && (files.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                return false;
            if (entryExists)
            {
                if (requireMissing || ReadBundle(path) is null) return false;
            }

            plaintext = SerializeBundle(normalized);
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes) return false;
            temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = files.CreateNewWriteThrough(temporary))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            if (!VerifyTemporary(temporary, plaintext, normalized, now)) return false;
            if (!ValidateExistingComponents(path)) return false;
            if (requireMissing || !entryExists)
            {
                if (files.EntryExists(path)) return false;
                files.MoveNew(temporary, path);
            }
            else
            {
                files.MoveOverwrite(temporary, path);
            }
            temporary = null;
            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { files.Delete(temporary); } catch (Exception) { }
            }
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private HoyoLabGameBundle? ReadBundle(string candidate)
    {
        if (!ValidateExistingComponents(candidate)) return null;
        var entryExists = files.EntryExists(candidate);
        var fileExists = files.Exists(candidate);
        if (!entryExists || !fileExists || entryExists != fileExists
            || (files.GetAttributes(candidate) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        using var stream = files.OpenRead(candidate);
        if (stream.Length is <= 0 or > MaximumCiphertextBytes) return null;
        var ciphertext = new byte[stream.Length];
        stream.ReadExactly(ciphertext);
        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(ciphertext);
            return TryParseBundle(plaintext, clock.GetUtcNow().ToUniversalTime(), out var bundle)
                ? bundle
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private bool VerifyTemporary(
        string temporary,
        byte[] expectedPlaintext,
        HoyoLabGameBundle expectedBundle,
        DateTimeOffset now)
    {
        if (!ValidateExistingComponents(temporary)
            || !files.Exists(temporary)
            || (files.GetAttributes(temporary) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        using var stream = files.OpenRead(temporary);
        if (stream.Length is <= 0 or > MaximumCiphertextBytes) return false;
        var ciphertext = new byte[stream.Length];
        stream.ReadExactly(ciphertext);
        byte[]? plaintext = null;
        byte[]? semantic = null;
        try
        {
            plaintext = protector.Unprotect(ciphertext);
            if (!plaintext.AsSpan().SequenceEqual(expectedPlaintext)
                || !TryParseBundle(plaintext, now, out var parsed)
                || parsed is null)
                return false;
            semantic = SerializeBundle(parsed);
            return semantic.AsSpan().SequenceEqual(expectedPlaintext)
                && HoyoLabGameBundleRules.IsValid(expectedBundle, now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (semantic is not null) CryptographicOperations.ZeroMemory(semantic);
        }
    }

    private void EnsureRoot()
    {
        if (!ValidateExistingComponents(protectedSlotRoot)
            || !ValidateExistingComponents(root))
            throw new IOException("Protected game bundle path cannot contain a reparse point.");
        files.CreateDirectory(protectedSlotRoot);
        if (!ValidateExistingComponents(protectedSlotRoot))
            throw new IOException("Protected slot root cannot be a reparse point.");
        files.CreateDirectory(root);
        if (!ValidateExistingComponents(root)
            || (files.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory)
            throw new IOException("Protected game bundle root cannot be a reparse point.");
    }

    private bool ValidateExistingComponents(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        if (!IsContained(fullPath)) return false;
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot)) return false;
        var current = volumeRoot;
        foreach (var component in fullPath[volumeRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!files.EntryExists(current)) continue;
            if ((files.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private bool IsContained(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        return string.Equals(fullPath, protectedSlotRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                protectedSlotRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private T SerializeMutation<T>(Func<T> mutation, T failure)
    {
        lock (mutationSync)
        {
            Mutex? mutex = null;
            var acquired = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, mutationMutexName);
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                return acquired ? mutation() : failure;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or WaitHandleCannotBeOpenedException)
            {
                return failure;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
                }
                try { mutex?.Dispose(); } catch (Exception) { }
            }
        }
    }

    internal static byte[] SerializeBundle(HoyoLabGameBundle bundle) =>
        SerializeBundle(bundle, null);

    internal static byte[] SerializeBundle(
        HoyoLabGameBundle bundle,
        Action<ReadOnlyMemory<byte>>? clearedBufferObserver)
    {
        using var owner = new BoundedSensitiveBuffer(clearedBufferObserver);
        using (var writer = new Utf8JsonWriter(owner, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", bundle.SchemaVersion);
            writer.WriteString("gameId", bundle.GameId);
            writer.WriteStartArray("roles");
            foreach (var role in bundle.Roles)
            {
                writer.WriteStartObject();
                WriteBinding(writer, "binding", role.Role.Binding);
                if (role.Role.Nickname is null) writer.WriteNull("nickname");
                else writer.WriteString("nickname", role.Role.Nickname);
                writer.WriteString("region", role.Role.ReadableRegion);
                WriteObservations(writer, role.Observations);
                WriteResource(writer, role.Resource);
                if (role.CompletedHsrAchievementIds is null)
                {
                    writer.WriteNull("completedAchievementIds");
                }
                else
                {
                    writer.WriteStartArray("completedAchievementIds");
                    foreach (var id in role.CompletedHsrAchievementIds) writer.WriteNumberValue(id);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (bundle.SelectedRole is null) writer.WriteNull("selectedRole");
            else WriteBinding(writer, "selectedRole", bundle.SelectedRole);
            WriteConsents(writer, bundle.Consents);
            writer.WriteStartArray("capabilityTombstones");
            foreach (var tombstone in bundle.CapabilityTombstones)
            {
                writer.WriteStartObject();
                WriteBinding(writer, "binding", tombstone.Binding);
                writer.WriteString("capability", tombstone.Capability);
                WriteTimestamp(writer, "deletedAt", tombstone.DeletedAt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("roleTombstones");
            foreach (var tombstone in bundle.RoleTombstones)
            {
                writer.WriteStartObject();
                WriteBinding(writer, "binding", tombstone.Binding);
                WriteTimestamp(writer, "deletedAt", tombstone.DeletedAt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return owner.WrittenSpan.ToArray();
    }

    internal static bool TryParseBundle(
        byte[] plaintext,
        DateTimeOffset utcNow,
        out HoyoLabGameBundle? bundle)
    {
        bundle = null;
        if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            var root = document.RootElement;
            if (!HasExactProperties(
                    root,
                    "schemaVersion",
                    "gameId",
                    "roles",
                    "selectedRole",
                    "consents",
                    "capabilityTombstones",
                    "roleTombstones")
                || !root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion)
                || root.GetProperty("gameId").ValueKind != JsonValueKind.String
                || root.GetProperty("gameId").GetString() is not { } gameId
                || !TryParseRoles(root.GetProperty("roles"), out var roles)
                || !TryParseNullableBinding(root.GetProperty("selectedRole"), out var selected)
                || !TryParseConsents(root.GetProperty("consents"), out var consents)
                || !TryParseCapabilityTombstones(
                    root.GetProperty("capabilityTombstones"),
                    out var capabilityTombstones)
                || !TryParseRoleTombstones(root.GetProperty("roleTombstones"), out var roleTombstones))
                return false;
            var candidate = new HoyoLabGameBundle(
                schemaVersion,
                gameId,
                roles,
                selected,
                consents!,
                capabilityTombstones,
                roleTombstones);
            if (!HoyoLabGameBundleRules.IsValid(candidate, utcNow)) return false;
            bundle = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseRoles(JsonElement element, out IReadOnlyList<HoyoLabGameBundleRole> roles)
    {
        roles = Array.Empty<HoyoLabGameBundleRole>();
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > HoyoLabGameBundleRules.MaximumRoles)
            return false;
        var parsed = new List<HoyoLabGameBundleRole>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!HasExactProperties(
                    item,
                    "binding",
                    "nickname",
                    "region",
                    "observations",
                    "resource",
                    "completedAchievementIds")
                || !TryParseBinding(item.GetProperty("binding"), out var binding)
                || !TryParseNullableString(item.GetProperty("nickname"), out var nickname)
                || item.GetProperty("region").ValueKind != JsonValueKind.String
                || item.GetProperty("region").GetString() is not { } region
                || !TryParseObservations(item.GetProperty("observations"), out var observations)
                || !TryParseResource(item.GetProperty("resource"), out var resource)
                || !TryParseAchievementIds(
                    item.GetProperty("completedAchievementIds"),
                    out var achievementIds))
                return false;
            parsed.Add(new(
                new(binding!, nickname, region),
                observations!,
                resource,
                achievementIds));
        }
        roles = parsed.AsReadOnly();
        return true;
    }

    private static bool TryParseBinding(JsonElement element, out PublisherRoleBinding? binding)
    {
        binding = null;
        if (!HasExactProperties(element, "roleId", "server")
            || element.GetProperty("roleId").ValueKind != JsonValueKind.String
            || element.GetProperty("roleId").GetString() is not { } roleId
            || element.GetProperty("server").ValueKind != JsonValueKind.String
            || element.GetProperty("server").GetString() is not { } server)
            return false;
        binding = new(roleId, server);
        return true;
    }

    private static bool TryParseNullableBinding(
        JsonElement element,
        out PublisherRoleBinding? binding)
    {
        binding = null;
        return element.ValueKind == JsonValueKind.Null || TryParseBinding(element, out binding);
    }

    private static bool TryParseObservations(
        JsonElement element,
        out HoyoLabCapabilityObservations? observations)
    {
        observations = null;
        if (!HasExactProperties(element, HoyoLabGameBundleRules.Capabilities.ToArray())) return false;
        var values = new DateTimeOffset?[HoyoLabGameBundleRules.Capabilities.Count];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseNullableTimestamp(
                    element.GetProperty(HoyoLabGameBundleRules.Capabilities[index]),
                    out values[index]))
                return false;
        }
        observations = new(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7]);
        return true;
    }

    private static bool TryParseResource(JsonElement element, out PublisherResourceSnapshot? resource)
    {
        resource = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!HasExactProperties(
                element,
                "name",
                "current",
                "maximum",
                "observedAt",
                "recoverySeconds",
                "reserve")
            || element.GetProperty("name").ValueKind != JsonValueKind.String
            || element.GetProperty("name").GetString() is not { } name
            || !element.GetProperty("current").TryGetInt32(out var current)
            || !element.GetProperty("maximum").TryGetInt32(out var maximum)
            || !TryParseTimestamp(element.GetProperty("observedAt"), out var observedAt)
            || !element.GetProperty("recoverySeconds").TryGetInt32(out var recovery)
            || !TryParseNullableInt32(element.GetProperty("reserve"), out var reserve))
            return false;
        resource = new(
            HoyoLabGameBundleRules.GameId,
            name,
            current,
            maximum,
            observedAt,
            IsStale: true,
            RecoverySeconds: recovery,
            Reserve: reserve);
        return true;
    }

    private static bool TryParseAchievementIds(JsonElement element, out IReadOnlyList<long>? ids)
    {
        ids = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > HoyoLabGameBundleRules.MaximumAchievementIds)
            return false;
        var parsed = new List<long>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!item.TryGetInt64(out var id)) return false;
            parsed.Add(id);
        }
        ids = parsed.AsReadOnly();
        return true;
    }

    private static bool TryParseConsents(JsonElement element, out HoyoLabCapabilityConsentSet? consents)
    {
        consents = null;
        if (!HasExactProperties(element, HoyoLabGameBundleRules.Capabilities.ToArray())) return false;
        var values = new bool[HoyoLabGameBundleRules.Capabilities.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var property = element.GetProperty(HoyoLabGameBundleRules.Capabilities[index]);
            if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            values[index] = property.GetBoolean();
        }
        consents = new(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7]);
        return true;
    }

    private static bool TryParseCapabilityTombstones(
        JsonElement element,
        out IReadOnlyList<HoyoLabCapabilityTombstone> tombstones)
    {
        tombstones = Array.Empty<HoyoLabCapabilityTombstone>();
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > HoyoLabGameBundleRules.MaximumCapabilityTombstones)
            return false;
        var parsed = new List<HoyoLabCapabilityTombstone>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!HasExactProperties(item, "binding", "capability", "deletedAt")
                || !TryParseBinding(item.GetProperty("binding"), out var binding)
                || item.GetProperty("capability").ValueKind != JsonValueKind.String
                || item.GetProperty("capability").GetString() is not { } capability
                || !TryParseTimestamp(item.GetProperty("deletedAt"), out var deletedAt))
                return false;
            parsed.Add(new(binding!, capability, deletedAt));
        }
        tombstones = parsed.AsReadOnly();
        return true;
    }

    private static bool TryParseRoleTombstones(
        JsonElement element,
        out IReadOnlyList<HoyoLabRoleTombstone> tombstones)
    {
        tombstones = Array.Empty<HoyoLabRoleTombstone>();
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > HoyoLabGameBundleRules.MaximumRoleTombstones)
            return false;
        var parsed = new List<HoyoLabRoleTombstone>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!HasExactProperties(item, "binding", "deletedAt")
                || !TryParseBinding(item.GetProperty("binding"), out var binding)
                || !TryParseTimestamp(item.GetProperty("deletedAt"), out var deletedAt))
                return false;
            parsed.Add(new(binding!, deletedAt));
        }
        tombstones = parsed.AsReadOnly();
        return true;
    }

    private static void WriteBinding(
        Utf8JsonWriter writer,
        string propertyName,
        PublisherRoleBinding binding)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("roleId", binding.RoleId);
        writer.WriteString("server", binding.Server);
        writer.WriteEndObject();
    }

    private static void WriteObservations(
        Utf8JsonWriter writer,
        HoyoLabCapabilityObservations observations)
    {
        writer.WriteStartObject("observations");
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Resources, observations.Resources);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Inventory, observations.Inventory);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Builds, observations.Builds);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Achievements, observations.Achievements);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Exploration, observations.Exploration);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Endgame, observations.Endgame);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Events, observations.Events);
        WriteNullableTimestamp(writer, HoyoLabGameBundleRules.Currency, observations.Currency);
        writer.WriteEndObject();
    }

    private static void WriteResource(Utf8JsonWriter writer, PublisherResourceSnapshot? resource)
    {
        if (resource is null)
        {
            writer.WriteNull("resource");
            return;
        }
        writer.WriteStartObject("resource");
        writer.WriteString("name", resource.ResourceName);
        writer.WriteNumber("current", resource.Current);
        writer.WriteNumber("maximum", resource.Maximum);
        WriteTimestamp(writer, "observedAt", resource.ObservedAt);
        writer.WriteNumber("recoverySeconds", resource.RecoverySeconds);
        if (resource.Reserve is null) writer.WriteNull("reserve");
        else writer.WriteNumber("reserve", resource.Reserve.Value);
        writer.WriteEndObject();
    }

    private static void WriteConsents(Utf8JsonWriter writer, HoyoLabCapabilityConsentSet consents)
    {
        writer.WriteStartObject("consents");
        writer.WriteBoolean(HoyoLabGameBundleRules.Resources, consents.Resources);
        writer.WriteBoolean(HoyoLabGameBundleRules.Inventory, consents.Inventory);
        writer.WriteBoolean(HoyoLabGameBundleRules.Builds, consents.Builds);
        writer.WriteBoolean(HoyoLabGameBundleRules.Achievements, consents.Achievements);
        writer.WriteBoolean(HoyoLabGameBundleRules.Exploration, consents.Exploration);
        writer.WriteBoolean(HoyoLabGameBundleRules.Endgame, consents.Endgame);
        writer.WriteBoolean(HoyoLabGameBundleRules.Events, consents.Events);
        writer.WriteBoolean(HoyoLabGameBundleRules.Currency, consents.Currency);
        writer.WriteEndObject();
    }

    private static void WriteNullableTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else WriteTimestamp(writer, propertyName, value.Value);
    }

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value) => writer.WriteString(
            propertyName,
            value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static bool TryParseNullableTimestamp(JsonElement element, out DateTimeOffset? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!TryParseTimestamp(element, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryParseTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(
                element.GetString(),
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }

    private static bool TryParseNullableString(JsonElement element, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is not null;
    }

    private static bool TryParseNullableInt32(JsonElement element, out int? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!element.TryGetInt32(out var parsed)) return false;
        value = parsed;
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

    private sealed class BoundedSensitiveBuffer(
        Action<ReadOnlyMemory<byte>>? clearedBufferObserver) : IBufferWriter<byte>, IDisposable
    {
        private byte[]? buffer = ArrayPool<byte>.Shared.Rent(MaximumPlaintextBytes);
        private int written;

        public ReadOnlySpan<byte> WrittenSpan =>
            (buffer ?? throw new ObjectDisposedException(nameof(BoundedSensitiveBuffer)))
            .AsSpan(0, written);

        public void Advance(int count)
        {
            if (count < 0 || count > MaximumPlaintextBytes - written)
                throw new InvalidOperationException("Bundle serialization exceeded its fixed limit.");
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var requested = Math.Max(1, sizeHint);
            if (requested > MaximumPlaintextBytes - written)
                throw new InvalidOperationException("Bundle serialization exceeded its fixed limit.");
            return (buffer ?? throw new ObjectDisposedException(nameof(BoundedSensitiveBuffer)))
                .AsMemory(written, MaximumPlaintextBytes - written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Dispose()
        {
            var rented = Interlocked.Exchange(ref buffer, null);
            if (rented is null) return;
            CryptographicOperations.ZeroMemory(rented);
            var observer = clearedBufferObserver;
            var clearedProof = observer is null
                ? null
                : rented.AsMemory(0, MaximumPlaintextBytes).ToArray();
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            if (clearedProof is not null) observer!(clearedProof);
        }
    }

    private static bool IsExpectedFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or CryptographicException
        or InvalidDataException
        or JsonException
        or EncoderFallbackException
        or InvalidOperationException;
}
