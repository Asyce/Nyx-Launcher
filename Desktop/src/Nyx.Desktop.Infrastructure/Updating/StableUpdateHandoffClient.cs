using System.Diagnostics;

namespace Nyx.Desktop.Infrastructure.Updating;

public static class StableUpdateHandoffClient
{
    public static async Task<bool> ConfirmCurrentAsync(
        string controlUpdaterPath,
        int callerProcessId,
        CancellationToken cancellationToken)
    {
        using var process = Start(controlUpdaterPath, "confirm-current", redirect: false, arguments =>
        {
            arguments.Add("--caller-pid");
            arguments.Add(callerProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    public static async Task<bool> HandoffAsync(
        string controlUpdaterPath,
        StableUpdateDownload download,
        int parentProcessId,
        Action beginShutdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(beginShutdown);
        using var process = Start(controlUpdaterPath, "handoff", redirect: true, arguments =>
        {
            arguments.Add("--manifest");
            arguments.Add(download.ManifestPath);
            arguments.Add("--package");
            arguments.Add(download.PackagePath);
            arguments.Add("--parent-pid");
            arguments.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        return await CompleteReadyHandshakeAsync(
            process.StandardOutput,
            process.StandardInput,
            beginShutdown,
            cancellationToken);
    }

    internal static async Task<bool> CompleteReadyHandshakeAsync(
        TextReader output,
        TextWriter input,
        Action beginShutdown,
        CancellationToken cancellationToken)
    {
        var ready = await output.ReadLineAsync(cancellationToken);
        if (!string.Equals(ready, "READY", StringComparison.Ordinal)) return false;

        beginShutdown();
        await input.WriteLineAsync("APPLY");
        await input.FlushAsync();
        return true;
    }

    private static Process Start(
        string updaterPath,
        string command,
        bool redirect,
        Action<System.Collections.ObjectModel.Collection<string>>? addArguments = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirect,
            RedirectStandardOutput = redirect,
            RedirectStandardError = false,
        };
        start.ArgumentList.Add(command);
        addArguments?.Invoke(start.ArgumentList);
        return Process.Start(start) ?? throw new InvalidOperationException("The stable updater did not start.");
    }
}
