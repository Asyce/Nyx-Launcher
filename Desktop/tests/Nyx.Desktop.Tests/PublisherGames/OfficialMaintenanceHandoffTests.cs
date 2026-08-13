using System.Reflection;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class OfficialMaintenanceHandoffTests
{
    [Fact]
    public void Wuwa_handoff_is_fixed_interactive_and_has_zero_arguments()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var target = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            fixture.CreateWuWaAdapter().Inspect(fixture.Root).MaintenanceTarget);

        var handoff = OfficialMaintenanceHandoffFactory.Create(target);

        Assert.Same(target, handoff.Target);
        Assert.Equal(fixture.PathOf("launcher.exe"), handoff.Target.LauncherPath);
        Assert.Equal("Use the validated Kuro launcher to maintain Wuthering Waves.", handoff.Instructions);
        Assert.Empty(handoff.Arguments);
        Assert.True(handoff.RequiresUserInteraction);
        Assert.True(handoff.RequiresImmediateRevalidation);
        Assert.True(handoff.RequiresFullInstallRevalidation);
        Assert.True(handoff.RequiresProtectedExecutableBinding);
        Assert.False(handoff.AllowsDirectUpdate);
        Assert.False(handoff.AllowsDirectGameLaunch);
    }

    [Fact]
    public void Endfield_handoff_is_generic_fixed_and_has_no_protocol_or_game_page()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var target = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            fixture.CreateEndfieldAdapter().Inspect(fixture.Root).MaintenanceTarget);

        var handoff = OfficialMaintenanceHandoffFactory.Create(target);

        Assert.Same(target, handoff.Target);
        Assert.Equal(fixture.PathOf("Launcher.exe"), handoff.Target.LauncherPath);
        Assert.Equal(
            "In GRYPHLINK, select Arknights: Endfield and use the official maintenance controls.",
            handoff.Instructions);
        Assert.Empty(handoff.Arguments);
        Assert.DoesNotContain("://", handoff.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_surface_cannot_mint_validation_tokens_or_handoff_requests()
    {
        Assert.Empty(typeof(ValidatedOfficialMaintenanceTarget).GetConstructors());
        Assert.Empty(typeof(OfficialMaintenanceHandoffRequest).GetConstructors());
        Assert.Empty(typeof(PublisherGameInspectionResult).GetConstructors());
        Assert.All(
            typeof(OfficialMaintenanceHandoffFactory).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => Assert.Equal(
                [typeof(ValidatedOfficialMaintenanceTarget)],
                method.GetParameters().Select(parameter => parameter.ParameterType).ToArray()));
    }

    [Fact]
    public void Production_adapters_expose_only_parameterless_construction()
    {
        Assert.All(typeof(WuWaIdentityAdapter).GetConstructors(), constructor =>
            Assert.Empty(constructor.GetParameters()));
        Assert.All(typeof(EndfieldIdentityAdapter).GetConstructors(), constructor =>
            Assert.Empty(constructor.GetParameters()));
        Assert.All(typeof(WuWaOfficialMaintenanceExecutor).GetConstructors(), constructor =>
            Assert.Empty(constructor.GetParameters()));
    }

    [Fact]
    public void Argument_collection_is_read_only()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var target = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            fixture.CreateEndfieldAdapter().Inspect(fixture.Root).MaintenanceTarget);
        var arguments = Assert.IsAssignableFrom<IList<string>>(
            OfficialMaintenanceHandoffFactory.Create(target).Arguments);

        Assert.Throws<NotSupportedException>(() => arguments.Add("--invented"));
    }
}
