using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using TotallyHot.ArcRouter.Sandbox.Firecracker;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="VsockGuestAgentClient"/> against a real Unix domain socket standing in for the
/// Firecracker-exposed vsock UDS - .NET's <see cref="UnixDomainSocketEndPoint"/> works on this Windows dev
/// machine too (AF_UNIX support landed in Windows 10 1803+), so the handshake, JSON request/response framing,
/// and line-reading logic are exercised for real rather than mocked at the interface boundary.
/// </summary>
/// <remarks>
/// Marked <see cref="SupportedOSPlatformAttribute"/>("linux") to satisfy CA1416 for every call site in
/// this class, mirroring <c>FirecrackerMicroVmLauncherTests</c>. Compile-time only - the client's socket
/// code is not actually Linux-specific, so these tests run and assert for real on any OS.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class VsockGuestAgentClientTests : IDisposable
{
    private readonly string _udsPath = Path.Combine(
        Path.GetTempPath(), "acrouter-vsock-test-" + Guid.NewGuid().ToString("N") + ".sock");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_udsPath))
            {
                File.Delete(_udsPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNullOrEmptyUdsPath()
    {
        var client = new VsockGuestAgentClient();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ExecuteAsync(string.Empty, 52, SandboxLanguage.Python, "print(1)", 5000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_CompletesHandshakeAndParsesGuestResponse()
    {
        using var listener = StartListener(_udsPath, async server =>
        {
            var handshake = await ReadLineAsync(server, TestContext.Current.CancellationToken);
            Assert.Equal("CONNECT 52", handshake);
            await WriteLineAsync(server, "OK 52", TestContext.Current.CancellationToken);

            var requestLine = await ReadLineAsync(server, TestContext.Current.CancellationToken);
            Assert.Contains("\"language\":\"python\"", requestLine);
            Assert.Contains("\"code\":\"print(1)\"", requestLine);
            Assert.Contains("\"timeout_ms\":5000", requestLine);

            var response = "{\"exit_code\":0,\"stdout\":\"1\\n\",\"stderr\":\"\",\"timed_out\":false,\"peak_memory_bytes\":4096}";
            await WriteLineAsync(server, response, TestContext.Current.CancellationToken);
        });

        var client = new VsockGuestAgentClient();
        var result = await client.ExecuteAsync(
            _udsPath, port: 52, SandboxLanguage.Python, "print(1)", timeoutMs: 5000, TestContext.Current.CancellationToken);

        await listener.ServerTask;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1\n", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.False(result.TimedOut);
        Assert.Equal(4096, result.PeakMemoryBytes);
    }

    [Fact]
    public async Task ExecuteAsync_FailedHandshake_ThrowsInvalidOperationException()
    {
        using var listener = StartListener(_udsPath, async server =>
        {
            _ = await ReadLineAsync(server, TestContext.Current.CancellationToken);
            await WriteLineAsync(server, "ERR guest port unreachable", TestContext.Current.CancellationToken);
        });

        var client = new VsockGuestAgentClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteAsync(_udsPath, 52, SandboxLanguage.Python, "print(1)", 5000, TestContext.Current.CancellationToken));

        Assert.Contains("handshake", ex.Message, StringComparison.OrdinalIgnoreCase);

        await listener.ServerTask;
    }

    [Fact]
    public async Task ExecuteAsync_NullGuestResponse_ThrowsInvalidOperationException()
    {
        using var listener = StartListener(_udsPath, async server =>
        {
            _ = await ReadLineAsync(server, TestContext.Current.CancellationToken);
            await WriteLineAsync(server, "OK 52", TestContext.Current.CancellationToken);
            _ = await ReadLineAsync(server, TestContext.Current.CancellationToken);

            // The literal JSON "null" deserializes successfully to a null GuestExecResponse, which is the
            // ?? throw branch's actual trigger - an empty/malformed line instead throws JsonException
            // before that null check is ever reached.
            await WriteLineAsync(server, "null", TestContext.Current.CancellationToken);
        });

        var client = new VsockGuestAgentClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteAsync(_udsPath, 52, SandboxLanguage.Python, "print(1)", 5000, TestContext.Current.CancellationToken));

        Assert.Contains("empty response", ex.Message, StringComparison.OrdinalIgnoreCase);

        await listener.ServerTask;
    }

    [Fact]
    public async Task ExecuteAsync_ResponseTerminatedByStreamCloseInsteadOfNewline_StillParses()
    {
        // ReadLineAsync's normal exit is finding '\n' in the buffer; this covers its other exit - hitting
        // end-of-stream (read == 0) after accumulating a chunk with no newline in it at all, which happens
        // if the guest agent closes the connection right after writing its response instead of following
        // it with a trailing newline.
        using var listener = StartListener(_udsPath, async server =>
        {
            _ = await ReadLineAsync(server, TestContext.Current.CancellationToken);
            await WriteLineAsync(server, "OK 52", TestContext.Current.CancellationToken);
            _ = await ReadLineAsync(server, TestContext.Current.CancellationToken);

            var response = "{\"exit_code\":0,\"stdout\":\"ok\",\"stderr\":\"\",\"timed_out\":false,\"peak_memory_bytes\":0}"u8.ToArray();
            await server.SendAsync(response, SocketFlags.None, TestContext.Current.CancellationToken);
            server.Shutdown(SocketShutdown.Send);
        });

        var client = new VsockGuestAgentClient();
        var result = await client.ExecuteAsync(
            _udsPath, port: 52, SandboxLanguage.Python, "print(1)", timeoutMs: 5000, TestContext.Current.CancellationToken);

        await listener.ServerTask;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
    }

    private static TestListener StartListener(string path, Func<Socket, Task> handleServerConnection)
    {
        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listenSocket.Bind(new UnixDomainSocketEndPoint(path));
        listenSocket.Listen(1);

        var serverTask = Task.Run(async () =>
        {
            using var server = await listenSocket.AcceptAsync();
            await handleServerConnection(server);
        });

        return new TestListener(listenSocket, serverTask);
    }

    private static async Task<string> ReadLineAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, newline));
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    private static async Task WriteLineAsync(Socket socket, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await socket.SendAsync(bytes, SocketFlags.None, cancellationToken);
    }

    private sealed class TestListener(Socket listenSocket, Task serverTask) : IDisposable
    {
        public Task ServerTask { get; } = serverTask;

        public void Dispose() => listenSocket.Dispose();
    }
}

