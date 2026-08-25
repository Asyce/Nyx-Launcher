using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Launching;

public sealed class WindowsRunningProcessInspector
    : IRunningProcessInspector, IStrictRunningProcessInspector
{
    private readonly IWindowsProcessPathQuery processPathQuery;

    public WindowsRunningProcessInspector()
        : this(new LimitedInformationWindowsProcessPathQuery())
    {
    }

    internal WindowsRunningProcessInspector(IWindowsProcessPathQuery processPathQuery)
    {
        this.processPathQuery = processPathQuery
            ?? throw new ArgumentNullException(nameof(processPathQuery));
    }

    public RunningProcessStatus Check(string processName, string expectedExecutablePath) =>
        Check(processName, expectedExecutablePath, differentPathIsUncertain: false);

    public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath) =>
        Check(processName, expectedExecutablePath, differentPathIsUncertain: true);

    private RunningProcessStatus Check(
        string processName,
        string expectedExecutablePath,
        bool differentPathIsUncertain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        return EvaluateSameNamePaths(
            processPathQuery.QueryExecutablePaths(processName),
            expectedExecutablePath,
            differentPathIsUncertain);
    }

    internal static RunningProcessStatus EvaluateSameNamePaths(
        IEnumerable<string?> observedPaths,
        string expectedExecutablePath,
        bool differentPathIsUncertain)
    {
        ArgumentNullException.ThrowIfNull(observedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        var uncertain = false;
        foreach (var actualPath in observedPaths)
        {
            if (string.Equals(actualPath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return RunningProcessStatus.Running;
            }

            // Strict game checks treat a different path as a possible older game
            // root. Ordinary checks preserve the existing behavior needed for
            // generic publisher process names such as launcher.exe.
            uncertain |= actualPath is null || differentPathIsUncertain;
        }

        return uncertain ? RunningProcessStatus.Uncertain : RunningProcessStatus.NotRunning;
    }
}

internal interface IWindowsProcessPathQuery
{
    IReadOnlyList<string?> QueryExecutablePaths(string processName);
}

/// <summary>
/// Reads only the executable image path from same-name processes. The Windows
/// handle requests PROCESS_QUERY_LIMITED_INFORMATION, which is specifically
/// sufficient for QueryFullProcessImageName and can inspect elevated processes
/// without making Nyx elevated. Every failed/racing candidate is retained as
/// unknown evidence rather than being converted to absence.
/// </summary>
internal sealed class LimitedInformationWindowsProcessPathQuery : IWindowsProcessPathQuery
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumWindowsPathCharacters = 32768;

    public IReadOnlyList<string?> QueryExecutablePaths(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var paths = new List<string?>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    paths.Add(QueryExecutablePath(process.Id));
                }
                catch (Exception exception) when (exception is Win32Exception
                                                      or InvalidOperationException
                                                      or NotSupportedException
                                                      or UnauthorizedAccessException
                                                      or SecurityException)
                {
                    // The process may have exited between enumeration and query,
                    // or Windows may deny even limited information. Both are
                    // uncertain same-name evidence and must fail closed.
                    paths.Add(null);
                }
            }
        }

        return paths;
    }

    private static string? QueryExecutablePath(int processId)
    {
        using var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            return null;
        }

        var path = new StringBuilder(MaximumWindowsPathCharacters);
        var capacity = checked((uint)path.Capacity);
        return QueryFullProcessImageName(handle, flags: 0, path, ref capacity)
            && capacity > 0
            ? path.ToString()
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);
}

public sealed class Genshin120FpsProcessStarter : IGenshin120FpsProcessStarter, IAsyncDisposable
{
    public const string ExpectedHelperFileName = "Nyx.Genshin120.Helper.exe";
    private const string PipePrefix = "Pengo.Nyx.Genshin120.";
    private const uint RequestMagic = 0x3152584E;
    private const uint ResponseMagic = 0x3153584E;
    private const ushort ProtocolVersion = 1;
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumPathCharacters = 32 * 1024;
    private const int MaximumArgumentCharacters = 4096;
    private const int MaximumArguments = 64;
    private static readonly TimeSpan PipeConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(65);
    private readonly string helperPath;
    private readonly byte[] expectedHelperSha256;
    private readonly TimeSpan responseTimeout;
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private readonly object admissionSync = new();
    private Task? disposal;
    private TaskCompletionSource? launchesDrained;
    private int activeLaunches;
    private bool admissionClosed;

