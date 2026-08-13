using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.Hoyo;

public sealed class HoyoGameIdentityAdapter
{
    private const int MaximumConfigBytes = 16 * 1024;
    private const int MaximumConfigLines = 128;
    private const int MaximumConfigLineLength = 1024;
    private const int MaximumVersionInfoBytes = 128;
    private const string ExpectedPublisher = "COGNOSPHERE PTE. LTD.";

    private static readonly HashSet<string> AllowedConfigKeys = new(StringComparer.Ordinal)
    {
        "channel",
        "sub_channel",
        "cps",
        "game_version",
    };

    private static readonly IReadOnlyDictionary<string, GameProfile> Profiles =
        new Dictionary<string, GameProfile>(StringComparer.Ordinal)
        {
            ["hsr"] = new(
                "hsr",
                "StarRail.exe",
                "StarRail_Data",
                "1",
                "hoyoverse_PC",
                "Star Rail",
                RequiresVersionInfo: false),
            ["zzz"] = new(
                "zzz",
                "ZenlessZoneZero.exe",
                "ZenlessZoneZero_Data",
                "0",
                "mihoyo",
                "Zenless Zone Zero",
                RequiresVersionInfo: true),
        };

    private readonly IExecutableMetadataReader metadataReader;
    private readonly HoyoReadOnlyPathGuard pathGuard;

    [SupportedOSPlatform("windows")]
    public HoyoGameIdentityAdapter()
        : this(new WindowsAuthenticodeExecutableMetadataReader(), new SystemDriveTypeReader())
    {
    }

    internal HoyoGameIdentityAdapter(
        IExecutableMetadataReader metadataReader,
        IDriveTypeReader driveTypeReader)
    {
        this.metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        pathGuard = new(driveTypeReader ?? throw new ArgumentNullException(nameof(driveTypeReader)));
    }

