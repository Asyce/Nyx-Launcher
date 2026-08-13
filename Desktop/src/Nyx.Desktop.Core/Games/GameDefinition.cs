namespace Nyx.Desktop.Core.Games;

public sealed class GameDefinition
{
    internal GameDefinition(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string DisplayName { get; }
}
