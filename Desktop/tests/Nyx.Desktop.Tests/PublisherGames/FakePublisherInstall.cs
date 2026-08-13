using System.Security.Cryptography;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

internal sealed class FakePublisherInstall : IDisposable
{
    internal const string KuroPublisher = "KURO TECHNOLOGY (HONG KONG) CO., LIMITED";
    internal const string GryphPublisher = "GRYPH FRONTIER PTE. LTD.";

    private FakePublisherInstall(string root, string gameId, FakeMetadataReader metadata)
    {
        Root = root;
        GameId = gameId;
        Metadata = metadata;
    }

    public string Root { get; }

    public string GameId { get; }

    public FakeMetadataReader Metadata { get; }

    public static FakePublisherInstall CreateWuWa(
        string configVersion = "3.5.0",
        string resourceVersion = "3.5.1")
    {
        var root = CreateRoot("wuwa");
        var metadata = new FakeMetadataReader();
        var launcherBytes = new byte[] { 1, 4, 7, 10 };
        var rootLauncher = WriteExecutable(root, "launcher.exe", launcherBytes);
        var versionLauncher = WriteExecutable(root, @"2.6.3.0\launcher.exe", launcherBytes);
        var gameRoot = Directory.CreateDirectory(Path.Combine(root, "Wuthering Waves Game")).FullName;
        var bootstrap = WriteExecutable(gameRoot, "Wuthering Waves.exe", [2, 5, 8]);
        byte[] runtimeBytes = [3, 6, 9];
        var runtime = WriteExecutable(
            gameRoot,
            @"Client\Binaries\Win64\Client-Win64-Shipping.exe",
            runtimeBytes);
        var runtimeMd5 = Convert.ToHexString(MD5.HashData(runtimeBytes)).ToLowerInvariant();
        WriteConfig(Path.Combine(gameRoot, "launcherDownloadConfig.json"), configVersion);
        WriteConfig(
            Path.Combine(gameRoot, @"launcherDownload\launcherDownloadConfig.json"),
            configVersion);
        File.WriteAllText(
            Path.Combine(gameRoot, "LocalGameResources.json"),
            $$"""
            {"resource":[
              {"dest":"unrelated/file.bin","fromFolder":null},
              {"dest":"{{WuWaPublicEvidenceParser.ExpectedRuntimeDestination}}","size":{{runtimeBytes.Length}},"md5":"{{runtimeMd5}}","fromFolder":"redacted/{{resourceVersion}}/redacted/"}
            ]}
            """);

        var launcherMetadata = new PublisherExecutableMetadata(
            true,
            KuroPublisher,
            "Wuthering Waves",
            null,
            "2.6.3.0",
            "launcher.exe",
            null);
        var blankGameMetadata = new PublisherExecutableMetadata(
            true,
            KuroPublisher,
            null,
            null,
            null,
            null,
            null);
        metadata.Set(rootLauncher, launcherMetadata);
        metadata.Set(versionLauncher, launcherMetadata);
        metadata.Set(bootstrap, blankGameMetadata);
        metadata.Set(runtime, blankGameMetadata);
        return new(root, "wuwa", metadata);
    }

    public static FakePublisherInstall CreateEndfield()
    {
        var root = CreateRoot("endfield");
        var metadata = new FakeMetadataReader();
        var launcherBytes = new byte[] { 1, 2, 3 };
        var rootLauncher = WriteExecutable(root, "Launcher.exe", launcherBytes);
        var versionLauncher = WriteExecutable(root, @"1.5.0\Launcher.exe", launcherBytes);
        var games = WriteExecutable(root, @"1.5.0\Games.exe", [7, 8, 9]);
        var gameRoot = Directory.CreateDirectory(Path.Combine(root, @"games\EndField Game")).FullName;
        var game = WriteExecutable(gameRoot, "Endfield.exe", [10, 11, 12]);
        var platform = WriteExecutable(gameRoot, "PlatformProcess.exe", [13, 14, 15]);

        var launcherMetadata = new PublisherExecutableMetadata(
            true,
            GryphPublisher,
            "GRYPHLINK",
            null,
            "1.5.0.1507",
            "Launcher.exe",
            null);
        metadata.Set(rootLauncher, launcherMetadata);
        metadata.Set(versionLauncher, launcherMetadata);
        metadata.Set(games, new PublisherExecutableMetadata(
            true,
            GryphPublisher,
            "GRYPHLINK",
            "GRYPHLINK",
            "1.5.0.1507",
            "Games.exe",
            "Gryph Frontier Pte. Ltd."));
        metadata.Set(game, new PublisherExecutableMetadata(
            true,
            GryphPublisher,
            null,
            null,
            "2021.3.34f5 (0)",
            null,
            null));
        metadata.Set(platform, new PublisherExecutableMetadata(
            true,
            GryphPublisher,
            "PlatformProcess",
            "PlatformProcess",
            "1.8.2.0",
            "PlatformProcess.exe",
            "PlatformProcess"));
        return new(root, "ae", metadata);
    }

    public WuWaIdentityAdapter CreateWuWaAdapter(
        DriveType driveType = DriveType.Fixed,
        string fileSystem = "NTFS") =>
        new(Metadata, new FakeDriveTypeReader(driveType), new FakeFileSystemReader(fileSystem));

