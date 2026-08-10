using System.Text;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Grpc.Core;
using Grpc.Core.Interceptors;
using FluentAssertions;

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
        MethodType.Unary,
        "TestService",
        "TestMethod",
        Marshallers.Create<string>(s => Encoding.UTF8.GetBytes(s), bytes => Encoding.UTF8.GetString(bytes)),
        Marshallers.Create<string>(s => Encoding.UTF8.GetBytes(s), bytes => Encoding.UTF8.GetString(bytes)));

    private static ClientInterceptorContext<string, string> NewContext() =>
        new(TestMethod, host: null, new CallOptions());

    [Fact]
    public void Attaches_the_supplied_token_as_a_header()
    {
        var interceptor = new TelemetryAuthClientInterceptor(token: "my-token");
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.BlockingUnaryCall("request", NewContext(), (_, ctx) =>
        {
            captured = ctx;
            return "response";
        });

        captured!.Value.Options.Headers!.Get(TokenHeaderName)!.Value.Should().Be("my-token");
    }

    [Fact]
    public void Reads_the_token_from_the_given_file_when_none_is_supplied()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, "file-token");
            var interceptor = new TelemetryAuthClientInterceptor(tokenPath: path);
            ClientInterceptorContext<string, string>? captured = null;

            interceptor.BlockingUnaryCall("request", NewContext(), (_, ctx) =>
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
    public void Leaves_the_call_untouched_when_no_token_file_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var interceptor = new TelemetryAuthClientInterceptor(tokenPath: path);
        ClientInterceptorContext<string, string>? captured = null;

        interceptor.BlockingUnaryCall("request", NewContext(), (_, ctx) =>
        {
            captured = ctx;
            return "response";
        });

        (captured!.Value.Options.Headers is null || captured.Value.Options.Headers.Get(TokenHeaderName) is null)
            .Should().BeTrue();
    }
}
