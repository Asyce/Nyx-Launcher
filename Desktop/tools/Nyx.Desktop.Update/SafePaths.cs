using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nyx.Desktop.Update;

public static class SafePaths
{
    private const int MaximumPathCharacters = 1024;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionPosixSemantics = 0x00000002;
    private const uint FileDispositionIgnoreReadonlyAttribute = 0x00000010;
    private const int FileDispositionInfoEx = 21;

    public static string RequireExistingFile(string path)
    {
        var fullPath = RequireAbsoluteLocal(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdateContractException("UnsafePath");
        }

        RequireNoReparseComponents(fullPath);
        return fullPath;
    }

    public static string RequireExistingDirectory(string path)
    {
        var fullPath = RequireAbsoluteLocal(path).TrimEnd(Path.DirectorySeparatorChar);
        var info = new DirectoryInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdateContractException("UnsafePath");
        }

        RequireNoReparseComponents(fullPath);
        return fullPath;
    }

    public static string CreateDirectoryTree(string path)
    {
        var fullPath = RequireAbsoluteLocal(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new UpdateContractException("UnsafePath");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UpdateContractException("UnsafePath");
            }
        }

        return RequireExistingDirectory(fullPath);
    }

    public static string RequireAbsoluteLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathCharacters || !Path.IsPathFullyQualified(path))
        {
            throw new UpdateContractException("UnsafePath");
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length != 3 || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
        {
            throw new UpdateContractException("UnsafePath");
        }

        return fullPath;
    }

    public static string RequireRelativeFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new UpdateContractException("UnsafeRelativePath");
        }

        var segments = relativePath.Split('/');
        if (segments.Length is <= 0 or > 32)
        {
            throw new UpdateContractException("UnsafeRelativePath");
        }

        foreach (var segment in segments)
        {
            if (segment.Length is <= 0 or > 128 || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(segment))
            {
                throw new UpdateContractException("UnsafeRelativePath");
            }
        }

        return string.Join('/', segments);
    }

    public static string CombineUnder(string trustedRoot, string relativePath)
    {
        var root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var relative = RequireRelativeFile(relativePath).Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateContractException("UnsafeRelativePath");
        }

        return combined;
    }

    public static void RequireNoReparseComponents(string path)
    {
        var fullPath = RequireAbsoluteLocal(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UpdateContractException("UnsafePath");
            }
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void DeleteTreeWithoutFollowingLinks(string path)
    {
        DeleteTreeWithoutFollowingLinks(path, checkpoint: null);
    }

    internal static void DeleteTreeWithoutFollowingLinks(
        string path,
        Action<SafeDeleteCheckpoint, string>? checkpoint)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var fullPath = RequireAbsoluteLocal(path).TrimEnd(Path.DirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullPath)!;
        if (string.Equals(
            fullPath,
            volumeRoot.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateContractException("UnsafePath");
        }

        using var chain = OpenBoundDirectoryChain(fullPath);
        checkpoint?.Invoke(SafeDeleteCheckpoint.RootOpened, fullPath);
        var discovered = 0;
        DeleteBoundDirectory(chain.Target, ref discovered, checkpoint);
    }

    public static string AuditTreeWithoutLinks(string path)
    {
        var root = RequireExistingDirectory(path);
        var directories = new Stack<string>();
        var discovered = 0;
        directories.Push(root);
        while (directories.Count > 0)
        {
            var current = directories.Pop();
            discovered++;
            if (discovered > 100_000)
            {
                throw new UpdateContractException("TreeTooLarge");
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdateContractException("UnsafePath");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Push(child);
                }
            }
        }

        return root;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }

    private static BoundDirectoryChain OpenBoundDirectoryChain(string fullPath)
    {
        var handles = new List<BoundPathHandle>();
        try
        {
            var volumeRoot = Path.GetPathRoot(fullPath)!;
            handles.Add(OpenBoundPath(volumeRoot, requireDirectory: true, allowDelete: false));
            var current = volumeRoot;
            var segments = fullPath[volumeRoot.Length..].Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                handles.Add(OpenBoundPath(
                    current,
                    requireDirectory: true,
                    allowDelete: index == segments.Length - 1));
            }

            return new BoundDirectoryChain(handles);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static void DeleteBoundDirectory(
        BoundPathHandle directory,
        ref int discovered,
        Action<SafeDeleteCheckpoint, string>? checkpoint)
    {
        if (++discovered > 100_000)
        {
            throw new UpdateContractException("TreeTooLarge");
        }

        checkpoint?.Invoke(SafeDeleteCheckpoint.BeforeDirectoryEnumeration, directory.Path);
        foreach (var child in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            if (++discovered > 100_000)
            {
                throw new UpdateContractException("TreeTooLarge");
            }

            checkpoint?.Invoke(SafeDeleteCheckpoint.BeforeChildOpen, child);
            using var boundChild = OpenBoundPath(child, requireDirectory: false, allowDelete: true);
            checkpoint?.Invoke(SafeDeleteCheckpoint.ChildOpened, child);

            if (boundChild.IsDirectory)
            {
                DeleteBoundDirectory(boundChild, ref discovered, checkpoint);
                continue;
            }

            checkpoint?.Invoke(SafeDeleteCheckpoint.BeforeEntryDelete, child);
            DeleteOpenedHandle(boundChild.Handle);
        }

        checkpoint?.Invoke(SafeDeleteCheckpoint.BeforeEntryDelete, directory.Path);
        DeleteOpenedHandle(directory.Handle);
    }

    private static BoundPathHandle OpenBoundPath(string path, bool requireDirectory, bool allowDelete)
    {
        var handle = CreateFileW(
            path,
            FileReadAttributes | (allowDelete ? DeleteAccess : 0),
            FileShare.Read | FileShare.Write,
            0,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new UpdateContractException("UnsafePath");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            handle.Dispose();
            throw new UpdateContractException("UnsafePath");
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0
            || (requireDirectory && !isDirectory))
        {
            handle.Dispose();
            throw new UpdateContractException("UnsafePath");
        }

        return new BoundPathHandle(path, handle, isDirectory);
    }

    private static void DeleteOpenedHandle(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformationEx
        {
            Flags = FileDispositionDelete
                | FileDispositionPosixSemantics
                | FileDispositionIgnoreReadonlyAttribute,
        };
        if (!SetFileInformationByHandle(
            handle,
            FileDispositionInfoEx,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformationEx>()))
        {
            throw new UpdateContractException("UnsafePath");
        }
    }

    private sealed class BoundDirectoryChain(List<BoundPathHandle> handles) : IDisposable
    {
        public BoundPathHandle Target => handles[^1];

        public void Dispose()
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    private sealed class BoundPathHandle(
        string path,
        SafeFileHandle handle,
        bool isDirectory) : IDisposable
    {
        public string Path { get; } = path;
        public SafeFileHandle Handle { get; } = handle;
        public bool IsDirectory { get; } = isDirectory;

        public void Dispose() => Handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformationEx fileInformation,
        uint bufferSize);
}

internal enum SafeDeleteCheckpoint
{
    RootOpened,
    BeforeDirectoryEnumeration,
    BeforeChildOpen,
    ChildOpened,
    BeforeEntryDelete,
}
