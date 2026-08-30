using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabSyncCryptoTests
{
    private const string FixtureSha256 = "ce6f3690401d1b54f6fb6cbac76ee67004cc03fd89404149ca1d261865699a0f";
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Vector Fixture = LoadVector();

    [Fact]
    public void Public_fixture_is_frozen_and_contains_only_fake_identifiers()
    {
        var bytes = File.ReadAllBytes(FixturePath);
        byte[]? digest = null;
        try
        {
            digest = SHA256.HashData(bytes);
            Assert.Equal(FixtureSha256, Convert.ToHexStringLower(digest));
            Assert.Equal("NYX-HOYO-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH", Fixture.DisplayCode);
            Assert.Equal("NYXHOYOAAAABBBBCCCCDDDDEEEEFFFFGGGGHHHH", Fixture.CanonicalCode);
            using var plaintext = JsonDocument.Parse(Fixture.Plaintext);
            var role = plaintext.RootElement.GetProperty("roles")[0];
            Assert.Equal("123456789", role.GetProperty("binding").GetProperty("roleId").GetString());
            Assert.Equal("Test Trailblazer", role.GetProperty("nickname").GetString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (digest is not null) CryptographicOperations.ZeroMemory(digest);
        }
    }

    [Fact]
    public void Recovery_code_normalization_and_derivation_match_the_public_vector()
    {
        Assert.True(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(
            "  ｎｙｘ－ｈｏｙｏ－ａａａａ－ｂｂｂｂ－ｃｃｃｃ－ｄｄｄｄ－ｅｅｅｅ－ｆｆｆｆ－ｇｇｇｇ－ｈｈｈｈ  ",
            out var normalized));
        Assert.Equal(Fixture.CanonicalCode, normalized);
        Assert.True(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(
            "\tNYX-HOYO-AAAA BBBB\nCCCC-DDDD-EEEE-FFFF-GGGG-HHHH\r",
            out normalized));
        Assert.Equal(Fixture.CanonicalCode, normalized);
        Assert.False(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(
            "NYX-HOYO-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHH",
            out _));
        Assert.False(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(
            "NYX-HOYO-1111-1111-1111-1111-1111-1111-1111-1111",
            out _));
        Assert.False(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(
            "NYX-HOYO-AAAA-BBBB-CCCC-DDDD\u200b-EEEE-FFFF-GGGG-HHHH",
            out _));

        Assert.True(HoyoLabSyncCrypto.TryDerive(Fixture.DisplayCode, out var derived));
        using var secrets = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(derived);
        Assert.Equal(Fixture.SyncId, secrets.SyncId);
        Assert.Equal(Fixture.Token, Convert.ToHexStringLower(secrets.Token));
        Assert.Equal(Fixture.KeyHex, Convert.ToHexStringLower(secrets.Key));
        Assert.Equal("nyx-hoyolab-sync-key:v1:" + Fixture.SyncId, Fixture.Salt);
        Assert.DoesNotContain(Fixture.DisplayCode, secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.CanonicalCode, secrets.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_iv_encryption_matches_WebCrypto_and_vector_decrypt_uses_the_canonical_bundle_parser()
    {
        Assert.True(HoyoLabSyncCrypto.TryDerive(Fixture.DisplayCode, out var derived));
        using var secrets = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(derived);
        var bundle = VectorBundle();
        var iv = Convert.FromBase64String(Fixture.Iv);
        ReadOnlyMemory<byte> clearedPlaintext = default;
        try
        {
            Assert.True(HoyoLabSyncCrypto.TryEncryptBundle(
                secrets,
                bundle,
                Now,
                iv,
                out var encrypted,
                bytes => clearedPlaintext = bytes));
            var envelope = Assert.IsType<HoyoLabSyncCrypto.Envelope>(encrypted);
            Assert.Equal(Fixture.Format, envelope.Format);
            Assert.Equal("PBKDF2", envelope.Kdf.Name);
            Assert.Equal("SHA-256", envelope.Kdf.Hash);
            Assert.Equal(150_000, envelope.Kdf.Iterations);
            Assert.Equal(Fixture.Iv, envelope.Iv);
            Assert.Equal(Fixture.Ciphertext, envelope.Ciphertext);
            var encryptedBytes = Convert.FromBase64String(envelope.Ciphertext);
            try
            {
                Assert.Equal(Fixture.TagHex, Convert.ToHexStringLower(encryptedBytes[^16..]));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedBytes);
            }
            Assert.All(clearedPlaintext.ToArray(), static value => Assert.Equal(0, value));

            Assert.True(HoyoLabSyncCrypto.TrySerializeEnvelope(envelope, out var json));
            try
            {
                var serialized = Encoding.UTF8.GetString(json);
                Assert.Equal(
                    $"{{\"format\":\"{Fixture.Format}\",\"kdf\":{{\"name\":\"PBKDF2\",\"hash\":\"SHA-256\",\"iterations\":150000}},\"iv\":\"{Fixture.Iv}\",\"ciphertext\":\"{Fixture.Ciphertext}\"}}",
                    serialized);
                Assert.DoesNotContain(Fixture.DisplayCode, serialized, StringComparison.Ordinal);
                Assert.DoesNotContain(Fixture.CanonicalCode, serialized, StringComparison.Ordinal);
                Assert.True(HoyoLabSyncCrypto.TryParseEnvelope(json, out var parsed));
                Assert.Equal(envelope, parsed);
                Assert.True(HoyoLabSyncCrypto.TryDecryptBundle(
                    secrets,
                    parsed,
                    Now,
                    out var decrypted));
                var plaintext = HoyoLabGameBundleStore.SerializeBundle(
                    Assert.IsType<HoyoLabGameBundle>(decrypted));
                try
                {
                    Assert.Equal(Fixture.Plaintext, Encoding.UTF8.GetString(plaintext));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    [Fact]
    public void Wrong_code_AAD_and_tampering_fail_closed_and_clear_plaintext_buffers()
    {
        var vectorEnvelope = VectorEnvelope();
        Assert.True(HoyoLabSyncCrypto.TryDerive(
            "NYX-HOYO-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ",
            out var wrongDerived));
        using (var wrong = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(wrongDerived))
        {
            ReadOnlyMemory<byte> cleared = default;
            Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(
                wrong,
                vectorEnvelope,
                Now,
                out _,
                bytes => cleared = bytes));
            var vectorCiphertext = Convert.FromBase64String(Fixture.Ciphertext);
            Assert.Equal(vectorCiphertext.Length - 16, cleared.Length);
            CryptographicOperations.ZeroMemory(vectorCiphertext);
            Assert.All(cleared.ToArray(), static value => Assert.Equal(0, value));
        }

        Assert.True(HoyoLabSyncCrypto.TryDerive(Fixture.DisplayCode, out var derived));
        using var secrets = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(derived);
        var wrongAadPlaintext = Encoding.UTF8.GetBytes(Fixture.Plaintext);
        try
        {
            var wrongAad = EncryptFixture(wrongAadPlaintext, "nyx-hoyolab-sync-v1|hoyolab|hsr|" + new string('0', 48));
            Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(secrets, wrongAad, Now, out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongAadPlaintext);
        }

        var tampered = Convert.FromBase64String(Fixture.Ciphertext);
        try
        {
            tampered[0] ^= 1;
            ReadOnlyMemory<byte> cleared = default;
            Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(
                secrets,
                vectorEnvelope with { Ciphertext = Convert.ToBase64String(tampered) },
                Now,
                out _,
                bytes => cleared = bytes));
            Assert.All(cleared.ToArray(), static value => Assert.Equal(0, value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tampered);
        }
    }

    [Fact]
    public void Envelope_shape_KDF_Base64_IV_and_ciphertext_bounds_are_strict()
    {
        var valid = VectorEnvelope();
        Assert.True(HoyoLabSyncCrypto.TrySerializeEnvelope(valid, out var validJson));
        try
        {
            Assert.True(HoyoLabSyncCrypto.TryParseEnvelope(validJson, out var parsed));
            Assert.Equal(valid, parsed);
            var text = Encoding.UTF8.GetString(validJson);
            Assert.False(ParseEnvelope(text.Replace(
                "{\"format\":",
                "{\"extra\":0,\"format\":",
                StringComparison.Ordinal)));
            Assert.False(ParseEnvelope(text.Replace(
                "{\"format\":",
                "{\"format\":\"nyx-hoyolab-sync-v1\",\"format\":",
                StringComparison.Ordinal)));
            Assert.False(ParseEnvelope(text.Replace(
                "{\"name\":",
                "{\"extra\":0,\"name\":",
                StringComparison.Ordinal)));
            Assert.False(ParseEnvelope(text.Replace(
                "{\"name\":",
                "{\"name\":\"PBKDF2\",\"name\":",
                StringComparison.Ordinal)));
            Assert.False(ParseEnvelope(text.Replace(
                ",\"ciphertext\":",
                ",\"missingCiphertext\":",
                StringComparison.Ordinal)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(validJson);
        }

        var invalid = new[]
        {
            valid with { Format = "nyx-hoyolab-sync-v2" },
            valid with { Kdf = valid.Kdf with { Name = "scrypt" } },
            valid with { Kdf = valid.Kdf with { Hash = "SHA-512" } },
            valid with { Kdf = valid.Kdf with { Iterations = 149_999 } },
            valid with { Iv = "AA==" },
            valid with { Iv = Fixture.Iv + "=" },
            valid with { Iv = Fixture.Iv.Insert(4, " ") },
            valid with { Ciphertext = Convert.ToBase64String(new byte[16]) },
        };
        Assert.All(invalid, envelope =>
            Assert.False(HoyoLabSyncCrypto.TrySerializeEnvelope(envelope, out _)));

        var oversized = new byte[HoyoLabSyncCrypto.MaximumCiphertextBytes + 1];
        try
        {
            Assert.False(HoyoLabSyncCrypto.TrySerializeEnvelope(
                valid with { Ciphertext = Convert.ToBase64String(oversized) },
                out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oversized);
        }
    }

    [Fact]
    public void Decrypt_rejects_noncanonical_bundles_at_the_existing_HSR_v2_parser_boundary()
    {
        Assert.True(HoyoLabSyncCrypto.TryDerive(Fixture.DisplayCode, out var derived));
        using var secrets = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(derived);
        var invalidPlaintexts = new[]
        {
            "{nope",
            Fixture.Plaintext.Replace(
                "{\"schemaVersion\":2",
                "{\"schemaVersion\":2,\"schemaVersion\":2",
                StringComparison.Ordinal),
            Fixture.Plaintext.Replace("\"schemaVersion\":2", "\"schemaVersion\":3", StringComparison.Ordinal),
        };
        foreach (var value in invalidPlaintexts)
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            try
            {
                var envelope = EncryptFixture(plaintext, Fixture.Aad);
                ReadOnlyMemory<byte> cleared = default;
                Assert.False(HoyoLabSyncCrypto.TryDecryptBundle(
                    secrets,
                    envelope,
                    Now,
                    out _,
                    bytes => cleared = bytes));
                Assert.Equal(plaintext.Length, cleared.Length);
                Assert.All(cleared.ToArray(), static item => Assert.Equal(0, item));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    [Fact]
    public void Generated_recovery_codes_have_exact_shape_round_trip_and_uniqueness()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 128; index++)
        {
            var display = HoyoLabSyncCrypto.GenerateRecoveryCode();
            Assert.Matches("^NYX-HOYO-(?:[A-Z2-7]{4}-){7}[A-Z2-7]{4}$", display);
            Assert.True(HoyoLabSyncCrypto.TryNormalizeRecoveryCode(display, out var canonical));
            Assert.Equal(39, canonical.Length);
            Assert.StartsWith("NYXHOYO", canonical, StringComparison.Ordinal);
            Assert.True(codes.Add(display));
        }
    }

    [Fact]
    public void Derived_token_and_key_buffers_are_zeroed_once_on_dispose_and_cannot_be_reused()
    {
        ReadOnlyMemory<byte> clearedToken = default;
        ReadOnlyMemory<byte> clearedKey = default;
        var clearCalls = 0;
        Assert.True(HoyoLabSyncCrypto.TryDerive(
            Fixture.DisplayCode,
            out var derived,
            (token, key) =>
            {
                clearCalls++;
                clearedToken = token;
                clearedKey = key;
            }));
        var secrets = Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(derived);
        Assert.Contains(secrets.Token.ToArray(), static value => value != 0);

        secrets.Dispose();
        secrets.Dispose();

        Assert.Equal(1, clearCalls);
        Assert.Equal(32, clearedToken.Length);
        Assert.Equal(32, clearedKey.Length);
        Assert.All(clearedToken.ToArray(), static value => Assert.Equal(0, value));
        Assert.All(clearedKey.ToArray(), static value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = secrets.Token.Length);
        Assert.False(HoyoLabSyncCrypto.TryEncryptBundle(
            secrets,
            VectorBundle(),
            Now,
            out _));

        Assert.False(HoyoLabSyncCrypto.TryDerive(
            "not-a-recovery-code",
            out var invalid,
            (_, _) => clearCalls++));
        Assert.Null(invalid);
        Assert.Equal(1, clearCalls);

        var invalidToken = Enumerable.Repeat((byte)0xa5, 31).ToArray();
        var rejectedKey = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        Assert.Throws<ArgumentException>(() => new HoyoLabSyncCrypto.DerivedSecrets(
            Fixture.SyncId,
            invalidToken,
            rejectedKey,
            null));
        Assert.All(invalidToken, static value => Assert.Equal(0, value));
        Assert.All(rejectedKey, static value => Assert.Equal(0, value));
    }

    private static bool ParseEnvelope(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            return HoyoLabSyncCrypto.TryParseEnvelope(bytes, out _);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static HoyoLabSyncCrypto.Envelope VectorEnvelope() => new(
        Fixture.Format,
        new("PBKDF2", "SHA-256", 150_000),
        Fixture.Iv,
        Fixture.Ciphertext);

    private static HoyoLabSyncCrypto.Envelope EncryptFixture(
        ReadOnlySpan<byte> plaintext,
        string aadValue)
    {
        var key = Convert.FromHexString(Fixture.KeyHex);
        var iv = Convert.FromBase64String(Fixture.Iv);
        var aad = Encoding.UTF8.GetBytes(aadValue);
        var ciphertext = new byte[plaintext.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(
                iv,
                plaintext,
                ciphertext.AsSpan(0, plaintext.Length),
                ciphertext.AsSpan(plaintext.Length),
                aad);
            return VectorEnvelope() with { Ciphertext = Convert.ToBase64String(ciphertext) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static HoyoLabGameBundle VectorBundle()
    {
        var plaintext = Encoding.UTF8.GetBytes(Fixture.Plaintext);
        try
        {
            Assert.True(HoyoLabGameBundleStore.TryParseBundle(plaintext, Now, out var bundle));
            return Assert.IsType<HoyoLabGameBundle>(bundle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static Vector LoadVector()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixturePath));
        var root = document.RootElement;
        return new(
            root.GetProperty("format").GetString()!,
            root.GetProperty("displayCode").GetString()!,
            root.GetProperty("canonicalCode").GetString()!,
            root.GetProperty("syncId").GetString()!,
            root.GetProperty("token").GetString()!,
            root.GetProperty("salt").GetString()!,
            root.GetProperty("keyHex").GetString()!,
            root.GetProperty("aad").GetString()!,
            root.GetProperty("iv").GetString()!,
            root.GetProperty("plaintext").GetString()!,
            root.GetProperty("tagHex").GetString()!,
            root.GetProperty("ciphertext").GetString()!);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "hoyo-sync-vector-v1.json");

    private sealed record Vector(
        string Format,
        string DisplayCode,
        string CanonicalCode,
        string SyncId,
        string Token,
        string Salt,
        string KeyHex,
        string Aad,
        string Iv,
        string Plaintext,
        string TagHex,
        string Ciphertext);
}
