using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using TotallyHot.ArcRouter.Sandbox.Firecracker;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="FirecrackerClient"/> and <see cref="FirecrackerClientFactory"/> against a minimal
/// hand-rolled HTTP/1.1 responder listening on a real Unix domain socket, standing in for Firecracker's
/// own UDS-served REST API. <see cref="UnixDomainSocketEndPoint"/> works on this Windows dev machine too,
/// so the client's <c>SocketsHttpHandler.ConnectCallback</c> plumbing and PUT/PATCH request framing are
/// exercised for real.
/// </summary>
/// <remarks>
/// Marked <see cref="SupportedOSPlatformAttribute"/>("linux") to satisfy CA1416 for every call site in
/// this class, mirroring <c>FirecrackerMicroVmLauncherTests</c>. Compile-time only - the client's HTTP-over-UDS
/// code is not actually Linux-specific, so these tests run and assert for real on any OS.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class FirecrackerClientTests : IDisposable
{
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath(), "acrouter-fcclient-test-" + Guid.NewGuid().ToString("N") + ".sock");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptyApiSocketPath()
    {
        Assert.Throws<ArgumentException>(() => new FirecrackerClient(string.Empty));
    }

    [Fact]
    public void Dispose_DoesNotThrowWithoutAnyRequestSent()
    {
        using var client = new FirecrackerClient(_socketPath);
    }

    [Fact]
    public void Factory_CreatesFirecrackerClient()
    {
        var factory = new FirecrackerClientFactory();

        using var client = factory.Create(_socketPath);

        Assert.IsType<FirecrackerClient>(client);
    }

    [Fact]
    public async Task ConfigureMachineAsync_SendsPutToMachineConfig()
    {
        using var server = StartHttpUdsServer(_socketPath, "204 No Content");

        using var client = new FirecrackerClient(_socketPath);
        await client.ConfigureMachineAsync("{\"vcpu_count\":2}", TestContext.Current.CancellationToken);

        var request = await server.RequestTask;
        Assert.StartsWith("PUT /machine-config HTTP/1.1", request);
        Assert.Contains("{\"vcpu_count\":2}", request);
    }

    [Fact]
    public async Task ConfigureVsockAsync_SendsPutToVsock()
    {
        using var server = StartHttpUdsServer(_socketPath, "204 No Content");

        using var client = new FirecrackerClient(_socketPath);
        await client.ConfigureVsockAsync("{\"guest_cid\":3}", TestContext.Current.CancellationToken);

        var request = await server.RequestTask;
        Assert.StartsWith("PUT /vsock HTTP/1.1", request);
    }

    [Fact]
    public async Task LoadSnapshotAsync_SendsPutToSnapshotLoad()
    {
        using var server = StartHttpUdsServer(_socketPath, "204 No Content");

        using var client = new FirecrackerClient(_socketPath);
        await client.LoadSnapshotAsync("{\"snapshot_path\":\"/x\"}", TestContext.Current.CancellationToken);

        var request = await server.RequestTask;
        Assert.StartsWith("PUT /snapshot/load HTTP/1.1", request);
    }

    [Fact]
    public async Task SetVmStateAsync_SendsPatchToVm()
    {
        using var server = StartHttpUdsServer(_socketPath, "204 No Content");

        using var client = new FirecrackerClient(_socketPath);
        await client.SetVmStateAsync("{\"state\":\"Resumed\"}", TestContext.Current.CancellationToken);

        var request = await server.RequestTask;
        Assert.StartsWith("PATCH /vm HTTP/1.1", request);
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_ThrowsHttpRequestException()
    {
        using var server = StartHttpUdsServer(_socketPath, "400 Bad Request");

        using var client = new FirecrackerClient(_socketPath);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ConfigureMachineAsync("{}", TestContext.Current.CancellationToken));

        await server.RequestTask;
    }

    /// <summary>
    /// Starts a background task that accepts exactly one connection on <paramref name="path"/>, reads the
    /// full HTTP request (headers + body, using Content-Length), and replies with a fixed status line and
    /// an empty body. Good enough for asserting what the client sent without needing a real HTTP stack.
    /// </summary>
    private static TestServer StartHttpUdsServer(string path, string statusLine)
    {
        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listenSocket.Bind(new UnixDomainSocketEndPoint(path));
        listenSocket.Listen(1);

        var requestTask = Task.Run(async () =>
        {
            using var server = await listenSocket.AcceptAsync();
            var request = await ReadHttpRequestAsync(server, TestContext.Current.CancellationToken);

            var response = $"HTTP/1.1 {statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await server.SendAsync(Encoding.ASCII.GetBytes(response), SocketFlags.None, TestContext.Current.CancellationToken);
            server.Shutdown(SocketShutdown.Send);

            return request;
        });

        return new TestServer(listenSocket, requestTask);
    }

    private static async Task<string> ReadHttpRequestAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        int headerEnd;
        int totalRead = 0;

        while (true)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(totalRead), SocketFlags.None, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            text.Clear();
            text.Append(Encoding.ASCII.GetString(buffer, 0, totalRead));
            headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            var headerText = text.ToString(0, headerEnd);
            var contentLength = 0;
            foreach (var line in headerText.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.Split(':')[1].Trim());
                }
            }

            var bodyReadSoFar = totalRead - (headerEnd + 4);
            while (bodyReadSoFar < contentLength)
            {
                var more = await socket.ReceiveAsync(buffer.AsMemory(totalRead), SocketFlags.None, cancellationToken);
                if (more == 0)
                {
                    break;
                }

                totalRead += more;
                bodyReadSoFar += more;
            }

            return Encoding.ASCII.GetString(buffer, 0, totalRead);
        }

        return text.ToString();
    }

    private sealed class TestServer(Socket listenSocket, Task<string> requestTask) : IDisposable
    {
        public Task<string> RequestTask { get; } = requestTask;

        public void Dispose() => listenSocket.Dispose();
    }
}

