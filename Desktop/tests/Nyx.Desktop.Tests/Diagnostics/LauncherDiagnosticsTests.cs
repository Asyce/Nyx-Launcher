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

    [Fact]
    public void Timing_entries_allow_only_fixed_operations_and_canonical_games_then_clamp_and_sort()
    {
        var snapshot = new LauncherDiagnosticsSnapshot(
            "1.2.3",
            LauncherFeatureFlags.Defaults(),
            timings:
            [
                new("render", null, -1),
                new("launch", "gi", 7),
                new("close", "wuwa", 2),
                new("banner", "ae", 5),
                new("background", "zzz", 6),
                new("account-restore", null, 4),
                new("account-refresh", "hsr", int.MaxValue),
                new("unknown-operation", "gi", 10),
                new("render", "GI", 11),
                new("render", "C:\\Users\\secret-account", 12),
                new("C:\\private\\provider-response", null, 13),
            ]);

        Assert.Equal(7, snapshot.Timings.Count);
        Assert.Equal(0, Assert.Single(snapshot.Timings, timing => timing.Operation == "render").Milliseconds);
        Assert.Equal(600_000, Assert.Single(snapshot.Timings, timing => timing.Operation == "account-refresh").Milliseconds);

        var text = LauncherDiagnosticsText.FormatForCopy(snapshot);
        Assert.Equal(
            [
                "timing:account-refresh game=hsr milliseconds=600000",
                "timing:account-restore milliseconds=4",
                "timing:background game=zzz milliseconds=6",
                "timing:banner game=ae milliseconds=5",
                "timing:close game=wuwa milliseconds=2",
                "timing:launch game=gi milliseconds=7",
                "timing:render milliseconds=0",
            ],
            text.Split(Environment.NewLine).Where(static line => line.StartsWith("timing:", StringComparison.Ordinal)));
        Assert.DoesNotContain("unknown-operation", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-account", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-response", text, StringComparison.OrdinalIgnoreCase);
    }
}
