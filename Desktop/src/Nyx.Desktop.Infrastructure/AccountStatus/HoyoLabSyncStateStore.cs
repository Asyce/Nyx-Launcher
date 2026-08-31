using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

/// <summary>
/// Keeps one protected, per-slot HoYo sync credential and its small deletion
/// outbox. The raw recovery code and account payloads never enter this file.
/// </summary>
public sealed class HoyoLabSyncStateStore
{
    public const int SchemaVersion = 2;
    public const int MaximumPendingDeletions = 8;
    public const string HsrScope = "hsr";
    public const string AllHoyoScope = "all-hoyolab";

    internal const int MaximumPlaintextBytes = 16 * 1024;
    internal const int MaximumCiphertextBytes = 64 * 1024;

    private const string StateDirectoryName = ".protected-hoyolab-sync-state";
    private const string StateFileName = "state.bin";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const int SyncIdCharacters = 48;
    private const int TokenBytes = 32;
    private const int KeyBytes = 32;
    private const int MaximumOperationIdCharacters = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string protectedSlotRoot;
    private readonly string root;
    private readonly string path;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IPublisherRoleBindingFileBoundary files;
    private readonly TimeProvider clock;
    private readonly string mutationMutexName;

    public HoyoLabSyncStateStore(string protectedSlotRoot)
        : this(
            protectedSlotRoot,
            new WindowsCurrentUserRoleBindingProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            TimeProvider.System)
    {
    }