    public Genshin120FpsProcessStarter(string helperPath, string expectedHelperSha256)
        : this(helperPath, expectedHelperSha256, DefaultResponseTimeout)
    {
    }

    internal Genshin120FpsProcessStarter(
        string helperPath,
        string expectedHelperSha256,
        TimeSpan responseTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        if (!IsAbsoluteLocalPath(helperPath)
            || !string.Equals(
                Path.GetFileName(helperPath),
                ExpectedHelperFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The helper path is not the fixed packaged helper.", nameof(helperPath));
        }
        ArgumentNullException.ThrowIfNull(expectedHelperSha256);
        if (expectedHelperSha256.Length != 64
            || expectedHelperSha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The reviewed helper SHA-256 is unavailable.", nameof(expectedHelperSha256));
        }
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout));

        this.helperPath = Path.GetFullPath(helperPath);
        this.expectedHelperSha256 = Convert.FromHexString(expectedHelperSha256);
        this.responseTimeout = responseTimeout;
    }

    public Genshin120FpsStartStatus StartValidatedGenshin120Fps(
        ValidatedGenshin120FpsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnterLaunch();
        try
        {
            if (!launchGate.Wait(0)) return Genshin120FpsStartStatus.Failed;
            try
            {
                return StartCoreAsync(request.Specification, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                launchGate.Release();
            }
        }
        finally
        {
            ReleaseLaunch();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (admissionSync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private void EnterLaunch()
    {
        lock (admissionSync)
        {
            ObjectDisposedException.ThrowIf(admissionClosed, this);
            activeLaunches++;
        }
    }

    private void ReleaseLaunch()
    {
        TaskCompletionSource? drained = null;
        lock (admissionSync)
        {
            activeLaunches--;
            if (admissionClosed && activeLaunches == 0)
            {
                drained = launchesDrained;
            }
        }

        drained?.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        Task drain;
        lock (admissionSync)
        {
            admissionClosed = true;
            drain = activeLaunches == 0
                ? Task.CompletedTask
                : (launchesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await drain.ConfigureAwait(false);
        launchGate.Dispose();
    }

    private async Task<Genshin120FpsStartStatus> StartCoreAsync(
        LaunchSpecification specification,
        CancellationToken cancellationToken)
    {
        if (!HasExactGenshinSpecification(specification))
            return Genshin120FpsStartStatus.Failed;

        cancellationToken.ThrowIfCancellationRequested();
        BoundExecutable helper;
        try
        {
            helper = BoundExecutable.Open(helperPath, Path.GetPathRoot(helperPath)!, expectedHelperSha256);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException
                                              or InvalidOperationException
                                              or Win32Exception)
        {
            return Genshin120FpsStartStatus.HelperUnavailable;
        }
        using (helper)
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var game = BoundExecutable.Open(
                specification.FileName,
                specification.WorkingDirectory,
                expectedSha256: null);
            cancellationToken.ThrowIfCancellationRequested();

            var correlation = Guid.NewGuid().ToString("D");
            using var pipe = new NamedPipeServerStream(
                PipePrefix + correlation,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                4096,
                4096);
            cancellationToken.ThrowIfCancellationRequested();
            using var process = StartElevatedHelper(correlation, helper);
            cancellationToken.ThrowIfCancellationRequested();
            using var connectionBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionBudget.CancelAfter(PipeConnectionTimeout);
            await pipe.WaitForConnectionAsync(connectionBudget.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId)
                || !IsExpectedClientProcessId(clientProcessId, process.Id))
            {
                return Genshin120FpsStartStatus.Failed;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var requestBytes = SerializeRequest(specification, game.Sha256);
            cancellationToken.ThrowIfCancellationRequested();
            var requestWritten = await WriteRequestAfterHandoffAsync(
                pipe,
                requestBytes,
                RequestWriteTimeout).ConfigureAwait(false);
            var result = requestWritten
                ? await ReadTerminalResponseAsync(pipe, responseTimeout).ConfigureAwait(false)
                : Genshin120FpsStartStatus.GameStartUnconfirmed;
            return result is Genshin120FpsStartStatus.GameStartUnconfirmed
                && TryRecoverExitedHelperResult(process, out var recovered)
                    ? recovered
                    : result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Genshin120FpsStartStatus.ElevationCancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Genshin120FpsStartStatus.TimedOut;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException
                                              or InvalidOperationException
                                              or Win32Exception)
        {
            return Genshin120FpsStartStatus.Failed;
        }
    }

    private Process StartElevatedHelper(string correlation, BoundExecutable helper)
    {
        helper.EnsureBound();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = Path.GetDirectoryName(helperPath)!,
                UseShellExecute = true,
                Verb = "runas",
            },
        };
        process.StartInfo.ArgumentList.Add(correlation);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The fixed helper did not start.");
            helper.EnsureBound();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static byte[] SerializeRequest(LaunchSpecification specification, byte[] executableSha256)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(executableSha256);
        if (!HasExactGenshinSpecification(specification)
            || executableSha256.Length != 32
            || specification.FileName.Length is 0 or > MaximumPathCharacters
            || specification.WorkingDirectory.Length is 0 or > MaximumPathCharacters
            || specification.Arguments.Count > MaximumArguments
            || specification.Arguments.Any(argument => argument.Length > MaximumArgumentCharacters))
        {
            throw new InvalidOperationException("The bounded helper request is invalid.");
        }

        var payloadBytes = checked(
            12 + 32
            + (specification.FileName.Length + specification.WorkingDirectory.Length) * sizeof(char)
            + specification.Arguments.Sum(argument => 4 + argument.Length * sizeof(char)));
        if (payloadBytes + 12 > MaximumRequestBytes)
            throw new InvalidOperationException("The bounded helper request is too large.");

        var bytes = new byte[payloadBytes + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RequestMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)payloadBytes));
        var offset = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)specification.FileName.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), checked((uint)specification.WorkingDirectory.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), checked((uint)specification.Arguments.Count));
        offset += 12;
        executableSha256.CopyTo(bytes, offset);
        offset += executableSha256.Length;
        WriteUtf16(bytes, ref offset, specification.FileName);
        WriteUtf16(bytes, ref offset, specification.WorkingDirectory);
        foreach (var argument in specification.Arguments)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)argument.Length));
            offset += 4;
            WriteUtf16(bytes, ref offset, argument);
        }
        return bytes;
    }

    internal static Genshin120FpsStartStatus ParseResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length != 12
            || BinaryPrimitives.ReadUInt32LittleEndian(response) != ResponseMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(response[4..]) != ProtocolVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(response[6..]) != 0)
        {
            return Genshin120FpsStartStatus.GameStartUnconfirmed;
        }
        return ParseResultCode(BinaryPrimitives.ReadUInt32LittleEndian(response[8..]));
    }

    private static Genshin120FpsStartStatus ParseResultCode(uint resultCode) => resultCode switch
        {
            0 => Genshin120FpsStartStatus.Ready,
            1 => Genshin120FpsStartStatus.GameStartedAttachFailed,
            2 => Genshin120FpsStartStatus.GameStartedAttachTimedOut,
            3 or 4 => Genshin120FpsStartStatus.Failed,
            _ => Genshin120FpsStartStatus.GameStartUnconfirmed,
        };

    private static bool TryRecoverExitedHelperResult(
        Process process,
        out Genshin120FpsStartStatus result)
    {
        try
        {
            result = process.HasExited && process.ExitCode is >= 0 and <= 4
                ? ParseResultCode(checked((uint)process.ExitCode))
                : Genshin120FpsStartStatus.GameStartUnconfirmed;
            return result is not Genshin120FpsStartStatus.GameStartUnconfirmed;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            result = Genshin120FpsStartStatus.GameStartUnconfirmed;
            return false;
        }
    }

    internal static async Task<Genshin120FpsStartStatus> ReadTerminalResponseAsync(
        Stream stream,
        TimeSpan responseTimeout)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        using var responseBudget = new CancellationTokenSource(responseTimeout);
        try
        {
            var response = new byte[12];
            await ReadExactAsync(stream, response, responseBudget.Token).ConfigureAwait(false);
            return ParseResponse(response);
        }
        catch (Exception exception) when (exception is IOException
                                              or OperationCanceledException
                                              or InvalidOperationException)
        {
            return Genshin120FpsStartStatus.GameStartUnconfirmed;
        }
    }

    internal static async Task<bool> WriteRequestAfterHandoffAsync(
        Stream stream,
        ReadOnlyMemory<byte> request,
        TimeSpan writeTimeout)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (request.IsEmpty) throw new ArgumentException("The helper request is empty.", nameof(request));
        if (writeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        using var writeBudget = new CancellationTokenSource(writeTimeout);
        try
        {
            await stream.WriteAsync(request, writeBudget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or OperationCanceledException
                                              or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsExpectedClientProcessId(uint clientProcessId, int helperProcessId) =>
        helperProcessId > 0 && clientProcessId == checked((uint)helperProcessId);

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The fixed helper response ended early.");
            offset += read;
        }
    }

    private static void WriteUtf16(byte[] bytes, ref int offset, string value)
    {
        offset += Encoding.Unicode.GetBytes(value, bytes.AsSpan(offset));
    }

    private static bool HasExactGenshinSpecification(LaunchSpecification specification) =>
        specification is not null
        && !specification.UseShellExecute
        && IsAbsoluteLocalPath(specification.FileName)
        && IsAbsoluteLocalPath(specification.WorkingDirectory)
        && string.Equals(Path.GetFileName(specification.FileName), "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetDirectoryName(specification.FileName), specification.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
        && CustomArgumentParser.IsValid(specification.Arguments);

    private static bool IsAbsoluteLocalPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && !path.StartsWith("\\\\", StringComparison.Ordinal)
        && !path.StartsWith("\\\\?\\", StringComparison.Ordinal)
        && !path.StartsWith("\\\\.\\", StringComparison.Ordinal);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    private sealed class BoundExecutable : IDisposable
    {
        private readonly PublisherAncestorDirectoryBinding ancestors;
        private readonly FileStream stream;
        private readonly IPublisherFileIdentityReader identityReader;
        private readonly PublisherNtfsFileIdentity identity;

        private BoundExecutable(
            string path,
            PublisherAncestorDirectoryBinding ancestors,
            FileStream stream,
            IPublisherFileIdentityReader identityReader,
            PublisherNtfsFileIdentity identity,
            byte[] sha256)
        {
            Path = path;
            this.ancestors = ancestors;
            this.stream = stream;
            this.identityReader = identityReader;
            this.identity = identity;
            Sha256 = sha256;
        }

        public string Path { get; }
        public byte[] Sha256 { get; }

        public static BoundExecutable Open(string path, string bindingRoot, byte[]? expectedSha256)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            PublisherAncestorDirectoryBinding? ancestors = null;
            SafeFileHandle? entry = null;
            FileStream? stream = null;
            try
            {
                ancestors = PublisherAncestorDirectoryBinding.Open(bindingRoot, fullPath);
                var identityReader = new WindowsPublisherFileIdentityReader();
                entry = PublisherPathIdentity.OpenNonReparseEntry(fullPath);
                var identity = identityReader.Read(entry);
                if (identity.NumberOfLinks != 1) throw new IOException("Hard-linked launch files are not accepted.");
                stream = new FileStream(entry, FileAccess.Read, 64 * 1024, isAsync: false);
                entry = null;
                PublisherPathIdentity.EnsurePathMatches(fullPath, identity, identityReader);
                var sha256 = PublisherFileIdentity.GetSha256(stream);
                stream.Position = 0;
                PublisherPathIdentity.EnsurePathMatches(fullPath, identity, identityReader);
                if (expectedSha256 is not null
                    && !CryptographicOperations.FixedTimeEquals(sha256, expectedSha256))
                {
                    throw new IOException("The fixed helper hash did not match.");
                }
                return new(fullPath, ancestors, stream, identityReader, identity, sha256);
            }
            catch
            {
                stream?.Dispose();
                entry?.Dispose();
                ancestors?.Dispose();
                throw;
            }
        }

        public void EnsureBound() => PublisherPathIdentity.EnsurePathMatches(Path, identity, identityReader);

        public void Dispose()
        {
            stream.Dispose();
            ancestors.Dispose();
        }
    }
}

