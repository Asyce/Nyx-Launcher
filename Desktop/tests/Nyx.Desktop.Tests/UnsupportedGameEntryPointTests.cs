using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launchers;
using Nyx.Desktop.Infrastructure.Installations;

namespace Nyx.Desktop.Tests;

public sealed class UnsupportedGameEntryPointTests
{
    [Theory]
    [InlineData("genshin")]
    [InlineData("GI")]
    [InlineData("ww")]
    [InlineData("endfield")]
    [InlineData("")]
    [InlineData(null)]
    public void Every_public_game_id_entry_point_rejects_noncanonical_ids(string? gameId)
    {
        using var sandbox = new TemporarySandbox();
        var probe = new SandboxInstallationProbe(sandbox.Path);

        Assert.False(GameCatalog.TryGet(gameId, out _));
        Assert.Throws<UnsupportedGameException>(() => GameCatalog.GetRequired(gameId));
        Assert.Throws<UnsupportedGameException>(
            () => OfficialLauncherHandoffPolicy.Decide(gameId, officialLauncherIsRegistered: true));
        Assert.Throws<UnsupportedGameException>(() => probe.Probe(gameId, sandbox.Path));
    }

    private sealed class TemporarySandbox : IDisposable
    {
        public TemporarySandbox()
        {
            Path = Directory.CreateDirectory(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "NyxDesktopTests",
                    Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