    public HoyoGameInspectionResult Inspect(string gameId, string? gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        if (!Profiles.TryGetValue(gameId, out var profile))
        {
            throw new ArgumentOutOfRangeException(nameof(gameId), "Only sealed HSR and ZZZ profiles are supported.");
        }

        var rootCheck = pathGuard.CheckRoot(gameRoot);
        if (rootCheck.Status is not HoyoInspectionStatus.Ready)
        {
            return new(profile.GameId, rootCheck.Status, rootCheck.Reason, rootCheck.CanonicalRoot);
        }

        var root = rootCheck.CanonicalRoot!;
        try
        {
            var executablePath = Path.Combine(root, profile.ExecutableName);
            var dataDirectoryPath = Path.Combine(root, profile.DataDirectoryName);
            var manifestPath = Path.Combine(root, "pkg_version");
            var configPath = Path.Combine(root, "config.ini");

            var requiredResult = CheckRequiredEntries(
                profile,
                root,
                executablePath,
                dataDirectoryPath,
                manifestPath,
                configPath);
            if (requiredResult is not null)
            {
                return requiredResult;
            }

            var configFirst = ReadConfig(configPath);
            if (configFirst.Reason is not HoyoInspectionReason.None)
            {
                return Review(profile.GameId, configFirst.Reason, root);
            }

            var values = configFirst.Values!;
            if (values["channel"] != "1"
                || values["sub_channel"] != profile.SubChannel
                || values["cps"] != profile.Cps)
            {
                return Review(profile.GameId, HoyoInspectionReason.ConfigIdentityMismatch, root);
            }

            var gameVersion = values["game_version"];
            if (!IsDottedNumericVersion(gameVersion))
            {
                return Review(profile.GameId, HoyoInspectionReason.GameVersionInvalid, root);
            }

            BoundedTextResult? versionInfoFirst = null;
            var versionInfoPath = Path.Combine(root, "version_info");
            if (profile.RequiresVersionInfo)
            {
                if (HoyoReadOnlyPathGuard.HasReparsePoint(versionInfoPath))
                {
                    return Review(profile.GameId, HoyoInspectionReason.ReparsePointFound, root);
                }

                if (!File.Exists(versionInfoPath))
                {
                    return Review(profile.GameId, HoyoInspectionReason.VersionInfoMissing, root);
                }

                versionInfoFirst = ReadBoundedText(
                    versionInfoPath,
                    MaximumVersionInfoBytes,
                    detectEncodingFromByteOrderMarks: false);
                if (versionInfoFirst.TooLarge)
                {
                    return Review(profile.GameId, HoyoInspectionReason.VersionInfoTooLarge, root);
                }

                if (!TryReadZzzVersion(versionInfoFirst.Text!, out var versionInfoVersion))
                {
                    return Review(profile.GameId, HoyoInspectionReason.VersionInfoMalformed, root);
                }

                if (versionInfoVersion != gameVersion)
                {
                    return Review(profile.GameId, HoyoInspectionReason.VersionInfoMismatch, root);
                }
            }

            var executableSnapshot = FileSnapshot.Capture(executablePath);
            var metadataFirst = metadataReader.Read(executablePath);
            var metadataReason = ValidateGameMetadata(profile, metadataFirst);
            if (metadataReason is not HoyoInspectionReason.None)
            {
                return Review(profile.GameId, metadataReason, root);
            }

            var configSecond = ReadConfig(configPath);
            var versionInfoSecond = profile.RequiresVersionInfo
                ? ReadBoundedText(
                    versionInfoPath,
                    MaximumVersionInfoBytes,
                    detectEncodingFromByteOrderMarks: false)
                : null;
            var metadataSecond = metadataReader.Read(executablePath);
            var executableSnapshotSecond = FileSnapshot.Capture(executablePath);
            var rootSecond = pathGuard.CheckRoot(root);

            if (configSecond.Reason is not HoyoInspectionReason.None
                || !string.Equals(
                    configSecond.Fingerprint,
                    configFirst.Fingerprint,
                    StringComparison.Ordinal)
                || (profile.RequiresVersionInfo
                    && (versionInfoSecond!.TooLarge || versionInfoSecond.Text != versionInfoFirst!.Text))
                || metadataSecond != metadataFirst
                || executableSnapshotSecond != executableSnapshot
                || rootSecond.Status is not HoyoInspectionStatus.Ready
                || !string.Equals(rootSecond.CanonicalRoot, root, StringComparison.OrdinalIgnoreCase)
                || !RequiredEntriesRemainStable(
                    executablePath,
                    dataDirectoryPath,
                    manifestPath,
                    configPath,
                    profile.RequiresVersionInfo ? versionInfoPath : null))
            {
                return Review(profile.GameId, HoyoInspectionReason.TargetChangedDuringInspection, root);
            }

            return new(
                profile.GameId,
                HoyoInspectionStatus.Ready,
                HoyoInspectionReason.None,
                root,
                gameVersion);
        }
        catch (Exception exception) when (
            HoyoReadOnlyPathGuard.IsInspectionException(exception)
            || exception is DecoderFallbackException)
        {
            return Review(profile.GameId, HoyoInspectionReason.InspectionFailed, root);
        }
    }

    private static HoyoGameInspectionResult? CheckRequiredEntries(
        GameProfile profile,
        string root,
        string executablePath,
        string dataDirectoryPath,
        string manifestPath,
        string configPath)
    {
        if (HoyoReadOnlyPathGuard.HasReparsePoint(executablePath))
        {
            return Review(profile.GameId, HoyoInspectionReason.ReparsePointFound, root);
        }

        if (!File.Exists(executablePath))
        {
            return Review(profile.GameId, HoyoInspectionReason.LaunchTargetMissing, root);
        }

        if (HoyoReadOnlyPathGuard.HasReparsePoint(dataDirectoryPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.ReparsePointFound, root);
        }

        if (!Directory.Exists(dataDirectoryPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.DataDirectoryMissing, root);
        }

        if (HoyoReadOnlyPathGuard.HasReparsePoint(manifestPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.ReparsePointFound, root);
        }

        if (!File.Exists(manifestPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.PackageManifestMissing, root);
        }

        if (new[] { "pkg_version.json", "package_version" }
            .Select(name => Path.Combine(root, name))
            .Any(path => File.Exists(path) || Directory.Exists(path) || HoyoReadOnlyPathGuard.HasReparsePoint(path)))
        {
            return Review(profile.GameId, HoyoInspectionReason.PackageManifestConflict, root);
        }

        if (HoyoReadOnlyPathGuard.HasReparsePoint(configPath))
        {
            return Review(profile.GameId, HoyoInspectionReason.ReparsePointFound, root);
        }

        return !File.Exists(configPath)
            ? Review(profile.GameId, HoyoInspectionReason.ConfigMissing, root)
            : null;
    }