public sealed class DotNetLaunchProcessStarter
    : ILaunchProcessStarter,
      IGenshinElevatedProcessStarter,
      IHoyoGameElevatedProcessStarter,
      IPublisherGameElevatedProcessStarter
{
    public void Start(LaunchSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.UseShellExecute
            || !HasAllowedOfficialStart(specification))
        {
            throw new InvalidOperationException("Only an exact, bounded, non-shell official game start is allowed.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = specification.FileName,
            WorkingDirectory = specification.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The process did not start.");
    }

    public void StartValidatedGenshin(ValidatedGenshinElevationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var specification = request.Specification;
        if (specification.UseShellExecute
            || !HasExactOfficialExecutable(specification, "GenshinImpact.exe")
            || !HasAllowedArguments(specification.Arguments, fixedArgument: null))
        {
            throw new InvalidOperationException("Only an internally validated Genshin launch can request elevation.");
        }

        var startInfo = CreateElevatedStartInfo(specification);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated Genshin process did not start.");
    }

    public void StartValidatedHoyoGame(ValidatedHoyoGameElevationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var specification = request.Specification;
        var expectedExecutableName = request.GameId switch
        {
            "hsr" => "StarRail.exe",
            "zzz" => "ZenlessZoneZero.exe",
            _ => null,
        };
        if (expectedExecutableName is null
            || specification.UseShellExecute
            || !HasAllowedHoyoArguments(request.GameId, specification.Arguments)
            || !HasExactOfficialExecutable(specification, expectedExecutableName))
        {
            throw new InvalidOperationException(
                "Only an internally validated HSR or ZZZ launch can request elevation.");
        }

        var startInfo = CreateElevatedStartInfo(specification);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated HoYo game process did not start.");
    }

    public void StartValidatedPublisherGame(ValidatedPublisherGameElevationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var specification = request.Specification;
        var expectedRelativePath = request.GameId switch
        {
            "wuwa" => @"Wuthering Waves Game\Wuthering Waves.exe",
            "ae" => @"games\EndField Game\Endfield.exe",
            _ => null,
        };
        var expectedPath = expectedRelativePath is null
            ? null
            : Path.Combine(request.CanonicalRoot, expectedRelativePath);
        if (expectedPath is null
            || specification.UseShellExecute
            || !HasAllowedPublisherArguments(request.GameId, specification.Arguments)
            || !string.Equals(
                specification.FileName,
                expectedPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(specification.FileName),
                specification.WorkingDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Only an internally validated WuWa or Endfield game can request elevation.");
        }

        var startInfo = CreateElevatedStartInfo(specification);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated publisher game did not start.");
    }

    private static bool HasAllowedOfficialStart(LaunchSpecification specification)
    {
        var executable = Path.GetFileName(specification.FileName);
        var fixedArgument = executable switch
        {
            "ZenlessZoneZero.exe" => "-force-d3d12",
            "Wuthering Waves.exe" => "-dx11",
            "GenshinImpact.exe" or "StarRail.exe" or "Endfield.exe" => null,
            _ => string.Empty,
        };
        return fixedArgument != string.Empty
            && HasExactOfficialExecutable(specification, executable)
            && HasAllowedArguments(specification.Arguments, fixedArgument);
    }

    private static bool HasAllowedHoyoArguments(
        string gameId,
        IReadOnlyList<string> arguments) =>
        gameId switch
        {
            "hsr" => HasAllowedArguments(arguments, fixedArgument: null),
            "zzz" => HasAllowedArguments(arguments, "-force-d3d12"),
            _ => false,
        };

    private static bool HasAllowedPublisherArguments(
        string gameId,
        IReadOnlyList<string> arguments) =>
        gameId switch
        {
            "wuwa" => HasAllowedArguments(arguments, "-dx11"),
            "ae" => HasAllowedArguments(arguments, fixedArgument: null),
            _ => false,
        };

    private static bool HasAllowedArguments(
        IReadOnlyList<string> arguments,
        string? fixedArgument)
    {
        if (arguments is null) return false;
        var offset = fixedArgument is not null
            && arguments.Count > 0
            && string.Equals(arguments[0], fixedArgument, StringComparison.Ordinal)
                ? 1
                : 0;
        return CustomArgumentParser.IsValid(arguments.Skip(offset).ToArray());
    }

    internal static ProcessStartInfo CreateElevatedStartInfo(LaunchSpecification specification)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = specification.FileName,
            WorkingDirectory = specification.WorkingDirectory,
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var argument in specification.Arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static bool HasExactOfficialExecutable(
        LaunchSpecification specification,
        string expectedExecutableName)
    {
        var path = specification.FileName;
        return !string.IsNullOrWhiteSpace(path)
            && !string.IsNullOrWhiteSpace(specification.WorkingDirectory)
            && path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == Path.VolumeSeparatorChar
            && path[2] == Path.DirectorySeparatorChar
            && string.Equals(Path.GetFileName(path), expectedExecutableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(path),
                specification.WorkingDirectory,
                StringComparison.OrdinalIgnoreCase);
    }
}
