using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers the infrastructure-level outage cascade: on a genuine upstream outage (unreachable, timeout,
/// 5xx, 429, 404, or a cross-provider 401/403/405) for the requested model, <see cref="ProxyMiddleware"/>
/// retries the request against another currently-configured model - dynamically ranked, not a
/// hand-authored per-model list - per <c>docs/router/agent-resilience-strategies.md</c>'s Circuit Breaker
/// (which replaced the old static <c>ModelRouteEntry.Fallbacks</c>). A same-provider 401/403/405 does NOT
/// retry (the backup would share the same broken credential/gateway); a true terminal client-fault status
/// (400/422) or a client abort never retries either way. This is distinct from the paper's
/// Verifier-driven <em>semantic</em> re-routing (the separate, already-built verifier/RouterMemory
/// loop); these tests exercise only the transport-outage cascade within a single request. See
/// <c>CircuitBreakerTests</c> for the circuit-breaker state machine itself (trip thresholds, exponential
/// cooldown, half-open probes) and its cross-request bypass behavior.
/// </summary>
public class ProxyMiddlewareFallbackTests
{
    private const string PrimaryHost = "primary.test";
    private const string BackupHost = "backup.test";

    [Fact]
    public async Task InvokeAsync_PrimarySucceeds_DoesNotUseFallback()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return Ok($"served-by-{request.RequestUri!.Host}");
        });

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver: resolver, handler: handler, telemetryPublisher: capturing,
            requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
        Assert.Equal(expected: "served-by-" + PrimaryHost, actual: await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.False(telemetry.IsFallback);
        Assert.Equal(expected: "primary", actual: telemetry.RequestedModel);
        Assert.Equal(expected: "prov-a", actual: telemetry.Provider);
    }

    [Fact]
    public async Task InvokeAsync_PrimaryConnectionRefused_FailsOverToBackup()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? throw new HttpRequestException("connection refused")
            : Ok("served-by-backup"));

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver: resolver, handler: handler, telemetryPublisher: capturing,
            requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.True(telemetry.IsFallback);
        // The client still asked for "primary"; the backup provider actually served it.
        Assert.Equal(expected: "primary", actual: telemetry.RequestedModel);
        Assert.Equal(expected: "prov-b", actual: telemetry.Provider);
        Assert.Equal(expected: "backup-upstream", actual: telemetry.ResolvedModel);
        // M2.3/M2.2: RoutedModel is "backup" (the model that served), and the transport-level failover
        // reports RoutingSubstitutionReason.Failover regardless of the resolution-time reason.
        Assert.Equal(expected: "backup", actual: telemetry.RoutedModel);
        Assert.Equal(expected: RoutingSubstitutionReason.Failover, actual: telemetry.SubstitutionReason);
        Assert.Equal(expected: "primary", actual: context.Response.Headers["X-ArcRouter-Requested-Model"].ToString());
        Assert.Equal(expected: "backup", actual: context.Response.Headers["X-ArcRouter-Routed-Model"].ToString());
        Assert.Equal(expected: RoutingSubstitutionReason.Failover.ToString(),
            actual: context.Response.Headers["X-ArcRouter-Substitution-Reason"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_Primary5xx_FailsOverToBackup()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.ServiceUnavailable)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_Primary429_DifferentProviderBackup_FailsOver()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status((HttpStatusCode)429)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_Primary429_SameProviderBackup_DoesNotFailOver()
    {
        // Both models live on the same provider (a shared quota pool), so a 429 must NOT fail over - the
        // backup would be throttled identically. The client sees the 429.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            // Same host for both; distinguish by the rewritten upstream model id in the body.
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status((HttpStatusCode)429);
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(429, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_AutoSelectedPrimary401_DifferentProviderBackup_FailsOver()
    {
        // docs/adr/0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-
        // trip.md: cross-provider failover on a provider-wide-trip status (401/403/405) is now reserved
        // for auto-selected/already-substituted requests - see
        // InvokeAsync_ExplicitPrimary401_DifferentProviderBackup_RelaysTheTruthInstead below for the
        // (changed) explicit-selection behavior this test used to assert.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.Unauthorized)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "auto",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimary401_DifferentProviderBackup_RelaysTheTruthInstead()
    {
        // docs/adr/0005 (expanded scope): an explicit selection is never silently substituted away from a
        // provider-wide trip discovered live, even when a different-provider backup exists - it sees the
        // real 401 instead.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost
                ? Status(HttpStatusCode.Unauthorized)
                : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(401, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary401_SameProviderBackup_DoesNotFailOver()
    {
        // Both models live on the same provider (the same credential), so a 401 must NOT fail over - the
        // backup would be rejected with the identical invalid/expired credential. The client sees the 401.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.Unauthorized);
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(401, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task
        InvokeAsync_AutoSelectedPrimaryGemini400EmbeddedUnauthenticated_DifferentProviderBackup_FailsOver()
    {
        // Gemini reports an invalid/expired API key as 400 (not 401), since the key travels as a "key="
        // query parameter rather than an Authorization header - with an embedded
        // {"status": "UNAUTHENTICATED"} error rather than a normal HTTP status signal. That must be
        // treated exactly like a real 401: fail over to a different-provider backup instead of surfacing
        // the 400 to the client (see the generic InvokeAsync_ClientFaultStatus_DoesNotFailOver theory
        // below for the case where a 400 is *not* Gemini's disguised-401 shape and correctly stays terminal).
        // docs/adr/0005 (expanded scope): this cross-provider failover is now reserved for auto-selected/
        // already-substituted requests - see
        // InvokeAsync_ExplicitPrimaryGemini400EmbeddedUnauthenticated_RelaysTheTruthInstead below.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "gemini", "gemini-2.5-pro", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? GeminiEmbeddedAuthErrorResponse()
            : Ok("served-by-backup"));

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimaryGemini400EmbeddedUnauthenticated_RelaysTheTruthInstead()
    {
        // docs/adr/0005 (expanded scope): an explicit selection sees Gemini's real disguised-401 instead
        // of being silently substituted to a different-provider backup.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "gemini", "gemini-2.5-pro", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost ? GeminiEmbeddedAuthErrorResponse() : Ok("served-by-backup");
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Contains(
            expectedSubstring: "API key not valid",
            actualString: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task
        InvokeAsync_PrimaryGemini400EmbeddedUnauthenticated_SameProviderBackup_DoesNotFailOver_AndForwardsRealMessage()
    {
        // Both models are on "gemini" (the same credential), so this must NOT fail over - the backup
        // would be rejected with the identical invalid key. The client still sees the 400, but with
        // Gemini's actual error message forwarded, rather than swallowed or mangled into a bogus empty
        // completion by TranslateResponse/the stream translator (both of which assume a success shape).
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "gemini", "gemini-2.5-pro", $"https://{PrimaryHost}"),
            ("backup", "gemini", "gemini-2.5-flash", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "gemini-2.5-flash", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return GeminiEmbeddedAuthErrorResponse();
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Contains(
            expectedSubstring: "API key not valid",
            actualString: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvokeAsync_PrimaryGemini400WithoutEmbeddedError_ForwardsRawBodyUnchanged()
    {
        // A Gemini 400 whose body has no recognizable {"error":{...}} shape (TryExtractEmbeddedError
        // returns false) - same provider on both sides so this can't fail over either way. The raw upstream
        // body must reach the client unchanged, per TryExtractEmbeddedError's documented contract, rather
        // than being replaced with a synthetic generic "Gemini returned a 400 Bad Request." message that
        // would lose whatever Gemini actually said.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "gemini", "gemini-2.5-pro", $"https://{PrimaryHost}"),
            ("backup", "gemini", "gemini-2.5-flash", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator()
        };

        const string rawBody = """{"reason":"invalid_argument","details":"request body too large"}""";

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "gemini-2.5-flash", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(content: rawBody, encoding: Encoding.UTF8, mediaType: "application/json")
            };
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
        Assert.Equal(expected: rawBody, actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_PrimaryAnthropic400EmbeddedError_ForwardsRealMessage()
    {
        // Anthropic's native error shape ({"type":"error","error":{"type":...,"message":...}}) has none of
        // the fields TranslateResponse expects (id/model/content/stop_reason), so running it through the
        // translator would null-coalesce those into a bogus empty completion (model:"", content:"",
        // finish_reason:"stop") that silently discards the real rejection reason - see
        // AnthropicPayloadTranslator.TryExtractEmbeddedError's doc comment. Same provider on both sides so
        // this can't fail over either way; the client must still see the actual Anthropic error message.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("backup", "anthropic", "claude-opus-4-7", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "claude-opus-4-7", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return AnthropicEmbeddedErrorResponse();
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(
            expected: "messages: at least one message is required",
            actual: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvokeAsync_PrimaryAnthropic400WithoutEmbeddedError_ForwardsRawBodyUnchanged()
    {
        // An Anthropic 400 whose body has no recognizable {"type":"error","error":{...}} shape
        // (TryExtractEmbeddedError returns false) - the raw upstream body must reach the client unchanged
        // rather than being mangled into a bogus empty completion by TranslateResponse.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("backup", "anthropic", "claude-opus-4-7", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        const string rawBody = """{"reason":"unrecognized_shape"}""";

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "claude-opus-4-7", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(content: rawBody, encoding: Encoding.UTF8, mediaType: "application/json")
            };
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
        Assert.Equal(expected: rawBody, actual: await ReadBodyAsync(context));
    }

    // ----- Out-of-credits (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md) -----

    [Fact]
    public async Task InvokeAsync_AutoSelectedPrimaryAnthropicOutOfCredits_DifferentProviderBackup_FailsOver()
    {
        var circuitBreaker = new CircuitBreaker();
        var interactionStatus = new ProviderInteractionStatusStore();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? AnthropicOutOfCreditsResponse()
            : Ok("served-by-backup"));

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                CircuitBreaker = circuitBreaker,
                InteractionStatusStore = interactionStatus
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
        Assert.True(circuitBreaker.IsProviderOpen("anthropic"));
        var liveTraffic = interactionStatus.GetLiveTraffic("anthropic");
        Assert.NotNull(liveTraffic);
        Assert.False(liveTraffic.Ok);
        Assert.Equal(expected: ProviderInteractionKind.OutOfCredits, actual: liveTraffic.Kind);
        Assert.Contains(expectedSubstring: "credit balance", actualString: liveTraffic.Message);
    }

    [Fact]
    public async Task InvokeAsync_PrimaryAnthropicOutOfCredits_SameProviderBackup_DoesNotFailOver()
    {
        // Both models share "anthropic" (the same billing account), so an auto-selected request must not
        // fail over - the backup would be out of credits identically.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("backup", "anthropic", "claude-opus-4-7", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "claude-opus-4-7", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return AnthropicOutOfCreditsResponse();
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Contains(
            expectedSubstring: "credit balance",
            actualString: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimaryAnthropicOutOfCredits_DoesNotFailOver_RelaysTheRealMessage()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost ? AnthropicOutOfCreditsResponse() : Ok("served-by-backup");
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators
            }
        );

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Contains(
            expectedSubstring: "credit balance",
            actualString: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task
        InvokeAsync_AnthropicOutOfCredits_TripsWholeProvider_SubsequentSameProviderRequestBypassesWithoutNetworkCall()
    {
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "anthropic", "claude-sonnet-4-6", $"https://{PrimaryHost}"),
            ("sibling", "anthropic", "claude-opus-4-7", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new AnthropicPayloadTranslator()
        };

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "claude-opus-4-7", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost ? Ok("served-by-backup") : AnthropicOutOfCreditsResponse();
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = translators,
                CircuitBreaker = circuitBreaker
            }
        );

        // Auto-selected first request: out-of-credits discovered live, fails over cross-provider.
        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");
        Assert.Equal(expected: StatusCodes.Status200OK, actual: firstContext.Response.StatusCode);
        Assert.True(circuitBreaker.IsProviderOpen("anthropic"));

        // A second, explicit request for "sibling" (same now-open provider) is never attempted at all.
        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "sibling");
        Assert.Equal(503, actual: secondContext.Response.StatusCode);
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task InvokeAsync_AutoSelectedPrimaryOpenAiInsufficientQuota_DifferentProviderBackup_FailsOver()
    {
        // OpenAI-compatible providers are untranslated (translator is null) - the typed insufficient_quota
        // error code, usually on a 429, is classified without any provider-specific extraction.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "gpt-5.4", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? OpenAiInsufficientQuotaResponse()
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "auto",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimaryOpenAiInsufficientQuota_DoesNotFailOver_RelaysTheRawBody()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "openai", "gpt-5.4", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost ? OpenAiInsufficientQuotaResponse() : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(429, actual: context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(expected: "insufficient_quota",
            actual: responseJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvokeAsync_Primary429_SameProviderBackup_NotClassifiedAsOutOfCredits_StillDoesNotFailOver()
    {
        // A plain rate-limit 429 (no insufficient_quota code, no billing keywords) must not be
        // misclassified as out-of-credits - it stays a target-level failure, same-provider backup still
        // shares the throttle either way.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent(
                    """{"error":{"message":"Rate limit exceeded.","type":"rate_limit_error"}}""",
                    encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(429, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_AutoSelectedPrimary405_DifferentProviderBackup_FailsOver()
    {
        // A 405 on a path ArcRouter itself constructs (the client never chooses the method) is treated like
        // a 401: a provider-side gateway/WAF block, not a genuine client-fault status - so it fails over to
        // a different-provider backup instead of being surfaced to the client immediately.
        // docs/adr/0005 (expanded scope): reserved for auto-selected/already-substituted requests - see
        // InvokeAsync_ExplicitPrimary405_DifferentProviderBackup_RelaysTheTruthInstead below.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.MethodNotAllowed)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "auto",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimary405_DifferentProviderBackup_RelaysTheTruthInstead()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost
                ? Status(HttpStatusCode.MethodNotAllowed)
                : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(405, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary405_SameProviderBackup_DoesNotFailOver()
    {
        // Both models live on the same provider (the same gateway/WAF policy), so a 405 must NOT fail over -
        // the backup would be blocked identically. The client sees the 405 as fast, direct feedback rather
        // than waiting out a cascade that can't succeed.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.MethodNotAllowed);
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(405, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_AutoSelectedPrimary403_DifferentProviderBackup_FailsOver()
    {
        // A 403 is treated like 401: almost always a permission/API-key-scope problem (API not enabled for
        // this key, region lock, tier restriction) rather than something specific to the one model
        // requested - so it fails over to a different-provider backup instead of being surfaced to the
        // client immediately.
        // docs/adr/0005 (expanded scope): reserved for auto-selected/already-substituted requests - see
        // InvokeAsync_ExplicitPrimary403_DifferentProviderBackup_RelaysTheTruthInstead below.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.Forbidden)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "auto",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_ExplicitPrimary403_DifferentProviderBackup_RelaysTheTruthInstead()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost ? Status(HttpStatusCode.Forbidden) : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(403, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary403_SameProviderBackup_DoesNotFailOver()
    {
        // Both models live on the same provider (the same credential/permission scope), so a 403 must NOT
        // fail over - the backup would be rejected with the identical permission problem. The client sees
        // the 403.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.Forbidden);
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(403, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task
        InvokeAsync_ExplicitPrimary403_TripsWholeProvider_SubsequentExplicitRequestRelaysTheTruthWithoutNetworkCall()
    {
        // A 403 trips every model on the provider at once (RecordProviderFailure) - a permission/API-key-
        // scope problem would reject any model on that provider identically, not just the one that
        // surfaced it. docs/adr/0005 (expanded scope): both requests here are explicit, so neither is
        // silently substituted - the first relays the real 403 it discovered live, and the second (for a
        // sibling model on the now-open provider) is blocked with a synthesized message and never attempted.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var backupAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "sibling-upstream", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            if (request.RequestUri!.Host == BackupHost) backupAttempted = true;

            return request.RequestUri!.Host == BackupHost ? Ok("served-by-backup") : Status(HttpStatusCode.Forbidden);
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        // First request against explicit "primary": 403 discovered live, relayed unchanged - no failover.
        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
        Assert.Equal(403, actual: firstContext.Response.StatusCode);
        Assert.False(backupAttempted);
        Assert.True(circuitBreaker.IsProviderOpen("prov-a"));

        // A second, explicit request for "sibling" (same provider as "primary", never itself failed) is
        // never attempted at all - the provider-wide circuit blocks it outright, and the client is told
        // why rather than silently routed to "backup".
        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "sibling");
        Assert.Equal(503, actual: secondContext.Response.StatusCode);
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task InvokeAsync_Primary404_DifferentProviderBackup_FailsOver()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.NotFound)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_Primary404_SameProviderBackup_FailsOver()
    {
        // Unlike 401/429 (a same-provider backup shares the same broken credential/quota - see
        // InvokeAsync_Primary401_SameProviderBackup_DoesNotFailOver /
        // InvokeAsync_Primary429_SameProviderBackup_DoesNotFailOver), a 404 says nothing about a sibling
        // model on the same provider - a wrong model id on "primary" doesn't mean "backup"'s id is also
        // wrong. Failover must happen even when the only remaining candidate shares the provider.
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "shared-prov", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "shared-prov", "backup-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "backup-upstream", comparisonType: StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.NotFound);
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
        Assert.True(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Repeated404_TripsPerTargetCircuit_NotProviderWide()
    {
        // Unlike 401 (provider-wide - see InvokeAsync_Primary401_TripsWholeProvider_...), a 404 means only
        // this target's configured model id is wrong; it must trip just that target's circuit, never the
        // whole provider's.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var primaryAttempts = 0;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost)
            {
                primaryAttempts++;
                return Status(HttpStatusCode.NotFound);
            }

            return Ok("served-by-backup");
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        // Default FailureThreshold is 3: the first three requests each attempt (and 404 on) "primary"
        // before failing over to the backup within the same request.
        for (var i = 0; i < 3; i++)
        {
            var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
            Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
            Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(context));
        }

        Assert.Equal(3, actual: primaryAttempts);
        Assert.True(circuitBreaker.IsOpen(new CircuitBreakerTargetKey(Provider: "prov-a",
            BaseUrl: $"https://{PrimaryHost}/", ProviderModelId: "primary-upstream")));
        Assert.False(circuitBreaker.IsProviderOpen("prov-a"));

        // A fourth, explicit request: the primary target's own circuit is now open, so - docs/adr/0005
        // (expanded scope) - it is never silently substituted, even though "prov-a" itself is never marked
        // unhealthy (unlike 401's provider-wide trip). It is blocked with a synthesized per-model message
        // and never attempted, rather than silently routed to "backup".
        var finalContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
        Assert.Equal(503, actual: finalContext.Response.StatusCode);
        Assert.Equal(3, actual: primaryAttempts); // unchanged - the 4th request never touched the primary target again
    }

    [Fact]
    public async Task
        InvokeAsync_AutoSelectedPrimary401_TripsWholeProvider_SubsequentAutoSelectedRequestBypassesADifferentModel_OnSameProvider()
    {
        // A 401 trips every model on the provider at once (RecordProviderFailure), not just the one that
        // surfaced it - a shared, real CircuitBreaker (not each class's own independent default instance)
        // is required for this to be visible across requests, matching production DI wiring. Both requests
        // here are auto-selected ("auto"), which still silently substitutes on a provider-wide trip - see
        // InvokeAsync_ExplicitPrimary401_TripsWholeProvider_SubsequentExplicitRequestRelaysTheTruthWithoutNetworkCall
        // below for the (changed) explicit-selection behavior this test used to assert.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "sibling-upstream", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost
                ? Ok("served-by-backup")
                : Status(HttpStatusCode.Unauthorized);
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        // First auto-selected request: 401, provider-wide trip, fails over cross-provider to "backup".
        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");
        Assert.Equal(expected: StatusCodes.Status200OK, actual: firstContext.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(firstContext));

        // A second auto-selected request must never attempt any model on the now-open provider at all -
        // the provider-wide circuit bypasses it outright.
        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");
        Assert.Equal(expected: StatusCodes.Status200OK, actual: secondContext.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(secondContext));
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task
        InvokeAsync_ExplicitPrimary401_TripsWholeProvider_SubsequentExplicitRequestRelaysTheTruthWithoutNetworkCall()
    {
        // docs/adr/0005 (expanded scope): both requests here are explicit, so neither is silently
        // substituted - the first relays the real 401 it discovered live, and the second (for a sibling
        // model on the now-open provider) is blocked with a synthesized message and never attempted.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var backupAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "sibling-upstream", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            if (request.RequestUri!.Host == BackupHost) backupAttempted = true;

            return request.RequestUri!.Host == BackupHost
                ? Ok("served-by-backup")
                : Status(HttpStatusCode.Unauthorized);
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
        Assert.Equal(401, actual: firstContext.Response.StatusCode);
        Assert.False(backupAttempted);
        Assert.True(circuitBreaker.IsProviderOpen("prov-a"));

        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "sibling");
        Assert.Equal(503, actual: secondContext.Response.StatusCode);
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task
        InvokeAsync_AutoSelectedPrimary405_TripsWholeProvider_SubsequentAutoSelectedRequestBypassesADifferentModel_OnSameProvider()
    {
        // Like 401 above, a 405 trips every model on the provider at once (RecordProviderFailure) - a
        // gateway/WAF block at the edge would reject any model on that provider identically, not just the
        // one that surfaced it. Both requests here are auto-selected - see
        // InvokeAsync_ExplicitPrimary405_TripsWholeProvider_SubsequentExplicitRequestRelaysTheTruthWithoutNetworkCall
        // below for the explicit-selection behavior.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "sibling-upstream", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost
                ? Ok("served-by-backup")
                : Status(HttpStatusCode.MethodNotAllowed);
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");
        Assert.Equal(expected: StatusCodes.Status200OK, actual: firstContext.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(firstContext));

        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "auto");
        Assert.Equal(expected: StatusCodes.Status200OK, actual: secondContext.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup", actual: await ReadBodyAsync(secondContext));
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task
        InvokeAsync_ExplicitPrimary405_TripsWholeProvider_SubsequentExplicitRequestRelaysTheTruthWithoutNetworkCall()
    {
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var backupAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains(value: "sibling-upstream", comparisonType: StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            if (request.RequestUri!.Host == BackupHost) backupAttempted = true;

            return request.RequestUri!.Host == BackupHost
                ? Ok("served-by-backup")
                : Status(HttpStatusCode.MethodNotAllowed);
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        var firstContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
        Assert.Equal(405, actual: firstContext.Response.StatusCode);
        Assert.False(backupAttempted);
        Assert.True(circuitBreaker.IsProviderOpen("prov-a"));

        var secondContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "sibling");
        Assert.Equal(503, actual: secondContext.Response.StatusCode);
        Assert.False(siblingAttempted);
    }

    // 401, 403, 405, and 404 are deliberately excluded here: 401, 403, and 405 are treated as provider-wide
    // outages (an invalid/expired credential, a permission/API-key-scope problem, or a provider-side
    // gateway/WAF block, respectively) that DO fail over to a different-provider backup for an
    // auto-selected request - see InvokeAsync_AutoSelectedPrimary401_DifferentProviderBackup_FailsOver,
    // InvokeAsync_AutoSelectedPrimary403_DifferentProviderBackup_FailsOver, and
    // InvokeAsync_AutoSelectedPrimary405_DifferentProviderBackup_FailsOver above (an explicit request
    // instead relays the truth - see the InvokeAsync_ExplicitPrimary4{01,03,05}_..._RelaysTheTruthInstead
    // tests above) - and 404 is treated as a per-target outage (a wrong/gone configured model id) that DOES
    // fail over unconditionally, explicit or not - see
    // InvokeAsync_Primary404_DifferentProviderBackup_FailsOver / InvokeAsync_Primary404_SameProviderBackup_FailsOver
    // above. Only genuine client-fault statuses, where a backup would reject the identical request the
    // same way, belong in this theory.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData((HttpStatusCode)422)]
    public async Task InvokeAsync_ClientFaultStatus_DoesNotFailOver(HttpStatusCode status)
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            return request.RequestUri!.Host == PrimaryHost ? Status(status) : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: (int)status, actual: context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_AllCandidatesUnreachable_Returns502()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(_ => throw new HttpRequestException("connection refused"));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
    }

    // Proves/disproves the socket-refusal-surfaced-as-403 question in
    // docs/router/agent-resilience-strategies.md's TODO: a production log showed a cascade ending in a
    // connection-refused SocketException (Ollama at localhost:11434, unreachable) followed by an
    // [INTERCEPTOR] line reporting status 403 for that request. Reading ProxyMiddleware.cs alone,
    // WriteUpstreamErrorResponseAsync unconditionally writes 502 on this path - this test pins that down
    // with the *exact* exception shape production hit (HttpRequestException wrapping a SocketException with
    // SocketError.ConnectionRefused, not a bare HttpRequestException), so a future change that special-cases
    // socket errors differently from other transport outages would be caught here.
    [Fact]
    public async Task InvokeAsync_LastCandidateConnectionRefused_Returns502NotForbidden()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"));

        var handler = new RoutingHandlerStub(_ => throw new HttpRequestException(
            message: "No connection could be made because the target machine actively refused it.",
            inner: new SocketException((int)SocketError.ConnectionRefused)));

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "primary",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
        Assert.NotEqual(expected: StatusCodes.Status403Forbidden, actual: context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(expected: "502", actual: json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // Mirrors the full multi-provider cascade from the production log (zhipu 405 -> moonshot 401 ->
    // minimax 401 -> ollama connection-refused, all exhausted): every candidate fails by a different
    // transport/HTTP mechanism, and the terminal response must still be 502, never 403. Auto-selected
    // ("auto"), since docs/adr/0005 (expanded scope) means an explicit selection would instead stop and
    // relay the truth at the very first provider-wide-trip status (405) rather than cascading through
    // every candidate - this test's whole point is exercising that full cascade.
    [Fact]
    public async Task InvokeAsync_MixedFailureCascade_AllExhausted_Returns502NotForbidden()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("zhipu-model", "zhipu", "glm-5", $"https://{PrimaryHost}"),
            ("moonshot-model", "moonshot", "kimi-k2.5", "https://moonshot.test"),
            ("minimax-model", "minimax", "minimax-m2.7", "https://minimax.test"),
            ("ollama-model", "ollama", "llama3", "https://ollama.test"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host switch
        {
            PrimaryHost => Status(HttpStatusCode.MethodNotAllowed),
            "moonshot.test" => Status(HttpStatusCode.Unauthorized),
            "minimax.test" => Status(HttpStatusCode.Unauthorized),
            "ollama.test" => throw new HttpRequestException(
                message: "No connection could be made because the target machine actively refused it.",
                inner: new SocketException((int)SocketError.ConnectionRefused)),
            _ => throw new InvalidOperationException("unexpected host")
        });

        var context = await RunAsync(resolver: resolver, handler: handler, requestedModel: "auto",
            requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
        Assert.NotEqual(expected: StatusCodes.Status403Forbidden, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_CascadesThroughMultipleBackups_UntilOneAnswers()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup1", "prov-b", "backup1-upstream", "https://backup1.test"),
            ("backup2", "prov-c", "backup2-upstream", "https://backup2.test"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host switch
        {
            "backup2.test" => Ok("served-by-backup2"),
            _ => Status(HttpStatusCode.BadGateway) // primary and backup1 both 5xx
        });

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver: resolver, handler: handler, telemetryPublisher: capturing,
            requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "served-by-backup2", actual: await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.True(telemetry.IsFallback);
        Assert.Equal(expected: "prov-c", actual: telemetry.Provider);
    }

    [Fact]
    public async Task InvokeAsync_ClientAbort_DoesNotFailOver_AndPropagates()
    {
        var backupCalled = false;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == BackupHost) backupCalled = true;

            // A genuine client abort: cancellation requested on the inbound request, surfaced as an OCE.
            throw new OperationCanceledException();
        });

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        // The client going away is not an outage - it must propagate, not silently fail over to a backup.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(resolver: resolver, handler: handler, requestedModel: "primary", requestAborted: aborted.Token));

        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_TargetTripped_SubsequentRequestBypassesWithoutNetworkCall()
    {
        // A shared, real CircuitBreaker (not each class's own independent default instance) is required for
        // a trip recorded by one ProxyMiddleware.InvokeAsync call to be visible to the next - exactly like
        // production DI wiring registers ICircuitBreaker as one singleton given to both RequestInterceptor
        // and ProxyMiddleware (see ServiceCollectionExtensions).
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var primaryAttempts = 0;
        var handler = new RoutingHandlerStub(request =>
        {
            if (request.RequestUri!.Host == PrimaryHost) primaryAttempts++;

            return request.RequestUri!.Host == PrimaryHost
                ? Status(HttpStatusCode.ServiceUnavailable)
                : Ok("served-by-backup");
        });

        var interceptor = new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CircuitBreaker = circuitBreaker
            }
        );

        // Default FailureThreshold is 3: the first three requests each attempt (and fail on) the primary
        // before falling over to the backup within the same request.
        for (var i = 0; i < 3; i++)
        {
            var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");
            Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        }

        Assert.Equal(3, actual: primaryAttempts);
        Assert.True(circuitBreaker.IsOpen(new CircuitBreakerTargetKey(Provider: "prov-a",
            BaseUrl: $"https://{PrimaryHost}/", ProviderModelId: "primary-upstream")));

        // A fourth, explicit request: the primary's circuit is now open, so - docs/adr/0005 (expanded
        // scope) - RequestInterceptor no longer substitutes the backup; it blocks the request with a
        // synthesized message instead, and no attempt against "primary" is ever made.
        var finalContext = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(503, actual: finalContext.Response.StatusCode);
        Assert.Equal(3, actual: primaryAttempts); // unchanged - the 4th request never touched the primary at all
    }

    private static async Task<HttpContext> RunWithSharedMiddleware(ProxyMiddleware middleware, string requestedModel)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = Encoding.UTF8.GetBytes($$"""{"model":"{{requestedModel}}"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = TestContext.Current.CancellationToken;

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        return context;
    }

    [Fact]
    public async Task
        InvokeAsync_CustomTranslatorOptsIntoEmbeddedErrorDecoding_SurfacesItsMessage_WithoutMiddlewareKnowingItsType()
    {
        // The reason IPayloadTranslator.HandlesEmbeddedErrorAt/TryExtractEmbeddedError exist: a translator
        // ProxyMiddleware has never heard of gets its own error envelope decoded and surfaced. The
        // middleware previously branched on `translator is GeminiPayloadTranslator` / `is
        // AnthropicPayloadTranslator` and called a static extractor on each, so a provider like this one
        // was silently un-classified - TranslateResponse would mangle its error into the bogus empty
        // completion this test asserts the client does NOT receive. Nothing here touches ProxyMiddleware.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-custom", "primary-upstream", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["prov-custom"] = new FakeTranslator(handlesEmbeddedErrors: true)
        };

        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"customError":{"detail":"tenant credential rejected"}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies { Translators = translators });

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        using var responseJson = JsonDocument.Parse(body);
        Assert.Equal(
            expected: "tenant credential rejected",
            actual: responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain(expectedSubstring: FakeTranslator.MangledResponseMarker, actualString: body,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_TranslatorThatDoesNotOptIntoEmbeddedErrorDecoding_KeepsTheUntouchedTranslatedPath()
    {
        // The other half of the seam: the interface defaults must stay behaviorally inert. A translator
        // that says nothing about embedded errors must not have its error body pre-read - pre-reading is
        // observable (a buffered body is forwarded whole rather than streamed, and it is what ADR-0004's
        // out-of-credits classifier inspects), so the default has to leave the response exactly where it
        // was: routed through TranslateResponse like any other non-2xx from a translated provider.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-custom", "primary-upstream", $"https://{PrimaryHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["prov-custom"] = new FakeTranslator(handlesEmbeddedErrors: false)
        };

        var handler = new RoutingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"customError":{"detail":"tenant credential rejected"}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        });

        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies { Translators = translators });

        var context = await RunWithSharedMiddleware(middleware: middleware, requestedModel: "primary");

        Assert.Equal(400, actual: context.Response.StatusCode);
        Assert.Contains(expectedSubstring: FakeTranslator.MangledResponseMarker,
            actualString: await ReadBodyAsync(context), comparisonType: StringComparison.Ordinal);
    }

    private static HttpResponseMessage Ok(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "text/plain") };
    }

    private static HttpResponseMessage Status(HttpStatusCode status)
    {
        return new HttpResponseMessage(status)
        { Content = new StringContent(content: "{}", encoding: Encoding.UTF8, mediaType: "application/json") };
    }

    private static HttpResponseMessage GeminiEmbeddedAuthErrorResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"UNAUTHENTICATED"}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        };
    }

    private static HttpResponseMessage AnthropicEmbeddedErrorResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"type":"error","error":{"type":"invalid_request_error","message":"messages: at least one message is required"}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        };
    }

    private static HttpResponseMessage AnthropicOutOfCreditsResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        };
    }

    private static HttpResponseMessage OpenAiInsufficientQuotaResponse()
    {
        return new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent(
                """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","code":"insufficient_quota"}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpContext> RunAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        ITelemetryPublisher? telemetryPublisher = null,
        string requestedModel = "primary",
        CancellationToken requestAborted = default)
    {
        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(
            logger: NullLogger<ProxyMiddleware>.Instance,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisher
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var body = Encoding.UTF8.GetBytes($$"""{"model":"{{requestedModel}}"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = requestAborted;

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        return context;
    }

    // -- helpers -------------------------------------------------------------

    /// <summary>
    /// A minimal third-party <see cref="IPayloadTranslator"/> for a provider ProxyMiddleware has no
    /// compile-time knowledge of. <paramref name="handlesEmbeddedErrors"/> flips only the two
    /// embedded-error members, so the pair of tests above differ in exactly the thing under test.
    /// <see cref="TranslateResponse"/> deliberately destroys the body it is handed (stamping
    /// <see cref="MangledResponseMarker"/>) - that is what makes "did the pre-read happen?" observable
    /// from the client's side, and it mirrors what a real translator does to an error envelope it was
    /// never meant to see.
    /// </summary>
    private sealed class FakeTranslator(bool handlesEmbeddedErrors) : IPayloadTranslator
    {
        internal const string MangledResponseMarker = "mangled-by-translate-response";

        public string Provider => "prov-custom";

        public Uri BuildRequestUri(Uri baseUrl, string providerModelId, bool isStreaming)
        {
            return new Uri(baseUri: baseUrl, relativeUri: "/v1/chat/completions");
        }

        public byte[] TranslateRequest(byte[] openAiShapedBody)
        {
            return openAiShapedBody;
        }

        public byte[] TranslateResponse(byte[] nativeShapedBody)
        {
            return Encoding.UTF8.GetBytes($$"""{"choices":[],"note":"{{MangledResponseMarker}}"}""");
        }

        public IStreamTranslator CreateStreamTranslator()
        {
            throw new NotSupportedException("These tests never take the streaming path.");
        }

        public bool HandlesEmbeddedErrorAt(int statusCode)
        {
            return handlesEmbeddedErrors && statusCode == StatusCodes.Status400BadRequest;
        }

        public bool TryExtractEmbeddedError(byte[] body, out EmbeddedProviderError error)
        {
            error = default;
            if (!handlesEmbeddedErrors) return false;

            var detail = JsonDocument.Parse(body).RootElement
                .GetProperty("customError").GetProperty("detail").GetString();
            if (string.IsNullOrEmpty(detail)) return false;

            error = new EmbeddedProviderError(Status: "CREDENTIAL_REJECTED", Message: detail, true);
            return true;
        }
    }

    private sealed class RoutingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class CapturingPublisher : ITelemetryPublisher
    {
        private readonly TaskCompletionSource<RoutingTelemetryEvent> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tcs.TrySetResult(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task<RoutingTelemetryEvent> WaitAsync()
        {
            var completed = await Task.WhenAny(task1: _tcs.Task, task2: Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(condition: ReferenceEquals(objA: completed, objB: _tcs.Task),
                userMessage: "Timed out waiting for a routing telemetry event.");
            return await _tcs.Task;
        }
    }
}