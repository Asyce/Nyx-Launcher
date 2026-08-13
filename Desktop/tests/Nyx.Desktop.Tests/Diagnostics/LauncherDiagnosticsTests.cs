using Nyx.Desktop.Core.Diagnostics;
using Nyx.Desktop.Core.Features;

namespace Nyx.Desktop.Tests.Diagnostics;

public sealed class LauncherDiagnosticsTests
{
    [Fact]
    public void Feature_flags_are_independent_and_proven_pull_lanes_default_on()
    {
        var defaults = LauncherFeatureFlags.Defaults();
        Assert.True(defaults.IsEnabled(LauncherFeatureFlag.GiPulls));
        Assert.True(defaults.IsEnabled(LauncherFeatureFlag.HsrAchievements));
        Assert.True(defaults.IsEnabled(LauncherFeatureFlag.ZzzPulls));
        Assert.True(defaults.IsEnabled(LauncherFeatureFlag.WuWaPulls));

        var changed = defaults with { GiPulls = false };
        Assert.False(changed.GiPulls);
        Assert.True(changed.GiAchievements);
        Assert.True(changed.HsrPulls);
    }

    [Fact]
    public void Diagnostics_copy_text_contains_only_safe_tokens_and_no_paths()
    {
        var snapshot = new LauncherDiagnosticsSnapshot(
            "1.2.3+test",
            LauncherFeatureFlags.Defaults(),
            [new LauncherDiagnosticGame("GI", "Running", "Succeeded", LauncherDiscoveryResultCategory.Ready, "C:\\Users\\secret\\token")],
            new string('A', 64),
            "ok",
            new LauncherCacheTotals(12, 34),
            "C:\\private\\error");

        var text = LauncherDiagnosticsText.FormatForCopy(snapshot);

        Assert.Contains("game:gi", text);
        Assert.Contains("error=unknown", text);
        Assert.DoesNotContain("C:\\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }
}