    public WuWaIdentityAdapter CreateWuWaAdapterWithFileSystems(params string[] fileSystems) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader(fileSystems));

    public WuWaIdentityAdapter CreateWuWaAdapterWithIdentityReader(
        IPublisherFileIdentityReader identityReader) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader("NTFS"),
            identityReader);

    public WuWaIdentityAdapter CreateWuWaAdapterWithReparsePoints(params string[] paths) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader("NTFS"),
            identityReader: null,
            reparsePointReader: new FakeReparsePointReader(paths));

    public WuWaIdentityAdapter CreateWuWaAdapterWithEntryOpener(
        IPublisherExecutableEntryOpener entryOpener) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader("NTFS"),
            identityReader: null,
            reparsePointReader: null,
            entryOpener: entryOpener);

    public EndfieldIdentityAdapter CreateEndfieldAdapter(
        DriveType driveType = DriveType.Fixed,
        string fileSystem = "NTFS") =>
        new(Metadata, new FakeDriveTypeReader(driveType), new FakeFileSystemReader(fileSystem));

    public EndfieldIdentityAdapter CreateEndfieldAdapterWithFileSystems(params string[] fileSystems) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader(fileSystems));

    public EndfieldIdentityAdapter CreateEndfieldAdapterWithReparsePoints(params string[] paths) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader("NTFS"),
            identityReader: null,
            reparsePointReader: new FakeReparsePointReader(paths));

    public EndfieldIdentityAdapter CreateEndfieldAdapterWithEntryOpener(
        IPublisherExecutableEntryOpener entryOpener) =>
        new(
            Metadata,
            new FakeDriveTypeReader(DriveType.Fixed),
            new FakeFileSystemReader("NTFS"),
            identityReader: null,
            reparsePointReader: null,
            entryOpener: entryOpener);

    public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

    public void Delete(string relativePath)
    {
        var path = PathOf(relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    public string Snapshot() => string.Join(
        "|",
        Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? $"{Path.GetRelativePath(Root, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
                : $"{Path.GetRelativePath(Root, path)}:dir"));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string CreateRoot(string gameId) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nyx-{gameId}-{Guid.NewGuid():N}"))
            .FullName;

    private static string WriteExecutable(string root, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteConfig(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $$"""
            {"version":"{{version}}","isPreDownload":false,"appId":"50004","ignored":"redacted-fixture"}
            """);
    }

    internal sealed class FakeMetadataReader : IPublisherExecutableMetadataReader
    {
        private readonly Dictionary<string, IReadOnlyList<PublisherExecutableMetadata>> values =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> indexes = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ReadPaths { get; } = [];

        public Action<string, int>? OnRead { get; set; }

        public PublisherExecutableMetadata Read(
            string executablePath,
            PublisherNtfsFileIdentity expectedIdentity,
            IPublisherFileIdentityReader identityReader)
        {
            ReadPaths.Add(executablePath);
            indexes.TryGetValue(executablePath, out var index);
            OnRead?.Invoke(executablePath, index);
            indexes[executablePath] = index + 1;
            var sequence = values[executablePath];
            return sequence[Math.Min(index, sequence.Count - 1)];
        }

        public void Set(string path, params PublisherExecutableMetadata[] sequence)
        {
            values[path] = sequence;
            indexes.Remove(path);
        }

        public PublisherExecutableMetadata Get(string path) => values[path][0];

        public void Import(FakeMetadataReader other)
        {
            foreach (var pair in other.values)
            {
                values[pair.Key] = pair.Value;
            }
        }
    }

    internal sealed class FakeDriveTypeReader(DriveType driveType) : IDriveTypeReader
    {
        public DriveType GetDriveType(string driveRoot) => driveType;
    }

    internal sealed class FakeFileSystemReader(params string[] formats) : IVolumeFileSystemReader
    {
        private int index;

        public string GetFormat(string driveRoot)
        {
            if (formats.Length == 0)
            {
                throw new InvalidOperationException("At least one fake file system is required.");
            }

            var value = formats[Math.Min(index, formats.Length - 1)];
            index++;
            return value;
        }
    }

    internal sealed class SequenceFileIdentityReader(
        params PublisherNtfsFileIdentity[] identities) : IPublisherFileIdentityReader
    {
        private int index;

        public PublisherNtfsFileIdentity Read(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            if (identities.Length == 0)
            {
                throw new InvalidOperationException("At least one fake file identity is required.");
            }

            var identity = identities[Math.Min(index, identities.Length - 1)];
            index++;
            return identity;
        }
    }

    internal sealed class FakeReparsePointReader(params string[] reparsePoints)
        : IPublisherReparsePointReader
    {
        private readonly HashSet<string> reparsePoints = new(
            reparsePoints.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        public bool ContainsReparsePoint(string path) =>
            reparsePoints.Contains(Path.GetFullPath(path));

        public bool PathOrParentsHaveReparsePoint(string path)
        {
            var current = Path.GetFullPath(path);
            while (current is not null)
            {
                if (reparsePoints.Contains(current))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current);
            }

            return false;
        }
    }

    internal sealed class ReparseRejectingEntryOpener(string rejectedPath)
        : IPublisherExecutableEntryOpener
    {
        private readonly WindowsPublisherExecutableEntryOpener inner = new();
        private readonly string rejectedPath = Path.GetFullPath(rejectedPath);

        public bool RejectionReached { get; private set; }

        public List<string> OpenedPaths { get; } = [];

        public Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
        {
            OpenedPaths.Add(path);
            if (string.Equals(
                    Path.GetFullPath(path),
                    rejectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                RejectionReached = true;
                throw new PublisherReparsePointException();
            }

            return inner.Open(path);
        }
    }
}
