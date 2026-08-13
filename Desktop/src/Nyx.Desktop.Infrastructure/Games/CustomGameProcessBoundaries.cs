using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Launching;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Games;

/// <summary>Production custom-game process boundary. It compares the full image path.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCustomGameProcessInspector : ICustomGameProcessInspector
{
    private readonly WindowsRunningProcessInspector inspector;

    public WindowsCustomGameProcessInspector()
    {
        inspector = new WindowsRunningProcessInspector();
    }

    public ExactProcessPresence Check(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        return inspector.CheckStrict(processName, executablePath) switch
        {
            RunningProcessStatus.Running => ExactProcessPresence.Present,
            RunningProcessStatus.Uncertain => ExactProcessPresence.Uncertain,
            _ => ExactProcessPresence.Absent,
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCustomGameProcessStarter : ICustomGameProcessStarter
{
    private readonly ICustomGameLaunchLeaseFactory leaseFactory;
    private readonly ICustomGameProcessDispatcher dispatcher;

    public WindowsCustomGameProcessStarter()
        : this(new WindowsCustomGameLaunchLeaseFactory(), new DotNetCustomGameProcessDispatcher())
    {
    }

    internal WindowsCustomGameProcessStarter(
        ICustomGameLaunchLeaseFactory leaseFactory,
        ICustomGameProcessDispatcher dispatcher)
    {
        this.leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(string executablePath, IReadOnlyList<string> arguments, bool requestAdministrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        using var launchLease = leaseFactory.Acquire(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = requestAdministrator,
            Verb = requestAdministrator ? "runas" : string.Empty,
        };
        foreach (var argument in arguments)
        {
            if (argument.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Arguments cannot contain control characters.", nameof(arguments));
            startInfo.ArgumentList.Add(argument);
        }

        // Keep every ancestor directory and the exact executable bound until
        // CreateProcess/ShellExecuteEx has returned a process. This closes the
        // path substitution window for both normal and runas launches.
        dispatcher.Start(startInfo);
    }
}

internal interface ICustomGameLaunchLeaseFactory
{
    IDisposable Acquire(string executablePath);
}

internal interface ICustomGameProcessDispatcher
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class DotNetCustomGameProcessDispatcher : ICustomGameProcessDispatcher
{
    public void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The custom game did not start.");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCustomGameLaunchLeaseFactory : ICustomGameLaunchLeaseFactory
{
    private readonly IPublisherFileIdentityReader identityReader =
        new WindowsPublisherFileIdentityReader();

    public IDisposable Acquire(string executablePath)
    {
        var path = Path.GetFullPath(executablePath);
        var root = Path.GetPathRoot(path)
            ?? throw new IOException("The custom executable has no local root.");
        var ancestors = PublisherAncestorDirectoryBinding.Open(root, path);
        SafeFileHandle? executable = null;
        try
        {
            executable = PublisherPathIdentity.OpenNonReparseEntry(path);
            var identity = identityReader.Read(executable);
            if (identity.NumberOfLinks != 1)
            {
                throw new IOException("Hard-linked custom executables are not accepted.");
            }

            PublisherPathIdentity.EnsurePathMatches(path, identity, identityReader);
            return new WindowsCustomGameLaunchLease(ancestors, executable);
        }
        catch
        {
            executable?.Dispose();
            ancestors.Dispose();
            throw;
        }
    }
}

internal sealed class WindowsCustomGameLaunchLease(
    PublisherAncestorDirectoryBinding ancestors,
    SafeFileHandle executable) : IDisposable
{
    private bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        executable.Dispose();
        ancestors.Dispose();
    }
}

public static class CustomGameSessionFactory
{
    [SupportedOSPlatform("windows")]
    public static CustomGameSessionAdapter Create(CustomGameDefinition game) =>
        new(game, new WindowsCustomGameProcessInspector(), new WindowsCustomGameProcessStarter());

    /// <summary>
    /// Loaded state is untrusted. Only a definition that still passes the full
    /// physical path audit may become a session adapter.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TryCreateValidated(
        CustomGameDefinition game,
        out CustomGameSessionAdapter? adapter,
        ICustomGamePathProbe? pathProbe = null)
    {
        var validation = CustomGameValidator.Revalidate(game, pathProbe);
        if (!validation.IsValid || validation.Game is null)
        {
            adapter = null;
            return false;
        }

        adapter = pathProbe is null
            ? Create(validation.Game)
            : new CustomGameSessionAdapter(
                validation.Game,
                new WindowsCustomGameProcessInspector(),
                new WindowsCustomGameProcessStarter(),
                pathProbe);
        return true;
    }
}
