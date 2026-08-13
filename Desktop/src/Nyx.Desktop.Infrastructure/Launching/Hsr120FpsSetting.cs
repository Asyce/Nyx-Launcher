using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Nyx.Desktop.Infrastructure.Launching;

public enum Hsr120FpsLaunchPreparationStatus
{
    Disabled,
    Applied,
    AlreadyEnabled,
    Failed,
}

public readonly record struct Hsr120FpsLaunchPreparationResult(
    Hsr120FpsLaunchPreparationStatus Status)
{
    public bool AllowsLaunch => Status is not Hsr120FpsLaunchPreparationStatus.Failed;
}

public sealed class Hsr120FpsSetting
{
    private const int MinimumExistingFps = 1;
    private const int MaximumExistingFps = 1000;
    private const int MaximumValueBytes = 16 * 1024;
    private readonly IHsrGraphicsRegistryValue value;

    [SupportedOSPlatform("windows")]
    public Hsr120FpsSetting() : this(new WindowsHsrGraphicsRegistryValue())
    {
    }

    internal Hsr120FpsSetting(IHsrGraphicsRegistryValue value)
    {
        this.value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Hsr120FpsLaunchPreparationResult Apply()
    {
        HsrGraphicsRegistrySnapshot original;
        try
        {
            original = value.Read();
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return Failed();
        }

        JsonDocument? document = null;
        if (!original.Exists
            || original.Kind is not HsrGraphicsRegistryValueKind.Binary
            || original.Bytes is null
            || !TryRead(original.Bytes, out document, out var fps))
        {
            document?.Dispose();
            return Failed();
        }

        using (var parsed = document!)
        {
            if (fps == 120)
            {
                return new(Hsr120FpsLaunchPreparationStatus.AlreadyEnabled);
            }

            var updated = Rewrite(parsed.RootElement);
            JsonDocument? updatedDocument = null;
            if (updated.Length > MaximumValueBytes
                || !TryRead(updated, out updatedDocument, out var updatedFps)
                || updatedFps != 120)
            {
                updatedDocument?.Dispose();
                return Failed();
            }
            updatedDocument!.Dispose();

            try
            {
                var current = value.Read();
                if (!current.Exists
                    || current.Kind is not HsrGraphicsRegistryValueKind.Binary
                    || current.Bytes is null
                    || !current.Bytes.AsSpan().SequenceEqual(original.Bytes))
                {
                    return Failed();
                }

                value.Write(updated);
                var verified = value.Read();
                if (verified.Exists
                    && verified.Kind is HsrGraphicsRegistryValueKind.Binary
                    && verified.Bytes is not null
                    && verified.Bytes.AsSpan().SequenceEqual(updated))
                {
                    return new(Hsr120FpsLaunchPreparationStatus.Applied);
                }
            }
            catch (Exception exception) when (IsBoundaryFailure(exception))
            {
                // The exact write may have partially completed. Restore below.
            }

            _ = TryRollbackOwned(original.Bytes, updated);
            return Failed();
        }
    }

    private static bool TryRead(byte[] bytes, out JsonDocument? document, out int fps)
    {
        document = null;
        fps = 0;
        if (bytes.Length is < 3 or > MaximumValueBytes
            || bytes[^1] != 0
            || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0)
            || bytes.AsSpan(0, Math.Min(3, bytes.Length - 1)).SequenceEqual("\uFEFF"u8))
        {
            return false;
        }

        try
        {
            var jsonBytes = bytes.AsSpan(0, bytes.Length - 1);
            var text = new UTF8Encoding(false, true).GetString(jsonBytes);
            if (text.Any(static character =>
                    char.IsControl(character) && character is not ('\t' or '\n' or '\r')))
            {
                return false;
            }

            document = JsonDocument.Parse(jsonBytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !HasUniquePropertyNames(document.RootElement))
            {
                document.Dispose();
                document = null;
                return false;
            }

            var fpsCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "FPS", StringComparison.Ordinal)) continue;
                fpsCount++;
                if (property.Value.ValueKind is not JsonValueKind.Number
                    || !property.Value.TryGetInt32(out fps)
                    || fps is < MinimumExistingFps or > MaximumExistingFps)
                {
                    document.Dispose();
                    document = null;
                    return false;
                }
            }

            if (fpsCount == 1) return true;
            document.Dispose();
            document = null;
            return false;
        }
        catch (Exception exception) when (exception is JsonException
                                              or DecoderFallbackException
                                              or ArgumentException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    private static bool HasUniquePropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Any(char.IsControl)
                        || !names.Add(property.Name)
                        || !HasUniquePropertyNames(property.Value)) return false;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!HasUniquePropertyNames(item)) return false;
                }
                break;
            case JsonValueKind.String:
                if (element.GetString()?.Any(char.IsControl) == true) return false;
                break;
        }

        return true;
    }

    private static byte[] Rewrite(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(property.Name, "FPS", StringComparison.Ordinal))
                {
                    writer.WriteNumberValue(120);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var json = stream.ToArray();
        Array.Resize(ref json, json.Length + 1);
        return json;
    }

    private bool TryRollbackOwned(byte[] original, byte[] intended)
    {
        try
        {
            var current = value.Read();
            if (!current.Exists
                || current.Kind is not HsrGraphicsRegistryValueKind.Binary
                || current.Bytes is null
                || !current.Bytes.AsSpan().SequenceEqual(intended))
            {
                return false;
            }

            value.Write(original);
            var restored = value.Read();
            return restored.Exists
                && restored.Kind is HsrGraphicsRegistryValueKind.Binary
                && restored.Bytes is not null
                && restored.Bytes.AsSpan().SequenceEqual(original);
        }
        catch (Exception exception) when (IsBoundaryFailure(exception))
        {
            return false;
        }
    }

    private static Hsr120FpsLaunchPreparationResult Failed() =>
        new(Hsr120FpsLaunchPreparationStatus.Failed);

    private static bool IsBoundaryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException
            or NotSupportedException;
}

internal interface IHsrGraphicsRegistryValue
{
    HsrGraphicsRegistrySnapshot Read();

    void Write(byte[] bytes);
}

internal sealed record HsrGraphicsRegistrySnapshot(
    bool Exists,
    HsrGraphicsRegistryValueKind Kind = HsrGraphicsRegistryValueKind.Other,
    byte[]? Bytes = null);

internal enum HsrGraphicsRegistryValueKind
{
    Other,
    Binary,
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsHsrGraphicsRegistryValue : IHsrGraphicsRegistryValue
{
    private const string ExactKey = @"Software\Cognosphere\Star Rail";
    private const string ExactValue = "GraphicsSettings_Model_h2986158309";

    public HsrGraphicsRegistrySnapshot Read()
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = currentUser.OpenSubKey(ExactKey, writable: false);
        if (key is null) return new(false);

        RegistryValueKind kind;
        try
        {
            kind = key.GetValueKind(ExactValue);
        }
        catch (IOException)
        {
            return new(false);
        }

        if (kind is not RegistryValueKind.Binary) return new(true);
        return key.GetValue(ExactValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] bytes
            ? new(true, HsrGraphicsRegistryValueKind.Binary, bytes)
            : new(true);
    }

    public void Write(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = currentUser.OpenSubKey(ExactKey, writable: true)
            ?? throw new IOException("The fixed Star Rail settings key is unavailable.");
        key.SetValue(ExactValue, bytes, RegistryValueKind.Binary);
        key.Flush();
    }
}
