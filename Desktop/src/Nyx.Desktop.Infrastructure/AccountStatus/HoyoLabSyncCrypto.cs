using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

internal static class HoyoLabSyncCrypto
{
    internal const string Format = "nyx-hoyolab-sync-v1";
    internal const string Kind = "hoyolab";
    internal const string Game = HoyoLabGameBundleRules.GameId;
    internal const int MaximumPlaintextBytes = HoyoLabGameBundleStore.MaximumPlaintextBytes;
    internal const int MaximumCiphertextBytes = MaximumPlaintextBytes + TagBytes;

    private const string DisplayPrefix = "NYX-HOYO-";
    private const string CanonicalPrefix = "NYXHOYO";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string SyncIdPrefix = "nyx-hoyolab-sync-id:v1:";
    private const string TokenPrefix = "nyx-hoyolab-sync-token:v1:";
    private const string KeySaltPrefix = "nyx-hoyolab-sync-key:v1:";
    private const string AadPrefix = Format + "|" + Kind + "|" + Game + "|";
    private const string KdfName = "PBKDF2";
    private const string KdfHash = "SHA-256";
    private const int KdfIterations = 150_000;
    private const int RecoveryBytes = 20;
    private const int RecoveryCharacters = 32;
    private const int CanonicalCharacters = 39;
    private const int SyncIdBytes = 24;
    private const int SyncIdCharacters = SyncIdBytes * 2;
    private const int TokenBytes = 32;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly int MaximumEnvelopeJsonBytes = MaximumBase64Length(MaximumCiphertextBytes) + 512;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal sealed record KdfParameters(string Name, string Hash, int Iterations)
    {
        public override string ToString() => nameof(KdfParameters);
    }

    internal sealed record Envelope(
        string Format,
        KdfParameters Kdf,
        string Iv,
        string Ciphertext)
    {
        public override string ToString() => nameof(Envelope);
    }

    internal sealed class DerivedSecrets : IDisposable
    {
        private byte[]? token;
        private byte[]? key;
        private int disposed;
        private readonly Action<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? clearedBufferObserver;

        internal DerivedSecrets(
            string syncId,
            byte[] token,
            byte[] key,
            Action<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? clearedBufferObserver)
        {
            if (!IsLowerHex(syncId, SyncIdCharacters)
                || token is null
                || token.Length != TokenBytes
                || key is null
                || key.Length != KeyBytes)
            {
                Clear(token);
                Clear(key);
                throw new ArgumentException("Derived sync secrets are invalid.");
            }
            SyncId = syncId;
            this.token = token;
            this.key = key;
            this.clearedBufferObserver = clearedBufferObserver;
        }

        internal string SyncId { get; }

        internal ReadOnlySpan<byte> Token =>
            (token ?? throw new ObjectDisposedException(nameof(DerivedSecrets))).AsSpan();

