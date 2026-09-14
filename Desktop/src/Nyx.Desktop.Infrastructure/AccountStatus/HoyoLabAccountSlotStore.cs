using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

public sealed class HoyoLabAccountSlotStore
{
    public const int MaximumCiphertextBytes = 64 * 1024;
    internal const int MaximumPlaintextBytes = 16 * 1024;
    private const int MaximumCopiedFilesPerStore = 32;
    private const int MaximumCopiedFileBytes = 16 * 1024;
    private static readonly string[] LegacyProtectedDirectories =
    [
        ".protected-role-bindings",
        ".protected-resource-snapshots",
    ];

    private readonly string publisherProfilesRoot;
    private readonly string protectedIndexRoot;
    private readonly string indexPath;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IHoyoLabAccountSlotFileBoundary files;
    private readonly TimeProvider clock;
    private readonly Func<string> slotIdFactory;
    private readonly object mutationSync = new();
    private readonly string mutationMutexName;
    private HoyoLabAccountSlotIndex? loadedIndex;

    public HoyoLabAccountSlotStore(string publisherProfilesRoot)
        : this(
            publisherProfilesRoot,
            new WindowsCurrentUserRoleBindingProtector(),
            new SystemHoyoLabAccountSlotFileBoundary(),
            TimeProvider.System,
            HoyoLabAccountSlotRules.CreateSlotId)
    {
    }

