using System.Buffers.Binary;
using System.Text;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Tests.Launching;

public sealed class Genshin120FpsProcessStarterTests
{
    private const string GameRoot = @"C:\Games\Genshin Impact Game";

    [Fact]
    public void Request_protocol_preserves_exact_ordered_arguments_and_executable_hash()
    {
        var hash = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var specification = Specification(["--name", "Traveler One", string.Empty]);

        var bytes = Genshin120FpsProcessStarter.SerializeRequest(specification, hash);

        Assert.Equal(0x3152584Eu, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(checked((uint)(bytes.Length - 12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        var offset = 12;
        var executableCharacters = ReadUInt32(bytes, ref offset);
        var directoryCharacters = ReadUInt32(bytes, ref offset);
        Assert.Equal(3u, ReadUInt32(bytes, ref offset));
        Assert.Equal(hash, bytes.AsSpan(offset, 32).ToArray());
        offset += 32;
        Assert.Equal(specification.FileName, ReadUtf16(bytes, ref offset, executableCharacters));
        Assert.Equal(specification.WorkingDirectory, ReadUtf16(bytes, ref offset, directoryCharacters));
        Assert.Equal("--name", ReadArgument(bytes, ref offset));
        Assert.Equal("Traveler One", ReadArgument(bytes, ref offset));
        Assert.Equal(string.Empty, ReadArgument(bytes, ref offset));
        Assert.Equal(bytes.Length, offset);
    }

    [Theory]
    [InlineData(0u, Genshin120FpsStartStatus.Ready)]
    [InlineData(1u, Genshin120FpsStartStatus.GameStartedAttachFailed)]
    [InlineData(2u, Genshin120FpsStartStatus.GameStartedAttachTimedOut)]
    [InlineData(3u, Genshin120FpsStartStatus.Failed)]
    [InlineData(4u, Genshin120FpsStartStatus.Failed)]
    [InlineData(99u, Genshin120FpsStartStatus.GameStartUnconfirmed)]
    public void Response_protocol_accepts_only_fixed_versioned_results(
        uint rawStatus,
        Genshin120FpsStartStatus expected)
    {
        var response = Response(rawStatus);

        Assert.Equal(expected, Genshin120FpsProcessStarter.ParseResponse(response));

        response[6] = 1;
        Assert.Equal(Genshin120FpsStartStatus.GameStartUnconfirmed, Genshin120FpsProcessStarter.ParseResponse(response));
        Assert.Equal(Genshin120FpsStartStatus.GameStartUnconfirmed, Genshin120FpsProcessStarter.ParseResponse(response[..11]));
    }

    [Fact]
    public async Task Response_budget_starts_only_when_the_terminal_read_begins()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        using var response = new MemoryStream(Response(0));

        var result = await Genshin120FpsProcessStarter.ReadTerminalResponseAsync(
            response,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(Genshin120FpsStartStatus.Ready, result);
    }

    [Fact]
    public async Task Missing_terminal_response_after_handoff_is_unconfirmed_not_prestart_timeout()
    {
        await using var response = new NeverReadStream();

        var result = await Genshin120FpsProcessStarter.ReadTerminalResponseAsync(
            response,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(Genshin120FpsStartStatus.GameStartUnconfirmed, result);
    }

    [Fact]
    public async Task Request_write_timeout_after_handoff_is_not_classified_as_prestart()
    {
        await using var pipe = new NeverWriteStream();

        var written = await Genshin120FpsProcessStarter.WriteRequestAfterHandoffAsync(
            pipe,
            new byte[] { 1 },
            TimeSpan.FromMilliseconds(20));

        Assert.False(written);
    }

    [Fact]
    public void Pipe_client_must_be_the_exact_returned_helper_process()
    {
        Assert.True(Genshin120FpsProcessStarter.IsExpectedClientProcessId(123, 123));
        Assert.False(Genshin120FpsProcessStarter.IsExpectedClientProcessId(124, 123));
        Assert.False(Genshin120FpsProcessStarter.IsExpectedClientProcessId(0, 0));
    }

    [Fact]
    public void Missing_or_tampered_packaged_helper_fails_before_any_game_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-genshin120-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var helperPath = Path.Combine(root, Genshin120FpsProcessStarter.ExpectedHelperFileName);
            var missing = new Genshin120FpsProcessStarter(helperPath, new string('0', 64));
            Assert.Equal(
                Genshin120FpsStartStatus.HelperUnavailable,
                missing.StartValidatedGenshin120Fps(Request(), default));

            File.WriteAllBytes(helperPath, [1, 2, 3]);
            var tampered = new Genshin120FpsProcessStarter(helperPath, new string('0', 64));
            Assert.Equal(
                Genshin120FpsStartStatus.HelperUnavailable,
                tampered.StartValidatedGenshin120Fps(Request(), default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pre_cancelled_request_never_opens_or_starts_the_helper()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var starter = new Genshin120FpsProcessStarter(
            Path.Combine(@"C:\Nyx\Assets\Tools", Genshin120FpsProcessStarter.ExpectedHelperFileName),
            new string('0', 64));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            starter.StartValidatedGenshin120Fps(Request(), cancellation.Token));
    }

    [Fact]
    public async Task Disposal_drains_an_active_launch_and_is_idempotent()
    {
        var starter = new Genshin120FpsProcessStarter(
            Path.Combine(@"C:\Nyx\Assets\Tools", Genshin120FpsProcessStarter.ExpectedHelperFileName),
            new string('0', 64));
        var enter = typeof(Genshin120FpsProcessStarter).GetMethod(
            "EnterLaunch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var release = typeof(Genshin120FpsProcessStarter).GetMethod(
            "ReleaseLaunch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        enter.Invoke(starter, null);

        var disposal = starter.DisposeAsync().AsTask();
        var disposalAgain = starter.DisposeAsync().AsTask();
        Assert.Same(disposal, disposalAgain);
        await Task.Delay(40);
        Assert.False(disposal.IsCompleted);

        release.Invoke(starter, null);
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Throws<ObjectDisposedException>(() => starter.StartValidatedGenshin120Fps(Request(), default));
    }

    private static ValidatedGenshin120FpsRequest Request() => new(Specification([]));

    private static LaunchSpecification Specification(IReadOnlyList<string> arguments) => new(
        Path.Combine(GameRoot, "GenshinImpact.exe"),
        GameRoot,
        arguments,
        UseShellExecute: false);

    private static byte[] Response(uint status)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3153584E);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), status);
        return bytes;
    }

    private static uint ReadUInt32(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static string ReadArgument(byte[] bytes, ref int offset) =>
        ReadUtf16(bytes, ref offset, ReadUInt32(bytes, ref offset));

    private static string ReadUtf16(byte[] bytes, ref int offset, uint characters)
    {
        var byteCount = checked((int)characters * sizeof(char));
        var value = Encoding.Unicode.GetString(bytes, offset, byteCount);
        offset += byteCount;
        return value;
    }

    private sealed class NeverReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NeverWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
