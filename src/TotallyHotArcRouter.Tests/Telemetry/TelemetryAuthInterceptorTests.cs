using TotallyHot.ArcRouter.Telemetry;
using Grpc.Core;
using Grpc.Core.Testing;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryAuthInterceptor"/>: calls presenting a matching <c>x-admin-token</c> reach
/// the continuation, and calls with a missing/wrong token are rejected with
/// <see cref="StatusCode.Unauthenticated"/> before the continuation runs.
/// </summary>
public class TelemetryAuthInterceptorTests
{
    private const string TokenHeaderName = "x-admin-token";

    private static ServerCallContext CreateContext(Metadata requestHeaders) =>
        TestServerCallContext.Create(
            method: "StreamEvents",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: requestHeaders,
            cancellationToken: CancellationToken.None,
            peer: "test-peer",
            authContext: null!,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });

    [Fact]
    public void Constructor_NullOrWhitespaceToken_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TelemetryAuthInterceptor(""));
        Assert.Throws<ArgumentNullException>(() => new TelemetryAuthInterceptor(null!));
    }

    [Fact]
    public async Task UnaryServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = new TelemetryAuthInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(TokenHeaderName, "expected-token")]);
        var invoked = false;

        await interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult("response");
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task UnaryServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = new TelemetryAuthInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult("response");
            }));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task UnaryServerHandler_WrongToken_ThrowsUnauthenticated()
    {
        var interceptor = new TelemetryAuthInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(TokenHeaderName, "wrong-token")]);

        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "request",
            context,
            (_, _) => Task.FromResult("response")));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;
    }

    [Fact]
    public async Task ServerStreamingServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = new TelemetryAuthInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(TokenHeaderName, "expected-token")]);
        var invoked = false;

        await interceptor.ServerStreamingServerHandler<string, string>(
            "request",
            new FakeServerStreamWriter<string>(),
            context,
            (_, _, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ServerStreamingServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = new TelemetryAuthInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.ServerStreamingServerHandler<string, string>(
            "request",
            new FakeServerStreamWriter<string>(),
            context,
            (_, _, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            }));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.False(invoked);
    }
}