    private static bool RequiredEntriesRemainStable(
        string executablePath,
        string dataDirectoryPath,
        string manifestPath,
        string configPath,
        string? versionInfoPath)
    {
        var paths = new[] { executablePath, dataDirectoryPath, manifestPath, configPath }
            .Concat(versionInfoPath is null ? [] : [versionInfoPath]);

        return File.Exists(executablePath)
            && Directory.Exists(dataDirectoryPath)
            && File.Exists(manifestPath)
            && File.Exists(configPath)
            && (versionInfoPath is null || File.Exists(versionInfoPath))
            && !new[] { "pkg_version.json", "package_version" }
                .Select(name => Path.Combine(Path.GetDirectoryName(manifestPath)!, name))
                .Any(path => File.Exists(path) || Directory.Exists(path) || HoyoReadOnlyPathGuard.HasReparsePoint(path))
            && paths.All(path => !HoyoReadOnlyPathGuard.HasReparsePoint(path));
    }

    private static ConfigReadResult ReadConfig(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parser = new ConfigLineParser();
        var firstBytes = new List<byte>(3);
        var oneByte = new byte[1];
        var byteCount = 0;

        while (firstBytes.Count < 3 && stream.ReadByte() is var first && first >= 0)
        {
            oneByte[0] = (byte)first;
            fingerprint.AppendData(oneByte);
            firstBytes.Add(oneByte[0]);
            byteCount++;
        }

        var hasUtf8Preamble = firstBytes.Count == 3
            && firstBytes[0] == 0xEF
            && firstBytes[1] == 0xBB
            && firstBytes[2] == 0xBF;
        if (!hasUtf8Preamble)
        {
            foreach (var value in firstBytes)
            {
                parser.Accept(value);
            }
        }

        while (stream.ReadByte() is var next && next >= 0)
        {
            byteCount++;
            if (byteCount > MaximumConfigBytes)
            {
                return new(HoyoInspectionReason.ConfigTooLarge);
            }

            oneByte[0] = (byte)next;
            fingerprint.AppendData(oneByte);
            parser.Accept(oneByte[0]);
            if (parser.Reason is not HoyoInspectionReason.None)
            {
                return new(parser.Reason);
            }
        }

        parser.Complete();
        if (parser.Reason is not HoyoInspectionReason.None)
        {
            return new(parser.Reason);
        }

        if (!AllowedConfigKeys.All(parser.Values.ContainsKey))
        {
            return new(HoyoInspectionReason.ConfigMalformed);
        }

        return new(
            HoyoInspectionReason.None,
            Convert.ToHexString(fingerprint.GetHashAndReset()),
            parser.Values);
    }

