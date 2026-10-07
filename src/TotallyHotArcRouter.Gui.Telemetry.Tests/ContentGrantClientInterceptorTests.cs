using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Text;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="ContentGrantClientInterceptor"/> (ADR-0020): it attaches the dashboard's in-memory grant
/// as <c>x-content-grant</c> metadata on every call shape, leaves a locked call untouched, strips a stale
/// header rather than forwarding it, and re-reads the grant on every call.
/// </summary>
public class ContentGrantClientInterceptorTests
{
    private const string HeaderName = "x-content-grant";

    private static readonly Method<string, string> TestMethod = new(
        type: MethodType.Unary,
        serviceName: "TestService",
        name: "TestMethod",
        requestMarshaller: Marshallers.Create(serializer: Encoding.UTF8.GetBytes,
            deserializer: Encoding.UTF8.GetString),
        responseMarshaller: Marshallers.Create(serializer: Encoding.UTF8.GetBytes,
            deserializer: Encoding.UTF8.GetString));

    private static ClientInterceptorContext<string, string> NewContext(CallOptions? options = null)
    {
        return new ClientInterceptorContext<string, string>(method: TestMethod, null, options: options ?? new CallOptions());
    }

    private static ClientInterceptorContext<string, string> Capture(
        ContentGrantClientInterceptor interceptor, CallOptions? options = null)
    {
        ClientInterceptorContext<string, string>? captured = null;
        interceptor.BlockingUnaryCall(request: "request", context: NewContext(options), continuation: (_, ctx) =>
        {
            captured = ctx;
            return "response";
        });
        return captured!.Value;
    }

    [Fact]
    public void Attaches_the_grant_as_the_x_content_grant_header()
    {
        var interceptor = new ContentGrantClientInterceptor(() => "grant-abc");

        var context = Capture(interceptor);

        context.Options.Headers!.Get(HeaderName)!.Value.Should().Be("grant-abc");
    }

    [Fact]
    public void Leaves_the_call_untouched_when_no_grant_is_held()
    {
        var interceptor = new ContentGrantClientInterceptor(() => null);

        var context = Capture(interceptor);

        context.Options.Headers.Should().BeNull();
    }

    [Fact]
    public void Treats_an_empty_grant_as_none()
    {
        var interceptor = new ContentGrantClientInterceptor(() => string.Empty);

        Capture(interceptor).Options.Headers.Should().BeNull();
    }

    [Fact]
    public void Re_reads_the_grant_on_every_call_so_unlock_and_lock_take_effect_without_rebuilding_the_channel()
    {
        string? grant = null;
        var interceptor = new ContentGrantClientInterceptor(() => grant);

        Capture(interceptor).Options.Headers.Should().BeNull();

        grant = "unlocked";
        Capture(interceptor).Options.Headers!.Get(HeaderName)!.Value.Should().Be("unlocked");

        grant = null;
        Capture(interceptor).Options.Headers.Should().BeNull();
    }

    [Fact]
    public void Keeps_the_callers_other_headers_and_replaces_a_stale_grant_header()
    {
        var interceptor = new ContentGrantClientInterceptor(() => "fresh");
        var options = new CallOptions(headers: new Metadata { { "x-other", "kept" }, { HeaderName, "stale" } });

        var headers = Capture(interceptor, options).Options.Headers!;

        headers.Get("x-other")!.Value.Should().Be("kept");
        headers.Where(e => e.Key == HeaderName).Should().ContainSingle().Which.Value.Should().Be("fresh");
    }

    [Fact]
    public void Strips_a_stale_grant_header_when_locked_instead_of_forwarding_it()
    {
        var interceptor = new ContentGrantClientInterceptor(() => null);
        var options = new CallOptions(headers: new Metadata { { HeaderName, "stale" }, { "x-other", "kept" } });

        var headers = Capture(interceptor, options).Options.Headers!;

        headers.Get(HeaderName).Should().BeNull();
        headers.Get("x-other")!.Value.Should().Be("kept");
    }

    [Fact]
    public void Attaches_the_grant_to_server_streaming_calls_such_as_StreamEvents()
    {
        var interceptor = new ContentGrantClientInterceptor(() => "stream-grant");
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.AsyncServerStreamingCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
        {
            captured = ctx;
            return null!;
        });

        captured!.Value.Options.Headers!.Get(HeaderName)!.Value.Should().Be("stream-grant");
    }

    [Fact]
    public void Attaches_the_grant_to_async_unary_calls()
    {
        var interceptor = new ContentGrantClientInterceptor(() => "unary-grant");
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.AsyncUnaryCall(request: "request", context: NewContext(), continuation: (_, ctx) =>
        {
            captured = ctx;
            return new AsyncUnaryCall<string>(
                responseAsync: Task.FromResult("response"),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        });

        captured!.Value.Options.Headers!.Get(HeaderName)!.Value.Should().Be("unary-grant");
    }

    [Fact]
    public void Rejects_a_null_accessor()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentGrantClientInterceptor(null!));
    }
}
