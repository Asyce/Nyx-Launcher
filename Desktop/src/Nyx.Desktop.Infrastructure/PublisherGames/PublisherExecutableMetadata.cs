using System.Diagnostics;
using System.Runtime.Versioning;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal sealed record PublisherExecutableMetadata(
    bool HasValidAuthenticodeSignature,
    string? Publisher,
    string? ProductName,
    string? FileDescription,
    string? ProductVersion,
    string? OriginalFilename,
    string? CompanyName);

internal interface IPublisherExecutableMetadataReader
{
    PublisherExecutableMetadata Read(
        string executablePath,
        PublisherNtfsFileIdentity expectedIdentity,
        IPublisherFileIdentityReader identityReader);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPublisherExecutableMetadataReader : IPublisherExecutableMetadataReader
{
    private readonly WindowsAuthenticodeExecutableMetadataReader authenticode = new();

    public PublisherExecutableMetadata Read(
        string executablePath,
        PublisherNtfsFileIdentity expectedIdentity,
        IPublisherFileIdentityReader identityReader)
    {
        PublisherPathIdentity.EnsurePathMatches(
            executablePath,
            expectedIdentity,
            identityReader);
        var trusted = authenticode.Read(executablePath);
        PublisherPathIdentity.EnsurePathMatches(
            executablePath,
            expectedIdentity,
            identityReader);
        PublisherPathIdentity.EnsurePathMatches(
            executablePath,
            expectedIdentity,
            identityReader);
        var version = FileVersionInfo.GetVersionInfo(executablePath);
        PublisherPathIdentity.EnsurePathMatches(
            executablePath,
            expectedIdentity,
            identityReader);
        return new(
            trusted.HasValidAuthenticodeSignature,
            trusted.Publisher,
            trusted.ProductName,
            trusted.FileDescription,
            trusted.ProductVersion,
            version.OriginalFilename,
            version.CompanyName);
    }
}
