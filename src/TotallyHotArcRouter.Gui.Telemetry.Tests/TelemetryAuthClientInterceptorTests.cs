using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Core.Testing;
using System.Text;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="TelemetryAuthClientInterceptor"/>: it attaches a supplied token to every call's
/// <c>x-admin-token</c> metadata entry, reads it from a token file when none is supplied, and leaves
/// calls untouched when no token is available anywhere.
/// </summary>
public class TelemetryAuthClientInterceptorTests
{
    private const string TokenHeaderName = "x-admin-token";

    private static readonly Method<string, string> TestMethod = new(
        type: MethodType.Unary,
        serviceName: "TestService",
        name: "TestMethod",
        requestMarshaller: Marshallers.Create(serializer: Encoding.UTF8.GetBytes,
            deserializer: Encoding.UTF8.GetString),
        responseMarshaller: Marshallers.Create(serializer: Encoding.UTF8.GetBytes,
            deserializer: Encoding.UTF8.GetString));

    private static ClientInterceptorContext<string, string> NewContext()
    {
        return new ClientInterceptorContext<string, string>(method: TestMethod, null, options: new CallOptions());
    }

    [Fact]
    public void Attaches_the_supplied_token_as_a_header()
    {
        var interceptor = new TelemetryAuthClientInterceptor(token: "my-token");
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.BlockingUnaryCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
        {
            captured = ctx;
            return "response";
        });

        captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("my-token");
    }

    [Fact]
    public void Reads_the_token_from_the_given_file_when_none_is_supplied()
    {
        var path = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path: path, contents: "file-token");
            var interceptor = new TelemetryAuthClientInterceptor(tokenPath: path);
            ClientInterceptorContext<string, string>? captured = null;

            interceptor.BlockingUnaryCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
            {
                captured = ctx;
                return "response";
            });

            captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("file-token");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Picks_up_a_token_file_created_after_construction()
    {
        var path = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid() + ".txt");
        try
        {
            var interceptor = new TelemetryAuthClientInterceptor(tokenPath: path);
            File.WriteAllText(path: path, contents: "late-token");
            ClientInterceptorContext<string, string>? captured = null;

            interceptor.BlockingUnaryCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
            {
                captured = ctx;
                return "response";
            });

            captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("late-token");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Leaves_the_call_untouched_when_no_token_file_exists()
    {
        var path = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid() + ".txt");
        var interceptor = new TelemetryAuthClientInterceptor(tokenPath: path);
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.BlockingUnaryCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
        {
            captured = ctx;
            return "response";
        });

        (captured!.Value.Options.Headers is null || captured.Value.Options.Headers.Get(TokenHeaderName) is null)
            .Should().BeTrue();
    }

    [Fact]
    public void AsyncClientStreamingCall_attaches_the_supplied_token_as_a_header()
    {
        var interceptor = new TelemetryAuthClientInterceptor(token: "my-token");
        ClientInterceptorContext<string, string>? captured = null;

        using var call = interceptor.AsyncClientStreamingCall(context: NewContext(), continuation: ctx =>
        {
            captured = ctx;
            return TestCalls.AsyncClientStreamingCall(
                requestStream: new FakeClientStreamWriter<string>(),
                responseAsync: Task.FromResult("response"),
                responseHeadersAsync: Task.FromResult(Metadata.Empty),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        });

        captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("my-token");
    }

    [Fact]
    public void AsyncDuplexStreamingCall_attaches_the_supplied_token_as_a_header()
    {
        var interceptor = new TelemetryAuthClientInterceptor(token: "my-token");
        ClientInterceptorContext<string, string>? captured = null;

        using var call = interceptor.AsyncDuplexStreamingCall(context: NewContext(), continuation: ctx =>
        {
            captured = ctx;
            return TestCalls.AsyncDuplexStreamingCall(
                requestStream: new FakeClientStreamWriter<string>(),
                responseStream: new FakeAsyncStreamReader<string>(),
                responseHeadersAsync: Task.FromResult(Metadata.Empty),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        });

        captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("my-token");
    }

    private sealed class FakeClientStreamWriter<T> : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync()
        {
            return Task.CompletedTask;
        }

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