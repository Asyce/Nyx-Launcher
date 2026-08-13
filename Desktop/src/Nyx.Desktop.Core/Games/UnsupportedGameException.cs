namespace Nyx.Desktop.Core.Games;

public sealed class UnsupportedGameException : ArgumentException
{
    public UnsupportedGameException(string? gameId)
        : base($"'{gameId ?? "<null>"}' is not a supported canonical game ID.", nameof(gameId))
    {
    }
}
