namespace Nyx.Desktop.Core.PublisherGames;

public sealed class LatestGenerationGate
{
    private readonly object sync = new();
    private int current;

    public int Next()
    {
        lock (sync)
        {
            current = unchecked(current + 1);
            return current;
        }
    }

    public bool IsCurrent(int generation)
    {
        lock (sync)
        {
            return generation == current;
        }
    }

    public bool TryApply(int generation, Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (sync)
        {
            if (generation != current)
            {
                return false;
            }

            apply();
            return true;
        }
    }
}
