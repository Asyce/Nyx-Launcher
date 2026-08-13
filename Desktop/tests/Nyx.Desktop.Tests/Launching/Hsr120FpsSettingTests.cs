using System.Reflection;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Tests.Launching;

public sealed class Hsr120FpsSettingTests
{
    [Fact]
    public void Exact_binary_contract_changes_only_fps_and_keeps_terminal_nul()
    {
        var original = Value("""{"FPS":60,"Quality":4,"Name":"Trailblazer","Nested":{"Enabled":true},"Items":[1,"two",null]}""");
        var registry = new FakeRegistry(original);

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Applied, result.Status);
        Assert.True(result.AllowsLaunch);
        Assert.Single(registry.Writes);
        Assert.Equal(0, registry.Bytes[^1]);
        Assert.DoesNotContain((byte)0, registry.Bytes[..^1]);
        using var json = JsonDocument.Parse(registry.Bytes[..^1]);
        Assert.Equal(120, json.RootElement.GetProperty("FPS").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("Quality").GetInt32());
        Assert.Equal("Trailblazer", json.RootElement.GetProperty("Name").GetString());
        Assert.True(json.RootElement.GetProperty("Nested").GetProperty("Enabled").GetBoolean());
        Assert.Equal("two", json.RootElement.GetProperty("Items")[1].GetString());
    }

    [Fact]
    public void Already_120_is_success_without_a_write()
    {
        var registry = new FakeRegistry(Value("""{"FPS":120,"Quality":4}"""));

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.AlreadyEnabled, result.Status);
        Assert.True(result.AllowsLaunch);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [MemberData(nameof(RejectedValues))]
    public void Missing_malformed_ambiguous_or_unbounded_values_fail_without_writing(byte[] value)
    {
        var registry = new FakeRegistry(value);

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.False(result.AllowsLaunch);
        Assert.Empty(registry.Writes);
    }

    public static TheoryData<byte[]> RejectedValues => new()
    {
        Value("""{"Quality":4}"""),
        Value("""{"FPS":60,"FPS":120}"""),
        Value("""{"FPS":60,"Nested":{"same":1,"same":2}}"""),
        Value("""{"FPS":"60"}"""),
        Value("""{"FPS":60.0}"""),
        Value("""{"FPS":0}"""),
        Value("""{"FPS":1001}"""),
        Value($"{{\"FPS\":{int.MinValue}}}"),
        Value($"{{\"FPS\":{int.MaxValue}}}"),
        Value("""{"FPS":60,"Name":"\u0001"}"""),
        Value("""[60]"""),
        Encoding.UTF8.GetBytes("""{"FPS":60}"""),
        Encoding.UTF8.GetBytes("""{"FPS":60}""").Concat(new byte[] { 0, (byte)'x', 0 }).ToArray(),
        new byte[] { 0xff, 0xfe, 0 },
        Enumerable.Repeat((byte)' ', 16 * 1024).Append((byte)0).ToArray(),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Positive_existing_fps_boundaries_are_accepted_and_changed_to_120(int fps)
    {
        var registry = new FakeRegistry(Value($"{{\"FPS\":{fps},\"Quality\":4}}"));

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Applied, result.Status);
        Assert.Single(registry.Writes);
        using var json = JsonDocument.Parse(registry.Bytes[..^1]);
        Assert.Equal(120, json.RootElement.GetProperty("FPS").GetInt32());
    }

    [Fact]
    public void Missing_and_wrong_registry_kinds_fail_closed()
    {
        var missing = new FakeRegistry(null);
        var wrongKind = new FakeRegistry(Value("""{"FPS":60}"""))
        {
            Kind = HsrGraphicsRegistryValueKind.Other,
        };

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, new Hsr120FpsSetting(missing).Apply().Status);
        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, new Hsr120FpsSetting(wrongKind).Apply().Status);
        Assert.Empty(missing.Writes);
        Assert.Empty(wrongKind.Writes);
    }

    [Fact]
    public void Third_party_readback_is_never_overwritten_by_rollback()
    {
        var original = Value("""{ "FPS" : 60, "token-like-field" : "kept-private" }""");
        var thirdParty = Value("""{"FPS":90,"Quality":5}""");
        var registry = new FakeRegistry(original) { ReplaceOnThirdRead = thirdParty };

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.Single(registry.Writes);
        Assert.Equal(thirdParty, registry.Bytes);
        Assert.DoesNotContain("kept-private", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_read_failure_rolls_back_only_after_proving_nyx_bytes_and_verifies_restore()
    {
        var original = Value("""{"FPS":60,"Quality":4}""");
        var registry = new FakeRegistry(original) { ThrowOnReadNumber = 3 };

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.Equal(2, registry.Writes.Count);
        Assert.Equal(5, registry.ReadCount);
        Assert.Equal(original, registry.Bytes);
    }

    [Fact]
    public void Rollback_verification_failure_remains_a_fixed_failure()
    {
        var original = Value("""{"FPS":60,"Quality":4}""");
        var registry = new FakeRegistry(original)
        {
            ThrowOnReadNumber = 3,
            ThrowOnAdditionalReadNumber = 5,
        };

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.False(result.AllowsLaunch);
        Assert.Equal(2, registry.Writes.Count);
        Assert.Equal(original, registry.Bytes);
    }

    [Fact]
    public void A_concurrent_settings_change_before_write_is_preserved_and_stops_the_launch_gate()
    {
        var original = Value("""{"FPS":60,"Quality":4}""");
        var concurrent = Value("""{"FPS":60,"Quality":5}""");
        var registry = new FakeRegistry(original) { ReplaceOnSecondRead = concurrent };

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.False(result.AllowsLaunch);
        Assert.Empty(registry.Writes);
        Assert.Equal(concurrent, registry.Bytes);
    }

    [Fact]
    public void Write_failure_after_mutation_attempts_rollback()
    {
        var original = Value("""{"FPS":60,"Quality":4}""");
        var registry = new FakeRegistry(original) { ThrowAfterFirstWrite = true };

        var result = new Hsr120FpsSetting(registry).Apply();

        Assert.Equal(Hsr120FpsLaunchPreparationStatus.Failed, result.Status);
        Assert.Equal(2, registry.Writes.Count);
        Assert.Equal(original, registry.Bytes);
    }

    [Fact]
    public void Production_boundary_seals_the_one_current_user_key_and_value()
    {
        var type = typeof(Hsr120FpsSetting).Assembly.GetType(
            "Nyx.Desktop.Infrastructure.Launching.WindowsHsrGraphicsRegistryValue",
            throwOnError: true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        Assert.Equal(@"Software\Cognosphere\Star Rail", type.GetField("ExactKey", flags)!.GetRawConstantValue());
        Assert.Equal("GraphicsSettings_Model_h2986158309", type.GetField("ExactValue", flags)!.GetRawConstantValue());
        Assert.DoesNotContain(type.GetMethods(), method => method.Name.Contains("Enumerate", StringComparison.Ordinal));
    }

    private static byte[] Value(string json) => [.. Encoding.UTF8.GetBytes(json), 0];

    private sealed class FakeRegistry(byte[]? bytes) : IHsrGraphicsRegistryValue
    {
        private int writeCount;
        private int readCount;

        public HsrGraphicsRegistryValueKind Kind { get; init; } = HsrGraphicsRegistryValueKind.Binary;
        public bool ThrowAfterFirstWrite { get; init; }
        public byte[]? ReplaceOnSecondRead { get; init; }
        public byte[]? ReplaceOnThirdRead { get; init; }
        public int? ThrowOnReadNumber { get; init; }
        public int? ThrowOnAdditionalReadNumber { get; init; }
        public byte[] Bytes { get; private set; } = bytes?.ToArray() ?? [];
        public List<byte[]> Writes { get; } = [];
        public int ReadCount => readCount;

        public HsrGraphicsRegistrySnapshot Read()
        {
            readCount++;
            if (readCount == ThrowOnReadNumber || readCount == ThrowOnAdditionalReadNumber)
                throw new IOException("simulated fixed-boundary read failure");
            if (readCount == 2 && ReplaceOnSecondRead is not null) Bytes = ReplaceOnSecondRead.ToArray();
            if (readCount == 3 && ReplaceOnThirdRead is not null) Bytes = ReplaceOnThirdRead.ToArray();
            return bytes is null && Writes.Count == 0
                ? new(false)
                : new(true, Kind, Bytes.ToArray());
        }

        public void Write(byte[] value)
        {
            Writes.Add(value.ToArray());
            writeCount++;
            Bytes = value.ToArray();
            if (ThrowAfterFirstWrite && writeCount == 1)
            {
                throw new IOException("simulated fixed-boundary failure");
            }
        }
    }
}
