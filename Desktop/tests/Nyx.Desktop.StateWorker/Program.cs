using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Infrastructure.State;

if (args.Length != 7 || !string.Equals(args[0], "append", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: append <root> <id> <ready> <go> <acquired> <delay-ms>");
    return 64;
}

try
{
    var root = args[1];
    var id = args[2];
    var readyPath = args[3];
    var goPath = args[4];
    var acquiredPath = args[5];
    var delay = int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
    File.WriteAllText(readyPath, id);

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (!File.Exists(goPath))
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Start barrier timed out.");
        Thread.Sleep(10);
    }

    var store = new LauncherStateStore(root, TimeSpan.FromSeconds(10));
    store.Update(state =>
    {
        File.WriteAllText(acquiredPath, id);
        if (delay > 0) Thread.Sleep(delay);
        var game = new CustomGameDefinition
        {
            Id = id,
            Name = id,
            ExecutablePath = $@"C:\Games\{id}.exe",
            IconPath = $@"C:\Games\{id}.png",
            CreationOrder = DateTime.UtcNow.Ticks,
        };
        return state with
        {
            CustomGames = state.CustomGames.Append(game).ToArray(),
            RailOrder = state.RailOrder.Append(id).ToArray(),
        };
    });
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