    internal HoyoLabSyncStateStore(
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
        root = Path.GetFullPath(Path.Combine(this.protectedSlotRoot, StateDirectoryName));
        path = Path.Combine(root, StateFileName);
        if (!IsContained(root) || !IsContained(path))
            throw new ArgumentException("Protected sync state escaped its configured slot root.", nameof(protectedSlotRoot));
        mutationMutexName = "Local\\Pengo.Nyx.Desktop.HoyoLabSyncState."
            + Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(
                root.ToUpperInvariant())));
    }

    internal string StatePath => path;
    internal string MutationMutexName => mutationMutexName;

    public HoyoLabSyncState? TryLoad() => SerializeMutation(
        TryLoadCore,
        null);

    public bool TryDeleteIfEmpty(CancellationToken cancellationToken = default)
    {
        try
        {
            return SerializeMutation(
                () =>
                {
                    try
                    {
                        using var current = ReadCurrentForMutation(out var existed);
                        if (current is null || current.CurrentCredential is not null
                            || current.PendingDeletions.Count != 0 || current.PendingRoleDeletions.Count != 0)
                            return false;
                        if (!existed) return true;
                        if (!ValidateExistingComponents(path)) return false;
                        cancellationToken.ThrowIfCancellationRequested();
                        files.Delete(path);
                        return true;
                    }
                    catch (Exception exception) when (IsExpectedFailure(exception))
                    {
                        return false;
                    }
                }, false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public bool TrySave(
        HoyoLabSyncState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SerializeMutation(
            () =>
            {
                try
                {
                    using var current = ReadCurrentForMutation(out var existed);
                    // Missing state must not replay queued work from a pre-cleanup snapshot.
                    if (current is null
                        || current.PendingDeletions.Count != state.PendingDeletions.Count
                        || !current.PendingDeletions.All(existing =>
                            state.PendingDeletions.Any(candidate => PendingEquals(existing, candidate)))
                        || current.PendingRoleDeletions.Count != state.PendingRoleDeletions.Count
                        || !current.PendingRoleDeletions.All(existing =>
                            state.PendingRoleDeletions.Any(candidate => PendingRoleEquals(existing, candidate))))
                        return false;
                    return TryWrite(state, existed, cancellationToken);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    return false;
                }
            },
            false,
            cancellationToken);
    }

    public bool TryRotateCurrentCredential(
        HoyoLabSyncCredential expectedCurrentCredential,
        HoyoLabSyncCredential replacementCredential,
        DateTimeOffset? workerRevision,
        HoyoLabPendingDeletion oldAccountDeletion,
        HoyoLabPendingDeletion preparedReplacementDeletion,
        CancellationToken cancellationToken = default) => TryMutate(
            current =>
            {
                if (!IsValidCredential(expectedCurrentCredential)
                    || !IsValidCredential(replacementCredential)
                    || !current.CurrentCredentialEquals(expectedCurrentCredential)
                    || replacementCredential.SyncId == expectedCurrentCredential.SyncId
                    || workerRevision is null
                    || !IsValidWorkerRevision(workerRevision, UtcNow())
                    || !IsValidPendingDeletion(oldAccountDeletion, UtcNow(), enforceClock: true)
                    || oldAccountDeletion.Scope != AllHoyoScope
                    || oldAccountDeletion.RemoveLocalSlot
                    || !oldAccountDeletion.RequireRevisionMatch
                    || oldAccountDeletion.SyncId != expectedCurrentCredential.SyncId
                    || !oldAccountDeletion.Token.Span.SequenceEqual(expectedCurrentCredential.Token.Span)
                    || !IsValidPendingDeletion(preparedReplacementDeletion, UtcNow(), enforceClock: true)
                    || preparedReplacementDeletion.Scope != AllHoyoScope
                    || preparedReplacementDeletion.RemoveLocalSlot
                    || preparedReplacementDeletion.RequireRevisionMatch
                    || preparedReplacementDeletion.SyncId != replacementCredential.SyncId
                    || !preparedReplacementDeletion.Token.Span.SequenceEqual(replacementCredential.Token.Span)
                    || oldAccountDeletion.OperationId == preparedReplacementDeletion.OperationId
                    || !current.PendingDeletions.Any(item => PendingEquals(item, preparedReplacementDeletion))
                    || current.PendingDeletions.Any(item => item.SyncId == replacementCredential.SyncId
                        && item.OperationId != preparedReplacementDeletion.OperationId)
                    || current.PendingRoleDeletions.Any(item => item.SyncId == replacementCredential.SyncId))
                    return null;
                using var prepared = current.CloneWith(
                    current.CurrentCredential,
                    current.WorkerRevision,
                    current.PendingDeletions.Where(item => item.OperationId != preparedReplacementDeletion.OperationId));
                var enqueued = Enqueue(prepared, oldAccountDeletion);
                try
                {
                    return enqueued?.CloneWith(
                        replacementCredential,
                        workerRevision,
                        enqueued.PendingDeletions);
                }
                finally
                {
                    if (!ReferenceEquals(enqueued, prepared)) enqueued?.Dispose();
                }
            },
            cancellationToken);

    public bool TrySetCurrentCredential(
        HoyoLabSyncCredential? credential,
        CancellationToken cancellationToken = default) => TryMutate(
            current => current.CurrentCredentialEquals(credential)
                && (credential is not null || current.WorkerRevision is null)
                ? current
                : current.CloneWith(credential, null, current.PendingDeletions),
            cancellationToken);

    public bool TryDetachCurrentCredential(
        HoyoLabSyncCredential expectedCurrentCredential,
        HoyoLabPendingDeletion? scopeDeletion,
        CancellationToken cancellationToken = default) => TryMutate(
            current =>
            {
                if (!IsValidCredential(expectedCurrentCredential)
                    || !current.CurrentCredentialEquals(expectedCurrentCredential))
                    return null;
                if (scopeDeletion is null)
                    return current.CloneWith(null, null, current.PendingDeletions);
                if (!IsValidPendingDeletion(scopeDeletion, UtcNow(), enforceClock: true)
                    || scopeDeletion.SyncId != expectedCurrentCredential.SyncId
                    || !scopeDeletion.Token.Span.SequenceEqual(expectedCurrentCredential.Token.Span)
                    || current.PendingRoleDeletions.Any(item => item.OperationId == scopeDeletion.OperationId))
                    return null;
                using var detached = current.CloneWith(
                    null,
                    null,
                    current.PendingDeletions,
                    current.PendingRoleDeletions.Where(item => item.SyncId != scopeDeletion.SyncId
                        || !item.Token.Span.SequenceEqual(scopeDeletion.Token.Span)));
                var enqueued = Enqueue(detached, scopeDeletion);
                return ReferenceEquals(enqueued, detached) ? detached.Normalize() : enqueued;
            },
            cancellationToken);

    public bool TryClearCurrentCredential(
        CancellationToken cancellationToken = default) => TrySetCurrentCredential(null, cancellationToken);

    public bool TrySetWorkerRevision(
        DateTimeOffset? workerRevision,
        CancellationToken cancellationToken = default) => TryMutate(
            current => current.WorkerRevision == workerRevision
                ? current
                : current.CloneWith(current.CurrentCredential, workerRevision, current.PendingDeletions),
            cancellationToken);

    public bool TryEnqueuePendingDeletion(
        HoyoLabPendingDeletion deletion,
        CancellationToken cancellationToken = default) => TryMutate(
            current => Enqueue(current, deletion),
            cancellationToken);

    public bool TryCompletePendingDeletion(
        string operationId,
        CancellationToken cancellationToken = default) => TryMutate(
            current => Complete(current, operationId),
            cancellationToken);

    public bool TryEnqueuePendingRoleDeletion(
        HoyoLabPendingRoleDeletion deletion,
        CancellationToken cancellationToken = default) => TryMutate(
            current =>
            {
                if (!IsValidPendingRoleDeletion(deletion, UtcNow())
                    || current.PendingDeletions.Any(item => item.OperationId == deletion.OperationId))
                    return null;
                var existing = current.PendingRoleDeletions.FirstOrDefault(item => item.OperationId == deletion.OperationId);
                if (existing is not null) return PendingRoleEquals(existing, deletion) ? current : null;
                if (current.PendingDeletions.Count + current.PendingRoleDeletions.Count >= MaximumPendingDeletions)
                    return null;
                return current.CloneWith(current.CurrentCredential, current.WorkerRevision,
                    current.PendingDeletions, current.PendingRoleDeletions.Append(deletion));
            },
            cancellationToken);

    public bool TryCompletePendingRoleDeletion(
        string operationId,
        CancellationToken cancellationToken = default) => TryMutate(
            current =>
            {
                if (!TryNormalizeOperationId(operationId, out _)) return null;
                var pending = current.PendingRoleDeletions.Where(item => item.OperationId != operationId).ToArray();
                return pending.Length == current.PendingRoleDeletions.Count
                    ? current
                    : current.CloneWith(current.CurrentCredential, current.WorkerRevision, current.PendingDeletions, pending);
            },
            cancellationToken);

    private HoyoLabSyncState? TryLoadCore()
    {
        try
        {
            if (!ValidateExistingComponents(protectedSlotRoot)
                || !ValidateExistingComponents(root)
                || !ValidateExistingComponents(path))
                return null;
            var entryExists = files.EntryExists(path);
            var fileExists = files.Exists(path);
            if (!entryExists && !fileExists) return null;
            if (entryExists != fileExists
                || (files.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            return ReadState(path);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return null;
        }
    }

    private bool TryMutate(
        Func<HoyoLabSyncState, HoyoLabSyncState?> mutation,
        CancellationToken cancellationToken)
    {
        return SerializeMutation(
            () =>
            {
                HoyoLabSyncState? current = null;
                HoyoLabSyncState? updated = null;
                try
                {
                    current = ReadCurrentForMutation(out var existed);
                    if (current is null) return false;
                    updated = mutation(current);
                    if (updated is null) return false;
                    if (ReferenceEquals(updated, current)) return true;
                    return TryWrite(updated, existed, cancellationToken);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    return false;
                }
                finally
                {
                    updated?.Dispose();
                    current?.Dispose();
                }
            },
            false,
            cancellationToken);
    }

    private HoyoLabSyncState? ReadCurrentForMutation(out bool existed)
    {
        existed = false;
        if (!ValidateExistingComponents(protectedSlotRoot)
            || !ValidateExistingComponents(root)
            || !ValidateExistingComponents(path))
            return null;

        var entryExists = files.EntryExists(path);
        var fileExists = files.Exists(path);
        if (entryExists != fileExists) return null;
        if (!entryExists) return HoyoLabSyncState.Empty();
        existed = true;
        if ((files.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        return ReadState(path);
    }

    private static HoyoLabSyncState? Enqueue(
        HoyoLabSyncState current,
        HoyoLabPendingDeletion deletion)
    {
        if (!IsValidPendingDeletion(deletion, DateTimeOffset.UtcNow, enforceClock: false)
            || current.PendingRoleDeletions.Any(item => item.OperationId == deletion.OperationId))
            return null;
        var existing = current.PendingDeletions.FirstOrDefault(item =>
            string.Equals(item.OperationId, deletion.OperationId, StringComparison.Ordinal));
        if (existing is not null)
            return PendingEquals(existing, deletion) ? current : null;
        if (current.PendingDeletions.Count + current.PendingRoleDeletions.Count >= MaximumPendingDeletions) return null;
        return current.CloneWith(
            current.CurrentCredential,
            current.WorkerRevision,
            current.PendingDeletions.Append(deletion));
    }

    private static HoyoLabSyncState? Complete(
        HoyoLabSyncState current,
        string operationId)
    {
        if (!TryNormalizeOperationId(operationId, out _)) return null;
        var pending = current.PendingDeletions
            .Where(item => !string.Equals(item.OperationId, operationId, StringComparison.Ordinal))
            .ToArray();
        return pending.Length == current.PendingDeletions.Count
            ? current
            : current.CloneWith(current.CurrentCredential, current.WorkerRevision, pending);
    }

    private bool TryWrite(
        HoyoLabSyncState state,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();

        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        string? temporary = null;
        HoyoLabSyncState? normalized = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRoot();
            var entryExists = files.EntryExists(path);
            var fileExists = files.Exists(path);
            if (entryExists != fileExists
                || !ValidateExistingComponents(path)
                || (entryExists
                    && (files.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                return false;
            if (entryExists)
            {
                using var existing = ReadState(path);
                if (existing is null) return false;
            }
            if (!overwrite && entryExists) return false;

            normalized = state.Normalize();
            if (!IsValidState(normalized, now)) return false;
            plaintext = SerializeState(normalized);
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes) return false;

            temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            if (!IsContained(temporary) || !ValidateExistingComponents(temporary)) return false;
            using (var stream = files.CreateNewWriteThrough(temporary))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            if (!VerifyTemporary(temporary, plaintext, now)) return false;
            if (!ValidateExistingComponents(path)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite || entryExists) files.MoveOverwrite(temporary, path);
            else files.MoveNew(temporary, path);
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
            normalized?.Dispose();
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private HoyoLabSyncState? ReadState(string candidate)
    {
        if (!ValidateExistingComponents(candidate)
            || !files.EntryExists(candidate)
            || !files.Exists(candidate)
            || (files.GetAttributes(candidate) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return null;
        using var stream = files.OpenRead(candidate);
        if (stream.Length is <= 0 or > MaximumCiphertextBytes) return null;
        var ciphertext = new byte[(int)stream.Length];
        byte[]? plaintext = null;
        try
        {
            stream.ReadExactly(ciphertext);
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return null;
            return TryParseState(plaintext, UtcNow(), out var state) ? state : null;
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
        DateTimeOffset now)
    {
        if (!ValidateExistingComponents(temporary)
            || !files.Exists(temporary)
            || (files.GetAttributes(temporary) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        using var stream = files.OpenRead(temporary);
        if (stream.Length is <= 0 or > MaximumCiphertextBytes) return false;
        var ciphertext = new byte[(int)stream.Length];
        byte[]? plaintext = null;
        byte[]? semantic = null;
        HoyoLabSyncState? parsed = null;
        try
        {
            stream.ReadExactly(ciphertext);
            plaintext = protector.Unprotect(ciphertext);
            if (!plaintext.AsSpan().SequenceEqual(expectedPlaintext)
                || !TryParseState(plaintext, now, out parsed)
                || parsed is null)
                return false;
            semantic = SerializeState(parsed);
            return semantic.AsSpan().SequenceEqual(expectedPlaintext);
        }
        finally
        {
            parsed?.Dispose();
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (semantic is not null) CryptographicOperations.ZeroMemory(semantic);
        }
    }

    private void EnsureRoot()
    {
        if (!ValidateExistingComponents(protectedSlotRoot)
            || !ValidateExistingComponents(root))
            throw new IOException("Protected sync state path cannot contain a reparse point.");
        files.CreateDirectory(protectedSlotRoot);
        if (!ValidateExistingComponents(protectedSlotRoot))
            throw new IOException("Protected slot root cannot be a reparse point.");
        files.CreateDirectory(root);
        if (!ValidateExistingComponents(root)
            || (files.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory)
            throw new IOException("Protected sync state root cannot be a reparse point.");
    }

    private bool ValidateExistingComponents(string candidate)
    {
        try
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
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsContained(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        return string.Equals(fullPath, protectedSlotRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                protectedSlotRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private DateTimeOffset UtcNow() => clock.GetUtcNow().ToUniversalTime();

    private T SerializeMutation<T>(
        Func<T> mutation,
        T failure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutationMutexName);
            try
            {
                var signaled = cancellationToken.CanBeCanceled
                    ? WaitHandle.WaitAny(
                        [mutex, cancellationToken.WaitHandle],
                        TimeSpan.FromSeconds(10))
                    : mutex.WaitOne(TimeSpan.FromSeconds(10)) ? 0 : WaitHandle.WaitTimeout;
                if (signaled == 1) throw new OperationCanceledException(cancellationToken);
                acquired = signaled == 0;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired) return failure;
            cancellationToken.ThrowIfCancellationRequested();
            return mutation();
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

    internal static byte[] SerializeState(HoyoLabSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var output = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                if (state.CurrentCredential is null)
                    writer.WriteNull("currentCredential");
                else
                {
                    writer.WriteStartObject("currentCredential");
                    writer.WriteString("syncId", state.CurrentCredential.SyncId);
                    writer.WriteBase64String("token", state.CurrentCredential.Token.Span);
                    writer.WriteBase64String("key", state.CurrentCredential.Key.Span);
                    writer.WriteEndObject();
                }
                WriteNullableTimestamp(writer, "workerRevision", state.WorkerRevision);
                writer.WriteStartArray("pendingDeletions");
                foreach (var deletion in state.PendingDeletions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("syncId", deletion.SyncId);
                    writer.WriteBase64String("token", deletion.Token.Span);
                    writer.WriteString("scope", deletion.Scope);
                    writer.WriteBoolean("removeLocalSlot", deletion.RemoveLocalSlot);
                    writer.WriteBoolean("requireRevisionMatch", deletion.RequireRevisionMatch);
                    WriteNullableTimestamp(writer, "expectedRevision", deletion.ExpectedRevision);
                    writer.WriteString("operationId", deletion.OperationId);
                    WriteTimestamp(writer, "requestedAt", deletion.RequestedAt);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("pendingRoleDeletions");
                foreach (var deletion in state.PendingRoleDeletions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("syncId", deletion.SyncId);
                    writer.WriteBase64String("token", deletion.Token.Span);
                    writer.WriteBase64String("key", deletion.Key.Span);
                    writer.WriteStartObject("binding");
                    writer.WriteString("roleId", deletion.Binding.RoleId);
                    writer.WriteString("server", deletion.Binding.Server);
                    writer.WriteEndObject();
                    writer.WriteString("operationId", deletion.OperationId);
                    WriteTimestamp(writer, "requestedAt", deletion.RequestedAt);
                    WriteNullableTimestamp(writer, "knownResourcesAt", deletion.KnownResourcesAt);
                    WriteNullableTimestamp(writer, "knownAchievementsAt", deletion.KnownAchievementsAt);
                    WriteTimestamp(writer, "deletedAt", deletion.DeletedAt);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return output.ToArray();
        }
        finally
        {
            if (output.TryGetBuffer(out var buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)output.Length)));
        }
    }

    internal static bool TryParseState(
        ReadOnlySpan<byte> plaintext,
        DateTimeOffset utcNow,
        out HoyoLabSyncState? state,
        Action<ReadOnlyMemory<byte>>? parsedSecretObserver = null)
    {
        state = null;
        if (plaintext.Length is <= 0 or > MaximumPlaintextBytes
            || ContainsRecoveryCode(plaintext))
            return false;
        byte[]? json = null;
        HoyoLabSyncCredential? credential = null;
        DateTimeOffset? workerRevision = null;
        IReadOnlyList<HoyoLabPendingDeletion> pending = Array.Empty<HoyoLabPendingDeletion>();
        IReadOnlyList<HoyoLabPendingRoleDeletion> pendingRoles = Array.Empty<HoyoLabPendingRoleDeletion>();
        try
        {
            json = plaintext.ToArray();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 5,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || !version.TryGetInt32(out var schemaVersion)
                || (schemaVersion == 1
                    ? !HasExactProperties(root, "schemaVersion", "currentCredential", "workerRevision", "pendingDeletions")
                    : schemaVersion != SchemaVersion
                        || !HasExactProperties(root, "schemaVersion", "currentCredential", "workerRevision", "pendingDeletions", "pendingRoleDeletions"))
                || !TryParseCredential(root.GetProperty("currentCredential"), out credential))
                return false;
            if (credential is not null)
            {
                parsedSecretObserver?.Invoke(credential.Token);
                parsedSecretObserver?.Invoke(credential.Key);
            }
            if (!TryParseNullableTimestamp(root.GetProperty("workerRevision"), out workerRevision)
                || !TryParsePendingDeletions(root.GetProperty("pendingDeletions"), schemaVersion, out pending, parsedSecretObserver)
                || (schemaVersion == SchemaVersion
                    && !TryParsePendingRoleDeletions(root.GetProperty("pendingRoleDeletions"), out pendingRoles, parsedSecretObserver)))
                return false;

            using var candidate = new HoyoLabSyncState(credential, workerRevision, pending, pendingRoles);
            if (!IsValidState(candidate, utcNow)) return false;
            state = candidate.Normalize();
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or ArgumentException
            or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            credential?.Dispose();
            foreach (var deletion in pending) deletion.Dispose();
            foreach (var deletion in pendingRoles) deletion.Dispose();
            if (json is not null) CryptographicOperations.ZeroMemory(json);
        }
    }

    private static bool TryParseCredential(
        JsonElement element,
        out HoyoLabSyncCredential? credential)
    {
        credential = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!HasExactProperties(element, "syncId", "token", "key")
            || element.GetProperty("syncId").ValueKind != JsonValueKind.String
            || element.GetProperty("syncId").GetString() is not { } syncId
            || !IsLowerHex(syncId, SyncIdCharacters))
            return false;

        byte[]? token = null;
        byte[]? key = null;
        try
        {
            if (!TryDecodeBase64(element.GetProperty("token"), TokenBytes, out token)
                || !TryDecodeBase64(element.GetProperty("key"), KeyBytes, out key))
                return false;
            credential = new HoyoLabSyncCredential(syncId, token, key);
            return true;
        }
        finally
        {
            if (token is not null) CryptographicOperations.ZeroMemory(token);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryParsePendingDeletions(
        JsonElement element,
        int schemaVersion,
        out IReadOnlyList<HoyoLabPendingDeletion> deletions,
        Action<ReadOnlyMemory<byte>>? parsedSecretObserver)
    {
        deletions = Array.Empty<HoyoLabPendingDeletion>();
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > MaximumPendingDeletions)
            return false;
        var parsed = new List<HoyoLabPendingDeletion>(element.GetArrayLength());
        var success = false;
        try
        {
            foreach (var item in element.EnumerateArray())
            {
                if ((schemaVersion == 1
                        ? !HasExactProperties(item, "syncId", "token", "scope", "operationId", "requestedAt")
                        : !HasExactProperties(item, "syncId", "token", "scope", "removeLocalSlot", "requireRevisionMatch", "expectedRevision", "operationId", "requestedAt"))
                    || item.GetProperty("syncId").ValueKind != JsonValueKind.String
                    || item.GetProperty("syncId").GetString() is not { } syncId
                    || !IsLowerHex(syncId, SyncIdCharacters)
                    || item.GetProperty("scope").ValueKind != JsonValueKind.String
                    || item.GetProperty("scope").GetString() is not { } scope
                    || item.GetProperty("operationId").ValueKind != JsonValueKind.String
                    || item.GetProperty("operationId").GetString() is not { } operationId
                    || !TryNormalizeOperationId(operationId, out operationId)
                    || !TryParseTimestamp(item.GetProperty("requestedAt"), out var requestedAt))
                    return false;
                if (schemaVersion == SchemaVersion
                    && item.GetProperty("removeLocalSlot").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                var removeLocalSlot = schemaVersion == SchemaVersion && item.GetProperty("removeLocalSlot").GetBoolean();
                DateTimeOffset? expectedRevision = null;
                if (schemaVersion == SchemaVersion
                    && (item.GetProperty("requireRevisionMatch").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !TryParseNullableTimestamp(item.GetProperty("expectedRevision"), out expectedRevision)))
                    return false;
                var requireRevisionMatch = schemaVersion == SchemaVersion && item.GetProperty("requireRevisionMatch").GetBoolean();
                byte[]? token = null;
                try
                {
                    if (!TryDecodeBase64(item.GetProperty("token"), TokenBytes, out token))
                        return false;
                    var deletion = new HoyoLabPendingDeletion(syncId, token, scope, operationId, requestedAt,
                        removeLocalSlot, requireRevisionMatch, expectedRevision);
                    parsed.Add(deletion);
                    parsedSecretObserver?.Invoke(deletion.Token);
                }
                finally
                {
                    if (token is not null) CryptographicOperations.ZeroMemory(token);
                }
            }
            deletions = parsed.AsReadOnly();
            success = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!success)
                foreach (var deletion in parsed) deletion.Dispose();
        }
    }

    private static bool TryParsePendingRoleDeletions(
        JsonElement element,
        out IReadOnlyList<HoyoLabPendingRoleDeletion> deletions,
        Action<ReadOnlyMemory<byte>>? parsedSecretObserver)
    {
        deletions = Array.Empty<HoyoLabPendingRoleDeletion>();
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumPendingDeletions)
            return false;
        var parsed = new List<HoyoLabPendingRoleDeletion>(element.GetArrayLength());
        var success = false;
        try
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasExactProperties(item, "syncId", "token", "key", "binding", "operationId",
                        "requestedAt", "knownResourcesAt", "knownAchievementsAt", "deletedAt")
                    || item.GetProperty("syncId").ValueKind != JsonValueKind.String
                    || item.GetProperty("syncId").GetString() is not { } syncId
                    || !IsLowerHex(syncId, SyncIdCharacters)
                    || item.GetProperty("operationId").ValueKind != JsonValueKind.String
                    || !TryNormalizeOperationId(item.GetProperty("operationId").GetString(), out var operationId)
                    || !TryParseTimestamp(item.GetProperty("requestedAt"), out var requestedAt)
                    || !TryParseNullableTimestamp(item.GetProperty("knownResourcesAt"), out var knownResourcesAt)
                    || !TryParseNullableTimestamp(item.GetProperty("knownAchievementsAt"), out var knownAchievementsAt)
                    || !TryParseTimestamp(item.GetProperty("deletedAt"), out var deletedAt))
                    return false;
                var binding = item.GetProperty("binding");
                if (!HasExactProperties(binding, "roleId", "server")
                    || binding.GetProperty("roleId").ValueKind != JsonValueKind.String
                    || binding.GetProperty("server").ValueKind != JsonValueKind.String)
                    return false;
                var exactBinding = new PublisherRoleBinding(
                    binding.GetProperty("roleId").GetString()!, binding.GetProperty("server").GetString()!);
                byte[]? token = null;
                byte[]? key = null;
                try
                {
                    if (!TryDecodeBase64(item.GetProperty("token"), TokenBytes, out token)
                        || !TryDecodeBase64(item.GetProperty("key"), KeyBytes, out key))
                        return false;
                    var deletion = new HoyoLabPendingRoleDeletion(syncId, token, key, exactBinding,
                        operationId, requestedAt, knownResourcesAt, knownAchievementsAt, deletedAt);
                    parsed.Add(deletion);
                    parsedSecretObserver?.Invoke(deletion.Token);
                    parsedSecretObserver?.Invoke(deletion.Key);
                }
                finally
                {
                    if (token is not null) CryptographicOperations.ZeroMemory(token);
                    if (key is not null) CryptographicOperations.ZeroMemory(key);
                }
            }
            deletions = parsed.AsReadOnly();
            success = true;
            return true;
        }
        finally
        {
            if (!success)
                foreach (var deletion in parsed) deletion.Dispose();
        }
    }

    private static bool IsValidState(HoyoLabSyncState? state, DateTimeOffset utcNow)
    {
        if (state is null
            || state.CurrentCredential is { } credential && !IsValidCredential(credential)
            || state.PendingDeletions is null
            || state.PendingRoleDeletions is null
            || state.PendingDeletions.Count + state.PendingRoleDeletions.Count > MaximumPendingDeletions
            || !IsValidWorkerRevision(state.WorkerRevision, utcNow))
            return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        HoyoLabPendingDeletion? previous = null;
        foreach (var deletion in state.PendingDeletions)
        {
            if (!IsValidPendingDeletion(deletion, utcNow, enforceClock: true)
                || !ids.Add(deletion.OperationId)
                || (previous is not null && Compare(previous, deletion) >= 0))
                return false;
            previous = deletion;
        }
        HoyoLabPendingRoleDeletion? previousRole = null;
        foreach (var deletion in state.PendingRoleDeletions)
        {
            if (!IsValidPendingRoleDeletion(deletion, utcNow)
                || !ids.Add(deletion.OperationId)
                || (previousRole is not null
                    && (previousRole.RequestedAt > deletion.RequestedAt
                        || previousRole.RequestedAt == deletion.RequestedAt
                            && string.CompareOrdinal(previousRole.OperationId, deletion.OperationId) >= 0)))
                return false;
            previousRole = deletion;
        }
        return true;
    }

    private static bool IsValidCredential(HoyoLabSyncCredential? credential)
    {
        try
        {
            return credential is not null
                && !credential.IsDisposed
                && IsLowerHex(credential.SyncId, SyncIdCharacters)
                && credential.Token.Length == TokenBytes
                && credential.Key.Length == KeyBytes;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal static bool IsValidPendingDeletion(
        HoyoLabPendingDeletion? deletion,
        DateTimeOffset utcNow,
        bool enforceClock)
    {
        try
        {
            return deletion is not null
                && !deletion.IsDisposed
                && IsLowerHex(deletion.SyncId, SyncIdCharacters)
                && deletion.Token.Length == TokenBytes
                && deletion.Scope is HsrScope or AllHoyoScope
                && (!deletion.RemoveLocalSlot || deletion.Scope == AllHoyoScope)
                && (deletion.RequireRevisionMatch
                    ? deletion.Scope == AllHoyoScope && !deletion.RemoveLocalSlot
                        && IsValidWorkerRevision(deletion.ExpectedRevision, enforceClock ? utcNow : DateTimeOffset.MaxValue)
                    : deletion.ExpectedRevision is null)
                && TryNormalizeOperationId(deletion.OperationId, out _)
                && IsValidTimestamp(deletion.RequestedAt, enforceClock ? utcNow : DateTimeOffset.MaxValue);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsValidWorkerRevision(DateTimeOffset? revision, DateTimeOffset utcNow) =>
        revision is null || IsValidTimestamp(revision.Value, utcNow);

    internal static bool IsValidPendingRoleDeletion(HoyoLabPendingRoleDeletion? deletion, DateTimeOffset utcNow)
    {
        try
        {
            return deletion is not null
                && !deletion.IsDisposed
                && IsLowerHex(deletion.SyncId, SyncIdCharacters)
                && deletion.Token.Length == TokenBytes
                && deletion.Key.Length == KeyBytes
                && PublisherAccountCatalog.IsValidRoleBinding(HoyoLabGameBundleRules.GameId, deletion.Binding)
                && TryNormalizeOperationId(deletion.OperationId, out _)
                && IsValidTimestamp(deletion.RequestedAt, utcNow)
                && IsValidObservation(deletion.KnownResourcesAt, utcNow)
                && IsValidObservation(deletion.KnownAchievementsAt, utcNow)
                && IsValidObservation(deletion.DeletedAt, utcNow)
                && (deletion.KnownResourcesAt is null || deletion.DeletedAt > deletion.KnownResourcesAt)
                && (deletion.KnownAchievementsAt is null || deletion.DeletedAt > deletion.KnownAchievementsAt);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsValidObservation(DateTimeOffset? value, DateTimeOffset utcNow) =>
        value is null || IsValidTimestamp(value.Value, utcNow) && value.Value.Ticks % TimeSpan.TicksPerSecond == 0;

    private static bool IsValidTimestamp(DateTimeOffset value, DateTimeOffset utcNow)
    {
        var now = utcNow.ToUniversalTime();
        var maximum = now > DateTimeOffset.MaxValue.AddMinutes(-5)
            ? DateTimeOffset.MaxValue
            : now.AddMinutes(5);
        return value.Offset == TimeSpan.Zero
            && value >= DateTimeOffset.UnixEpoch
            && value <= maximum
            && value.Ticks % TimeSpan.TicksPerMillisecond == 0;
    }

    private static int Compare(
        HoyoLabPendingDeletion left,
        HoyoLabPendingDeletion right)
    {
        var timestamp = left.RequestedAt.CompareTo(right.RequestedAt);
        return timestamp != 0
            ? timestamp
            : string.CompareOrdinal(left.OperationId, right.OperationId);
    }

    private static bool PendingEquals(
        HoyoLabPendingDeletion left,
        HoyoLabPendingDeletion right)
    {
        try
        {
            return left.SyncId == right.SyncId
                && left.Scope == right.Scope
                && left.RemoveLocalSlot == right.RemoveLocalSlot
                && left.RequireRevisionMatch == right.RequireRevisionMatch
                && left.ExpectedRevision == right.ExpectedRevision
                && left.OperationId == right.OperationId
                && left.RequestedAt == right.RequestedAt
                && left.Token.Span.SequenceEqual(right.Token.Span);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool PendingRoleEquals(HoyoLabPendingRoleDeletion left, HoyoLabPendingRoleDeletion right)
    {
        try
        {
            return left.SyncId == right.SyncId
                && left.Binding == right.Binding
                && left.OperationId == right.OperationId
                && left.RequestedAt == right.RequestedAt
                && left.KnownResourcesAt == right.KnownResourcesAt
                && left.KnownAchievementsAt == right.KnownAchievementsAt
                && left.DeletedAt == right.DeletedAt
                && left.Token.Span.SequenceEqual(right.Token.Span)
                && left.Key.Span.SequenceEqual(right.Key.Span);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal static bool TryNormalizeOperationId(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumOperationIdCharacters)
            return false;
        foreach (var character in value)
        {
            if (character is < (char)0x21 or > (char)0x7e) return false;
        }
        if (ContainsRecoveryCode(value)) return false;
        normalized = value;
        return true;
    }

    private static bool TryDecodeBase64(
        JsonElement element,
        int expectedBytes,
        out byte[] decoded)
    {
        decoded = [];
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || value.Length > ((expectedBytes + 2) / 3) * 4)
            return false;
        try
        {
            decoded = Convert.FromBase64String(value);
            if (decoded.Length != expectedBytes || Convert.ToBase64String(decoded) != value)
            {
                CryptographicOperations.ZeroMemory(decoded);
                decoded = [];
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            CryptographicOperations.ZeroMemory(decoded);
            decoded = [];
            return false;
        }
    }

    private static bool TryParseNullableTimestamp(
        JsonElement element,
        out DateTimeOffset? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!TryParseTimestamp(element, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryParseTimestamp(
        JsonElement element,
        out DateTimeOffset value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(
                element.GetString(),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value)
            && value.Offset == TimeSpan.Zero
            && value.Ticks % TimeSpan.TicksPerMillisecond == 0;
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
            value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

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

    private static bool IsLowerHex(string? value, int length) => value is not null
        && value.Length == length
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ContainsRecoveryCode(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> displayPrefix = "NYX-HOYO-"u8;
        ReadOnlySpan<byte> canonicalPrefix = "NYXHOYO"u8;
        for (var start = 0; start < value.Length; start++)
        {
            var prefix = StartsWithAsciiIgnoreCase(value[start..], displayPrefix)
                ? displayPrefix
                : StartsWithAsciiIgnoreCase(value[start..], canonicalPrefix)
                    ? canonicalPrefix
                    : ReadOnlySpan<byte>.Empty;
            if (prefix.IsEmpty) continue;
            var cursor = start + prefix.Length;
            var bodyCharacters = 0;
            while (cursor < value.Length && bodyCharacters < 32)
            {
                var character = value[cursor++];
                if (character is (byte)'-' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                    continue;
                if (character is >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'a' and <= (byte)'z')
                {
                    bodyCharacters++;
                    continue;
                }
                if (character is >= (byte)'2' and <= (byte)'7')
                {
                    bodyCharacters++;
                    continue;
                }
                break;
            }
            if (bodyCharacters == 32) return true;
        }
        return false;
    }

    internal static bool ContainsRecoveryCode(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            return ContainsRecoveryCode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool StartsWithAsciiIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (var index = 0; index < prefix.Length; index++)
        {
            var character = value[index];
            if (character is >= (byte)'a' and <= (byte)'z')
                character = (byte)(character - ('a' - 'A'));
            if (character != prefix[index]) return false;
        }
        return true;
    }

    private static bool IsExpectedFailure(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or CryptographicException
        or InvalidDataException
        or JsonException
        or InvalidOperationException
        or ArgumentException
        or NotSupportedException
        or ObjectDisposedException;
}

public sealed class HoyoLabSyncCredential : IDisposable
{
    private byte[]? token;
    private byte[]? key;
    private int disposed;

    public HoyoLabSyncCredential(
        string syncId,
        ReadOnlyMemory<byte> token,
        ReadOnlyMemory<byte> key)
    {
        if (!IsLowerHex(syncId)
            || token.Length != 32
            || key.Length != 32)
            throw new ArgumentException("HoYo sync credential is invalid.");
        SyncId = syncId;
        byte[]? copiedToken = null;
        byte[]? copiedKey = null;
        try
        {
            copiedToken = token.ToArray();
            copiedKey = key.ToArray();
            this.token = copiedToken;
            this.key = copiedKey;
            copiedToken = null;
            copiedKey = null;
        }
        finally
        {
            if (copiedToken is not null) CryptographicOperations.ZeroMemory(copiedToken);
            if (copiedKey is not null) CryptographicOperations.ZeroMemory(copiedKey);
        }
    }

    public string SyncId { get; }
    public ReadOnlyMemory<byte> Token => (token ?? throw new ObjectDisposedException(nameof(HoyoLabSyncCredential))).AsMemory();
    public ReadOnlyMemory<byte> Key => (key ?? throw new ObjectDisposedException(nameof(HoyoLabSyncCredential))).AsMemory();
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public HoyoLabSyncCredential Clone() => new(SyncId, Token, Key);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var releasedToken = Interlocked.Exchange(ref token, null);
        var releasedKey = Interlocked.Exchange(ref key, null);
        if (releasedToken is not null) CryptographicOperations.ZeroMemory(releasedToken);
        if (releasedKey is not null) CryptographicOperations.ZeroMemory(releasedKey);
    }

    public override string ToString() => nameof(HoyoLabSyncCredential);

    private static bool IsLowerHex(string? value) => value is not null
        && value.Length == 48
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}

public sealed class HoyoLabPendingDeletion : IDisposable
{
    private byte[]? token;
    private int disposed;

    public HoyoLabPendingDeletion(
        string syncId,
        ReadOnlyMemory<byte> token,
        string scope,
        string operationId,
        DateTimeOffset requestedAt,
        bool removeLocalSlot = false,
        bool requireRevisionMatch = false,
        DateTimeOffset? expectedRevision = null)
    {
        if (!IsLowerHex(syncId)
            || token.Length != 32
            || scope is not (HoyoLabSyncStateStore.HsrScope or HoyoLabSyncStateStore.AllHoyoScope)
            || removeLocalSlot && scope != HoyoLabSyncStateStore.AllHoyoScope
            || requireRevisionMatch && (scope != HoyoLabSyncStateStore.AllHoyoScope || removeLocalSlot)
            || !requireRevisionMatch && expectedRevision is not null
            || string.IsNullOrEmpty(operationId)
            || operationId.Length > 128
            || operationId.Any(static character => character < (char)0x21 || character > (char)0x7e)
            || HoyoLabSyncStateStore.ContainsRecoveryCode(operationId))
            throw new ArgumentException("HoYo pending deletion is invalid.");
        SyncId = syncId;
        byte[]? copiedToken = null;
        try
        {
            copiedToken = token.ToArray();
            this.token = copiedToken;
            copiedToken = null;
        }
        finally
        {
            if (copiedToken is not null) CryptographicOperations.ZeroMemory(copiedToken);
        }
        Scope = scope;
        OperationId = operationId;
        RequestedAt = requestedAt;
        RemoveLocalSlot = removeLocalSlot;
        RequireRevisionMatch = requireRevisionMatch;
        ExpectedRevision = expectedRevision;
    }

    public string SyncId { get; }
    public ReadOnlyMemory<byte> Token => (token ?? throw new ObjectDisposedException(nameof(HoyoLabPendingDeletion))).AsMemory();
    public string Scope { get; }
    public string OperationId { get; }
    public DateTimeOffset RequestedAt { get; }
    public bool RemoveLocalSlot { get; }
    public bool RequireRevisionMatch { get; }
    public DateTimeOffset? ExpectedRevision { get; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public HoyoLabPendingDeletion Clone() => new(SyncId, Token, Scope, OperationId, RequestedAt,
        RemoveLocalSlot, RequireRevisionMatch, ExpectedRevision);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var releasedToken = Interlocked.Exchange(ref token, null);
        if (releasedToken is not null) CryptographicOperations.ZeroMemory(releasedToken);
    }

    public override string ToString() => nameof(HoyoLabPendingDeletion);

    private static bool IsLowerHex(string? value) => value is not null
        && value.Length == 48
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}

public sealed class HoyoLabPendingRoleDeletion : IDisposable
{
    private readonly HoyoLabSyncCredential credential;

    public HoyoLabPendingRoleDeletion(
        string syncId,
        ReadOnlyMemory<byte> token,
        ReadOnlyMemory<byte> key,
        PublisherRoleBinding binding,
        string operationId,
        DateTimeOffset requestedAt,
        DateTimeOffset? knownResourcesAt,
        DateTimeOffset? knownAchievementsAt,
        DateTimeOffset deletedAt)
    {
        if (binding is null
            || binding.RoleId is null || binding.Server is null
            || !PublisherAccountCatalog.IsValidRoleBinding(HoyoLabGameBundleRules.GameId, binding)
            || !HoyoLabSyncStateStore.TryNormalizeOperationId(operationId, out _))
            throw new ArgumentException("HoYo pending role deletion is invalid.");
        credential = new(syncId, token, key);
        Binding = binding;
        OperationId = operationId;
        RequestedAt = requestedAt;
        KnownResourcesAt = knownResourcesAt;
        KnownAchievementsAt = knownAchievementsAt;
        DeletedAt = deletedAt;
    }

    public string SyncId => credential.SyncId;
    public ReadOnlyMemory<byte> Token => credential.Token;
    public ReadOnlyMemory<byte> Key => credential.Key;
    public PublisherRoleBinding Binding { get; }
    public string OperationId { get; }
    public DateTimeOffset RequestedAt { get; }
    public DateTimeOffset? KnownResourcesAt { get; }
    public DateTimeOffset? KnownAchievementsAt { get; }
    public DateTimeOffset DeletedAt { get; }
    public bool IsDisposed => credential.IsDisposed;

    public HoyoLabPendingRoleDeletion Clone() => new(SyncId, Token, Key, Binding, OperationId,
        RequestedAt, KnownResourcesAt, KnownAchievementsAt, DeletedAt);

    public void Dispose() => credential.Dispose();

    public override string ToString() => nameof(HoyoLabPendingRoleDeletion);
}

public sealed class HoyoLabSyncState : IDisposable
{
    public HoyoLabSyncState(
        HoyoLabSyncCredential? currentCredential,
        DateTimeOffset? workerRevision,
        IReadOnlyList<HoyoLabPendingDeletion> pendingDeletions,
        IReadOnlyList<HoyoLabPendingRoleDeletion>? pendingRoleDeletions = null)
    {
        ArgumentNullException.ThrowIfNull(pendingDeletions);
        HoyoLabSyncCredential? clonedCredential = null;
        var clonedDeletions = new List<HoyoLabPendingDeletion>(pendingDeletions.Count);
        var clonedRoles = new List<HoyoLabPendingRoleDeletion>(pendingRoleDeletions?.Count ?? 0);
        try
        {
            clonedCredential = currentCredential?.Clone();
            foreach (var deletion in pendingDeletions)
            {
                if (deletion is null)
                    throw new ArgumentException(
                        "Pending deletion list contains null.",
                        nameof(pendingDeletions));
                clonedDeletions.Add(deletion.Clone());
            }
            foreach (var deletion in pendingRoleDeletions ?? Array.Empty<HoyoLabPendingRoleDeletion>())
            {
                if (deletion is null)
                    throw new ArgumentException("Pending role deletion list contains null.", nameof(pendingRoleDeletions));
                clonedRoles.Add(deletion.Clone());
            }

            CurrentCredential = clonedCredential;
            WorkerRevision = workerRevision;
            PendingDeletions = clonedDeletions.ToArray();
            PendingRoleDeletions = clonedRoles.ToArray();
            clonedCredential = null;
            clonedDeletions.Clear();
            clonedRoles.Clear();
        }
        finally
        {
            clonedCredential?.Dispose();
            foreach (var deletion in clonedDeletions) deletion.Dispose();
            foreach (var deletion in clonedRoles) deletion.Dispose();
        }
    }

    public HoyoLabSyncCredential? CurrentCredential { get; }
    public DateTimeOffset? WorkerRevision { get; }
    public IReadOnlyList<HoyoLabPendingDeletion> PendingDeletions { get; }
    public IReadOnlyList<HoyoLabPendingRoleDeletion> PendingRoleDeletions { get; }

    internal static HoyoLabSyncState Empty() => new(null, null, Array.Empty<HoyoLabPendingDeletion>());

    internal HoyoLabSyncState CloneWith(
        HoyoLabSyncCredential? credential,
        DateTimeOffset? workerRevision,
        IEnumerable<HoyoLabPendingDeletion> pending,
        IEnumerable<HoyoLabPendingRoleDeletion>? pendingRoles = null) => new(
            credential,
            workerRevision,
            pending.ToArray(),
            (pendingRoles ?? PendingRoleDeletions).ToArray());

    internal HoyoLabSyncState Normalize() => CloneWith(
        CurrentCredential,
        WorkerRevision,
        PendingDeletions.OrderBy(static item => item.RequestedAt)
            .ThenBy(static item => item.OperationId, StringComparer.Ordinal),
        PendingRoleDeletions.OrderBy(static item => item.RequestedAt)
            .ThenBy(static item => item.OperationId, StringComparer.Ordinal));

    internal bool CurrentCredentialEquals(HoyoLabSyncCredential? other)
    {
        if (CurrentCredential is null || other is null)
            return CurrentCredential is null && other is null;
        try
        {
            return CurrentCredential.SyncId == other.SyncId
                && CurrentCredential.Token.Span.SequenceEqual(other.Token.Span)
                && CurrentCredential.Key.Span.SequenceEqual(other.Key.Span);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        CurrentCredential?.Dispose();
        foreach (var deletion in PendingDeletions) deletion.Dispose();
        foreach (var deletion in PendingRoleDeletions) deletion.Dispose();
    }

    public override string ToString() => nameof(HoyoLabSyncState);
}