    private static BoundedTextResult ReadBoundedText(
        string path,
        int maximumBytes,
        bool detectEncodingFromByteOrderMarks)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > maximumBytes)
        {
            return new(true, null);
        }

        using var memory = new MemoryStream(bytes, 0, count, writable: false);
        using var reader = new StreamReader(
            memory,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks);
        return new(false, reader.ReadToEnd());
    }

    private static HoyoInspectionReason ValidateGameMetadata(
        GameProfile profile,
        ExecutableMetadata metadata)
    {
        if (!metadata.HasValidAuthenticodeSignature)
        {
            return HoyoInspectionReason.SignatureInvalid;
        }

        if (!string.Equals(metadata.Publisher?.Trim(), ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            return HoyoInspectionReason.PublisherMismatch;
        }

        if (profile.GameId == "zzz" && string.IsNullOrWhiteSpace(metadata.ProductName))
        {
            return HoyoInspectionReason.None;
        }

        return string.Equals(metadata.ProductName, profile.ProductName, StringComparison.Ordinal)
            ? HoyoInspectionReason.None
            : HoyoInspectionReason.ProductIdentityMismatch;
    }

    private static bool TryReadZzzVersion(string text, out string version)
    {
        const string prefix = "OSPRODWin";
        version = string.Empty;
        if (!text.StartsWith(prefix, StringComparison.Ordinal)
            || text.Contains('\r')
            || text.Contains('\n'))
        {
            return false;
        }

        version = text[prefix.Length..];
        return IsDottedNumericVersion(version);
    }

    private static bool IsDottedNumericVersion(string value)
    {
        var segments = value.Split('.');
        return segments.Length is 3 or 4
            && segments.All(segment =>
                segment.Length > 0
                && segment.All(char.IsAsciiDigit)
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static HoyoGameInspectionResult Review(
        string gameId,
        HoyoInspectionReason reason,
        string? root = null) =>
        new(gameId, HoyoInspectionStatus.NeedsReview, reason, root);

    private sealed record GameProfile(
        string GameId,
        string ExecutableName,
        string DataDirectoryName,
        string SubChannel,
        string Cps,
        string ProductName,
        bool RequiresVersionInfo);

    private sealed record ConfigReadResult(
        HoyoInspectionReason Reason,
        string? Fingerprint = null,
        IReadOnlyDictionary<string, string>? Values = null);

    private sealed record BoundedTextResult(bool TooLarge, string? Text);

    private sealed class ConfigLineParser
    {
        private readonly List<byte> keyBytes = [];
        private readonly List<byte> valueBytes = [];
        private ConfigLineState state;
        private string? allowedKey;
        private int lineCount;
        private int lineLength;
        private bool lineObserved;

        public HoyoInspectionReason Reason { get; private set; }

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public void Accept(byte value)
        {
            if (Reason is not HoyoInspectionReason.None)
            {
                return;
            }

            if (value == (byte)'\n')
            {
                EndLine();
                return;
            }

            lineObserved = true;
            if (value == (byte)'\r')
            {
                return;
            }

            lineLength++;
            if (lineLength > MaximumConfigLineLength)
            {
                Reason = HoyoInspectionReason.ConfigTooLarge;
                return;
            }

            switch (state)
            {
                case ConfigLineState.ReadingKey:
                    AcceptKeyByte(value);
                    break;
                case ConfigLineState.ReadingAllowedValue:
                    valueBytes.Add(value);
                    break;
                case ConfigLineState.Ignore:
                    break;
                default:
                    throw new InvalidOperationException("Unknown parser state.");
            }
        }

        public void Complete()
        {
            if (lineObserved || lineLength > 0 || keyBytes.Count > 0 || valueBytes.Count > 0)
            {
                EndLine();
            }
        }

        private void AcceptKeyByte(byte value)
        {
            if (keyBytes.Count == 0 && IsAsciiWhitespace(value))
            {
                return;
            }

            if (keyBytes.Count == 0 && value is (byte)';' or (byte)'#' or (byte)'[')
            {
                state = ConfigLineState.Ignore;
                return;
            }

            if (value != (byte)'=')
            {
                keyBytes.Add(value);
                return;
            }

            allowedKey = MatchAllowedKey(TrimAsciiWhitespace(keyBytes));
            state = allowedKey is null
                ? ConfigLineState.Ignore
                : ConfigLineState.ReadingAllowedValue;
        }

        private void EndLine()
        {
            lineCount++;
            if (lineCount > MaximumConfigLines)
            {
                Reason = HoyoInspectionReason.ConfigTooLarge;
                return;
            }

            if (state is ConfigLineState.ReadingAllowedValue)
            {
                var boundedValue = TrimAsciiWhitespace(valueBytes);
                if (boundedValue.Length == 0
                    || boundedValue.Any(value => value > 0x7F)
                    || !Values.TryAdd(allowedKey!, Encoding.ASCII.GetString(boundedValue)))
                {
                    Reason = HoyoInspectionReason.ConfigMalformed;
                    return;
                }
            }

            keyBytes.Clear();
            valueBytes.Clear();
            state = ConfigLineState.ReadingKey;
            allowedKey = null;
            lineLength = 0;
            lineObserved = false;
        }

        private static string? MatchAllowedKey(ReadOnlySpan<byte> key) =>
            key.SequenceEqual("channel"u8) ? "channel"
            : key.SequenceEqual("sub_channel"u8) ? "sub_channel"
            : key.SequenceEqual("cps"u8) ? "cps"
            : key.SequenceEqual("game_version"u8) ? "game_version"
            : null;

        private static byte[] TrimAsciiWhitespace(List<byte> bytes)
        {
            var start = 0;
            while (start < bytes.Count && IsAsciiWhitespace(bytes[start]))
            {
                start++;
            }

            var end = bytes.Count - 1;
            while (end >= start && IsAsciiWhitespace(bytes[end]))
            {
                end--;
            }

            return start > end ? [] : bytes.GetRange(start, end - start + 1).ToArray();
        }

        private static bool IsAsciiWhitespace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\v' or (byte)'\f';
    }

    private enum ConfigLineState
    {
        ReadingKey,
        ReadingAllowedValue,
        Ignore,
    }

    private sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc)
    {
        public static FileSnapshot Capture(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            return new(info.Length, info.LastWriteTimeUtc);
        }
    }
}
