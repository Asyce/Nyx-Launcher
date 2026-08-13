using Nyx.Desktop.Infrastructure.Cache;

namespace Nyx.Desktop.Tests.Cache;

public sealed class LauncherCacheServiceTests
{
    [Fact]
    public void Clear_generated_cache_preserves_user_assets_state_and_exports()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LauncherCacheService(root);
            Directory.CreateDirectory(cache.GeneratedDirectory);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            Directory.CreateDirectory(cache.UserAssetsDirectory);
            Directory.CreateDirectory(cache.UserArtCacheDirectory);
            Directory.CreateDirectory(cache.ExportsDirectory);
            File.WriteAllText(Path.Combine(cache.GeneratedDirectory, "generated.bin"), "generated");
            File.WriteAllText(Path.Combine(cache.LastKnownGoodDirectory, "manifest.json"), "generated");
            File.WriteAllText(Path.Combine(cache.UserAssetsDirectory, "kept.webp"), "user");
            File.WriteAllText(Path.Combine(cache.UserArtCacheDirectory, "kept-remote.webp"), "user-art");
            File.WriteAllText(cache.StatePath, "state");
            File.WriteAllText(Path.Combine(cache.ExportsDirectory, "kept.json"), "export");

            var totals = cache.ClearGeneratedCache();

            Assert.Equal(0, totals.GeneratedBytes);
            Assert.True(File.Exists(Path.Combine(cache.UserAssetsDirectory, "kept.webp")));
            Assert.True(File.Exists(Path.Combine(cache.UserArtCacheDirectory, "kept-remote.webp")));
            Assert.True(File.Exists(cache.StatePath));
            Assert.True(File.Exists(Path.Combine(cache.ExportsDirectory, "kept.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
