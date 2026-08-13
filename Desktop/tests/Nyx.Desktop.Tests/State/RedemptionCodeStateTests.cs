using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Tests.State;

public sealed class RedemptionCodeStateTests
{
    [Fact]
    public void Copied_codes_round_trip_without_accepting_unknown_games_or_unsafe_values()
    {
        var state = LauncherState.Defaults() with
        {
            Preferences = new LauncherGlobalPreferences
            {
                CopiedRedemptionCodes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["gi"] = ["SAFE_CODE", "SAFE_CODE", "bad code"],
                    ["unknown"] = ["IGNORED"],
                },
            },
        };

        var result = LauncherStateMigrations.Read(LauncherStateMigrations.Write(state));

        Assert.True(result.IsUsable);
        Assert.Equal(["SAFE_CODE"], result.State!.Preferences.CopiedRedemptionCodes["gi"]);
        Assert.False(result.State.Preferences.CopiedRedemptionCodes.ContainsKey("unknown"));
    }
}
