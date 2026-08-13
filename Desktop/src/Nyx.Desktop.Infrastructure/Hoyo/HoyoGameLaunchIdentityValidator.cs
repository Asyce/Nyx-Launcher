using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;

namespace Nyx.Desktop.Infrastructure.Hoyo;

public sealed class HoyoGameLaunchIdentityValidator(HoyoGameIdentityAdapter adapter)
    : IHoyoGameLaunchIdentityValidator
{
    private readonly HoyoGameIdentityAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));

    public HoyoGameInspectionResult Validate(string gameId, string? root) =>
        adapter.Inspect(gameId, root);
}
