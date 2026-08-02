using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// A minimal client for the Firecracker REST API, which is served over a Unix domain socket. Used to load a
/// snapshot and resume/pause the guest.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class FirecrackerClient : IFirecrackerClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="FirecrackerClient"/> class.</summary>
    /// <param name="apiSocketPath">The path of the Firecracker API Unix socket.</param>
    public FirecrackerClient(string apiSocketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiSocketPath);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(apiSocketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        _httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    /// <inheritdoc />
    public Task LoadSnapshotAsync(string payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/snapshot/load", payload, cancellationToken);

    /// <inheritdoc />
    public Task ConfigureMachineAsync(string payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/machine-config", payload, cancellationToken);

    /// <inheritdoc />
    public Task ConfigureVsockAsync(string payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, "/vsock", payload, cancellationToken);

    /// <inheritdoc />
    public Task SetVmStateAsync(string payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, "/vm", payload, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    /// <summary>Sends a JSON payload to the Firecracker API over the Unix domain socket and throws if the response is not successful.</summary>
    private async Task SendAsync(HttpMethod method, string path, string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Default <see cref="IFirecrackerClientFactory"/>, creating a real socket-backed <see cref="FirecrackerClient"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class FirecrackerClientFactory : IFirecrackerClientFactory
{
    /// <inheritdoc />
    public IFirecrackerClient Create(string apiSocketPath) => new FirecrackerClient(apiSocketPath);
}

