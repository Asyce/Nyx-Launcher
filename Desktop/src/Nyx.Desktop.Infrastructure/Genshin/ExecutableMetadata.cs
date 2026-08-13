namespace Nyx.Desktop.Infrastructure.Genshin;

public sealed record ExecutableMetadata(
    bool HasValidAuthenticodeSignature,
    string? Publisher,
    string? ProductName,
    string? FileDescription,
    string? ProductVersion);

public interface IExecutableMetadataReader
{
    ExecutableMetadata Read(string executablePath);
}

public interface IDriveTypeReader
{
    DriveType GetDriveType(string driveRoot);
}

public sealed class SystemDriveTypeReader : IDriveTypeReader
{
    public DriveType GetDriveType(string driveRoot) => new DriveInfo(driveRoot).DriveType;
}
