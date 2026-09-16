using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Proxy.Auth;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="TelemetryAuthInterceptor"/>: calls presenting a matching <c>x-admin-token</c> reach
/// the continuation, and calls with a missing/wrong token are rejected with
/// <see cref="StatusCode.Unauthenticated"/> before the continuation runs.
/// </summary>
public class TelemetryAuthInterceptorTests
{
    private const string TokenHeaderName = "x-admin-token";

    private static ServerCallContext CreateContext(Metadata requestHeaders)
    {
        return TestServerCallContext.Create(
            method: "StreamEvents",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: requestHeaders,
            cancellationToken: CancellationToken.None,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    private static TelemetryAuthInterceptor CreateInterceptor(string expectedToken)
    {
        var tokenProvider = new FakeManagementTokenProvider(expectedToken);
        return new TelemetryAuthInterceptor(tokenProvider, new ManagementSessionTicketService(tokenProvider));
    }

    [Fact]
    public void Constructor_NullArguments_Throws()
    {
        var tokenProvider = new FakeManagementTokenProvider("token");
        var sessionTickets = new ManagementSessionTicketService(tokenProvider);

        Assert.Throws<ArgumentNullException>(() => new TelemetryAuthInterceptor(null!, sessionTickets));
        Assert.Throws<ArgumentNullException>(() => new TelemetryAuthInterceptor(tokenProvider, null!));
    }

    // The cookie-acceptance path (falling back to the ADR-0012 session ticket when no/wrong
    // x-admin-token is presented) needs a real HttpContext behind the call - TestServerCallContext
    // (Grpc.Core.Testing) has no supported way to attach one, unlike production's
    // Grpc.AspNetCore.Server pipeline. See ProxyServerWebInterfaceTests for that path exercised over a
    // real Kestrel host instead, consistent with this codebase's established pattern of preferring a
    // real network test over reasoning about an internal implementation detail.

    [Fact]
    public async Task UnaryServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(key: TokenHeaderName, value: "expected-token")]);
        var invoked = false;

        await interceptor.UnaryServerHandler<string, string>(
            request: "request",
            context: context,
            continuation: (_, _) =>
            {
                invoked = true;
                return Task.FromResult("response");
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task UnaryServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            request: "request",
            context: context,
            continuation: (_, _) =>
            {
                invoked = true;
                return Task.FromResult("response");
            }));

        Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task UnaryServerHandler_WrongToken_ThrowsUnauthenticated()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(key: TokenHeaderName, value: "wrong-token")]);

        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            request: "request",
            context: context,
            continuation: (_, _) => Task.FromResult("response")));

        Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
    }

    [Fact]
    public async Task ServerStreamingServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(key: TokenHeaderName, value: "expected-token")]);
        var invoked = false;

        await interceptor.ServerStreamingServerHandler<string, string>(
            request: "request",
            responseStream: new FakeServerStreamWriter<string>(),
            context: context,
            continuation: (_, _, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ServerStreamingServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.ServerStreamingServerHandler<string, string>(
                request: "request",
                responseStream: new FakeServerStreamWriter<string>(),
                context: context,
                continuation: (_, _, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                }));

        Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ClientStreamingServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(key: TokenHeaderName, value: "expected-token")]);
        var invoked = false;

        await interceptor.ClientStreamingServerHandler<string, string>(
            requestStream: new FakeAsyncStreamReader<string>(),
            context: context,
            continuation: (_, _) =>
            {
                invoked = true;
                return Task.FromResult("response");
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ClientStreamingServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.ClientStreamingServerHandler<string, string>(
                requestStream: new FakeAsyncStreamReader<string>(),
                context: context,
                continuation: (_, _) =>
                {
                    invoked = true;
                    return Task.FromResult("response");
                }));

        Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task DuplexStreamingServerHandler_MatchingToken_InvokesContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([new Metadata.Entry(key: TokenHeaderName, value: "expected-token")]);
        var invoked = false;

        await interceptor.DuplexStreamingServerHandler(
            requestStream: new FakeAsyncStreamReader<string>(),
            responseStream: new FakeServerStreamWriter<string>(),
            context: context,
            continuation: (_, _, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            });

        Assert.True(invoked);
    }

    [Fact]
    public async Task DuplexStreamingServerHandler_MissingToken_ThrowsUnauthenticatedAndSkipsContinuation()
    {
        var interceptor = CreateInterceptor("expected-token");
        var context = CreateContext([]);
        var invoked = false;

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.DuplexStreamingServerHandler(
                requestStream: new FakeAsyncStreamReader<string>(),
                responseStream: new FakeServerStreamWriter<string>(),
                context: context,
                continuation: (_, _, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                }));

        Assert.Equal(expected: StatusCode.Unauthenticated, actual: exception.StatusCode);
        Assert.False(invoked);
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }
}