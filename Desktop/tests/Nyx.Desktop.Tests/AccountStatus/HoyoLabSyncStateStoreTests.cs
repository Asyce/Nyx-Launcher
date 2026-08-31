using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabSyncStateStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_store_round_trips_updates_and_uses_a_non_uid_filename()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);

        Assert.Null(store.TryLoad());
        Assert.Equal("state.bin", Path.GetFileName(store.StatePath));
        Assert.DoesNotContain(SyncId(1), store.StatePath, StringComparison.OrdinalIgnoreCase);

        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TrySetWorkerRevision(Now.AddMilliseconds(123)));

        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(credential.SyncId, loaded.CurrentCredential!.SyncId);
        Assert.Equal(credential.Token.ToArray(), loaded.CurrentCredential.Token.ToArray());
        Assert.Equal(credential.Key.ToArray(), loaded.CurrentCredential.Key.ToArray());
        Assert.Equal(Now.AddMilliseconds(123), loaded.WorkerRevision);
        Assert.Empty(loaded.PendingDeletions);
    }

    [Fact]
    public void Disk_and_serialized_protected_fixture_contain_no_recovery_code_or_payload()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var store = CreateStore(root.Path, protector);
        using var credential = Credential(1);
        using var deletion = Pending(
            2,
            HoyoLabSyncStateStore.HsrScope,
            Now.AddMinutes(-1));
        using var state = new HoyoLabSyncState(credential, Now, [deletion]);
        var recoveryCode = "NYX-HOYO-" + new string('A', 32);

        Assert.True(store.TrySave(state));
        var disk = File.ReadAllBytes(store.StatePath);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(recoveryCode), disk);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(credential.SyncId), disk);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("pendingDeletions"), disk);

        Assert.NotEmpty(protector.ProtectedPlaintextSnapshots);
        Assert.All(protector.ProtectedPlaintextSnapshots, plaintext =>
        {
            var json = Encoding.UTF8.GetString(plaintext);
            Assert.DoesNotContain(recoveryCode, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("recoveryCode", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accountPayload", json, StringComparison.OrdinalIgnoreCase);
        });

        var before = File.ReadAllBytes(store.StatePath);
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(
            SyncId(9),
            Bytes(9),
            HoyoLabSyncStateStore.HsrScope,
            recoveryCode,
            Now));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        protector.ClearSnapshots();
    }

    [Fact]
    public void Current_credential_can_be_replaced_and_cleared_idempotently()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var first = Credential(1);
        using var second = Credential(2);

        Assert.True(store.TrySetCurrentCredential(first));
        var firstBytes = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TrySetCurrentCredential(second));
        var secondBytes = File.ReadAllBytes(store.StatePath);
        Assert.NotEqual(firstBytes, secondBytes);
        using (var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad()))
            Assert.Equal(second.SyncId, loaded.CurrentCredential!.SyncId);

        Assert.True(store.TryClearCurrentCredential());
        var clearedBytes = File.ReadAllBytes(store.StatePath);
        Assert.NotEqual(secondBytes, clearedBytes);
        using (var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad()))
            Assert.Null(loaded.CurrentCredential);
        Assert.True(store.TryClearCurrentCredential());
        Assert.Equal(clearedBytes, File.ReadAllBytes(store.StatePath));
    }

    [Theory]
    [InlineData("sync-id")]
    [InlineData("token")]
    [InlineData("scope")]
    [InlineData("timestamp")]
    public void Whole_state_save_cannot_drop_or_alter_a_deletion_enqueued_after_load(string alteration)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        using var stale = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        using var pending = Pending(2, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.True(CreateStore(root.Path).TryEnqueuePendingDeletion(pending));
        var before = File.ReadAllBytes(store.StatePath);

        Assert.False(store.TrySave(stale));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        using var changed = new HoyoLabPendingDeletion(
            alteration == "sync-id" ? SyncId(3) : pending.SyncId,
            alteration == "token" ? Bytes(99) : pending.Token,
            alteration == "scope" ? HoyoLabSyncStateStore.AllHoyoScope : pending.Scope,
            pending.OperationId,
            alteration == "timestamp" ? Now.AddMilliseconds(1) : pending.RequestedAt);
        using var altered = new HoyoLabSyncState(credential, Now, [changed]);
        Assert.False(store.TrySave(altered));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        using var preserved = new HoyoLabSyncState(credential, Now, [pending]);
        Assert.True(store.TrySave(preserved));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(pending.Scope, Assert.Single(loaded.PendingDeletions).Scope);
    }

    [Fact]
    public void Rotation_rechecks_current_credentials_and_preserves_deletions_enqueued_after_load()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        Assert.True(store.TrySetCurrentCredential(previous));
        using var stale = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        using var unrelated = Pending(3, HoyoLabSyncStateStore.HsrScope, Now.AddMinutes(-1));
        Assert.True(CreateStore(root.Path).TryEnqueuePendingDeletion(unrelated));
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now);

        Assert.True(store.TryRotateCurrentCredential(
            stale.CurrentCredential!, replacement, Now, oldAccount));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(replacement.SyncId, loaded.CurrentCredential!.SyncId);
        Assert.Equal(Now, loaded.WorkerRevision);
        Assert.Equal(new[] { unrelated.OperationId, oldAccount.OperationId },
            loaded.PendingDeletions.Select(item => item.OperationId));
        Assert.Equal(unrelated.Token.ToArray(), loaded.PendingDeletions[0].Token.ToArray());
        Assert.Equal(previous.Token.ToArray(), loaded.PendingDeletions[1].Token.ToArray());
        var beforeStaleRotation = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryRotateCurrentCredential(
            stale.CurrentCredential!, replacement, Now, oldAccount));
        Assert.Equal(beforeStaleRotation, File.ReadAllBytes(store.StatePath));
    }

    [Fact]
    public void Whole_state_save_cannot_restore_a_deletion_completed_after_load()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var pending = Pending(1, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.True(store.TryEnqueuePendingDeletion(pending));
        using var stale = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.True(CreateStore(root.Path).TryCompletePendingDeletion(pending.OperationId));
        var afterCompletion = File.ReadAllBytes(store.StatePath);

        Assert.False(store.TrySave(stale));
        Assert.Equal(afterCompletion, File.ReadAllBytes(store.StatePath));
        Assert.False(Assert.Single(stale.PendingDeletions).IsDisposed);
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Empty(loaded.PendingDeletions);
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Theory]
    [InlineData("same-sync-id")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-sync-id")]
    [InlineData("wrong-token")]
    [InlineData("future-revision")]
    public void Rotation_rejects_unbound_or_invalid_replacements_without_writing(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        Assert.True(store.TrySetCurrentCredential(previous));
        var before = File.ReadAllBytes(store.StatePath);
        using var deletion = new HoyoLabPendingDeletion(
            failure == "wrong-sync-id" ? replacement.SyncId : previous.SyncId,
            failure == "wrong-token" ? replacement.Token : previous.Token,
            failure == "wrong-scope" ? HoyoLabSyncStateStore.HsrScope : HoyoLabSyncStateStore.AllHoyoScope,
            "old-account",
            Now);

        Assert.False(store.TryRotateCurrentCredential(
            previous,
            failure == "same-sync-id" ? previous : replacement,
            failure == "future-revision" ? Now.AddMinutes(6) : Now,
            deletion));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Fact]
    public void Rotation_failure_and_cancellation_leave_current_credentials_and_outbox_untouched()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = CreateStore(root.Path, boundary: boundary);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(previous));
        var before = File.ReadAllBytes(store.StatePath);

        boundary.FailMove = true;
        Assert.False(store.TryRotateCurrentCredential(previous, replacement, Now, oldAccount));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
        boundary.FailMove = false;
        using var cancellation = new CancellationTokenSource();
        boundary.TemporaryReadObserved = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => store.TryRotateCurrentCredential(
            previous, replacement, Now, oldAccount, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Fact]
    public void Malformed_surrogate_revision_clears_credentials_created_before_decode_failure()
    {
        using var credential = Credential(1);
        using var source = new HoyoLabSyncState(credential, null, []);
        var serialized = HoyoLabSyncStateStore.SerializeState(source);
        var fixture = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(serialized).Replace(
            "\"workerRevision\":null", "\"workerRevision\":\"\\ud800\"", StringComparison.Ordinal));
        var captured = new List<ReadOnlyMemory<byte>>();
        try
        {
            Assert.False(HoyoLabSyncStateStore.TryParseState(fixture, Now, out var parsed, memory =>
            {
                Assert.Contains(memory.ToArray(), value => value != 0);
                captured.Add(memory);
            }));
            Assert.Null(parsed);
            Assert.Equal(2, captured.Count);
            Assert.All(captured, memory => Assert.All(memory.ToArray(), value => Assert.Equal(0, value)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
            CryptographicOperations.ZeroMemory(fixture);
        }
    }

    [Fact]
    public void Outbox_keeps_rotated_credentials_in_timestamp_order_for_both_scopes()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var current = Credential(2);
        using var allHoyo = PendingForCredential(
            previous,
            HoyoLabSyncStateStore.AllHoyoScope,
            Now.AddMinutes(-2));
        using var hsr = PendingForCredential(
            current,
            HoyoLabSyncStateStore.HsrScope,
            Now.AddMinutes(-1));

        Assert.True(store.TrySetCurrentCredential(previous));
        Assert.True(store.TrySetCurrentCredential(current));
        Assert.True(store.TryEnqueuePendingDeletion(allHoyo));
        Assert.True(store.TryEnqueuePendingDeletion(hsr));

        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(current.SyncId, loaded.CurrentCredential!.SyncId);
        Assert.Equal(
            new[] { allHoyo.OperationId, hsr.OperationId },
            loaded.PendingDeletions.Select(item => item.OperationId));
        Assert.Equal(previous.SyncId, loaded.PendingDeletions[0].SyncId);
        Assert.Equal(previous.Token.ToArray(), loaded.PendingDeletions[0].Token.ToArray());
        Assert.Equal(HoyoLabSyncStateStore.AllHoyoScope, loaded.PendingDeletions[0].Scope);
        Assert.Equal(HoyoLabSyncStateStore.HsrScope, loaded.PendingDeletions[1].Scope);
    }

    [Fact]
    public void Outbox_has_an_exact_eight_item_bound_duplicate_idempotency_and_retry_safe_completion()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        for (var index = 1; index <= HoyoLabSyncStateStore.MaximumPendingDeletions; index++)
        {
            using var deletion = Pending(
                index,
                index % 2 == 0
                    ? HoyoLabSyncStateStore.AllHoyoScope
                    : HoyoLabSyncStateStore.HsrScope,
                Now.AddMinutes(-index));
            Assert.True(store.TryEnqueuePendingDeletion(deletion));
        }

        using var duplicate = Pending(1, HoyoLabSyncStateStore.HsrScope, Now.AddMinutes(-1));
        var beforeDuplicate = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TryEnqueuePendingDeletion(duplicate));
        Assert.Equal(beforeDuplicate, File.ReadAllBytes(store.StatePath));

        using var conflicting = Pending(
            99,
            HoyoLabSyncStateStore.HsrScope,
            Now.AddMinutes(-1),
            duplicate.OperationId);
        Assert.False(store.TryEnqueuePendingDeletion(conflicting));
        Assert.Equal(beforeDuplicate, File.ReadAllBytes(store.StatePath));

        using var ninth = Pending(
            9,
            HoyoLabSyncStateStore.HsrScope,
            Now.AddMinutes(-9));
        Assert.False(store.TryEnqueuePendingDeletion(ninth));
        Assert.Equal(beforeDuplicate, File.ReadAllBytes(store.StatePath));

        Assert.True(store.TryCompletePendingDeletion(duplicate.OperationId));
        var afterComplete = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TryCompletePendingDeletion(duplicate.OperationId));
        Assert.Equal(afterComplete, File.ReadAllBytes(store.StatePath));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(HoyoLabSyncStateStore.MaximumPendingDeletions - 1, loaded.PendingDeletions.Count);
        Assert.DoesNotContain(loaded.PendingDeletions, item => item.OperationId == duplicate.OperationId);
    }

    [Fact]
    public void Invalid_timestamps_scopes_sizes_and_operation_ids_are_rejected_without_mutation()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);

        Assert.False(store.TrySetWorkerRevision(Now.AddMinutes(6)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.False(store.TrySetWorkerRevision(new DateTimeOffset(
            Now.Ticks + 1,
            TimeSpan.Zero)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));

        using var future = Pending(
            2,
            HoyoLabSyncStateStore.HsrScope,
            Now.AddMinutes(6));
        Assert.False(store.TryEnqueuePendingDeletion(future));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));

        using var localTime = new HoyoLabPendingDeletion(
            SyncId(2),
            Bytes(2),
            HoyoLabSyncStateStore.HsrScope,
            "local-time",
            new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.FromHours(1)));
        using var localState = new HoyoLabSyncState(null, null, [localTime]);
        Assert.False(store.TrySave(localState));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));

        Assert.Throws<ArgumentException>(() => new HoyoLabSyncCredential(
            "ABC",
            Bytes(1),
            Bytes(2)));
        Assert.Throws<ArgumentException>(() => new HoyoLabSyncCredential(
            SyncId(3),
            new byte[31],
            Bytes(3)));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(
            SyncId(4),
            Bytes(4),
            "zzz",
            "operation",
            Now));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(
            SyncId(4),
            Bytes(4),
            HoyoLabSyncStateStore.HsrScope,
            "bad\noperation",
            Now));
    }

    [Fact]
    public void Unknown_duplicate_malformed_future_and_oversize_fixtures_fail_closed_and_preserve_bytes()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var store = CreateStore(root.Path, protector);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var validBytes = File.ReadAllBytes(store.StatePath);

        var timestamp = FormatTimestamp(Now);
        var future = FormatTimestamp(Now.AddMinutes(6));
        var fixtures = new[]
        {
            "{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":null,\"pendingDeletions\":[],\"unknown\":1}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":null,\"pendingDeletions\":[]}",
            "{\"schemaVersion\":1,\"currentCredential\":{\"syncId\":\"ABC\",\"token\":\"\",\"key\":\"\"},\"workerRevision\":null,\"pendingDeletions\":[]}",
            "{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":\"not-a-timestamp\",\"pendingDeletions\":[]}",
            $"{{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":\"{future}\",\"pendingDeletions\":[]}}",
            $"{{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":null,\"pendingDeletions\":[{{\"syncId\":\"{SyncId(2)}\",\"token\":\"AA==\",\"scope\":\"zzz\",\"operationId\":\"operation-2\",\"requestedAt\":\"{timestamp}\"}}]}}",
            $"{{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":null,\"pendingDeletions\":[{{\"syncId\":\"{SyncId(2)}\",\"token\":\"AA==\",\"scope\":\"hsr\",\"operationId\":\"operation-2\",\"requestedAt\":\"{future}\",\"extra\":1}}]}}",
            $"{{\"schemaVersion\":1,\"currentCredential\":null,\"workerRevision\":null,\"pendingDeletions\":[{{\"syncId\":\"{SyncId(2)}\",\"token\":\"AA==\",\"scope\":\"hsr\",\"operationId\":\"NYX-HOYO-{new string('A', 32)}\",\"requestedAt\":\"{timestamp}\"}}]}}",
        };

        foreach (var fixture in fixtures)
        {
            WriteProtectedFixture(store.StatePath, Encoding.UTF8.GetBytes(fixture));
            var malformedBytes = File.ReadAllBytes(store.StatePath);
            Assert.Null(store.TryLoad());
            Assert.False(store.TrySetWorkerRevision(Now));
            Assert.Equal(malformedBytes, File.ReadAllBytes(store.StatePath));
        }

        var unprotectCallsBeforeOversize = protector.UnprotectCalls;
        File.WriteAllBytes(store.StatePath, new byte[HoyoLabSyncStateStore.MaximumCiphertextBytes + 1]);
        var oversizedBytes = File.ReadAllBytes(store.StatePath);
        Assert.Null(store.TryLoad());
        Assert.Equal(unprotectCallsBeforeOversize, protector.UnprotectCalls);
        Assert.Equal(oversizedBytes, File.ReadAllBytes(store.StatePath));

        File.WriteAllBytes(store.StatePath, validBytes);
        Assert.NotNull(store.TryLoad());
    }

    [Fact]
    public void Protector_and_move_failures_preserve_existing_bytes_and_clean_temporary_files()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var boundary = new FaultBoundary();
        var store = CreateStore(root.Path, protector, boundary);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);

        protector.FailProtect = true;
        Assert.False(store.TrySetWorkerRevision(Now.AddMilliseconds(1)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        protector.FailProtect = false;

        boundary.FailMove = true;
        Assert.False(store.TrySetWorkerRevision(Now.AddMilliseconds(2)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
        boundary.FailMove = false;

        protector.FailUnprotect = true;
        Assert.Null(store.TryLoad());
        Assert.False(store.TrySetWorkerRevision(Now.AddMilliseconds(3)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        protector.FailUnprotect = false;

        protector.ProtectedLength = HoyoLabSyncStateStore.MaximumCiphertextBytes + 1;
        Assert.False(store.TrySetWorkerRevision(Now.AddMilliseconds(4)));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Fact]
    public void Reparse_and_containment_boundaries_fail_closed()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(root.Path);
        var boundary = new FaultBoundary { ReparsePath = root.Path };
        var store = CreateStore(root.Path, boundary: boundary);
        using var credential = Credential(1);

        Assert.False(store.TrySetCurrentCredential(credential));
        Assert.Null(store.TryLoad());

        boundary.ReparsePath = null;
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);
        boundary.ReparsePath = store.StatePath;
        Assert.Null(store.TryLoad());
        Assert.False(store.TrySetWorkerRevision(Now));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));

        var normalizedRoot = Path.GetFullPath(root.Path);
        Assert.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            store.StatePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", store.StatePath);
    }

    [Fact]
    public async Task Mutex_contention_cancellation_preserves_canonical_bytes()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, store.MutationMutexName);
            mutex.WaitOne();
            try
            {
                held.SetResult(true);
                release.Wait();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });
        await held.Task;

        using var cancellation = new CancellationTokenSource();
        var mutation = Task.Run(() => store.TrySetWorkerRevision(Now, cancellation.Token));
        try
        {
            await Task.Delay(100);
            Assert.False(mutation.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await mutation);
        }
        finally
        {
            release.Set();
            await holder;
        }

        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Fact]
    public void Cancellation_after_temporary_write_before_promotion_preserves_bytes()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = CreateStore(root.Path, boundary: boundary);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);
        using var cancellation = new CancellationTokenSource();
        boundary.TemporaryReadObserved = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => store.TrySetWorkerRevision(
            Now.AddMilliseconds(1),
            cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Fact]
    public void Corrupt_temporary_file_is_ignored_but_corrupt_target_fails_closed()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var target = store.StatePath;
        var directory = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(directory, "state.bin.tmp.corrupt");
        File.WriteAllBytes(temporary, [0x01, 0x02, 0x03]);
        using (var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad()))
            Assert.Equal(credential.SyncId, loaded.CurrentCredential!.SyncId);
        File.Delete(temporary);

        File.WriteAllBytes(target, [0x01, 0x02, 0x03]);
        var corruptTarget = File.ReadAllBytes(target);
        Assert.Null(store.TryLoad());
        Assert.False(store.TrySetWorkerRevision(Now));
        Assert.Equal(corruptTarget, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Concurrent_writers_are_serialized_by_the_named_mutex()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector { DelayMilliseconds = 5 };
        var first = CreateStore(root.Path, protector);
        using var credential = Credential(1);
        Assert.True(first.TrySetCurrentCredential(credential));
        protector.ResetConcurrency();

        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(index => Task.Run(() =>
        {
            var store = CreateStore(root.Path, protector);
            return store.TrySetWorkerRevision(Now.AddMilliseconds(index));
        })));

        Assert.All(results, Assert.True);
        Assert.Equal(1, protector.MaximumConcurrentOperations);
        using var loaded = Assert.IsType<HoyoLabSyncState>(first.TryLoad());
        Assert.NotNull(loaded.WorkerRevision);
    }

    [Fact]
    public void Store_and_loaded_secret_buffers_are_zeroed_on_cleanup()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var store = CreateStore(root.Path, protector);
        using var sourceCredential = Credential(1);
        var sourceToken = sourceCredential.Token;
        var sourceKey = sourceCredential.Key;
        Assert.True(store.TrySetCurrentCredential(sourceCredential));
        sourceCredential.Dispose();
        Assert.All(sourceToken.ToArray(), value => Assert.Equal(0, value));
        Assert.All(sourceKey.ToArray(), value => Assert.Equal(0, value));

        using var pending = Pending(2, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.True(store.TryEnqueuePendingDeletion(pending));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        var loadedToken = loaded.CurrentCredential!.Token;
        var loadedKey = loaded.CurrentCredential.Key;
        var pendingToken = loaded.PendingDeletions[0].Token;
        loaded.Dispose();
        Assert.All(loadedToken.ToArray(), value => Assert.Equal(0, value));
        Assert.All(loadedKey.ToArray(), value => Assert.Equal(0, value));
        Assert.All(pendingToken.ToArray(), value => Assert.Equal(0, value));
        Assert.All(protector.Buffers, buffer =>
            Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    private static HoyoLabSyncStateStore CreateStore(
        string root,
        IPublisherRoleBindingProtector? protector = null,
        IPublisherRoleBindingFileBoundary? boundary = null,
        TimeProvider? clock = null) => new(
            root,
            protector ?? new TrackingProtector(),
            boundary ?? new SystemPublisherRoleBindingFileBoundary(),
            clock ?? new FixedTimeProvider(Now));

    private static HoyoLabSyncCredential Credential(int seed) => new(
        SyncId(seed),
        Bytes((byte)seed),
        Bytes((byte)(seed + 64)));

    private static HoyoLabPendingDeletion Pending(
        int seed,
        string scope,
        DateTimeOffset requestedAt,
        string? operationId = null) => new(
            SyncId(seed),
            Bytes((byte)(seed + 10)),
            scope,
            operationId ?? OperationId(seed),
            requestedAt);

    private static HoyoLabPendingDeletion PendingForCredential(
        HoyoLabSyncCredential credential,
        string scope,
        DateTimeOffset requestedAt) => new(
            credential.SyncId,
            credential.Token,
            scope,
            "operation-" + credential.SyncId[^2..],
            requestedAt);

    private static string SyncId(int value) => value.ToString(
        "x",
        CultureInfo.InvariantCulture).PadLeft(48, '0');

    private static string OperationId(int value) => "operation-" + value.ToString(
        "D2",
        CultureInfo.InvariantCulture);

    private static byte[] Bytes(byte seed) => Enumerable.Range(0, 32)
        .Select(index => unchecked((byte)(seed + index)))
        .ToArray();

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture);

    private static void WriteProtectedFixture(string path, byte[] plaintext)
    {
        try
        {
            var ciphertext = plaintext.Select(value => (byte)(value ^ TrackingProtector.Mask)).ToArray();
            File.WriteAllBytes(path, ciphertext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string[] TemporaryFiles(string statePath)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "state.bin.tmp.*")
            : Array.Empty<string>();
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nyx-hoyolab-sync-state-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TrackingProtector : IPublisherRoleBindingProtector
    {
        public const byte Mask = 0xa5;
        private readonly ConcurrentBag<byte[]> buffers = [];
        private readonly ConcurrentBag<byte[]> protectedPlaintextSnapshots = [];
        private int activeOperations;
        private int maximumConcurrentOperations;
        private int unprotectCalls;

        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }
        public int? ProtectedLength { get; set; }
        public int DelayMilliseconds { get; set; }
        public int UnprotectCalls => Volatile.Read(ref unprotectCalls);
        public IEnumerable<byte[]> Buffers => buffers;
        public IEnumerable<byte[]> ProtectedPlaintextSnapshots => protectedPlaintextSnapshots;
        public int MaximumConcurrentOperations => Volatile.Read(ref maximumConcurrentOperations);

        public byte[] Protect(byte[] plaintext)
        {
            BeginOperation();
            try
            {
                if (FailProtect) throw new CryptographicException("Injected protect failure.");
                protectedPlaintextSnapshots.Add([.. plaintext]);
                var ciphertext = ProtectedLength is { } length
                    ? new byte[length]
                    : plaintext.Select(value => (byte)(value ^ Mask)).ToArray();
                if (ProtectedLength is not null)
                    for (var index = 0; index < Math.Min(plaintext.Length, ciphertext.Length); index++)
                        ciphertext[index] = (byte)(plaintext[index] ^ Mask);
                buffers.Add(ciphertext);
                return ciphertext;
            }
            finally
            {
                EndOperation();
            }
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            BeginOperation();
            try
            {
                Interlocked.Increment(ref unprotectCalls);
                if (FailUnprotect) throw new CryptographicException("Injected unprotect failure.");
                var plaintext = ciphertext.Select(value => (byte)(value ^ Mask)).ToArray();
                buffers.Add(plaintext);
                return plaintext;
            }
            finally
            {
                EndOperation();
            }
        }

        public void ResetConcurrency()
        {
            Volatile.Write(ref activeOperations, 0);
            Volatile.Write(ref maximumConcurrentOperations, 0);
        }

        public void ClearSnapshots()
        {
            while (protectedPlaintextSnapshots.TryTake(out var snapshot))
                CryptographicOperations.ZeroMemory(snapshot);
        }

        private void BeginOperation()
        {
            var active = Interlocked.Increment(ref activeOperations);
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrentOperations);
                if (active <= current
                    || Interlocked.CompareExchange(ref maximumConcurrentOperations, active, current) == current)
                    break;
            }
            if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
        }

        private void EndOperation() => Interlocked.Decrement(ref activeOperations);
    }

    private sealed class FaultBoundary : IPublisherRoleBindingFileBoundary
    {
        private readonly SystemPublisherRoleBindingFileBoundary inner = new();
        private int temporaryReadObserved;

        public string? ReparsePath { get; set; }
        public bool FailMove { get; set; }
        public Action? TemporaryReadObserved { get; set; }

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public bool EntryExists(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                || inner.EntryExists(path);

        public bool Exists(string path) => inner.Exists(path);

        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                ? inner.GetAttributes(path) | FileAttributes.ReparsePoint
                : inner.GetAttributes(path);

        public FileStream OpenRead(string path)
        {
            if (TemporaryReadObserved is not null
                && path.Contains(".tmp.", StringComparison.Ordinal)
                && Interlocked.Exchange(ref temporaryReadObserved, 1) == 0)
                TemporaryReadObserved?.Invoke();
            return inner.OpenRead(path);
        }

        public FileStream CreateNewWriteThrough(string path) => inner.CreateNewWriteThrough(path);

        public void MoveNew(string source, string destination)
        {
            if (FailMove) throw new IOException("Injected move failure.");
            inner.MoveNew(source, destination);
        }

        public void MoveOverwrite(string source, string destination)
        {
            if (FailMove) throw new IOException("Injected move failure.");
            inner.MoveOverwrite(source, destination);
        }

        public void Delete(string path) => inner.Delete(path);
    }
}