        internal ReadOnlySpan<byte> Key =>
            (key ?? throw new ObjectDisposedException(nameof(DerivedSecrets))).AsSpan();

        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var releasedToken = Interlocked.Exchange(ref token, null);
            var releasedKey = Interlocked.Exchange(ref key, null);
            if (releasedToken is not null) CryptographicOperations.ZeroMemory(releasedToken);
            if (releasedKey is not null) CryptographicOperations.ZeroMemory(releasedKey);
            clearedBufferObserver?.Invoke(
                releasedToken ?? ReadOnlyMemory<byte>.Empty,
                releasedKey ?? ReadOnlyMemory<byte>.Empty);
        }
    }

    internal static string GenerateRecoveryCode()
    {
        Span<byte> random = stackalloc byte[RecoveryBytes];
        Span<char> body = stackalloc char[RecoveryCharacters];
        Span<char> display = stackalloc char[DisplayPrefix.Length + RecoveryCharacters + 7];
        try
        {
            RandomNumberGenerator.Fill(random);
            WriteBase32(random, body);
            DisplayPrefix.AsSpan().CopyTo(display);
            var written = DisplayPrefix.Length;
            for (var group = 0; group < 8; group++)
            {
                if (group > 0) display[written++] = '-';
                body.Slice(group * 4, 4).CopyTo(display[written..]);
                written += 4;
            }
            return new string(display);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            body.Clear();
            display.Clear();
        }
    }

    internal static bool TryNormalizeRecoveryCode(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(value)) return false;
        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        Span<char> characters = stackalloc char[CanonicalCharacters];
        try
        {
            var written = 0;
            foreach (var character in normalized)
            {
                if (character is '-' or ' ' or '\t' or '\n' or '\v' or '\f' or '\r') continue;
                if (written == characters.Length) return false;
                characters[written++] = character;
            }
            if (written != CanonicalCharacters
                || !characters[..CanonicalPrefix.Length].SequenceEqual(CanonicalPrefix)
                || characters[CanonicalPrefix.Length..].ContainsAnyExcept(Base32Alphabet))
                return false;
            canonical = new string(characters);
            return true;
        }
        finally
        {
            characters.Clear();
        }
    }

    internal static bool TryDerive(
        string? recoveryCode,
        out DerivedSecrets? secrets,
        Action<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? clearedBufferObserver = null)
    {
        secrets = null;
        if (!TryNormalizeRecoveryCode(recoveryCode, out var canonical)) return false;
        byte[]? syncDigest = null;
        byte[]? token = null;
        byte[]? material = null;
        byte[]? salt = null;
        byte[]? key = null;
        try
        {
            syncDigest = HashPrefixed(SyncIdPrefix, canonical);
            var syncId = Convert.ToHexStringLower(syncDigest.AsSpan(0, SyncIdBytes));
            token = HashPrefixed(TokenPrefix, canonical);
            material = StrictUtf8.GetBytes(canonical);
            salt = StrictUtf8.GetBytes(KeySaltPrefix + syncId);
            key = new byte[KeyBytes];
            Rfc2898DeriveBytes.Pbkdf2(
                material,
                salt,
                key,
                KdfIterations,
                HashAlgorithmName.SHA256);
            secrets = new(syncId, token, key, clearedBufferObserver);
            token = null;
            key = null;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException
            or ArgumentException
            or EncoderFallbackException)
        {
            return false;
        }
        finally
        {
            Clear(syncDigest);
            Clear(token);
            Clear(material);
            Clear(salt);
            Clear(key);
        }
    }

    internal static bool TryEncryptBundle(
        DerivedSecrets? secrets,
        HoyoLabGameBundle? bundle,
        DateTimeOffset utcNow,
        out Envelope? envelope)
    {
        envelope = null;
        Span<byte> nonce = stackalloc byte[NonceBytes];
        try
        {
            RandomNumberGenerator.Fill(nonce);
            return TryEncryptBundle(secrets, bundle, utcNow, nonce, out envelope);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    internal static bool TryEncryptBundle(
        DerivedSecrets? secrets,
        HoyoLabGameBundle? bundle,
        DateTimeOffset utcNow,
        ReadOnlySpan<byte> nonce,
        out Envelope? envelope,
        Action<ReadOnlyMemory<byte>>? clearedPlaintextObserver = null)
    {
        envelope = null;
        if (secrets is null
            || secrets.IsDisposed
            || bundle is null
            || nonce.Length != NonceBytes
            || !HoyoLabGameBundleRules.IsValid(bundle, utcNow))
            return false;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        byte[]? aad = null;
        try
        {
            plaintext = HoyoLabGameBundleStore.SerializeBundle(HoyoLabGameBundleRules.Normalize(bundle));
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes) return false;
            ciphertext = new byte[plaintext.Length + TagBytes];
            aad = StrictUtf8.GetBytes(AadPrefix + secrets.SyncId);
            using (var aes = new AesGcm(secrets.Key, TagBytes))
            {
                aes.Encrypt(
                    nonce,
                    plaintext,
                    ciphertext.AsSpan(0, plaintext.Length),
                    ciphertext.AsSpan(plaintext.Length, TagBytes),
                    aad);
            }
            envelope = new(
                Format,
                new(KdfName, KdfHash, KdfIterations),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext));
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException
            or ArgumentException
            or EncoderFallbackException
            or InvalidOperationException
            or JsonException)
        {
            return false;
        }
        finally
        {
            Clear(plaintext, clearedPlaintextObserver);
            Clear(ciphertext);
            Clear(aad);
        }
    }

    internal static bool TryDecryptBundle(
        DerivedSecrets? secrets,
        Envelope? envelope,
        DateTimeOffset utcNow,
        out HoyoLabGameBundle? bundle,
        Action<ReadOnlyMemory<byte>>? clearedPlaintextObserver = null)
    {
        bundle = null;
        if (secrets is null || secrets.IsDisposed || !TryDecodeEnvelope(envelope, out var iv, out var ciphertext))
            return false;
        byte[]? plaintext = null;
        byte[]? aad = null;
        try
        {
            var plaintextLength = ciphertext.Length - TagBytes;
            plaintext = new byte[plaintextLength];
            aad = StrictUtf8.GetBytes(AadPrefix + secrets.SyncId);
            using (var aes = new AesGcm(secrets.Key, TagBytes))
            {
                aes.Decrypt(
                    iv,
                    ciphertext.AsSpan(0, plaintextLength),
                    ciphertext.AsSpan(plaintextLength, TagBytes),
                    plaintext,
                    aad);
            }
            return plaintext.Length is > 0 and <= MaximumPlaintextBytes
                && HoyoLabGameBundleStore.TryParseBundle(
                    plaintext,
                    utcNow.ToUniversalTime(),
                    out bundle);
        }
        catch (Exception exception) when (exception is CryptographicException
            or ArgumentException
            or EncoderFallbackException
            or InvalidOperationException
            or JsonException)
        {
            bundle = null;
            return false;
        }
        finally
        {
            Clear(iv);
            Clear(ciphertext);
            Clear(plaintext, clearedPlaintextObserver);
            Clear(aad);
        }
    }

    internal static bool TrySerializeEnvelope(Envelope? envelope, out byte[] json)
    {
        json = [];
        if (!TryDecodeEnvelope(envelope, out var iv, out var ciphertext)) return false;
        try
        {
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", envelope!.Format);
                writer.WriteStartObject("kdf");
                writer.WriteString("name", envelope.Kdf.Name);
                writer.WriteString("hash", envelope.Kdf.Hash);
                writer.WriteNumber("iterations", envelope.Kdf.Iterations);
                writer.WriteEndObject();
                writer.WriteString("iv", envelope.Iv);
                writer.WriteString("ciphertext", envelope.Ciphertext);
                writer.WriteEndObject();
            }
            json = output.WrittenSpan.ToArray();
            return true;
        }
        finally
        {
            Clear(iv);
            Clear(ciphertext);
        }
    }

    internal static bool TryParseEnvelope(ReadOnlyMemory<byte> json, out Envelope? envelope)
    {
        envelope = null;
        if (json.Length is <= 0 || json.Length > MaximumEnvelopeJsonBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
            var root = document.RootElement;
            if (!HasExactProperties(root, "format", "kdf", "iv", "ciphertext")
                || root.GetProperty("format").ValueKind != JsonValueKind.String
                || root.GetProperty("format").GetString() is not { } format
                || !HasExactProperties(root.GetProperty("kdf"), "name", "hash", "iterations"))
                return false;
            var kdf = root.GetProperty("kdf");
            if (kdf.GetProperty("name").ValueKind != JsonValueKind.String
                || kdf.GetProperty("name").GetString() is not { } name
                || kdf.GetProperty("hash").ValueKind != JsonValueKind.String
                || kdf.GetProperty("hash").GetString() is not { } hash
                || !kdf.GetProperty("iterations").TryGetInt32(out var iterations)
                || root.GetProperty("iv").ValueKind != JsonValueKind.String
                || root.GetProperty("iv").GetString() is not { } iv
                || root.GetProperty("ciphertext").ValueKind != JsonValueKind.String
                || root.GetProperty("ciphertext").GetString() is not { } ciphertext)
                return false;
            var candidate = new Envelope(format, new(name, hash, iterations), iv, ciphertext);
            if (!TryDecodeEnvelope(candidate, out var decodedIv, out var decodedCiphertext)) return false;
            Clear(decodedIv);
            Clear(decodedCiphertext);
            envelope = candidate;
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

    private static byte[] HashPrefixed(string prefix, string canonical)
    {
        var input = new byte[StrictUtf8.GetByteCount(prefix) + StrictUtf8.GetByteCount(canonical)];
        try
        {
            var written = StrictUtf8.GetBytes(prefix, input);
            StrictUtf8.GetBytes(canonical, input.AsSpan(written));
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static bool TryDecodeEnvelope(
        Envelope? envelope,
        out byte[] iv,
        out byte[] ciphertext)
    {
        iv = [];
        ciphertext = [];
        if (envelope is null
            || envelope.Format != Format
            || envelope.Kdf is null
            || envelope.Kdf.Name != KdfName
            || envelope.Kdf.Hash != KdfHash
            || envelope.Kdf.Iterations != KdfIterations
            || !TryDecodeBase64(envelope.Iv, NonceBytes, NonceBytes, out iv))
            return false;
        if (TryDecodeBase64(
                envelope.Ciphertext,
                TagBytes + 1,
                MaximumCiphertextBytes,
                out ciphertext))
            return true;
        Clear(iv);
        iv = [];
        return false;
    }

    private static bool TryDecodeBase64(
        string? value,
        int minimumBytes,
        int maximumBytes,
        out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(value)
            || value.Length % 4 != 0
            || value.Length > MaximumBase64Length(maximumBytes))
            return false;
        var padding = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '=')
            {
                padding++;
                if (padding > 2 || index < value.Length - 2) return false;
            }
            else if (padding != 0 || !IsBase64Character(character))
            {
                return false;
            }
        }
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }
        if (decoded.Length >= minimumBytes
            && decoded.Length <= maximumBytes
            && Convert.ToBase64String(decoded) == value)
            return true;
        Clear(decoded);
        decoded = [];
        return false;
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

    private static bool IsBase64Character(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '+'
            or '/';

    private static bool IsLowerHex(string? value, int length) => value is not null
        && value.Length == length
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static int MaximumBase64Length(int bytes) => ((bytes + 2) / 3) * 4;

    private static void WriteBase32(ReadOnlySpan<byte> bytes, Span<char> output)
    {
        for (var block = 0; block < RecoveryBytes / 5; block++)
        {
            var offset = block * 5;
            var value = ((ulong)bytes[offset] << 32)
                | ((ulong)bytes[offset + 1] << 24)
                | ((ulong)bytes[offset + 2] << 16)
                | ((ulong)bytes[offset + 3] << 8)
                | bytes[offset + 4];
            for (var character = 0; character < 8; character++)
            {
                var shift = 35 - character * 5;
                output[block * 8 + character] = Base32Alphabet[(int)(value >> shift) & 31];
            }
        }
    }

    private static void Clear(
        byte[]? bytes,
        Action<ReadOnlyMemory<byte>>? clearedBufferObserver = null)
    {
        if (bytes is null) return;
        CryptographicOperations.ZeroMemory(bytes);
        clearedBufferObserver?.Invoke(bytes);
    }
}
