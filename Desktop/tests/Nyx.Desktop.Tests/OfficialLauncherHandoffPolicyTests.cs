using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launchers;

namespace Nyx.Desktop.Tests;

public sealed class OfficialLauncherHandoffPolicyTests
{
    public static TheoryData<string> CanonicalGameIds =>
    [
        "gi",
        "hsr",
        "zzz",
        "wuwa",
        "ae",
    ];

    [Theory]
    [MemberData(nameof(CanonicalGameIds))]
    public void Registered_official_launcher_still_requires_the_user(string gameId)
    {
        var decision = OfficialLauncherHandoffPolicy.Decide(gameId, officialLauncherIsRegistered: true);

        Assert.True(decision.CanOpenOfficialLauncher);
        Assert.True(decision.RequiresUserInteraction);
        Assert.False(decision.AllowsDirectUpdate);
        Assert.Contains("user must", decision.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CanonicalGameIds))]
    public void Missing_official_launcher_never_falls_back_to_direct_updates(string gameId)
    {
        var decision = OfficialLauncherHandoffPolicy.Decide(gameId, officialLauncherIsRegistered: false);

        Assert.False(decision.CanOpenOfficialLauncher);
        Assert.True(decision.RequiresUserInteraction);
        Assert.False(decision.AllowsDirectUpdate);
        Assert.Contains("do not update", decision.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_game_cannot_receive_a_handoff_decision()
    {
        Assert.Throws<UnsupportedGameException>(
            () => OfficialLauncherHandoffPolicy.Decide("star-rail", officialLauncherIsRegistered: true));
    }

    [Fact]
    public void Unsafe_handoff_decisions_cannot_be_constructed_or_mutated_by_callers()
    {
        Assert.Empty(typeof(OfficialLauncherHandoffDecision).GetConstructors());
        Assert.All(
            typeof(OfficialLauncherHandoffDecision).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));

        var interactionProperty = typeof(OfficialLauncherHandoffDecision)
            .GetProperty(nameof(OfficialLauncherHandoffDecision.RequiresUserInteraction));
        var directUpdateProperty = typeof(OfficialLauncherHandoffDecision)
            .GetProperty(nameof(OfficialLauncherHandoffDecision.AllowsDirectUpdate));

        Assert.NotNull(interactionProperty);
        Assert.NotNull(directUpdateProperty);
        Assert.Null(interactionProperty.SetMethod);
        Assert.Null(directUpdateProperty.SetMethod);
    }
}
