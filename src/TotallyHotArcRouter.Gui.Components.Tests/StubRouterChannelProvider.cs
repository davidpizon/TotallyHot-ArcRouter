using Grpc.Core;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// An <see cref="IRouterChannelProvider"/> whose <see cref="CallInvoker"/> fails every RPC as
/// <see cref="StatusCode.Unavailable"/>. Replaces the retired MAUI-era
/// production channel provider in GUI tests that only need the "router is not running" path, without
/// opening a real TCP+TLS channel.
/// </summary>
internal sealed class StubRouterChannelProvider : IRouterChannelProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StubRouterChannelProvider"/> class that reports
    /// <paramref name="serverAddress"/> and fails every call as unavailable.
    /// </summary>
    /// <param name="serverAddress">The address stores should report in their unreachable state.</param>
    public StubRouterChannelProvider(string serverAddress)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ServerAddress = serverAddress;
        CallInvoker = new UnavailableCallInvoker();
    }

    /// <inheritdoc/>
    public CallInvoker CallInvoker { get; }

    /// <inheritdoc/>
    public string ServerAddress { get; }

    private sealed class UnavailableCallInvoker : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request)
        {
            throw Unavailable();
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            return new AsyncUnaryCall<TResponse>(
                responseAsync: Task.FromException<TResponse>(Unavailable()),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => new Status(StatusCode.Unavailable, "No listener (test stub)."),
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            return new AsyncServerStreamingCall<TResponse>(
                responseStream: new UnavailableStreamReader<TResponse>(),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => new Status(StatusCode.Unavailable, "No listener (test stub)."),
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            throw Unavailable();
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            throw Unavailable();
        }

        private static RpcException Unavailable()
        {
            return new RpcException(new Status(StatusCode.Unavailable, "No listener (test stub)."));
        }
    }

    private sealed class UnavailableStreamReader<T> : IAsyncStreamReader<T>
        where T : class
    {
        public T Current => throw new InvalidOperationException("No stream response; the stub never connects.");

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<bool>(cancellationToken);

            return Task.FromException<bool>(
                new RpcException(new Status(StatusCode.Unavailable, "No listener (test stub).")));
        }
    }
}
