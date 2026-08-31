using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.AccountStatus;
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

        Assert.True(store.TryEnqueuePendingDeletion(deletion));
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
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now, requireRevisionMatch: true);
        using var prepared = PendingForCredential(replacement, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TryEnqueuePendingDeletion(prepared));

        Assert.True(store.TryRotateCurrentCredential(
            stale.CurrentCredential!, replacement, Now, oldAccount, prepared));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(replacement.SyncId, loaded.CurrentCredential!.SyncId);
        Assert.Equal(Now, loaded.WorkerRevision);
        Assert.Equal(new[] { unrelated.OperationId, oldAccount.OperationId },
            loaded.PendingDeletions.Select(item => item.OperationId));
        Assert.Equal(unrelated.Token.ToArray(), loaded.PendingDeletions[0].Token.ToArray());
        Assert.Equal(previous.Token.ToArray(), loaded.PendingDeletions[1].Token.ToArray());
        var beforeStaleRotation = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryRotateCurrentCredential(
            stale.CurrentCredential!, replacement, Now, oldAccount, prepared));
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
        using var prepared = PendingForCredential(replacement, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TryEnqueuePendingDeletion(prepared));
        var before = File.ReadAllBytes(store.StatePath);
        using var deletion = new HoyoLabPendingDeletion(
            failure == "wrong-sync-id" ? replacement.SyncId : previous.SyncId,
            failure == "wrong-token" ? replacement.Token : previous.Token,
            failure == "wrong-scope" ? HoyoLabSyncStateStore.HsrScope : HoyoLabSyncStateStore.AllHoyoScope,
            "old-account",
            Now,
            requireRevisionMatch: failure != "wrong-scope",
            expectedRevision: failure != "wrong-scope" ? Now : null);

        Assert.False(store.TryRotateCurrentCredential(
            previous,
            failure == "same-sync-id" ? previous : replacement,
            failure == "future-revision" ? Now.AddMinutes(6) : Now,
            deletion,
            prepared));
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
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now, requireRevisionMatch: true);
        Assert.True(store.TrySetCurrentCredential(previous));
        using var prepared = PendingForCredential(replacement, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TryEnqueuePendingDeletion(prepared));
        var before = File.ReadAllBytes(store.StatePath);

        boundary.FailMove = true;
        Assert.False(store.TryRotateCurrentCredential(previous, replacement, Now, oldAccount, prepared));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
        boundary.FailMove = false;
        using var cancellation = new CancellationTokenSource();
        boundary.TemporaryReadObserved = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => store.TryRotateCurrentCredential(
            previous, replacement, Now, oldAccount, prepared, cancellation.Token));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutex_contention_cancellation_preserves_canonical_bytes(bool deleteEmptyState)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        if (deleteEmptyState) Assert.True(store.TryClearCurrentCredential());
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
        var mutation = Task.Run(() => deleteEmptyState
            ? store.TryDeleteIfEmpty(cancellation.Token)
            : store.TrySetWorkerRevision(Now, cancellation.Token));
        try
        {
            await Task.Delay(100);
            Assert.False(mutation.IsCompleted);
            cancellation.Cancel();
            if (deleteEmptyState) Assert.False(await mutation);
            else await Assert.ThrowsAsync<OperationCanceledException>(async () => await mutation);
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

    [Theory]
    [InlineData("hsr")]
    [InlineData("all-hoyolab")]
    public void Schema_one_loads_without_role_intents_and_next_write_promotes_exact_schema_two(string scope)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var pending = Pending(2, scope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        using var source = new HoyoLabSyncState(credential, Now, [pending]);
        var legacy = StateJson(source);
        legacy["schemaVersion"] = 1;
        legacy.Remove("pendingRoleDeletions");
        foreach (var item in legacy["pendingDeletions"]!.AsArray())
        {
            item!.AsObject().Remove("removeLocalSlot");
            item.AsObject().Remove("requireRevisionMatch");
            item.AsObject().Remove("expectedRevision");
        }
        WriteProtectedFixture(store.StatePath, Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var before = File.ReadAllBytes(store.StatePath);
        using (var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad()))
        {
            Assert.Equal(credential.SyncId, loaded.CurrentCredential!.SyncId);
            Assert.Equal(pending.OperationId, Assert.Single(loaded.PendingDeletions).OperationId);
            Assert.False(loaded.PendingDeletions[0].RemoveLocalSlot);
            Assert.False(loaded.PendingDeletions[0].RequireRevisionMatch);
            Assert.Null(loaded.PendingDeletions[0].ExpectedRevision);
            Assert.Empty(loaded.PendingRoleDeletions);
        }
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TrySetWorkerRevision(Now.AddMilliseconds(1)));
        var written = File.ReadAllBytes(store.StatePath).Select(value => (byte)(value ^ TrackingProtector.Mask)).ToArray();
        try
        {
            var json = JsonNode.Parse(written)!.AsObject();
            Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
            Assert.Empty(json["pendingRoleDeletions"]!.AsArray());
            Assert.False(json["pendingDeletions"]![0]!["removeLocalSlot"]!.GetValue<bool>());
            Assert.False(json["pendingDeletions"]![0]!["requireRevisionMatch"]!.GetValue<bool>());
            Assert.Null(json["pendingDeletions"]![0]!["expectedRevision"]);
            Assert.Equal(5, json.Count);
        }
        finally { CryptographicOperations.ZeroMemory(written); }
    }

    [Fact]
    public void Changed_or_cleared_credentials_reset_revision_but_identical_credentials_preserve_it()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var first = Credential(1);
        using var changedKey = new HoyoLabSyncCredential(first.SyncId, first.Token, Bytes(99));
        Assert.True(store.TrySetCurrentCredential(first));
        Assert.True(store.TrySetWorkerRevision(Now));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TrySetCurrentCredential(first));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TrySetCurrentCredential(changedKey));
        using (var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad())) Assert.Null(loaded.WorkerRevision);
        Assert.True(store.TrySetWorkerRevision(Now));
        Assert.True(store.TryClearCurrentCredential());
        using var cleared = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Null(cleared.CurrentCredential);
        Assert.Null(cleared.WorkerRevision);
        Assert.True(store.TrySetWorkerRevision(Now));
        Assert.True(store.TryClearCurrentCredential());
        using var clearedAgain = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Null(clearedAgain.WorkerRevision);
    }

    [Fact]
    public void Role_intents_round_trip_order_dispose_secrets_and_do_not_retain_payloads()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var store = CreateStore(root.Path, protector);
        using var later = RolePending(2, requestedAt: Now);
        using var earlier = RolePending(1, requestedAt: Now.AddMinutes(-1));
        Assert.True(store.TryEnqueuePendingRoleDeletion(later));
        Assert.True(store.TryEnqueuePendingRoleDeletion(earlier));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(new[] { earlier.OperationId, later.OperationId }, loaded.PendingRoleDeletions.Select(item => item.OperationId));
        var exact = loaded.PendingRoleDeletions[0];
        Assert.Equal(earlier.Binding, exact.Binding);
        Assert.Equal(earlier.KnownResourcesAt, exact.KnownResourcesAt);
        Assert.Equal(earlier.KnownAchievementsAt, exact.KnownAchievementsAt);
        Assert.Equal(earlier.DeletedAt, exact.DeletedAt);
        Assert.Equal(earlier.Key.ToArray(), exact.Key.ToArray());
        var token = exact.Token;
        var key = exact.Key;
        loaded.Dispose();
        Assert.All(token.ToArray(), value => Assert.Equal(0, value));
        Assert.All(key.ToArray(), value => Assert.Equal(0, value));
        Assert.False(earlier.IsDisposed);
        Assert.Equal(nameof(HoyoLabPendingRoleDeletion), earlier.ToString());
        Assert.All(protector.Buffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
        Assert.All(protector.ProtectedPlaintextSnapshots, bytes =>
        {
            var json = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completedHsrAchievementIds", json, StringComparison.Ordinal);
        });
        protector.ClearSnapshots();
    }

    [Theory]
    [InlineData("syncId")]
    [InlineData("token")]
    [InlineData("key")]
    [InlineData("binding")]
    [InlineData("requestedAt")]
    [InlineData("knownResourcesAt")]
    [InlineData("knownAchievementsAt")]
    [InlineData("deletedAt")]
    public void Role_intents_are_immutable_to_enqueue_and_stale_whole_state_save(string alteredField)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        using var staleWithout = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        using var intent = RolePending(2);
        Assert.True(store.TryEnqueuePendingRoleDeletion(intent));
        var afterEnqueue = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TryEnqueuePendingRoleDeletion(intent));
        Assert.Equal(afterEnqueue, File.ReadAllBytes(store.StatePath));
        Assert.False(store.TrySave(staleWithout));
        using var altered = new HoyoLabPendingRoleDeletion(
            alteredField == "syncId" ? SyncId(3) : intent.SyncId,
            alteredField == "token" ? Bytes(99) : intent.Token,
            alteredField == "key" ? Bytes(99) : intent.Key,
            alteredField == "binding" ? RoleBinding(3) : intent.Binding,
            intent.OperationId,
            alteredField == "requestedAt" ? Now.AddMilliseconds(1) : intent.RequestedAt,
            alteredField == "knownResourcesAt" ? Now.AddMinutes(-3) : intent.KnownResourcesAt,
            alteredField == "knownAchievementsAt" ? null : intent.KnownAchievementsAt,
            alteredField == "deletedAt" ? Now.AddSeconds(1) : intent.DeletedAt);
        Assert.False(store.TryEnqueuePendingRoleDeletion(altered));
        using var alteredState = new HoyoLabSyncState(credential, null, [], [altered]);
        Assert.False(store.TrySave(alteredState));
        Assert.Equal(afterEnqueue, File.ReadAllBytes(store.StatePath));
        using var staleWith = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.True(CreateStore(root.Path).TryCompletePendingRoleDeletion(intent.OperationId));
        var completed = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TrySave(staleWith));
        Assert.True(store.TryCompletePendingRoleDeletion(intent.OperationId));
        Assert.Equal(completed, File.ReadAllBytes(store.StatePath));
        Assert.False(intent.IsDisposed);
        Assert.False(staleWith.PendingRoleDeletions[0].IsDisposed);
    }

    [Fact]
    public void Combined_queue_bound_is_eight_and_operation_ids_are_unique_across_both_lists()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var token = Pending(1, HoyoLabSyncStateStore.HsrScope, Now, "shared-id");
        using var role = RolePending(2, operationId: "shared-id");
        Assert.True(store.TryEnqueuePendingDeletion(token));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryEnqueuePendingRoleDeletion(role));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TryCompletePendingDeletion(token.OperationId));
        Assert.True(store.TryEnqueuePendingRoleDeletion(role));
        before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryEnqueuePendingDeletion(token));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        for (var index = 3; index <= 9; index++)
        {
            if (index % 2 == 0)
            {
                using var next = RolePending(index);
                Assert.True(store.TryEnqueuePendingRoleDeletion(next));
            }
            else
            {
                using var next = Pending(index, HoyoLabSyncStateStore.HsrScope, Now);
                Assert.True(store.TryEnqueuePendingDeletion(next));
            }
        }
        before = File.ReadAllBytes(store.StatePath);
        using var ninthRole = RolePending(10);
        using var ninthToken = Pending(10, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.False(store.TryEnqueuePendingRoleDeletion(ninthRole));
        Assert.False(store.TryEnqueuePendingDeletion(ninthToken));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(8, loaded.PendingDeletions.Count + loaded.PendingRoleDeletions.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hsr")]
    [InlineData("all-hoyolab")]
    public void Detach_atomically_clears_identity_revision_and_only_supersedes_matching_role_intents(string? scope)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var matching = RolePending(1);
        using var other = RolePending(2);
        using var differentToken = new HoyoLabPendingRoleDeletion(credential.SyncId, Bytes(99), credential.Key,
            RoleBinding(3), "different-token", Now, Now.AddMinutes(-2), Now.AddMinutes(-1), Now);
        using var pending = Pending(4, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TrySetWorkerRevision(Now));
        Assert.True(store.TryEnqueuePendingRoleDeletion(matching));
        Assert.True(store.TryEnqueuePendingRoleDeletion(other));
        Assert.True(store.TryEnqueuePendingRoleDeletion(differentToken));
        Assert.True(store.TryEnqueuePendingDeletion(pending));
        using var deletion = scope is null ? null : PendingForCredential(credential, scope, Now);
        Assert.True(store.TryDetachCurrentCredential(credential, deletion));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Null(loaded.CurrentCredential);
        Assert.Null(loaded.WorkerRevision);
        Assert.Equal(scope is null ? 3 : 2, loaded.PendingRoleDeletions.Count);
        Assert.Contains(loaded.PendingRoleDeletions, item => item.OperationId == other.OperationId);
        Assert.Contains(loaded.PendingRoleDeletions, item => item.OperationId == differentToken.OperationId);
        Assert.Contains(loaded.PendingDeletions, item => item.OperationId == pending.OperationId);
        Assert.Equal(scope is null ? 1 : 2, loaded.PendingDeletions.Count);
        var after = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryDetachCurrentCredential(credential, deletion));
        Assert.Equal(after, File.ReadAllBytes(store.StatePath));
        Assert.False(credential.IsDisposed);
        Assert.False(matching.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detach_at_combined_bound_can_only_make_space_by_superseding_matching_role_intents(bool matching)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var role = RolePending(matching ? 1 : 2);
        using var deletion = PendingForCredential(credential, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TryEnqueuePendingRoleDeletion(role));
        for (var index = 3; index <= 9; index++)
        {
            using var pending = Pending(index, HoyoLabSyncStateStore.HsrScope, Now);
            Assert.True(store.TryEnqueuePendingDeletion(pending));
        }
        var before = File.ReadAllBytes(store.StatePath);
        Assert.Equal(matching, store.TryDetachCurrentCredential(credential, deletion));
        if (!matching) Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(8, loaded.PendingDeletions.Count + loaded.PendingRoleDeletions.Count);
        if (matching) Assert.Null(loaded.CurrentCredential);
        else Assert.Equal(credential.SyncId, loaded.CurrentCredential!.SyncId);
    }

    [Theory]
    [InlineData("wrong-current")]
    [InlineData("wrong-sync-id")]
    [InlineData("wrong-token")]
    [InlineData("future")]
    [InlineData("role-id-collision")]
    [InlineData("token-id-collision")]
    public void Detach_rejects_stale_or_unbound_deletion_without_mutation(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var other = Credential(2);
        using var role = RolePending(1);
        using var pending = Pending(3, HoyoLabSyncStateStore.HsrScope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TrySetWorkerRevision(Now));
        Assert.True(store.TryEnqueuePendingRoleDeletion(role));
        Assert.True(store.TryEnqueuePendingDeletion(pending));
        var before = File.ReadAllBytes(store.StatePath);
        using var deletion = new HoyoLabPendingDeletion(
            failure == "wrong-sync-id" ? other.SyncId : credential.SyncId,
            failure == "wrong-token" ? other.Token : credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope,
            failure == "role-id-collision" ? role.OperationId : failure == "token-id-collision" ? pending.OperationId : "detach",
            failure == "future" ? Now.AddMinutes(6) : Now);
        Assert.False(store.TryDetachCurrentCredential(failure == "wrong-current" ? other : credential, deletion));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scope")]
    [InlineData("sync-id")]
    [InlineData("token")]
    [InlineData("operation-id")]
    [InlineData("timestamp")]
    [InlineData("null-revision")]
    [InlineData("other-replacement-token-intent")]
    [InlineData("other-replacement-role-intent")]
    [InlineData("shared-operation-id")]
    [InlineData("old-token-id-collision")]
    [InlineData("old-role-id-collision")]
    [InlineData("disposed-current")]
    [InlineData("disposed-replacement")]
    [InlineData("disposed-prepared")]
    [InlineData("disposed-old")]
    public void Rotation_requires_the_exact_prepared_replacement_and_rejects_remaining_replacement_deletions(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now, requireRevisionMatch: true);
        using var prepared = PendingForCredential(replacement, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(previous));
        if (failure != "missing") Assert.True(store.TryEnqueuePendingDeletion(prepared));
        if (failure == "other-replacement-token-intent")
        {
            using var other = new HoyoLabPendingDeletion(replacement.SyncId, Bytes(99), HoyoLabSyncStateStore.HsrScope, "other", Now);
            Assert.True(store.TryEnqueuePendingDeletion(other));
        }
        if (failure == "other-replacement-role-intent")
        {
            using var other = RolePending(2);
            Assert.True(store.TryEnqueuePendingRoleDeletion(other));
        }
        if (failure == "old-token-id-collision")
        {
            using var other = Pending(9, HoyoLabSyncStateStore.HsrScope, Now, oldAccount.OperationId);
            Assert.True(store.TryEnqueuePendingDeletion(other));
        }
        if (failure == "old-role-id-collision")
        {
            using var other = RolePending(9, operationId: oldAccount.OperationId);
            Assert.True(store.TryEnqueuePendingRoleDeletion(other));
        }
        var before = File.ReadAllBytes(store.StatePath);
        using var supplied = new HoyoLabPendingDeletion(
            failure == "sync-id" ? previous.SyncId : prepared.SyncId,
            failure == "token" ? previous.Token : prepared.Token,
            failure == "scope" ? HoyoLabSyncStateStore.HsrScope : prepared.Scope,
            failure == "operation-id" ? "not-prepared" : prepared.OperationId,
            failure == "timestamp" ? Now.AddMilliseconds(1) : prepared.RequestedAt);
        using var suppliedOld = new HoyoLabPendingDeletion(oldAccount.SyncId, oldAccount.Token, oldAccount.Scope,
            failure == "shared-operation-id" ? prepared.OperationId : oldAccount.OperationId, oldAccount.RequestedAt,
            requireRevisionMatch: true, expectedRevision: oldAccount.ExpectedRevision);
        if (failure == "disposed-current") previous.Dispose();
        if (failure == "disposed-replacement") replacement.Dispose();
        if (failure == "disposed-prepared") supplied.Dispose();
        if (failure == "disposed-old") suppliedOld.Dispose();
        Assert.False(store.TryRotateCurrentCredential(previous, replacement,
            failure == "null-revision" ? null : Now, suppliedOld, supplied));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
    }

    [Fact]
    public void Rotation_at_combined_bound_preserves_unrelated_roles_and_is_crash_safe_on_both_sides_of_commit()
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        using var oldAccount = PendingForCredential(previous, HoyoLabSyncStateStore.AllHoyoScope, Now, requireRevisionMatch: true);
        using var prepared = PendingForCredential(replacement, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(previous));
        Assert.True(store.TryEnqueuePendingDeletion(prepared));
        for (var index = 3; index <= 9; index++)
        {
            using var intent = RolePending(index);
            Assert.True(store.TryEnqueuePendingRoleDeletion(intent));
        }
        using (var before = Assert.IsType<HoyoLabSyncState>(CreateStore(root.Path).TryLoad()))
        {
            Assert.Equal(previous.SyncId, before.CurrentCredential!.SyncId);
            Assert.Equal(prepared.SyncId, Assert.Single(before.PendingDeletions).SyncId);
        }
        Assert.True(store.TryRotateCurrentCredential(previous, replacement, Now, oldAccount, prepared));
        using var after = Assert.IsType<HoyoLabSyncState>(CreateStore(root.Path).TryLoad());
        Assert.Equal(replacement.SyncId, after.CurrentCredential!.SyncId);
        Assert.Equal(previous.SyncId, Assert.Single(after.PendingDeletions).SyncId);
        Assert.Equal(7, after.PendingRoleDeletions.Count);
        Assert.Equal(Now, after.WorkerRevision);
        Assert.All(after.PendingRoleDeletions, intent => Assert.Equal(Now, intent.DeletedAt));
    }

    [Theory]
    [InlineData("enqueue-role", false)]
    [InlineData("enqueue-role", true)]
    [InlineData("complete-role", false)]
    [InlineData("complete-role", true)]
    [InlineData("detach", false)]
    [InlineData("detach", true)]
    public void Role_and_detach_fault_or_cancellation_preserves_exact_bytes_and_caller_secrets(string operation, bool cancel)
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = CreateStore(root.Path, boundary: boundary);
        using var credential = Credential(1);
        using var intent = RolePending(1);
        using var deletion = PendingForCredential(credential, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TrySetWorkerRevision(Now));
        if (operation != "enqueue-role") Assert.True(store.TryEnqueuePendingRoleDeletion(intent));
        var before = File.ReadAllBytes(store.StatePath);
        using var cancellation = new CancellationTokenSource();
        if (cancel) boundary.TemporaryReadObserved = cancellation.Cancel;
        else boundary.FailMove = true;
        bool Mutate() => operation switch
        {
            "enqueue-role" => store.TryEnqueuePendingRoleDeletion(intent, cancellation.Token),
            "complete-role" => store.TryCompletePendingRoleDeletion(intent.OperationId, cancellation.Token),
            _ => store.TryDetachCurrentCredential(credential, deletion, cancellation.Token),
        };
        if (cancel) Assert.Throws<OperationCanceledException>(() => Mutate());
        else Assert.False(Mutate());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.Empty(TemporaryFiles(store.StatePath));
        Assert.False(credential.IsDisposed);
        Assert.False(intent.IsDisposed);
        Assert.False(deletion.IsDisposed);
    }

    [Theory]
    [InlineData("v1-with-role-field")]
    [InlineData("v2-missing-role-field")]
    [InlineData("future-schema")]
    [InlineData("unknown-root")]
    [InlineData("unknown-role-field")]
    [InlineData("unknown-binding-field")]
    [InlineData("missing-role-field")]
    [InlineData("bad-server")]
    [InlineData("bad-role-id")]
    [InlineData("short-token")]
    [InlineData("short-key")]
    [InlineData("null-binding")]
    [InlineData("null-role-list")]
    [InlineData("duplicate-id-cross-list")]
    [InlineData("duplicate-role-id")]
    [InlineData("noncanonical-order")]
    [InlineData("combined-nine")]
    [InlineData("deleted-equals-observation")]
    [InlineData("deleted-older-than-observation")]
    [InlineData("fractional-observation")]
    [InlineData("fractional-deletion")]
    [InlineData("future-deletion")]
    [InlineData("future-request")]
    [InlineData("non-utc-deletion")]
    public void Malformed_schema_two_and_role_intents_fail_closed_without_rewriting_or_secret_retention(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var token = Pending(3, HoyoLabSyncStateStore.HsrScope, Now);
        using var first = RolePending(1);
        using var second = RolePending(2);
        using var source = new HoyoLabSyncState(credential, Now, [token], [first, second]);
        Assert.True(store.TryEnqueuePendingDeletion(token));
        Assert.True(store.TryEnqueuePendingRoleDeletion(first));
        Assert.True(store.TryEnqueuePendingRoleDeletion(second));
        Assert.True(store.TrySave(source));
        var json = StateJson(source);
        var roles = json["pendingRoleDeletions"]!.AsArray();
        var item = roles[1]!.AsObject();
        switch (failure)
        {
            case "v1-with-role-field": json["schemaVersion"] = 1; break;
            case "v2-missing-role-field": json.Remove("pendingRoleDeletions"); break;
            case "future-schema": json["schemaVersion"] = 3; break;
            case "unknown-root": json["payload"] = "forbidden"; break;
            case "unknown-role-field": item["payload"] = "forbidden"; break;
            case "unknown-binding-field": item["binding"]!["game"] = "hsr"; break;
            case "missing-role-field": item.Remove("key"); break;
            case "bad-server": item["binding"]!["server"] = "os_euro"; break;
            case "bad-role-id": item["binding"]!["roleId"] = "nickname"; break;
            case "short-token": item["token"] = "AA=="; break;
            case "short-key": item["key"] = "AA=="; break;
            case "null-binding": item["binding"] = null; break;
            case "null-role-list": json["pendingRoleDeletions"] = null; break;
            case "duplicate-id-cross-list": item["operationId"] = token.OperationId; break;
            case "duplicate-role-id": item["operationId"] = first.OperationId; break;
            case "noncanonical-order": item["requestedAt"] = FormatTimestamp(Now.AddMinutes(-5)); break;
            case "combined-nine":
                for (var index = 4; index <= 9; index++)
                {
                    var extra = item.DeepClone();
                    extra["operationId"] = "role-operation-" + index.ToString("D2", CultureInfo.InvariantCulture);
                    roles.Add(extra);
                }
                break;
            case "deleted-equals-observation": item["deletedAt"] = FormatTimestamp(second.KnownAchievementsAt!.Value); break;
            case "deleted-older-than-observation": item["deletedAt"] = FormatTimestamp(Now.AddMinutes(-3)); break;
            case "fractional-observation": item["knownResourcesAt"] = FormatTimestamp(Now.AddMinutes(-2).AddMilliseconds(1)); break;
            case "fractional-deletion": item["deletedAt"] = FormatTimestamp(Now.AddMilliseconds(1)); break;
            case "future-deletion": item["deletedAt"] = FormatTimestamp(Now.AddMinutes(6)); break;
            case "future-request": item["requestedAt"] = FormatTimestamp(Now.AddMinutes(6)); break;
            case "non-utc-deletion": item["deletedAt"] = "2026-08-31T13:00:00.000+01:00"; break;
        }
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var captured = new List<ReadOnlyMemory<byte>>();
        try
        {
            Assert.False(HoyoLabSyncStateStore.TryParseState(bytes, Now, out var parsed, captured.Add));
            Assert.Null(parsed);
            Assert.All(captured, buffer => Assert.All(buffer.ToArray(), value => Assert.Equal(0, value)));
            WriteProtectedFixture(store.StatePath, bytes);
            var before = File.ReadAllBytes(store.StatePath);
            Assert.Null(store.TryLoad());
            Assert.False(store.TrySetWorkerRevision(Now));
            Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public void Malformed_surrogate_after_a_parsed_role_clears_all_owned_role_and_current_secrets()
    {
        using var credential = Credential(1);
        using var first = RolePending(1);
        using var second = RolePending(2);
        using var source = new HoyoLabSyncState(credential, Now, [], [first, second]);
        var json = StateJson(source).ToJsonString().Replace(
            second.OperationId, "\\ud800", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        var captured = new List<ReadOnlyMemory<byte>>();
        try
        {
            Assert.False(HoyoLabSyncStateStore.TryParseState(bytes, Now, out var parsed, memory =>
            {
                Assert.Contains(memory.ToArray(), value => value != 0);
                captured.Add(memory);
            }));
            Assert.Null(parsed);
            Assert.Equal(4, captured.Count);
            Assert.All(captured, memory => Assert.All(memory.ToArray(), value => Assert.Equal(0, value)));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("older")]
    [InlineData("fractional")]
    [InlineData("offset")]
    [InlineData("future")]
    [InlineData("disposed")]
    public void Invalid_role_intent_times_and_disposal_are_rejected_before_mutation(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);
        using var intent = new HoyoLabPendingRoleDeletion(credential.SyncId, credential.Token, credential.Key,
            RoleBinding(1), "invalid-time", Now, Now.AddMinutes(-2), Now.AddMinutes(-1), failure switch
            {
                "equal" => Now.AddMinutes(-1),
                "older" => Now.AddMinutes(-3),
                "fractional" => Now.AddMilliseconds(1),
                "offset" => Now.ToOffset(TimeSpan.FromHours(1)),
                "future" => Now.AddMinutes(6),
                _ => Now,
            });
        if (failure == "disposed") intent.Dispose();
        Assert.False(store.TryEnqueuePendingRoleDeletion(intent));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
    }

    [Fact]
    public void Role_intent_requires_exact_hsr_binding_valid_secret_lengths_and_safe_operation_id()
    {
        using var credential = Credential(1);
        foreach (var binding in new[] { new PublisherRoleBinding(null!, "prod_official_eur"),
                     new PublisherRoleBinding("700000001", null!), new PublisherRoleBinding("abc", "prod_official_eur"),
                     new PublisherRoleBinding("700000001", "os_euro") })
            Assert.Throws<ArgumentException>(() => new HoyoLabPendingRoleDeletion(credential.SyncId, credential.Token,
                credential.Key, binding, "role", Now, null, null, Now));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingRoleDeletion(credential.SyncId, new byte[31],
            credential.Key, RoleBinding(1), "role", Now, null, null, Now));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingRoleDeletion(credential.SyncId, credential.Token,
            new byte[31], RoleBinding(1), "role", Now, null, null, Now));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingRoleDeletion(credential.SyncId, credential.Token,
            credential.Key, RoleBinding(1), "NYX-HOYO-" + new string('A', 32), Now, null, null, Now));
    }

    [Fact]
    public void Empty_state_deletion_is_exact_idempotent_and_refuses_nonempty_corrupt_reparse_failed_or_canceled_state()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = CreateStore(root.Path, boundary: boundary);
        Assert.True(store.TryDeleteIfEmpty());
        Assert.False(Directory.Exists(root.Path));
        using var credential = Credential(1);
        using var token = Pending(2, HoyoLabSyncStateStore.HsrScope, Now);
        using var role = RolePending(3);
        Assert.True(store.TrySetCurrentCredential(credential));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TryClearCurrentCredential());
        Assert.True(store.TryEnqueuePendingDeletion(token));
        before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TryCompletePendingDeletion(token.OperationId));
        Assert.True(store.TryEnqueuePendingRoleDeletion(role));
        before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.True(store.TryCompletePendingRoleDeletion(role.OperationId));
        before = File.ReadAllBytes(store.StatePath);
        boundary.ReparsePath = store.StatePath;
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        boundary.ReparsePath = null;
        boundary.FailDelete = true;
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        boundary.FailDelete = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(store.TryDeleteIfEmpty(cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        WriteProtectedFixture(store.StatePath, "{}"u8.ToArray());
        var malformed = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryDeleteIfEmpty());
        Assert.Equal(malformed, File.ReadAllBytes(store.StatePath));
        File.WriteAllBytes(store.StatePath, before);
        var sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.StatePath)!, "preserve.bin");
        File.WriteAllBytes(sibling, [1, 2, 3]);
        Assert.True(store.TryDeleteIfEmpty());
        Assert.False(File.Exists(store.StatePath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(sibling));
        Assert.True(Directory.Exists(root.Path));
        Assert.True(store.TryDeleteIfEmpty());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_deletion_cannot_be_restored_by_a_stale_save_after_empty_file_cleanup(bool roleDeletion)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var token = Pending(1, HoyoLabSyncStateStore.HsrScope, Now);
        using var role = RolePending(2);
        Assert.True(roleDeletion ? store.TryEnqueuePendingRoleDeletion(role) : store.TryEnqueuePendingDeletion(token));
        using var stale = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        var savedToken = roleDeletion ? stale.PendingRoleDeletions[0].Token : stale.PendingDeletions[0].Token;
        var expectedToken = savedToken.ToArray();
        var savedKey = roleDeletion ? stale.PendingRoleDeletions[0].Key : ReadOnlyMemory<byte>.Empty;
        var expectedKey = savedKey.ToArray();
        Assert.True(roleDeletion
            ? store.TryCompletePendingRoleDeletion(role.OperationId)
            : store.TryCompletePendingDeletion(token.OperationId));
        Assert.True(store.TryDeleteIfEmpty());
        Assert.False(File.Exists(store.StatePath));

        Assert.False(CreateStore(root.Path).TrySave(stale));
        Assert.False(File.Exists(store.StatePath));
        Assert.Null(store.TryLoad());
        Assert.Equal(expectedToken, savedToken.ToArray());
        Assert.Equal(expectedKey, savedKey.ToArray());
        Assert.False(roleDeletion ? stale.PendingRoleDeletions[0].IsDisposed : stale.PendingDeletions[0].IsDisposed);
        Assert.Empty(TemporaryFiles(store.StatePath));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("credential")]
    [InlineData("token")]
    [InlineData("role")]
    [InlineData("both")]
    public void Missing_state_whole_save_allows_only_empty_or_credential_initialization(string contents)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var token = Pending(2, HoyoLabSyncStateStore.HsrScope, Now);
        using var role = RolePending(3);
        using var initial = new HoyoLabSyncState(
            contents == "empty" ? null : credential, null,
            contents is "token" or "both" ? [token] : [],
            contents is "role" or "both" ? [role] : []);
        var allowed = contents is "empty" or "credential";
        Assert.Equal(allowed, store.TrySave(initial));
        if (allowed)
        {
            using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
            Assert.Equal(initial.CurrentCredential?.SyncId, loaded.CurrentCredential?.SyncId);
            Assert.Empty(loaded.PendingDeletions);
            Assert.Empty(loaded.PendingRoleDeletions);
        }
        else
        {
            Assert.False(File.Exists(store.StatePath));
            Assert.False(Directory.Exists(root.Path));
            Assert.Null(store.TryLoad());
            Assert.True(store.TryEnqueuePendingDeletion(token));
            Assert.True(store.TryEnqueuePendingRoleDeletion(role));
            using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
            Assert.Equal(token.OperationId, Assert.Single(loaded.PendingDeletions).OperationId);
            Assert.Equal(role.OperationId, Assert.Single(loaded.PendingRoleDeletions).OperationId);
        }
        Assert.False(credential.IsDisposed);
        Assert.False(token.IsDisposed);
        Assert.False(role.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_slot_removal_intent_survives_atomic_detach_and_stays_out_of_http(bool removeLocalSlot)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path, clock: TimeProvider.System);
        using var credential = Credential(1);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var deletion = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "remove-everywhere", now, removeLocalSlot);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TrySetWorkerRevision(now));
        Assert.True(store.TryDetachCurrentCredential(credential, deletion));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Null(loaded.CurrentCredential);
        Assert.Null(loaded.WorkerRevision);
        var saved = Assert.Single(loaded.PendingDeletions);
        Assert.Equal(removeLocalSlot, saved.RemoveLocalSlot);
        using var clone = saved.Clone();
        Assert.Equal(removeLocalSlot, clone.RemoveLocalSlot);
        Assert.Equal(removeLocalSlot, StateJson(loaded)["pendingDeletions"]![0]!["removeLocalSlot"]!.GetValue<bool>());

        var handler = new DeletionRequestHandler();
        using var client = new HoyoLabSyncClient(handler, TimeSpan.FromSeconds(5));
        Assert.True((await client.DeletePendingAsync(saved)).IsSuccess);
        Assert.Equal("delete-account", handler.Route);
        Assert.Equal(new[] { "game", "kind", "syncId", "token" }, handler.PropertyNames);
        Assert.False(deletion.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_slot_flag_is_immutable_for_same_operation_id_and_stale_saves(bool originalFlag)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var original = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "same-operation", Now, originalFlag);
        using var altered = new HoyoLabPendingDeletion(original.SyncId, original.Token,
            original.Scope, original.OperationId, original.RequestedAt, !originalFlag);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TryEnqueuePendingDeletion(original));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TryEnqueuePendingDeletion(original));
        Assert.False(store.TryEnqueuePendingDeletion(altered));
        using var stale = new HoyoLabSyncState(credential, null, [altered]);
        Assert.False(store.TrySave(stale));
        Assert.False(store.TryDetachCurrentCredential(credential, altered));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        using var loaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        Assert.Equal(originalFlag, Assert.Single(loaded.PendingDeletions).RemoveLocalSlot);
        Assert.Equal(credential.SyncId, loaded.CurrentCredential!.SyncId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotation_rejects_local_slot_removal_on_old_or_prepared_deletion(bool flagOnReplacement)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var oldCredential = Credential(1);
        using var replacement = Credential(2);
        using var oldDeletion = new HoyoLabPendingDeletion(oldCredential.SyncId, oldCredential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "old-delete", Now, !flagOnReplacement,
            requireRevisionMatch: flagOnReplacement);
        using var prepared = new HoyoLabPendingDeletion(replacement.SyncId, replacement.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "replacement-compensation", Now, flagOnReplacement);
        Assert.True(store.TrySetCurrentCredential(oldCredential));
        Assert.True(store.TryEnqueuePendingDeletion(prepared));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.False(store.TryRotateCurrentCredential(oldCredential, replacement, Now, oldDeletion, prepared));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        Assert.False(oldDeletion.IsDisposed);
        Assert.False(prepared.IsDisposed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("null")]
    [InlineData("hsr-true")]
    [InlineData("v1-field")]
    [InlineData("duplicate")]
    public void Invalid_local_slot_flag_shapes_and_scopes_fail_closed_and_clear_parsed_secrets(string failure)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var first = Pending(2, HoyoLabSyncStateStore.AllHoyoScope, Now);
        using var second = Pending(3, HoyoLabSyncStateStore.AllHoyoScope, Now);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TryEnqueuePendingDeletion(first));
        Assert.True(store.TryEnqueuePendingDeletion(second));
        using var state = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
        var json = StateJson(state);
        var record = json["pendingDeletions"]![1]!.AsObject();
        switch (failure)
        {
            case "missing": record.Remove("removeLocalSlot"); break;
            case "string": record["removeLocalSlot"] = "true"; break;
            case "number": record["removeLocalSlot"] = 1; break;
            case "null": record["removeLocalSlot"] = null; break;
            case "hsr-true": record["scope"] = "hsr"; record["removeLocalSlot"] = true; break;
            case "v1-field": json["schemaVersion"] = 1; json.Remove("pendingRoleDeletions"); break;
        }
        var serialized = json.ToJsonString();
        if (failure == "duplicate") serialized = serialized.Replace("\"removeLocalSlot\":false",
            "\"removeLocalSlot\":false,\"removeLocalSlot\":false", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(serialized);
        var captured = new List<ReadOnlyMemory<byte>>();
        try
        {
            Assert.False(HoyoLabSyncStateStore.TryParseState(bytes, Now, out var parsed, captured.Add));
            Assert.Null(parsed);
            Assert.True(captured.Count >= 2);
            Assert.All(captured, memory => Assert.All(memory.ToArray(), value => Assert.Equal(0, value)));
            WriteProtectedFixture(store.StatePath, bytes);
            var before = File.ReadAllBytes(store.StatePath);
            Assert.Null(store.TryLoad());
            Assert.False(store.TrySetWorkerRevision(Now));
            Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public void Local_slot_removal_cannot_be_requested_for_hsr_only()
    {
        using var credential = Credential(1);
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.HsrScope, "delete-game", Now, removeLocalSlot: true));
        using var ordinary = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.HsrScope, "delete-game", Now);
        Assert.False(ordinary.RemoveLocalSlot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conditional_deletion_preserves_the_copied_revision_across_restart_clone_and_later_revision_updates(bool expectAbsent)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        DateTimeOffset? copiedRevision = expectAbsent ? null : Now.AddMinutes(-1);
        using var deletion = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "rotation-cleanup", Now,
            requireRevisionMatch: true, expectedRevision: copiedRevision);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TryEnqueuePendingDeletion(deletion));
        Assert.True(store.TrySetWorkerRevision(Now));
        using var loaded = Assert.IsType<HoyoLabSyncState>(CreateStore(root.Path).TryLoad());
        var saved = Assert.Single(loaded.PendingDeletions);
        using var clone = saved.Clone();
        Assert.True(saved.RequireRevisionMatch);
        Assert.True(clone.RequireRevisionMatch);
        Assert.Equal(copiedRevision, saved.ExpectedRevision);
        Assert.Equal(copiedRevision, clone.ExpectedRevision);
        Assert.False(saved.RemoveLocalSlot);
        var json = StateJson(loaded)["pendingDeletions"]![0]!.AsObject();
        Assert.True(json["requireRevisionMatch"]!.GetValue<bool>());
        Assert.Equal(copiedRevision?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            json["expectedRevision"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("condition")]
    [InlineData("revision")]
    [InlineData("absence")]
    public void Conditional_deletion_equality_prevents_stale_save_detach_or_operation_id_reuse(string alteration)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var credential = Credential(1);
        using var original = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "rotation-cleanup", Now,
            requireRevisionMatch: true, expectedRevision: Now.AddMinutes(-1));
        using var changed = new HoyoLabPendingDeletion(original.SyncId, original.Token, original.Scope,
            original.OperationId, original.RequestedAt,
            requireRevisionMatch: alteration != "condition",
            expectedRevision: alteration == "revision" ? Now : null);
        Assert.True(store.TrySetCurrentCredential(credential));
        Assert.True(store.TryEnqueuePendingDeletion(original));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.True(store.TryEnqueuePendingDeletion(original));
        Assert.False(store.TryEnqueuePendingDeletion(changed));
        using var stale = new HoyoLabSyncState(credential, null, [changed]);
        Assert.False(store.TrySave(stale));
        Assert.False(store.TryDetachCurrentCredential(credential, changed));
        Assert.Equal(before, File.ReadAllBytes(store.StatePath));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Rotation_requires_conditioned_old_cleanup_and_unconditional_replacement_compensation(
        bool oldConditioned, bool replacementConditioned, bool succeeds)
    {
        using var root = new TemporaryRoot();
        var store = CreateStore(root.Path);
        using var previous = Credential(1);
        using var replacement = Credential(2);
        using var oldDeletion = new HoyoLabPendingDeletion(previous.SyncId, previous.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "old-cleanup", Now, requireRevisionMatch: oldConditioned);
        using var prepared = new HoyoLabPendingDeletion(replacement.SyncId, replacement.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "replacement-compensation", Now, requireRevisionMatch: replacementConditioned);
        Assert.True(store.TrySetCurrentCredential(previous));
        Assert.True(store.TryEnqueuePendingDeletion(prepared));
        var before = File.ReadAllBytes(store.StatePath);
        Assert.Equal(succeeds, store.TryRotateCurrentCredential(previous, replacement, Now, oldDeletion, prepared));
        if (!succeeds) Assert.Equal(before, File.ReadAllBytes(store.StatePath));
        else
        {
            using var loaded = Assert.IsType<HoyoLabSyncState>(CreateStore(root.Path).TryLoad());
            Assert.Equal(replacement.SyncId, loaded.CurrentCredential!.SyncId);
            var saved = Assert.Single(loaded.PendingDeletions);
            Assert.True(saved.RequireRevisionMatch);
            Assert.Null(saved.ExpectedRevision);
        }
    }

    [Theory]
    [InlineData("missing-condition")]
    [InlineData("missing-revision")]
    [InlineData("string-condition")]
    [InlineData("number-condition")]
    [InlineData("null-condition")]
    [InlineData("unconditional-revision")]
    [InlineData("hsr-condition")]
    [InlineData("local-removal")]
    [InlineData("noncanonical-revision")]
    [InlineData("future-revision")]
    [InlineData("number-revision")]
    [InlineData("duplicate-condition")]
    [InlineData("duplicate-revision")]
    public void Schema_two_strictly_rejects_invalid_conditional_deletion_fields(string failure)
    {
        using var credential = Credential(1);
        using var deletion = new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "rotation-cleanup", Now,
            requireRevisionMatch: true, expectedRevision: Now.AddMinutes(-1));
        using var state = new HoyoLabSyncState(credential, null, [deletion]);
        var json = StateJson(state);
        var item = json["pendingDeletions"]![0]!.AsObject();
        switch (failure)
        {
            case "missing-condition": item.Remove("requireRevisionMatch"); break;
            case "missing-revision": item.Remove("expectedRevision"); break;
            case "string-condition": item["requireRevisionMatch"] = "true"; break;
            case "number-condition": item["requireRevisionMatch"] = 1; break;
            case "null-condition": item["requireRevisionMatch"] = null; break;
            case "unconditional-revision": item["requireRevisionMatch"] = false; break;
            case "hsr-condition": item["scope"] = "hsr"; break;
            case "local-removal": item["removeLocalSlot"] = true; break;
            case "noncanonical-revision": item["expectedRevision"] = "2026-08-31T12:00:00Z"; break;
            case "future-revision": item["expectedRevision"] = "2026-08-31T12:06:00.000Z"; break;
            case "number-revision": item["expectedRevision"] = 1; break;
        }
        var serialized = json.ToJsonString();
        if (failure == "duplicate-condition") serialized = serialized.Replace("\"requireRevisionMatch\":true",
            "\"requireRevisionMatch\":true,\"requireRevisionMatch\":true", StringComparison.Ordinal);
        if (failure == "duplicate-revision") serialized = serialized.Replace("\"expectedRevision\":",
            "\"expectedRevision\":null,\"expectedRevision\":", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(serialized);
        try
        {
            Assert.False(HoyoLabSyncStateStore.TryParseState(bytes, Now, out var parsed));
            Assert.Null(parsed);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public void Conditional_deletion_cannot_be_combined_with_game_scope_local_removal_or_an_unconditional_revision()
    {
        using var credential = Credential(1);
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.HsrScope, "invalid", Now, requireRevisionMatch: true));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "invalid", Now, removeLocalSlot: true, requireRevisionMatch: true));
        Assert.Throws<ArgumentException>(() => new HoyoLabPendingDeletion(credential.SyncId, credential.Token,
            HoyoLabSyncStateStore.AllHoyoScope, "invalid", Now, expectedRevision: Now));
    }

    private sealed class DeletionRequestHandler : HttpMessageHandler
    {
        public string? Route { get; private set; }
        public string[] PropertyNames { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Route = request.RequestUri!.Segments[^1];
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(body);
                PropertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(body); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"deleted\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static JsonObject StateJson(HoyoLabSyncState state)
    {
        var bytes = HoyoLabSyncStateStore.SerializeState(state);
        try { return JsonNode.Parse(bytes)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static PublisherRoleBinding RoleBinding(int seed) => new(
        (700000000 + seed).ToString(CultureInfo.InvariantCulture), "prod_official_eur");

    private static HoyoLabPendingRoleDeletion RolePending(int seed,
        string? operationId = null, DateTimeOffset? requestedAt = null)
    {
        using var credential = Credential(seed);
        return new(credential.SyncId, credential.Token, credential.Key, RoleBinding(seed),
            operationId ?? "role-" + OperationId(seed), requestedAt ?? Now,
            Now.AddMinutes(-2), Now.AddMinutes(-1), Now);
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
        DateTimeOffset requestedAt,
        bool requireRevisionMatch = false) => new(
            credential.SyncId,
            credential.Token,
            scope,
            "operation-" + credential.SyncId[^2..],
            requestedAt,
            requireRevisionMatch: requireRevisionMatch,
            expectedRevision: requireRevisionMatch ? requestedAt : null);

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
        public bool FailDelete { get; set; }
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

        public void Delete(string path)
        {
            if (FailDelete) throw new IOException("Injected delete failure.");
            inner.Delete(path);
        }
    }
}
