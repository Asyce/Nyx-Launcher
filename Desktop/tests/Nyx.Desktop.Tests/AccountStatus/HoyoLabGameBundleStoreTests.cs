using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabGameBundleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstObservation = Now.AddHours(-2);
    private static readonly DateTimeOffset SecondObservation = Now.AddHours(-1);

    [Fact]
    public void Eight_exact_roles_round_trip_with_stable_selection_and_independent_typed_observations()
    {
        using var root = new TemporaryRoot();
        var roles = Enumerable.Range(1, 8)
            .Select(index => RoleData(index, index == 2 ? "prod_official_usa" : "prod_official_eur"))
            .ToArray();
        roles[1] = roles[1] with
        {
            Role = RoleRecord(RoleId(1), "prod_official_usa", "Second"),
            Observations = Observations(achievements: SecondObservation),
            CompletedHsrAchievementIds = [1, 7, 20],
        };
        roles[0] = roles[0] with
        {
            Observations = Observations(resources: FirstObservation),
            Resource = Resource(FirstObservation),
        };
        var selected = roles[1].Role.Binding;
        var bundle = Bundle(
            roles,
            selected,
            Consents(resources: true, achievements: true));
        var store = Store(root.Path);

        Assert.True(store.TrySave(bundle));
        var loaded = Assert.IsType<HoyoLabGameBundle>(store.TryLoad());

        Assert.Equal(8, loaded.Roles.Count);
        Assert.Equal(selected, loaded.SelectedRole);
        Assert.Equal(2, loaded.Roles.Count(role => role.Role.Binding.RoleId == RoleId(1)));
        Assert.Equal(FirstObservation, loaded.Roles[0].Observations.Resources);
        Assert.Equal(SecondObservation, loaded.Roles[1].Observations.Achievements);
        Assert.True(loaded.Roles[0].Resource!.IsStale);
        Assert.Equal([1, 7, 20], loaded.Roles[1].CompletedHsrAchievementIds);
        Assert.DoesNotContain(RoleId(1), BundlePath(root.Path), StringComparison.Ordinal);
        Assert.Equal(nameof(HoyoLabGameBundle), loaded.ToString());
        Assert.Equal(nameof(HoyoLabGameBundleRole), loaded.Roles[0].ToString());

        Assert.False(store.TrySave(bundle with { Roles = [.. roles, RoleData(9)] }));
        Assert.False(store.TrySave(bundle with { Roles = [roles[0], roles[0]] }));
        Assert.False(store.TrySave(bundle with { SelectedRole = RoleRecord(RoleId(9)).Binding }));
        Assert.Equal(selected, store.TryLoad()!.SelectedRole);
    }

    [Fact]
    public void Strict_schema_bounds_and_typed_payload_rules_fail_closed()
    {
        using var root = new TemporaryRoot();
        var bundle = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
        var valid = HoyoLabGameBundleStore.SerializeBundle(bundle);

        Assert.True(HoyoLabGameBundleStore.TryParseBundle(valid, Now, out _));
        Assert.False(Parse(Mutate(valid, "\"schemaVersion\":2", "\"schemaVersion\":3")));
        Assert.False(Parse(Mutate(valid, "\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2")));
        Assert.False(Parse(Mutate(valid, "\"gameId\":\"hsr\"", "\"gameId\":\"hsr\",\"extra\":0")));
        Assert.False(HoyoLabGameBundleStore.TryParseBundle(
            new byte[HoyoLabGameBundleStore.MaximumPlaintextBytes + 1],
            Now,
            out _));

        var store = Store(root.Path);
        var badMetadata = bundle with
        {
            Roles = [RoleData(1) with { Role = RoleRecord(RoleId(1)) with { Nickname = "bad\nname" } }],
        };
        Assert.False(store.TrySave(badMetadata));
        Assert.False(store.TrySave(bundle with
        {
            Roles =
            [
                RoleData(1) with
                {
                    Observations = Observations(achievements: FirstObservation),
                    CompletedHsrAchievementIds = [7, 7],
                },
            ],
            Consents = Consents(achievements: true),
        }));
        Assert.False(store.TrySave(bundle with
        {
            Roles =
            [
                RoleData(1) with
                {
                    Observations = Observations(achievements: FirstObservation),
                    CompletedHsrAchievementIds = Enumerable.Range(
                        1,
                        HoyoLabGameBundleRules.MaximumAchievementIds + 1)
                        .Select(static id => (long)id)
                        .ToArray(),
                },
            ],
            Consents = Consents(achievements: true),
        }));
        Assert.False(store.TrySave(bundle with
        {
            Roles =
            [
                RoleData(1) with
                {
                    Observations = Observations(inventory: FirstObservation),
                },
            ],
        }));
        Assert.False(store.TrySave(bundle with
        {
            Roles =
            [
                RoleData(1) with
                {
                    Observations = Observations(resources: Now.AddMinutes(6)),
                    Resource = Resource(Now.AddMinutes(6)),
                },
            ],
            Consents = Consents(resources: true),
        }));
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Exact_role_capability_tombstones_preserve_other_roles_and_require_canonical_order()
    {
        using var root = new TemporaryRoot();
        var firstRole = RoleData(1);
        var secondRole = RoleData(2) with
        {
            Observations = Observations(achievements: SecondObservation),
            CompletedHsrAchievementIds = [1, 7, 20],
        };
        var capabilityTombstones = new[]
        {
            new HoyoLabCapabilityTombstone(
                firstRole.Role.Binding,
                HoyoLabGameBundleRules.Achievements,
                FirstObservation),
            new HoyoLabCapabilityTombstone(
                firstRole.Role.Binding,
                HoyoLabGameBundleRules.Resources,
                FirstObservation),
        };
        var roleTombstones = new[]
        {
            new HoyoLabRoleTombstone(RoleRecord(RoleId(3)).Binding, FirstObservation),
            new HoyoLabRoleTombstone(RoleRecord(RoleId(4)).Binding, FirstObservation),
        };
        var valid = Bundle(
            [firstRole, secondRole],
            secondRole.Role.Binding,
            Consents(achievements: true),
            capabilityTombstones: capabilityTombstones,
            roleTombstones: roleTombstones);
        var store = Store(root.Path);

        Assert.True(store.TrySave(valid));
        var loaded = store.TryLoad()!;
        Assert.Equal(capabilityTombstones, loaded.CapabilityTombstones);
        Assert.Equal([1, 7, 20], loaded.Roles[1].CompletedHsrAchievementIds);
        Assert.False(store.TrySave(valid with
        {
            CapabilityTombstones = capabilityTombstones.Reverse().ToArray(),
        }));
        Assert.False(store.TrySave(valid with
        {
            RoleTombstones = roleTombstones.Reverse().ToArray(),
        }));
    }

    [Fact]
    public void Zero_active_roles_round_trip_and_role_history_does_not_reduce_the_active_cap()
    {
        using var root = new TemporaryRoot();
        var deleted = new HoyoLabRoleTombstone(RoleRecord(RoleId(9)).Binding, FirstObservation);
        var empty = Bundle(
            Array.Empty<HoyoLabGameBundleRole>(),
            null,
            roleTombstones: [deleted]);
        var store = Store(root.Path);

        Assert.True(store.TrySave(empty));
        var loadedEmpty = store.TryLoad()!;
        Assert.Empty(loadedEmpty.Roles);
        Assert.Null(loadedEmpty.SelectedRole);
        Assert.Equal(deleted, Assert.Single(loadedEmpty.RoleTombstones));

        var roles = Enumerable.Range(1, HoyoLabGameBundleRules.MaximumRoles)
            .Select(static index => RoleData(index))
            .ToArray();
        var full = Bundle(roles, roles[0].Role.Binding, roleTombstones: [deleted]);
        Assert.True(store.TrySave(full));
        Assert.Equal(HoyoLabGameBundleRules.MaximumRoles, store.TryLoad()!.Roles.Count);
    }

    [Fact]
    public void Incomplete_capability_consents_remain_disabled()
    {
        using var root = new TemporaryRoot();
        var role = RoleData(1);
        var bundle = Bundle([role], role.Role.Binding);
        var incomplete = new[]
        {
            bundle.Consents with { Inventory = true },
            bundle.Consents with { Builds = true },
            bundle.Consents with { Exploration = true },
            bundle.Consents with { Endgame = true },
            bundle.Consents with { Events = true },
            bundle.Consents with { Currency = true },
        };

        Assert.All(incomplete, consents =>
            Assert.False(Store(root.Path).TrySave(bundle with { Consents = consents })));
        Assert.False(File.Exists(BundlePath(root.Path)));
    }

    [Fact]
    public void Maximum_safe_achievement_bundle_fits_the_locked_ciphertext_cap()
    {
        using var root = new TemporaryRoot();
        var firstId = HoyoLabGameBundleRules.MaximumAchievementId
            - HoyoLabGameBundleRules.MaximumAchievementIds
            + 1;
        var ids = Enumerable.Range(0, HoyoLabGameBundleRules.MaximumAchievementIds)
            .Select(index => firstId + index)
            .ToArray();
        var roles = Enumerable.Range(1, HoyoLabGameBundleRules.MaximumRoles)
            .Select(index => RoleData(index) with
            {
                Observations = Observations(achievements: FirstObservation),
                CompletedHsrAchievementIds = ids,
            })
            .ToArray();
        var bundle = Bundle(
            roles,
            roles[0].Role.Binding,
            Consents(achievements: true));
        ReadOnlyMemory<byte> cleared = default;
        var plaintext = HoyoLabGameBundleStore.SerializeBundle(
            bundle,
            buffer => cleared = buffer);
        try
        {
            Assert.InRange(plaintext.Length, 512 * 1024 + 1, HoyoLabGameBundleStore.MaximumPlaintextBytes);
            Assert.Equal(HoyoLabGameBundleStore.MaximumPlaintextBytes, cleared.Length);
            Assert.All(cleared.ToArray(), value => Assert.Equal(0, value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        var store = new HoyoLabGameBundleStore(root.Path);

        Assert.True(store.TrySave(bundle));
        var length = new FileInfo(BundlePath(root.Path)).Length;
        Assert.InRange(length, 512 * 1024 + 1, HoyoLabGameBundleStore.MaximumCiphertextBytes);
        var loaded = store.TryLoad()!;
        Assert.Equal(HoyoLabGameBundleRules.MaximumRoles, loaded.Roles.Count);
        Assert.All(loaded.Roles, role =>
            Assert.Equal(HoyoLabGameBundleRules.MaximumAchievementIds, role.CompletedHsrAchievementIds!.Count));
    }

    [Fact]
    public void Serialization_clears_its_used_backing_buffer_on_success_and_exception()
    {
        var role = RoleData(1);
        var bundle = Bundle([role], role.Role.Binding);
        ReadOnlyMemory<byte> cleared = default;

        var serialized = HoyoLabGameBundleStore.SerializeBundle(
            bundle,
            buffer => cleared = buffer);

        Assert.NotEmpty(serialized);
        Assert.Equal(HoyoLabGameBundleStore.MaximumPlaintextBytes, cleared.Length);
        Assert.All(cleared.ToArray(), value => Assert.Equal(0, value));

        cleared = default;
        Assert.Throws<NullReferenceException>(() => HoyoLabGameBundleStore.SerializeBundle(
            bundle with { Roles = [null!] },
            buffer => cleared = buffer));
        Assert.NotEmpty(cleared.ToArray());
        Assert.All(cleared.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Wrong_named_kernel_object_fails_closed_without_writing()
    {
        using var root = new TemporaryRoot();
        var store = Store(root.Path);
        var role = RoleData(1);
        var bundle = Bundle([role], role.Role.Binding);
        using var collision = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            store.MutationMutexName);

        Assert.False(store.TrySave(bundle));
        Assert.Null(store.TryLoad());
        Assert.False(store.TryMigrateFromV1(role.Role));
        Assert.False(File.Exists(BundlePath(root.Path)));
    }

    [Fact]
    public void Migration_rereads_and_verifies_temporary_data_without_touching_v1_sources()
    {
        using var root = new TemporaryRoot();
        var protector = new TrackingProtector();
        var role = RoleRecord(RoleId(1), nickname: "Test account");
        var resource = Resource(FirstObservation);
        var roles = new PublisherRoleBindingStore(root.Path, protector);
        var resources = new PublisherResourceSnapshotStore(root.Path, protector);
        Assert.True(roles.SaveRecord(HoyoLabGameBundleRules.GameId, role));
        Assert.True(resources.Save(resource, role.Binding));
        var rolePath = LegacyRolePath(root.Path);
        var resourcePath = LegacyResourcePath(root.Path);
        var roleBytes = File.ReadAllBytes(rolePath);
        var resourceBytes = File.ReadAllBytes(resourcePath);
        var boundary = new FaultBoundary();
        var store = Store(root.Path, protector, boundary);

        Assert.False(store.TryMigrateFromV1(role, resource, RoleRecord(RoleId(2)).Binding));
        Assert.True(store.TryMigrateFromV1(role, resource, role.Binding));

        Assert.True(boundary.MoveNewObservedAfterTemporaryRead);
        Assert.Equal(roleBytes, File.ReadAllBytes(rolePath));
        Assert.Equal(resourceBytes, File.ReadAllBytes(resourcePath));
        var migrated = Assert.IsType<HoyoLabGameBundle>(store.TryLoad());
        Assert.Equal(role, migrated.Roles[0].Role);
        Assert.Equal(role.Binding, migrated.SelectedRole);
        Assert.True(migrated.Consents.Resources);
        Assert.Equal(resource with { IsStale = true }, migrated.Roles[0].Resource);
        Assert.Empty(TemporaryFiles(root.Path));
    }

    [Fact]
    public void Migration_never_downgrades_or_replaces_any_existing_bundle()
    {
        using var root = new TemporaryRoot();
        var store = Store(root.Path);
        var first = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
        Assert.True(store.TrySave(first));
        var before = File.ReadAllBytes(BundlePath(root.Path));

        Assert.False(store.TryMigrateFromV1(RoleRecord(RoleId(2))));
        Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));

        File.WriteAllText(BundlePath(root.Path), "{\"schemaVersion\":3}");
        var future = File.ReadAllBytes(BundlePath(root.Path));
        Assert.False(store.TryMigrateFromV1(RoleRecord(RoleId(2))));
        Assert.Equal(future, File.ReadAllBytes(BundlePath(root.Path)));
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Injected_write_failures_preserve_v1_and_prior_v2_and_clean_temporaries()
    {
        foreach (var failure in Enum.GetValues<InjectedFailure>())
        {
            using var root = new TemporaryRoot();
            var role = RoleRecord(RoleId(1));
            var legacyProtector = new TrackingProtector();
            var legacy = new PublisherRoleBindingStore(root.Path, legacyProtector);
            Assert.True(legacy.SaveRecord(HoyoLabGameBundleRules.GameId, role));
            var legacyPath = LegacyRolePath(root.Path);
            var legacyBytes = File.ReadAllBytes(legacyPath);
            var protector = new TrackingProtector();
            var boundary = new FaultBoundary();
            Configure(failure, protector, boundary, migration: true);

            Assert.False(Store(root.Path, protector, boundary).TryMigrateFromV1(role));
            Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
            Assert.False(File.Exists(BundlePath(root.Path)));
            Assert.Empty(TemporaryFiles(root.Path));
        }

        foreach (var failure in Enum.GetValues<InjectedFailure>())
        {
            using var root = new TemporaryRoot();
            var boundary = new FaultBoundary();
            var protector = new TrackingProtector();
            var store = Store(root.Path, protector, boundary);
            var prior = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
            Assert.True(store.TrySave(prior));
            var before = File.ReadAllBytes(BundlePath(root.Path));
            Configure(failure, protector, boundary, migration: false);
            var replacement = Bundle([RoleData(2)], RoleRecord(RoleId(2)).Binding);

            Assert.False(store.TrySave(replacement));
            Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));
            Reset(protector, boundary);
            Assert.Equal(prior.SelectedRole, store.TryLoad()!.SelectedRole);
            Assert.Empty(TemporaryFiles(root.Path));
        }
    }

    [Fact]
    public void Reparse_and_current_user_protection_boundaries_are_enforced()
    {
        using var reparseRoot = new TemporaryRoot();
        Directory.CreateDirectory(reparseRoot.Path);
        var boundary = new FaultBoundary { ReparsePath = reparseRoot.Path };
        var bundle = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
        Assert.False(Store(reparseRoot.Path, boundary: boundary).TrySave(bundle));
        Assert.Null(Store(reparseRoot.Path, boundary: boundary).TryLoad());

        using var protectedRoot = new TemporaryRoot();
        var protectedStore = new HoyoLabGameBundleStore(protectedRoot.Path);
        Assert.True(protectedStore.TrySave(bundle));
        var ciphertext = File.ReadAllBytes(BundlePath(protectedRoot.Path));
        Assert.Equal(-1, ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RoleId(1))));
        Assert.Equal(bundle.SelectedRole, protectedStore.TryLoad()!.SelectedRole);
    }

    [Fact]
    public void Captured_old_slot_delete_never_touches_the_new_slot_root()
    {
        using var firstRoot = new TemporaryRoot();
        using var secondRoot = new TemporaryRoot();
        var first = Store(firstRoot.Path);
        var second = Store(secondRoot.Path);
        var firstRole = RoleRecord(RoleId(1));
        var secondRole = RoleRecord(RoleId(2), "prod_official_usa");
        var firstProtector = new TrackingProtector();
        var secondProtector = new TrackingProtector();
        var firstRoles = new PublisherRoleBindingStore(firstRoot.Path, firstProtector);
        var firstResources = new PublisherResourceSnapshotStore(firstRoot.Path, firstProtector);
        var secondRoles = new PublisherRoleBindingStore(secondRoot.Path, secondProtector);
        var secondResources = new PublisherResourceSnapshotStore(secondRoot.Path, secondProtector);

        Assert.True(first.TryMigrateFromV1(firstRole));
        Assert.True(second.TryMigrateFromV1(secondRole));
        Assert.True(firstRoles.SaveRecord(HoyoLabGameBundleRules.GameId, firstRole));
        Assert.True(firstResources.Save(Resource(FirstObservation), firstRole.Binding));
        Assert.True(secondRoles.SaveRecord(HoyoLabGameBundleRules.GameId, secondRole));
        Assert.True(secondResources.Save(Resource(FirstObservation), secondRole.Binding));
        Assert.True(first.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.True(first.TryRecordResource(firstRole.Binding, Resource(FirstObservation)));

        Assert.Equal(firstRole.Binding, first.TryLoad()!.SelectedRole);
        Assert.Equal(secondRole.Binding, second.TryLoad()!.SelectedRole);
        Assert.Null(second.TryLoad()!.Roles.Single().Resource);

        Assert.True(PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(
            () => firstResources.DeleteProvider("HoYoLAB"),
            () => firstRoles.DeleteProvider("HoYoLAB")));
        Assert.True(first.TryDelete());
        Assert.Null(first.TryLoad());
        Assert.Null(firstRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
        Assert.Null(firstResources.TryLoad(HoyoLabGameBundleRules.GameId, firstRole.Binding));
        Assert.Equal(secondRole.Binding, second.TryLoad()!.SelectedRole);
        Assert.Equal(secondRole, secondRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
        Assert.NotNull(secondResources.TryLoad(HoyoLabGameBundleRules.GameId, secondRole.Binding));
    }

    [Fact]
    public async Task Buffers_are_zeroed_and_concurrent_store_instances_serialize_writers()
    {
        using var zeroRoot = new TemporaryRoot();
        var tracking = new TrackingProtector();
        var bundle = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
        var store = Store(zeroRoot.Path, tracking);
        Assert.True(store.TrySave(bundle));
        Assert.NotEmpty(tracking.ExposedBuffers);
        Assert.All(tracking.ExposedBuffers, buffer =>
            Assert.All(buffer, value => Assert.Equal(0, value)));

        using var concurrentRoot = new TemporaryRoot();
        var serial = new SerialTrackingProtector();
        var first = Bundle([RoleData(1)], RoleRecord(RoleId(1)).Binding);
        Assert.True(Store(concurrentRoot.Path, serial).TrySave(first));
        serial.Reset();
        var tasks = Enumerable.Range(2, 12).Select(index => Task.Run(() =>
        {
            var candidate = Bundle([RoleData(index)], RoleRecord(RoleId(index)).Binding);
            return Store(concurrentRoot.Path, serial).TrySave(candidate);
        }));

        Assert.All(await Task.WhenAll(tasks), Assert.True);
        Assert.Equal(1, serial.MaximumConcurrentOperations);
        Assert.NotNull(Store(concurrentRoot.Path, serial).TryLoad());
    }

    [Fact]
    public async Task Concurrent_read_modify_write_keeps_every_explicit_role_selection()
    {
        using var root = new TemporaryRoot();
        var first = RoleData(1);
        Assert.True(Store(root.Path).TrySave(Bundle([first], first.Role.Binding)));

        var results = await Task.WhenAll(Enumerable.Range(2, 7).Select(index =>
            Task.Run(() => Store(root.Path).TrySelectRole(RoleRecord(RoleId(index))))));

        Assert.All(results, Assert.True);
        var loaded = Store(root.Path).TryLoad()!;
        Assert.Equal(HoyoLabGameBundleRules.MaximumRoles, loaded.Roles.Count);
        Assert.Contains(loaded.Roles, role => role.Role.Binding == loaded.SelectedRole);
        Assert.Equal(
            Enumerable.Range(1, 8).Select(RoleId).Order().ToArray(),
            loaded.Roles.Select(role => role.Role.Binding.RoleId).Order().ToArray());
        Assert.False(Store(root.Path).TrySelectRole(RoleRecord(RoleId(9))));
        Assert.Equal(HoyoLabGameBundleRules.MaximumRoles, Store(root.Path).TryLoad()!.Roles.Count);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("resource")]
    [InlineData("delete")]
    public async Task Cancellation_while_named_mutex_is_contended_never_reads_or_writes_canonical_state(
        string mutationKind)
    {
        using var root = new TemporaryRoot();
        var store = Store(root.Path);
        var role = RoleData(1);
        Assert.True(store.TrySave(Bundle(
            [role],
            role.Role.Binding,
            Consents(resources: true))));
        var before = File.ReadAllBytes(BundlePath(root.Path));
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
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = Task.Run(() =>
        {
            started.SetResult(true);
            return mutationKind switch
            {
                "consent" => store.TrySetCapabilityConsent(
                    HoyoLabGameBundleRules.Resources,
                    false,
                    cancellation.Token),
                "resource" => store.TryRecordResource(
                    role.Role.Binding,
                    Resource(SecondObservation),
                    cancellation.Token),
                "delete" => store.TryDeleteRole(role.Role.Binding, cancellation.Token),
                _ => throw new InvalidOperationException(),
            };
        });
        try
        {
            await started.Task;
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

        Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));
        Assert.Empty(TemporaryFiles(root.Path));
    }

    [Fact]
    public void Cancellation_after_temporary_write_but_before_promotion_preserves_canonical_bytes()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = Store(root.Path, boundary: boundary);
        var role = RoleData(1);
        Assert.True(store.TrySave(Bundle([role], role.Role.Binding)));
        var before = File.ReadAllBytes(BundlePath(root.Path));
        using var cancellation = new CancellationTokenSource();
        boundary.TemporaryReadObserved = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => store.TrySetCapabilityConsent(
            HoyoLabGameBundleRules.Resources,
            true,
            cancellation.Token));

        Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));
        Assert.Empty(TemporaryFiles(root.Path));
    }

    [Fact]
    public void Observation_mutations_reject_stale_and_equal_conflicts_but_accept_exact_idempotence()
    {
        using var root = new TemporaryRoot();
        var role = RoleData(1);
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle([role], role.Role.Binding)));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, true));

        Assert.True(store.TryRecordResource(role.Role.Binding, Resource(FirstObservation)));
        Assert.True(store.TryRecordResource(role.Role.Binding, Resource(FirstObservation)));
        Assert.False(store.TryRecordResource(
            role.Role.Binding,
            Resource(FirstObservation) with { Current = 99 }));
        Assert.False(store.TryRecordResource(role.Role.Binding, Resource(FirstObservation.AddSeconds(-1))));

        Assert.True(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [20, 1, 7, 7],
            FirstObservation));
        Assert.True(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7, 20],
            FirstObservation));
        Assert.False(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 8, 20],
            FirstObservation));
        Assert.False(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1],
            FirstObservation.AddSeconds(-1)));
        Assert.False(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1],
            new DateTimeOffset(FirstObservation.DateTime, TimeSpan.FromHours(1))));
        Assert.False(store.TryRecordResource(
            role.Role.Binding,
            Resource(FirstObservation.AddTicks(1))));

        var loaded = store.TryLoad()!;
        Assert.Equal(Resource(FirstObservation) with { IsStale = true }, loaded.Roles[0].Resource);
        Assert.Equal([1, 7, 20], loaded.Roles[0].CompletedHsrAchievementIds);
    }

    [Fact]
    public void Consent_disable_uses_strict_timestamps_and_fresh_data_removes_only_its_older_tombstone()
    {
        using var root = new TemporaryRoot();
        var future = Now.AddMinutes(4);
        var newer = Now.AddMinutes(5);
        var roles = new[] { RoleData(1), RoleData(2), RoleData(3) };
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle(roles, roles[0].Role.Binding)));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, true));
        Assert.True(store.TryRecordResource(roles[0].Role.Binding, Resource(future)));
        Assert.True(store.TryRecordResource(roles[1].Role.Binding, Resource(FirstObservation)));
        Assert.True(store.TryRecordResource(roles[2].Role.Binding, Resource(Now)));
        Assert.True(store.TryRecordCompletedAchievements(
            roles[0].Role.Binding,
            [1, 7],
            future));
        Assert.True(store.TryRecordCompletedAchievements(
            roles[1].Role.Binding,
            [1, 7],
            FirstObservation));
        Assert.True(store.TryRecordCompletedAchievements(
            roles[2].Role.Binding,
            [1, 7],
            Now));

        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, false));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, false));
        var disabled = store.TryLoad()!;
        Assert.False(disabled.Consents.Resources);
        Assert.False(disabled.Consents.Achievements);
        Assert.All(disabled.Roles, item =>
        {
            Assert.Null(item.Resource);
            Assert.Null(item.Observations.Resources);
            Assert.Null(item.CompletedHsrAchievementIds);
            Assert.Null(item.Observations.Achievements);
        });
        Assert.Equal(3, disabled.CapabilityTombstones.Count(item =>
            item.Capability == HoyoLabGameBundleRules.Resources));
        Assert.Equal(3, disabled.CapabilityTombstones.Count(item =>
            item.Capability == HoyoLabGameBundleRules.Achievements));
        Assert.Equal(future.AddSeconds(1), disabled.CapabilityTombstones.Single(item =>
            item.Binding == roles[0].Role.Binding
            && item.Capability == HoyoLabGameBundleRules.Resources).DeletedAt);
        Assert.Equal(future.AddSeconds(1), disabled.CapabilityTombstones.Single(item =>
            item.Binding == roles[0].Role.Binding
            && item.Capability == HoyoLabGameBundleRules.Achievements).DeletedAt);
        Assert.All(disabled.CapabilityTombstones.Where(item =>
                item.Binding == roles[1].Role.Binding),
            item => Assert.Equal(Now, item.DeletedAt));
        Assert.All(disabled.CapabilityTombstones.Where(item =>
                item.Binding == roles[2].Role.Binding),
            item => Assert.Equal(Now.AddSeconds(1), item.DeletedAt));

        var disabledBytes = File.ReadAllBytes(BundlePath(root.Path));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, false));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, false));
        Assert.Equal(disabledBytes, File.ReadAllBytes(BundlePath(root.Path)));

        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, true));
        Assert.False(store.TryRecordResource(roles[0].Role.Binding, Resource(FirstObservation)));
        Assert.False(store.TryRecordResource(roles[0].Role.Binding, Resource(future)));
        Assert.False(store.TryRecordResource(
            roles[0].Role.Binding,
            Resource(future.AddSeconds(1))));
        Assert.False(store.TryRecordCompletedAchievements(
            roles[0].Role.Binding,
            [1, 7],
            FirstObservation));
        Assert.False(store.TryRecordCompletedAchievements(
            roles[0].Role.Binding,
            [1, 7],
            future));
        Assert.False(store.TryRecordCompletedAchievements(
            roles[0].Role.Binding,
            [1, 7],
            future.AddSeconds(1)));
        Assert.True(store.TryRecordResource(roles[0].Role.Binding, Resource(newer)));
        Assert.True(store.TryRecordCompletedAchievements(
            roles[0].Role.Binding,
            [1, 7],
            newer));
        var restored = store.TryLoad()!;
        Assert.DoesNotContain(restored.CapabilityTombstones, item =>
            item.Binding == roles[0].Role.Binding
            && item.Capability is HoyoLabGameBundleRules.Resources
                or HoyoLabGameBundleRules.Achievements);
        Assert.Contains(restored.CapabilityTombstones, item =>
            item.Binding == roles[1].Role.Binding
            && item.Capability == HoyoLabGameBundleRules.Resources);
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("achievements")]
    public void Consent_disable_fails_atomically_when_any_role_is_at_the_maximum_future_boundary(
        string capability)
    {
        using var root = new TemporaryRoot();
        var roles = new[] { RoleData(1), RoleData(2) };
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle(
            roles,
            roles[0].Role.Binding,
            Consents(resources: true, achievements: true))));
        if (capability == HoyoLabGameBundleRules.Resources)
        {
            Assert.True(store.TryRecordResource(
                roles[0].Role.Binding,
                Resource(FirstObservation)));
            Assert.True(store.TryRecordResource(
                roles[1].Role.Binding,
                Resource(Now.AddMinutes(5))));
        }
        else
        {
            Assert.True(store.TryRecordCompletedAchievements(
                roles[0].Role.Binding,
                [1],
                FirstObservation));
            Assert.True(store.TryRecordCompletedAchievements(
                roles[1].Role.Binding,
                [1],
                Now.AddMinutes(5)));
        }
        var before = File.ReadAllBytes(BundlePath(root.Path));

        Assert.False(store.TrySetCapabilityConsent(capability, false));

        Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));
        var unchanged = store.TryLoad()!;
        Assert.True(unchanged.Consents.IsEnabled(capability));
        Assert.All(unchanged.Roles, role =>
            Assert.NotNull(capability == HoyoLabGameBundleRules.Resources
                ? role.Observations.Resources
                : role.Observations.Achievements));
    }

    [Fact]
    public void Role_delete_preserves_selection_deterministically_and_final_delete_is_representable()
    {
        using var root = new TemporaryRoot();
        var first = RoleData(1, "prod_official_usa");
        var second = RoleData(2, "prod_official_eur");
        var third = RoleData(3, "prod_official_eur");
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle([first, third, second], first.Role.Binding)));

        Assert.True(store.TryDeleteRole(first.Role.Binding));
        var afterSelectedDelete = store.TryLoad()!;
        Assert.Equal(second.Role.Binding, afterSelectedDelete.SelectedRole);
        Assert.Contains(afterSelectedDelete.RoleTombstones, item => item.Binding == first.Role.Binding);
        Assert.Equal(2, afterSelectedDelete.CapabilityTombstones.Count(item =>
            item.Binding == first.Role.Binding));

        Assert.True(store.TryDeleteRole(third.Role.Binding));
        Assert.Equal(second.Role.Binding, store.TryLoad()!.SelectedRole);
        Assert.True(store.TryDeleteRole(second.Role.Binding));
        var empty = store.TryLoad()!;
        Assert.Empty(empty.Roles);
        Assert.Null(empty.SelectedRole);
        Assert.Contains(empty.RoleTombstones, item => item.Binding == second.Role.Binding);
        Assert.Equal(2, empty.CapabilityTombstones.Count(item =>
            item.Binding == second.Role.Binding));
        Assert.False(store.TryDeleteRole(second.Role.Binding));

        Assert.False(store.TrySelectRole(second.Role));
        var laterStore = Store(root.Path, clock: new FixedTimeProvider(Now.AddSeconds(1)));
        Assert.True(laterStore.TrySelectRole(second.Role));
        Assert.Equal(second.Role.Binding, laterStore.TryLoad()!.SelectedRole);
        Assert.DoesNotContain(laterStore.TryLoad()!.RoleTombstones, item =>
            item.Binding == second.Role.Binding);
    }

    [Fact]
    public void Role_delete_is_strictly_later_than_each_capability_and_the_newest_observation()
    {
        using var root = new TemporaryRoot();
        var resourceObservation = Now.AddMinutes(4);
        var achievementObservation = Now.AddMinutes(3);
        var newer = Now.AddMinutes(5);
        var role = RoleData(1);
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle(
            [role],
            role.Role.Binding,
            Consents(resources: true, achievements: true))));
        Assert.True(store.TryRecordResource(role.Role.Binding, Resource(resourceObservation)));
        Assert.True(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7],
            achievementObservation));

        Assert.True(store.TryDeleteRole(role.Role.Binding));
        var deleted = store.TryLoad()!;
        Assert.Equal(resourceObservation.AddSeconds(1),
            Assert.Single(deleted.RoleTombstones).DeletedAt);
        Assert.Equal(resourceObservation.AddSeconds(1), deleted.CapabilityTombstones.Single(item =>
            item.Binding == role.Role.Binding
            && item.Capability == HoyoLabGameBundleRules.Resources).DeletedAt);
        Assert.Equal(achievementObservation.AddSeconds(1), deleted.CapabilityTombstones.Single(item =>
            item.Binding == role.Role.Binding
            && item.Capability == HoyoLabGameBundleRules.Achievements).DeletedAt);
        Assert.False(store.TrySelectRole(role.Role));

        var laterStore = Store(root.Path, clock: new FixedTimeProvider(newer));
        Assert.True(laterStore.TrySelectRole(role.Role));
        Assert.False(laterStore.TryRecordResource(role.Role.Binding, Resource(FirstObservation)));
        Assert.False(laterStore.TryRecordResource(role.Role.Binding, Resource(resourceObservation)));
        Assert.False(laterStore.TryRecordResource(
            role.Role.Binding,
            Resource(resourceObservation.AddSeconds(1))));
        Assert.False(laterStore.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7],
            FirstObservation));
        Assert.False(laterStore.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7],
            achievementObservation));
        Assert.False(laterStore.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7],
            achievementObservation.AddSeconds(1)));
        Assert.True(laterStore.TryRecordResource(role.Role.Binding, Resource(newer)));
        Assert.True(laterStore.TryRecordCompletedAchievements(role.Role.Binding, [1, 7], newer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Role_delete_at_a_time_boundary_preserves_canonical_bytes(bool dateTimeRangeEnd)
    {
        using var root = new TemporaryRoot();
        var clock = dateTimeRangeEnd ? DateTimeOffset.MaxValue : Now;
        var maximumObservation = dateTimeRangeEnd
            ? new DateTimeOffset(
                DateTimeOffset.MaxValue.Ticks
                    - DateTimeOffset.MaxValue.Ticks % TimeSpan.TicksPerSecond,
                TimeSpan.Zero)
            : Now.AddMinutes(5);
        var role = RoleData(1);
        var store = Store(root.Path, clock: new FixedTimeProvider(clock));
        Assert.True(store.TrySave(Bundle(
            [role],
            role.Role.Binding,
            Consents(resources: true, achievements: true))));
        Assert.True(store.TryRecordResource(role.Role.Binding, Resource(FirstObservation)));
        Assert.True(store.TryRecordCompletedAchievements(
            role.Role.Binding,
            [1, 7],
            maximumObservation));
        var before = File.ReadAllBytes(BundlePath(root.Path));

        Assert.False(store.TryDeleteRole(role.Role.Binding));

        Assert.Equal(before, File.ReadAllBytes(BundlePath(root.Path)));
        Assert.Equal(role.Role.Binding, store.TryLoad()!.SelectedRole);
    }

    [Fact]
    public void Role_reselection_rebuilds_missing_capability_barriers_after_history_pruning()
    {
        using var root = new TemporaryRoot();
        var target = RoleRecord(RoleId(1));
        var history = Enumerable.Range(100, HoyoLabGameBundleRules.MaximumCapabilityTombstones / 2)
            .Select(index => new HoyoLabRoleTombstone(
                RoleRecord(RoleId(index)).Binding,
                FirstObservation.AddSeconds(index)))
            .ToArray();
        HoyoLabRoleTombstone[] roleTombstones =
        [
            new HoyoLabRoleTombstone(target.Binding, FirstObservation),
            .. history,
        ];
        var capabilityTombstones = history.SelectMany(item => new[]
            {
                new HoyoLabCapabilityTombstone(
                    item.Binding,
                    HoyoLabGameBundleRules.Achievements,
                    item.DeletedAt),
                new HoyoLabCapabilityTombstone(
                    item.Binding,
                    HoyoLabGameBundleRules.Resources,
                    item.DeletedAt),
            })
            .ToArray();
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle(
            Array.Empty<HoyoLabGameBundleRole>(),
            null,
            capabilityTombstones: capabilityTombstones,
            roleTombstones: roleTombstones)));

        Assert.True(store.TrySelectRole(target));
        var selected = store.TryLoad()!;
        Assert.Equal(HoyoLabGameBundleRules.MaximumCapabilityTombstones,
            selected.CapabilityTombstones.Count);
        Assert.Equal(2, selected.CapabilityTombstones.Count(item =>
            item.Binding == target.Binding && item.DeletedAt == FirstObservation));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Achievements, true));
        Assert.False(store.TryRecordResource(target.Binding, Resource(FirstObservation.AddSeconds(-1))));
        Assert.False(store.TryRecordResource(target.Binding, Resource(FirstObservation)));
        Assert.False(store.TryRecordCompletedAchievements(
            target.Binding,
            [1, 7],
            FirstObservation.AddSeconds(-1)));
        Assert.False(store.TryRecordCompletedAchievements(target.Binding, [1, 7], FirstObservation));
        Assert.True(store.TryRecordResource(target.Binding, Resource(SecondObservation)));
        Assert.True(store.TryRecordCompletedAchievements(target.Binding, [1, 7], SecondObservation));
    }

    [Fact]
    public void Tombstone_history_prunes_oldest_entries_and_keeps_the_new_delete()
    {
        using var root = new TemporaryRoot();
        var active = RoleData(1);
        var roleTombstones = Enumerable.Range(100, HoyoLabGameBundleRules.MaximumRoleTombstones)
            .Select(index => new HoyoLabRoleTombstone(
                RoleRecord(RoleId(index)).Binding,
                FirstObservation.AddSeconds(index)))
            .ToArray();
        var capabilityTombstones = roleTombstones.Select(item =>
            new HoyoLabCapabilityTombstone(
                item.Binding,
                HoyoLabGameBundleRules.Resources,
                item.DeletedAt))
            .ToArray();
        var store = Store(root.Path);
        Assert.True(store.TrySave(Bundle(
            [active],
            active.Role.Binding,
            capabilityTombstones: capabilityTombstones,
            roleTombstones: roleTombstones)));

        Assert.True(store.TryDeleteRole(active.Role.Binding));
        var loaded = store.TryLoad()!;
        Assert.Equal(HoyoLabGameBundleRules.MaximumRoleTombstones, loaded.RoleTombstones.Count);
        Assert.Equal(HoyoLabGameBundleRules.MaximumCapabilityTombstones, loaded.CapabilityTombstones.Count);
        Assert.Contains(loaded.RoleTombstones, item => item.Binding == active.Role.Binding);
        Assert.Equal(2, loaded.CapabilityTombstones.Count(item => item.Binding == active.Role.Binding));
        Assert.DoesNotContain(loaded.RoleTombstones, item => item.Binding == roleTombstones[0].Binding);
        Assert.DoesNotContain(loaded.CapabilityTombstones, item => item.Binding == roleTombstones[0].Binding);
    }

    [Fact]
    public void Delete_failure_and_unreadable_or_future_state_preserve_existing_bytes()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = Store(root.Path, boundary: boundary);
        var role = RoleData(1);
        Assert.True(store.TrySave(Bundle([role], role.Role.Binding)));
        var beforeDelete = File.ReadAllBytes(BundlePath(root.Path));

        boundary.FailDelete = true;
        Assert.False(store.TryDelete());
        Assert.Equal(beforeDelete, File.ReadAllBytes(BundlePath(root.Path)));
        boundary.ResetFailures();

        File.WriteAllText(BundlePath(root.Path), "{\"schemaVersion\":3}");
        var future = File.ReadAllBytes(BundlePath(root.Path));
        Assert.False(store.TrySelectRole(RoleRecord(RoleId(2))));
        Assert.False(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, true));
        Assert.False(store.TryRecordResource(role.Role.Binding, Resource(SecondObservation)));
        Assert.False(store.TryDeleteRole(role.Role.Binding));
        Assert.Equal(future, File.ReadAllBytes(BundlePath(root.Path)));

        File.WriteAllText(BundlePath(root.Path), "{\"schemaVersion\":2}");
        var invalid = File.ReadAllBytes(BundlePath(root.Path));
        Assert.False(store.TrySelectRole(RoleRecord(RoleId(2))));
        Assert.Equal(invalid, File.ReadAllBytes(BundlePath(root.Path)));

        Assert.True(store.TryDelete());
        Assert.False(File.Exists(BundlePath(root.Path)));
        Assert.True(store.TryDelete());
    }

    private static HoyoLabGameBundle Bundle(
        IReadOnlyList<HoyoLabGameBundleRole> roles,
        PublisherRoleBinding? selected,
        HoyoLabCapabilityConsentSet? consents = null,
        IReadOnlyList<HoyoLabCapabilityTombstone>? capabilityTombstones = null,
        IReadOnlyList<HoyoLabRoleTombstone>? roleTombstones = null) => new(
            HoyoLabGameBundleRules.SchemaVersion,
            HoyoLabGameBundleRules.GameId,
            roles,
            selected,
            consents ?? Consents(),
            capabilityTombstones ?? Array.Empty<HoyoLabCapabilityTombstone>(),
            roleTombstones ?? Array.Empty<HoyoLabRoleTombstone>());

    private static HoyoLabGameBundleRole RoleData(
        int index,
        string server = "prod_official_eur") => new(
            RoleRecord(RoleId(index), server, $"Test {index}"),
            Observations(),
            null,
            null);

    private static PublisherRoleRecord RoleRecord(
        string roleId,
        string server = "prod_official_eur",
        string? nickname = null) => new(
            new(roleId, server),
            nickname,
            PublisherRoleRecordRules.CanonicalRegionLabel(server));

    private static string RoleId(int index) => index.ToString("D20");

    private static PublisherResourceSnapshot Resource(DateTimeOffset observedAt) => new(
        HoyoLabGameBundleRules.GameId,
        "Trailblaze Power",
        100,
        300,
        observedAt,
        RecoverySeconds: 120,
        Reserve: 20);

    private static HoyoLabCapabilityObservations Observations(
        DateTimeOffset? resources = null,
        DateTimeOffset? inventory = null,
        DateTimeOffset? achievements = null) => new(
            resources,
            inventory,
            null,
            achievements,
            null,
            null,
            null,
            null);

    private static HoyoLabCapabilityConsentSet Consents(
        bool resources = false,
        bool achievements = false) => new(
            resources,
            Inventory: false,
            Builds: false,
            achievements,
            Exploration: false,
            Endgame: false,
            Events: false,
            Currency: false);

    private static HoyoLabGameBundleStore Store(
        string root,
        IPublisherRoleBindingProtector? protector = null,
        FaultBoundary? boundary = null,
        TimeProvider? clock = null) => new(
            root,
            protector ?? new TrackingProtector(),
            boundary ?? new FaultBoundary(),
            clock ?? new FixedTimeProvider(Now));

    private static bool Parse(byte[] bytes) =>
        HoyoLabGameBundleStore.TryParseBundle(bytes, Now, out _);

    private static byte[] Mutate(byte[] bytes, string oldValue, string newValue) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal));

    private static string BundlePath(string root) => Path.Combine(
        root,
        ".protected-hoyolab-game-bundles",
        "hsr-v2.bin");

    private static string LegacyRolePath(string root) =>
        Path.Combine(root, ".protected-role-bindings", "hsr.bin");

    private static string LegacyResourcePath(string root) =>
        Path.Combine(root, ".protected-resource-snapshots", "hsr.bin");

    private static IEnumerable<string> TemporaryFiles(string root)
    {
        var directory = Path.GetDirectoryName(BundlePath(root))!;
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "hsr-v2.bin.tmp.*")
            : Array.Empty<string>();
    }

    private static void Configure(
        InjectedFailure failure,
        TrackingProtector protector,
        FaultBoundary boundary,
        bool migration)
    {
        Reset(protector, boundary);
        switch (failure)
        {
            case InjectedFailure.Protect:
                protector.FailProtect = true;
                break;
            case InjectedFailure.Create:
                boundary.FailCreate = true;
                break;
            case InjectedFailure.VerificationRead:
                boundary.FailOpenReadAt = migration ? 1 : 2;
                break;
            case InjectedFailure.Unprotect:
                protector.FailUnprotectAt = migration ? 1 : 2;
                break;
            case InjectedFailure.Move:
                if (migration) boundary.FailMoveNew = true;
                else boundary.FailMoveOverwrite = true;
                break;
        }
    }

    private static void Reset(TrackingProtector protector, FaultBoundary boundary)
    {
        protector.ResetFailures();
        boundary.ResetFailures();
    }

    private enum InjectedFailure
    {
        Protect,
        Create,
        VerificationRead,
        Unprotect,
        Move,
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private class TrackingProtector : IPublisherRoleBindingProtector
    {
        private int unprotectCalls;
        public bool FailProtect { get; set; }
        public int? FailUnprotectAt { get; set; }
        public List<byte[]> ExposedBuffers { get; } = [];

        public virtual byte[] Protect(byte[] plaintext)
        {
            if (FailProtect) throw new CryptographicException("Injected protect failure.");
            var ciphertext = plaintext.ToArray();
            ExposedBuffers.Add(plaintext);
            ExposedBuffers.Add(ciphertext);
            return ciphertext;
        }

        public virtual byte[] Unprotect(byte[] ciphertext)
        {
            if (++unprotectCalls == FailUnprotectAt)
                throw new CryptographicException("Injected unprotect failure.");
            var plaintext = ciphertext.ToArray();
            ExposedBuffers.Add(ciphertext);
            ExposedBuffers.Add(plaintext);
            return plaintext;
        }

        public void ResetFailures()
        {
            FailProtect = false;
            FailUnprotectAt = null;
            unprotectCalls = 0;
        }
    }

    private sealed class SerialTrackingProtector : TrackingProtector
    {
        private int active;
        private int maximum;
        public int MaximumConcurrentOperations => Volatile.Read(ref maximum);

        public override byte[] Protect(byte[] plaintext) => Observe(() => base.Protect(plaintext));
        public override byte[] Unprotect(byte[] ciphertext) => Observe(() => base.Unprotect(ciphertext));

        public void Reset()
        {
            ResetFailures();
            Volatile.Write(ref active, 0);
            Volatile.Write(ref maximum, 0);
        }

        private byte[] Observe(Func<byte[]> action)
        {
            var current = Interlocked.Increment(ref active);
            var snapshot = Volatile.Read(ref maximum);
            while (current > snapshot)
            {
                var prior = Interlocked.CompareExchange(ref maximum, current, snapshot);
                if (prior == snapshot) break;
                snapshot = prior;
            }
            try
            {
                Thread.Sleep(5);
                return action();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class FaultBoundary : IPublisherRoleBindingFileBoundary
    {
        private readonly SystemPublisherRoleBindingFileBoundary inner = new();
        private int openReadCalls;
        public string? ReparsePath { get; set; }
        public bool FailCreate { get; set; }
        public int? FailOpenReadAt { get; set; }
        public bool FailMoveNew { get; set; }
        public bool FailMoveOverwrite { get; set; }
        public bool FailDelete { get; set; }
        public Action? TemporaryReadObserved { get; set; }
        public bool MoveNewObservedAfterTemporaryRead { get; private set; }

        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public bool EntryExists(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                || inner.EntryExists(path);
        public bool Exists(string path) => inner.Exists(path);
        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : inner.GetAttributes(path);
        public FileStream OpenRead(string path)
        {
            if (++openReadCalls == FailOpenReadAt) throw new IOException("Injected read failure.");
            var stream = inner.OpenRead(path);
            if (path.Contains(".tmp.", StringComparison.Ordinal)) TemporaryReadObserved?.Invoke();
            return stream;
        }
        public FileStream CreateNewWriteThrough(string path)
        {
            if (FailCreate) throw new IOException("Injected create failure.");
            return inner.CreateNewWriteThrough(path);
        }
        public void MoveNew(string source, string destination)
        {
            MoveNewObservedAfterTemporaryRead = openReadCalls > 0;
            if (FailMoveNew) throw new IOException("Injected move failure.");
            inner.MoveNew(source, destination);
        }
        public void MoveOverwrite(string source, string destination)
        {
            if (FailMoveOverwrite) throw new IOException("Injected move failure.");
            inner.MoveOverwrite(source, destination);
        }
        public void Delete(string path)
        {
            if (FailDelete) throw new IOException("Injected delete failure.");
            inner.Delete(path);
        }

        public void ResetFailures()
        {
            FailCreate = false;
            FailOpenReadAt = null;
            FailMoveNew = false;
            FailMoveOverwrite = false;
            FailDelete = false;
            TemporaryReadObserved = null;
            openReadCalls = 0;
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nyx-hoyolab-bundle-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
