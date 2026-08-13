using System.Reflection;
using System.Security.Cryptography;

namespace Nyx.Desktop.Core.Exports;

/// <summary>
/// Reports achievement capability only when the fixed packaged helper exists
/// and matches the SHA-256 stamped into the entry assembly at package build time.
/// The process-launch boundary repeats stronger path and file-identity checks.
/// </summary>
public static class PackagedAchievementHelperReadiness
{
    public const string HelperFileName = "pengo-achievements-launcher.exe";
    public const string HashMetadataKey = "PengoAchievementHelperSha256";

    public static bool IsCurrentProcessReady()
    {
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly is null) return false;
            var hashes = entryAssembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(static attribute => attribute.Key == HashMetadataKey)
                .Select(static attribute => attribute.Value)
                .Take(2)
                .ToArray();
            return hashes.Length == 1
                && IsReady(AppContext.BaseDirectory, hashes[0]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsReady(string baseDirectory, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)
            || expectedSha256?.Length != 64
            || expectedSha256.Any(static character =>
                !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
            return false;

        try
        {
            var root = Path.GetFullPath(baseDirectory);
            var assets = Path.Combine(root, "Assets");
            var tools = Path.Combine(assets, "Tools");
            var helper = Path.Combine(tools, HelperFileName);
            foreach (var path in new[] { assets, tools, helper })
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            }

            using var stream = new FileStream(
                helper,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var actual = SHA256.HashData(stream);
            var expected = Convert.FromHexString(expectedSha256);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