    internal HoyoLabAccountSlotStore(
        string publisherProfilesRoot,
        IPublisherRoleBindingProtector protector,
        IHoyoLabAccountSlotFileBoundary files,
        TimeProvider clock,
        Func<string> slotIdFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherProfilesRoot);
        this.publisherProfilesRoot = Path.GetFullPath(publisherProfilesRoot);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.slotIdFactory = slotIdFactory ?? throw new ArgumentNullException(nameof(slotIdFactory));
        mutationMutexName = "Local\\Pengo.Nyx.Desktop.HoyoLabSlots."
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                this.publisherProfilesRoot.ToUpperInvariant())));
        protectedIndexRoot = Path.GetFullPath(Path.Combine(
            this.publisherProfilesRoot,
            ".protected-hoyolab-slots"));
        indexPath = Path.Combine(protectedIndexRoot, "index.bin");
        if (!IsContained(protectedIndexRoot) || !IsContained(indexPath))
            throw new ArgumentException("Protected slot index escaped its configured root.", nameof(publisherProfilesRoot));
    }

    public HoyoLabAccountSlotIndex? CurrentIndex => loadedIndex;

    public HoyoLabAccountSlotInitializationResult TryInitialize() => SerializeMutation(
        TryInitializeCore,
        Unavailable());

    private HoyoLabAccountSlotInitializationResult TryInitializeCore()
    {
        loadedIndex = null;
        var compatibilityEligible = false;
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)) return Unavailable();
            var protectedRootEntryExists = files.EntryExists(protectedIndexRoot);
            var protectedRootIsDirectory = files.DirectoryExists(protectedIndexRoot);
            if (protectedRootEntryExists != protectedRootIsDirectory
                || !ValidateExistingComponents(protectedIndexRoot))
                return Unavailable();

            var indexEntryExists = files.EntryExists(indexPath);
            var indexFileExists = files.FileExists(indexPath);
            if (indexEntryExists != indexFileExists
                || !ValidateExistingComponents(indexPath))
                return Unavailable();

            if (indexFileExists)
            {
                var existing = ReadIndex();
                if (existing is null) return Unavailable();
                loadedIndex = existing;
                return Ready(existing);
            }

            var legacyProfile = LegacyProfilePath();
            var legacyEntryExists = files.EntryExists(legacyProfile);
            var hasLegacy = files.DirectoryExists(legacyProfile);
            if (legacyEntryExists != hasLegacy) return Unavailable();
            if (hasLegacy && !ValidateExistingComponents(legacyProfile))
                return Unavailable();
            compatibilityEligible = true;

            HoyoLabAccountSlotIndex index;
            if (hasLegacy)
            {
                var id = slotIdFactory();
                if (!HoyoLabAccountSlotRules.IsValidSlotId(id)) return Compatibility();
                var now = CanonicalUtcNow();
                var slot = new HoyoLabAccountSlot(
                    id,
                    "HoYoLAB account",
                    IsLegacy: true,
                    now,
                    now,
                    RemovalPending: false);
                index = new(
                    HoyoLabAccountSlotRules.SchemaVersion,
                    id,
                    [slot],
                    LegacyFallback: true);
                if (!CopyLegacyProtectedState(slot)) return Compatibility();
            }
            else
            {
                index = new(
                    HoyoLabAccountSlotRules.SchemaVersion,
                    null,
                    Array.Empty<HoyoLabAccountSlot>(),
                    LegacyFallback: false);
            }

            if (!WriteIndex(index, overwrite: false)) return Compatibility();
            loadedIndex = index;
            return Ready(index);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            loadedIndex = null;
            return compatibilityEligible ? Compatibility() : Unavailable();
        }
    }

    public HoyoLabAccountSlotIndex? TryLoad() => SerializeMutation<HoyoLabAccountSlotIndex?>(
        TryLoadCore,
        null);

    public bool IsLegacyCompatibilityStillSafe() => SerializeMutation(
        IsLegacyCompatibilityStillSafeCore,
        false);

    private bool IsLegacyCompatibilityStillSafeCore()
    {
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)) return false;
            var protectedRootEntryExists = files.EntryExists(protectedIndexRoot);
            var protectedRootIsDirectory = files.DirectoryExists(protectedIndexRoot);
            if (protectedRootEntryExists != protectedRootIsDirectory
                || !ValidateExistingComponents(protectedIndexRoot)
                || files.EntryExists(indexPath))
                return false;
            var legacyProfile = LegacyProfilePath();
            var legacyEntryExists = files.EntryExists(legacyProfile);
            var legacyIsDirectory = files.DirectoryExists(legacyProfile);
            return legacyEntryExists == legacyIsDirectory
                && (!legacyEntryExists || ValidateExistingComponents(legacyProfile));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return false;
        }
    }

    private HoyoLabAccountSlotIndex? TryLoadCore()
    {
        loadedIndex = null;
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)
                || !ValidateExistingComponents(protectedIndexRoot)
                || !ValidateExistingComponents(indexPath)
                || !files.FileExists(indexPath))
                return null;
            loadedIndex = ReadIndex();
            return loadedIndex;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            loadedIndex = null;
            return null;
        }
    }

    public bool TryCreateSlot(string label, out HoyoLabAccountSlot? slot)
    {
        HoyoLabAccountSlot? created = null;
        var saved = SerializeMutation(
            () => TryCreateSlotCore(label, select: false, out created),
            false);
        slot = created;
        return saved;
    }

    public bool TryCreateAndSelectSlot(string label, out HoyoLabAccountSlot? slot)
    {
        HoyoLabAccountSlot? created = null;
        var saved = SerializeMutation(
            () => TryCreateSlotCore(label, select: true, out created),
            false);
        slot = created;
        return saved;
    }

    private bool TryCreateSlotCore(
        string label,
        bool select,
        out HoyoLabAccountSlot? slot)
    {
        slot = null;
        if (!HoyoLabAccountSlotRules.TryNormalizeLabel(label, out var normalized)) return false;
        var current = ReadCurrentForMutation();
        if (current is null || current.Slots.Count >= HoyoLabAccountSlotRules.MaximumSlots)
            return false;
        var id = slotIdFactory();
        if (!HoyoLabAccountSlotRules.IsValidSlotId(id)
            || current.Slots.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
            return false;
        var now = CanonicalUtcNow();
        var created = new HoyoLabAccountSlot(
            id,
            normalized,
            IsLegacy: false,
            now,
            now,
            RemovalPending: false);
        var next = current with
        {
            Slots = [.. current.Slots, created],
            ActiveSlotId = select ? created.Id : current.ActiveSlotId,
        };
        if (!WriteIndex(next, overwrite: true)) return false;
        loadedIndex = next;
        slot = created;
        return true;
    }

    public bool TryRenameSlot(string slotId, string label) => SerializeMutation(
        () => TryRenameSlotCore(slotId, label),
        false);

    private bool TryRenameSlotCore(string slotId, string label)
    {
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slotId)
            || !HoyoLabAccountSlotRules.TryNormalizeLabel(label, out var normalized))
            return false;
        var current = ReadCurrentForMutation();
        if (current is null) return false;
        var position = FindSlot(current, slotId);
        if (position < 0 || current.Slots[position].RemovalPending) return false;
        var existing = current.Slots[position];
        if (string.Equals(existing.Label, normalized, StringComparison.Ordinal)) return true;
        var updatedAt = NextTimestamp(existing.UpdatedAt);
        if (updatedAt is null) return false;
        var slots = current.Slots.ToArray();
        slots[position] = existing with { Label = normalized, UpdatedAt = updatedAt.Value };
        return SaveMutation(current with { Slots = slots });
    }

    public bool TrySetActiveSlot(string? slotId) => SerializeMutation(
        () => TrySetActiveSlotCore(slotId),
        false);

    private bool TrySetActiveSlotCore(string? slotId)
    {
        if (slotId is not null && !HoyoLabAccountSlotRules.IsValidSlotId(slotId)) return false;
        var current = ReadCurrentForMutation();
        if (current is null) return false;
        if (slotId is not null
            && !current.Slots.Any(slot =>
                string.Equals(slot.Id, slotId, StringComparison.Ordinal)
                && !slot.RemovalPending))
            return false;
        if (string.Equals(current.ActiveSlotId, slotId, StringComparison.Ordinal)) return true;
        return SaveMutation(current with { ActiveSlotId = slotId });
    }

    public bool TryMarkRemovalPending(string slotId) => SerializeMutation(
        () => TryMarkRemovalPendingCore(slotId),
        false);

    private bool TryMarkRemovalPendingCore(string slotId)
    {
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slotId)) return false;
        var current = ReadCurrentForMutation();
        if (current is null) return false;
        var position = FindSlot(current, slotId);
        if (position < 0) return false;
        var existing = current.Slots[position];
        if (existing.RemovalPending) return false;
        var updatedAt = NextTimestamp(existing.UpdatedAt);
        if (updatedAt is null) return false;
        var slots = current.Slots.ToArray();
        slots[position] = existing with
        {
            RemovalPending = true,
            UpdatedAt = updatedAt.Value,
        };
        return SaveMutation(current with
        {
            Slots = slots,
            ActiveSlotId = string.Equals(current.ActiveSlotId, slotId, StringComparison.Ordinal)
                ? null
                : current.ActiveSlotId,
        });
    }

    public bool TryRemoveSlot(string slotId) => SerializeMutation(
        () => TryRemoveSlotCore(slotId),
        false);

    public bool IsSlotRemoved(string slotId) => SerializeMutation(() =>
    {
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slotId)
            || !ValidateExistingComponents(indexPath))
            return false;
        if (files.EntryExists(indexPath))
        {
            var index = TryLoadCore();
            if (index is null || index.Slots.Any(slot => slot.Id == slotId)) return false;
        }
        var container = Path.Combine(publisherProfilesRoot, "Accounts", "HoYoLAB", slotId);
        return ValidateExistingComponents(container) && !files.EntryExists(container);
    }, false);

    private bool TryRemoveSlotCore(string slotId)
    {
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slotId)) return false;
        var current = ReadCurrentForMutation();
        if (current is null) return false;
        var existing = current.Slots.FirstOrDefault(slot =>
            string.Equals(slot.Id, slotId, StringComparison.Ordinal));
        if (existing is null || !existing.RemovalPending) return false;
        var next = current with
        {
            Slots = current.Slots
                .Where(slot => !string.Equals(slot.Id, slotId, StringComparison.Ordinal))
                .ToArray(),
            ActiveSlotId = string.Equals(current.ActiveSlotId, slotId, StringComparison.Ordinal)
                ? null
                : current.ActiveSlotId,
        };
        return SaveMutation(next);
    }

    public bool TryGetWebView2ProfilePath(HoyoLabAccountSlot slot, out string path)
    {
        path = string.Empty;
        if (!IsLoadedSlot(slot)) return false;
        var candidate = slot.IsLegacy
            ? LegacyProfilePath()
            : Path.Combine(
                publisherProfilesRoot,
                "Accounts",
                "HoYoLAB",
                slot.Id,
                "WebView2");
        if (!IsContained(candidate) || !ValidateExistingComponents(candidate)) return false;
        path = Path.GetFullPath(candidate);
        return true;
    }

    public bool TryGetProtectedStateRoot(HoyoLabAccountSlot slot, out string path)
    {
        path = string.Empty;
        if (!IsLoadedSlot(slot)) return false;
        var candidate = ProtectedStateRoot(slot.Id);
        if (!IsContained(candidate) || !ValidateExistingComponents(candidate)) return false;
        path = candidate;
        return true;
    }

    public bool TryGetSlotContainerPath(HoyoLabAccountSlot slot, out string path)
    {
        path = string.Empty;
        if (!IsLoadedSlot(slot)) return false;
        var candidate = Path.GetFullPath(Path.Combine(
            publisherProfilesRoot,
            "Accounts",
            "HoYoLAB",
            slot.Id));
        if (!IsContained(candidate) || !ValidateExistingComponents(candidate)) return false;
        path = candidate;
        return true;
    }

    public bool TryDeleteIndex() => SerializeMutation(TryDeleteIndexCore, false);

    private bool TryDeleteIndexCore()
    {
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)
                || !ValidateExistingComponents(protectedIndexRoot)
                || !ValidateExistingComponents(indexPath))
                return false;
            if (!files.FileExists(indexPath))
            {
                loadedIndex = null;
                return true;
            }
            if ((files.GetAttributes(indexPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            files.DeleteFile(indexPath);
            if (files.FileExists(indexPath)) return false;
            loadedIndex = null;
            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return false;
        }
    }

    private HoyoLabAccountSlotIndex? ReadCurrentForMutation()
    {
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)
                || !ValidateExistingComponents(protectedIndexRoot)
                || !ValidateExistingComponents(indexPath)
                || !files.FileExists(indexPath))
                return null;
            return ReadIndex();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return null;
        }
    }

    private bool SaveMutation(HoyoLabAccountSlotIndex index)
    {
        if (!WriteIndex(index, overwrite: true)) return false;
        loadedIndex = index;
        return true;
    }

    private HoyoLabAccountSlotIndex? ReadIndex()
    {
        if (!files.FileExists(indexPath)
            || (files.GetAttributes(indexPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        using var stream = files.OpenRead(indexPath);
        if (stream.Length is <= 0 or > MaximumCiphertextBytes) return null;
        var ciphertext = new byte[stream.Length];
        stream.ReadExactly(ciphertext);
        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return null;
            return TryParseIndex(plaintext, out var index) ? index : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private bool WriteIndex(HoyoLabAccountSlotIndex index, bool overwrite)
    {
        if (!HoyoLabAccountSlotRules.IsValidIndex(index)) return false;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        try
        {
            if (!ValidateExistingComponents(publisherProfilesRoot)
                || !ValidateExistingComponents(protectedIndexRoot)
                || !ValidateExistingComponents(indexPath))
                return false;
            files.CreateDirectory(publisherProfilesRoot);
            if (!ValidateExistingComponents(publisherProfilesRoot)) return false;
            files.CreateDirectory(protectedIndexRoot);
            if (!ValidateExistingComponents(protectedIndexRoot)) return false;
            if (files.FileExists(indexPath)
                && (files.GetAttributes(indexPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            if (!overwrite && files.FileExists(indexPath)) return false;

            plaintext = SerializeIndex(index);
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes) return false;
            temporary = indexPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = files.CreateNewWriteThrough(temporary))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            if (overwrite)
                files.MoveOverwrite(temporary, indexPath);
            else
                files.MoveNew(temporary, indexPath);
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
                try { files.DeleteFile(temporary); } catch (Exception) { }
            }
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private bool CopyLegacyProtectedState(HoyoLabAccountSlot legacySlot)
    {
        var destinationRoot = ProtectedStateRoot(legacySlot.Id);
        if (!ValidateExistingComponents(destinationRoot)) return false;
        foreach (var directoryName in LegacyProtectedDirectories)
        {
            var sourceDirectory = Path.Combine(publisherProfilesRoot, directoryName);
            var sourceEntryExists = files.EntryExists(sourceDirectory);
            var sourceDirectoryExists = files.DirectoryExists(sourceDirectory);
            if (sourceEntryExists != sourceDirectoryExists) return false;
            if (!sourceDirectoryExists) continue;
            if (!ValidateExistingComponents(sourceDirectory)
                || (files.GetAttributes(sourceDirectory) & FileAttributes.Directory) == 0)
                return false;
            var entries = files.EnumerateFileSystemEntries(sourceDirectory).ToArray();
            if (entries.Length > MaximumCopiedFilesPerStore) return false;
            var destinationDirectory = Path.Combine(destinationRoot, directoryName);
            if (!ValidateExistingComponents(destinationDirectory)) return false;
            files.CreateDirectory(destinationDirectory);
            if (!ValidateExistingComponents(destinationDirectory)) return false;
            foreach (var source in entries)
            {
                if (!IsContained(source)
                    || !ValidateExistingComponents(source))
                    return false;
                var attributes = files.GetAttributes(source);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return false;
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
                if (!CopyAndVerifyFile(source, destination)) return false;
            }
        }
        return true;
    }

    private bool CopyAndVerifyFile(string source, string destination)
    {
        string? temporary = null;
        try
        {
            if (!IsContained(destination) || !ValidateExistingComponents(destination)) return false;
            if (files.FileExists(destination))
                return (files.GetAttributes(destination) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                    && FilesEqual(source, destination);

            temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
            using (var sourceStream = files.OpenRead(source))
            {
                if (sourceStream.Length is < 0 or > MaximumCopiedFileBytes) return false;
                using var destinationStream = files.CreateNewWriteThrough(temporary);
                sourceStream.CopyTo(destinationStream);
                destinationStream.Flush(flushToDisk: true);
            }
            if (!FilesEqual(source, temporary)) return false;
            try
            {
                files.MoveNew(temporary, destination);
                temporary = null;
            }
            catch (IOException) when (files.FileExists(destination))
            {
                if (!FilesEqual(source, destination)) return false;
            }
            return FilesEqual(source, destination);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { files.DeleteFile(temporary); } catch (Exception) { }
            }
        }
    }

    private bool FilesEqual(string first, string second)
    {
        using var left = files.OpenRead(first);
        using var right = files.OpenRead(second);
        if (left.Length is < 0 or > MaximumCopiedFileBytes || left.Length != right.Length)
            return false;
        Span<byte> leftBuffer = stackalloc byte[4096];
        Span<byte> rightBuffer = stackalloc byte[4096];
        while (true)
        {
            var leftRead = left.Read(leftBuffer);
            var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead])) return false;
        }
    }

    private bool IsLoadedSlot(HoyoLabAccountSlot? slot) =>
        slot is not null
        && loadedIndex is not null
        && HoyoLabAccountSlotRules.IsValidIndex(loadedIndex)
        && loadedIndex.Slots.Any(item => item == slot);

    private bool ValidateExistingComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsContained(fullPath)) return false;
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot)) return false;
        var relative = fullPath[volumeRoot.Length..];
        var current = volumeRoot;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!files.EntryExists(current)) continue;
            if ((files.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private bool IsContained(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, publisherProfilesRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                publisherProfilesRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private string LegacyProfilePath() => Path.Combine(publisherProfilesRoot, "HoYoLAB");

    private string ProtectedStateRoot(string slotId) => ProtectedStateRootFor(publisherProfilesRoot, slotId);

    internal static string ProtectedStateRootFor(string publisherProfilesRoot, string slotId) => Path.GetFullPath(Path.Combine(
        publisherProfilesRoot,
        "Accounts",
        "HoYoLAB",
        slotId,
        "Protected"));

    private DateTimeOffset CanonicalUtcNow() => clock.GetUtcNow().ToUniversalTime();

    private DateTimeOffset? NextTimestamp(DateTimeOffset previous)
    {
        var now = CanonicalUtcNow();
        if (now > previous) return now;
        return previous == DateTimeOffset.MaxValue.ToUniversalTime()
            ? null
            : previous.AddTicks(1);
    }

    private static int FindSlot(HoyoLabAccountSlotIndex index, string slotId)
    {
        for (var i = 0; i < index.Slots.Count; i++)
        {
            if (string.Equals(index.Slots[i].Id, slotId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static HoyoLabAccountSlotInitializationResult Ready(HoyoLabAccountSlotIndex index) =>
        new(HoyoLabAccountSlotInitializationState.Ready, index);

    private static HoyoLabAccountSlotInitializationResult Compatibility() =>
        new(HoyoLabAccountSlotInitializationState.LegacyCompatibility, null);

    private static HoyoLabAccountSlotInitializationResult Unavailable() =>
        new(HoyoLabAccountSlotInitializationState.Unavailable, null);

    private static bool IsExpectedFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or CryptographicException
        or InvalidDataException
        or JsonException
        or ArgumentException
        or NotSupportedException;

    internal static byte[] SerializeIndex(HoyoLabAccountSlotIndex index)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", index.SchemaVersion);
            if (index.ActiveSlotId is null) writer.WriteNull("activeSlotId");
            else writer.WriteString("activeSlotId", index.ActiveSlotId);
            writer.WriteBoolean("legacyFallback", index.LegacyFallback);
            writer.WriteStartArray("slots");
            foreach (var slot in index.Slots)
            {
                writer.WriteStartObject();
                writer.WriteString("id", slot.Id);
                writer.WriteString("label", slot.Label);
                writer.WriteBoolean("isLegacy", slot.IsLegacy);
                writer.WriteString("createdAt", slot.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("updatedAt", slot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteBoolean("removalPending", slot.RemovalPending);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static bool TryParseIndex(byte[] plaintext, out HoyoLabAccountSlotIndex? index)
    {
        index = null;
        if (plaintext is null || plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 5,
            });
            var root = document.RootElement;
            if (!HasExactProperties(root, "schemaVersion", "activeSlotId", "legacyFallback", "slots")
                || !TryGetUnique(root, "schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != HoyoLabAccountSlotRules.SchemaVersion
                || !TryGetUnique(root, "activeSlotId", out var activeProperty)
                || !TryGetUnique(root, "legacyFallback", out var fallbackProperty)
                || fallbackProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !TryGetUnique(root, "slots", out var slotsProperty)
                || slotsProperty.ValueKind != JsonValueKind.Array
                || slotsProperty.GetArrayLength() > HoyoLabAccountSlotRules.MaximumSlots)
                return false;

            string? activeSlotId;
            if (activeProperty.ValueKind == JsonValueKind.Null)
                activeSlotId = null;
            else if (activeProperty.ValueKind == JsonValueKind.String)
                activeSlotId = activeProperty.GetString();
            else
                return false;

            var slots = new List<HoyoLabAccountSlot>(slotsProperty.GetArrayLength());
            foreach (var element in slotsProperty.EnumerateArray())
            {
                if (!HasExactProperties(
                        element,
                        "id",
                        "label",
                        "isLegacy",
                        "createdAt",
                        "updatedAt",
                        "removalPending")
                    || !TryGetString(element, "id", out var id)
                    || !TryGetString(element, "label", out var label)
                    || !TryGetBoolean(element, "isLegacy", out var isLegacy)
                    || !TryGetString(element, "createdAt", out var createdRaw)
                    || !TryGetString(element, "updatedAt", out var updatedRaw)
                    || !TryGetBoolean(element, "removalPending", out var removalPending)
                    || !TryParseCanonicalTimestamp(createdRaw, out var createdAt)
                    || !TryParseCanonicalTimestamp(updatedRaw, out var updatedAt))
                    return false;
                slots.Add(new(id, label, isLegacy, createdAt, updatedAt, removalPending));
            }
            var candidate = new HoyoLabAccountSlotIndex(
                schemaVersion,
                activeSlotId,
                slots,
                fallbackProperty.GetBoolean());
            if (!HoyoLabAccountSlotRules.IsValidIndex(candidate)) return false;
            index = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException)
        {
            return false;
        }
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

    private static bool TryGetUnique(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetUnique(element, name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!TryGetUnique(element, name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryParseCanonicalTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value.Length <= 40
            && DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out timestamp)
            && timestamp.Offset == TimeSpan.Zero
            && string.Equals(
                value,
                timestamp.ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private T SerializeMutation<T>(Func<T> mutation, T failure)
    {
        lock (mutationSync)
        {
            using var mutex = new Mutex(initiallyOwned: false, mutationMutexName);
            var acquired = false;
            try
            {
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
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or WaitHandleCannotBeOpenedException)
            {
                return failure;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
                }
            }
        }
    }
}

internal interface IHoyoLabAccountSlotFileBoundary
{
    bool EntryExists(string path);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    FileAttributes GetAttributes(string path);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    FileStream OpenRead(string path);
    FileStream CreateNewWriteThrough(string path);
    void MoveNew(string source, string destination);
    void MoveOverwrite(string source, string destination);
    void DeleteFile(string path);
}

internal sealed class SystemHoyoLabAccountSlotFileBoundary : IHoyoLabAccountSlotFileBoundary
{
    public bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);
    public FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        4096,
        FileOptions.SequentialScan);
    public FileStream CreateNewWriteThrough(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        4096,
        FileOptions.WriteThrough);
    public void MoveNew(string source, string destination) => File.Move(source, destination);
    public void MoveOverwrite(string source, string destination) =>
        File.Move(source, destination, overwrite: true);
    public void DeleteFile(string path) => File.Delete(path);
}
