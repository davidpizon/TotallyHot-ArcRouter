using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers the infrastructure-level outage cascade: on a genuine upstream outage (unreachable, timeout,
/// 5xx, 429, 404, or a cross-provider 401/403/405) for the requested model, <see cref="ProxyMiddleware"/>
/// retries the request against another currently-configured model - dynamically ranked, not a
/// hand-authored per-model list - per <c>docs/router/agent-resilience-strategies.md</c>'s Circuit Breaker
/// (which replaced the old static <c>ModelRouteEntry.Fallbacks</c>). A same-provider 401/403/405 does NOT
/// retry (the backup would share the same broken credential/gateway); a true terminal client-fault status
/// (400/422) or a client abort never retries either way. This is distinct from the paper's
/// Verifier-driven <em>semantic</em> re-routing (the separate, already-built sandbox/RouterMemory
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
            if (request.RequestUri!.Host == BackupHost)
            {
                backupCalled = true;
            }

            return Ok($"served-by-{request.RequestUri!.Host}");
        });

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver, handler, capturing, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(backupCalled);
        Assert.Equal("served-by-" + PrimaryHost, await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.False(telemetry.IsFallback);
        Assert.Equal("primary", telemetry.RequestedModel);
        Assert.Equal("prov-a", telemetry.Provider);
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
        var context = await RunAsync(resolver, handler, capturing, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.True(telemetry.IsFallback);
        // The client still asked for "primary"; the backup provider actually served it.
        Assert.Equal("primary", telemetry.RequestedModel);
        Assert.Equal("prov-b", telemetry.Provider);
        Assert.Equal("backup-upstream", telemetry.ResolvedModel);
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

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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
            if (body.Contains("backup-upstream", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status((HttpStatusCode)429);
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary401_DifferentProviderBackup_FailsOver()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.Unauthorized)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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
            if (body.Contains("backup-upstream", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.Unauthorized);
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_PrimaryGemini400EmbeddedUnauthenticated_DifferentProviderBackup_FailsOver()
    {
        // Gemini reports an invalid/expired API key as 400 (not 401), since the key travels as a "key="
        // query parameter rather than an Authorization header - with an embedded
        // {"status": "UNAUTHENTICATED"} error rather than a normal HTTP status signal. That must be
        // treated exactly like a real 401: fail over to a different-provider backup instead of surfacing
        // the 400 to the client (see the generic InvokeAsync_ClientFaultStatus_DoesNotFailOver theory
        // below for the case where a 400 is *not* Gemini's disguised-401 shape and correctly stays terminal).
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "gemini", "gemini-2.5-pro", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = new GeminiPayloadTranslator(),
        };

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? GeminiEmbeddedAuthErrorResponse()
            : Ok("served-by-backup"));

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            translators: translators);

        var context = await RunWithSharedMiddleware(middleware, requestedModel: "primary");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_PrimaryGemini400EmbeddedUnauthenticated_SameProviderBackup_DoesNotFailOver_AndForwardsRealMessage()
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
            ["gemini"] = new GeminiPayloadTranslator(),
        };

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("gemini-2.5-flash", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return GeminiEmbeddedAuthErrorResponse();
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            translators: translators);

        var context = await RunWithSharedMiddleware(middleware, requestedModel: "primary");

        Assert.Equal(400, context.Response.StatusCode);
        Assert.False(backupCalled);

        using var responseJson = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Contains(
            "API key not valid",
            responseJson.RootElement.GetProperty("error").GetProperty("message").GetString());
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
            ["gemini"] = new GeminiPayloadTranslator(),
        };

        const string rawBody = """{"reason":"invalid_argument","details":"request body too large"}""";

        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("gemini-2.5-flash", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("should-not-be-served");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
            };
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            translators: translators);

        var context = await RunWithSharedMiddleware(middleware, requestedModel: "primary");

        Assert.Equal(400, context.Response.StatusCode);
        Assert.False(backupCalled);
        Assert.Equal(rawBody, await ReadBodyAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_Primary405_DifferentProviderBackup_FailsOver()
    {
        // A 405 on a path ArcRouter itself constructs (the client never chooses the method) is treated like
        // a 401: a provider-side gateway/WAF block, not a genuine client-fault status - so it fails over to
        // a different-provider backup instead of being surfaced to the client immediately.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.MethodNotAllowed)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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
            if (body.Contains("backup-upstream", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.MethodNotAllowed);
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(405, context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary403_DifferentProviderBackup_FailsOver()
    {
        // A 403 is treated like 401: almost always a permission/API-key-scope problem (API not enabled for
        // this key, region lock, tier restriction) rather than something specific to the one model
        // requested - so it fails over to a different-provider backup instead of being surfaced to the
        // client immediately.
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(request => request.RequestUri!.Host == PrimaryHost
            ? Status(HttpStatusCode.Forbidden)
            : Ok("served-by-backup"));

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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
            if (body.Contains("backup-upstream", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.Forbidden);
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_Primary403_TripsWholeProvider_SubsequentRequestBypassesADifferentModel_OnSameProvider()
    {
        // Like 401 (InvokeAsync_Primary401_TripsWholeProvider_... above), a 403 trips every model on the
        // provider at once (RecordProviderFailure) - a permission/API-key-scope problem would reject any
        // model on that provider identically, not just the one that surfaced it.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("sibling-upstream", StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost ? Ok("served-by-backup") : Status(HttpStatusCode.Forbidden);
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            circuitBreaker: circuitBreaker);

        // First request against "primary": 403, provider-wide trip, fails over cross-provider to "backup".
        var firstContext = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(firstContext));

        // A second request explicitly asking for "sibling" (same provider as "primary", never itself
        // failed) must never be attempted at all - the provider-wide circuit bypasses it outright.
        var secondContext = await RunWithSharedMiddleware(middleware, requestedModel: "sibling");
        Assert.Equal(StatusCodes.Status200OK, secondContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(secondContext));
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

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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
            if (body.Contains("backup-upstream", StringComparison.Ordinal))
            {
                backupCalled = true;
                return Ok("served-by-backup");
            }

            return Status(HttpStatusCode.NotFound);
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(context));
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

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            circuitBreaker: circuitBreaker);

        // Default FailureThreshold is 3: the first three requests each attempt (and 404 on) "primary"
        // before failing over to the backup within the same request.
        for (var i = 0; i < 3; i++)
        {
            var context = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("served-by-backup", await ReadBodyAsync(context));
        }

        Assert.Equal(3, primaryAttempts);
        Assert.True(circuitBreaker.IsOpen(new CircuitBreakerTargetKey("prov-a", $"https://{PrimaryHost}/", "primary-upstream")));
        Assert.False(circuitBreaker.IsProviderOpen("prov-a"));

        // A fourth request: the primary target's own circuit is now open, so it's bypassed with no
        // network call - but (unlike 401) "prov-a" itself is never marked unhealthy.
        var finalContext = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
        Assert.Equal(StatusCodes.Status200OK, finalContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(finalContext));
        Assert.Equal(3, primaryAttempts); // unchanged - the 4th request never touched the primary target again
    }

    [Fact]
    public async Task InvokeAsync_Primary401_TripsWholeProvider_SubsequentRequestBypassesADifferentModel_OnSameProvider()
    {
        // A 401 trips every model on the provider at once (RecordProviderFailure), not just the one that
        // surfaced it - a shared, real CircuitBreaker (not each class's own independent default instance)
        // is required for this to be visible across requests, matching production DI wiring.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("sibling-upstream", StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost ? Ok("served-by-backup") : Status(HttpStatusCode.Unauthorized);
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            circuitBreaker: circuitBreaker);

        // First request against "primary": 401, provider-wide trip, fails over cross-provider to "backup".
        var firstContext = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(firstContext));

        // A second request explicitly asking for "sibling" (same provider as "primary", never itself
        // failed) must never be attempted at all - the provider-wide circuit bypasses it outright.
        var secondContext = await RunWithSharedMiddleware(middleware, requestedModel: "sibling");
        Assert.Equal(StatusCodes.Status200OK, secondContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(secondContext));
        Assert.False(siblingAttempted);
    }

    [Fact]
    public async Task InvokeAsync_Primary405_TripsWholeProvider_SubsequentRequestBypassesADifferentModel_OnSameProvider()
    {
        // Like 401 (InvokeAsync_Primary401_TripsWholeProvider_... above), a 405 trips every model on the
        // provider at once (RecordProviderFailure) - a gateway/WAF block at the edge would reject any model
        // on that provider identically, not just the one that surfaced it.
        var circuitBreaker = new CircuitBreaker();
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("sibling", "prov-a", "sibling-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var siblingAttempted = false;
        var handler = new RoutingHandlerStub(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("sibling-upstream", StringComparison.Ordinal))
            {
                siblingAttempted = true;
                return Ok("should-not-be-served");
            }

            return request.RequestUri!.Host == BackupHost ? Ok("served-by-backup") : Status(HttpStatusCode.MethodNotAllowed);
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            circuitBreaker: circuitBreaker);

        // First request against "primary": 405, provider-wide trip, fails over cross-provider to "backup".
        var firstContext = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(firstContext));

        // A second request explicitly asking for "sibling" (same provider as "primary", never itself
        // failed) must never be attempted at all - the provider-wide circuit bypasses it outright.
        var secondContext = await RunWithSharedMiddleware(middleware, requestedModel: "sibling");
        Assert.Equal(StatusCodes.Status200OK, secondContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(secondContext));
        Assert.False(siblingAttempted);
    }

    // 401, 403, 405, and 404 are deliberately excluded here: 401, 403, and 405 are treated as provider-wide
    // outages (an invalid/expired credential, a permission/API-key-scope problem, or a provider-side
    // gateway/WAF block, respectively) that DO fail over to a different-provider backup - see
    // InvokeAsync_Primary401_DifferentProviderBackup_FailsOver, InvokeAsync_Primary403_DifferentProviderBackup_FailsOver,
    // and InvokeAsync_Primary405_DifferentProviderBackup_FailsOver above - and 404 is treated as a per-target
    // outage (a wrong/gone configured model id) that DOES fail over unconditionally - see
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
            if (request.RequestUri!.Host == BackupHost)
            {
                backupCalled = true;
            }

            return request.RequestUri!.Host == PrimaryHost ? Status(status) : Ok("served-by-backup");
        });

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal((int)status, context.Response.StatusCode);
        Assert.False(backupCalled);
    }

    [Fact]
    public async Task InvokeAsync_AllCandidatesUnreachable_Returns502()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("primary", "prov-a", "primary-upstream", $"https://{PrimaryHost}"),
            ("backup", "prov-b", "backup-upstream", $"https://{BackupHost}"));

        var handler = new RoutingHandlerStub(_ => throw new HttpRequestException("connection refused"));

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
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
            "No connection could be made because the target machine actively refused it.",
            new SocketException((int)SocketError.ConnectionRefused)));

        var context = await RunAsync(resolver, handler, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("502", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // Mirrors the full multi-provider cascade from the production log (zhipu 405 -> moonshot 401 ->
    // minimax 401 -> ollama connection-refused, all exhausted): every candidate fails by a different
    // transport/HTTP mechanism, and the terminal response must still be 502, never 403.
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
                "No connection could be made because the target machine actively refused it.",
                new SocketException((int)SocketError.ConnectionRefused)),
            _ => throw new InvalidOperationException("unexpected host"),
        });

        var context = await RunAsync(resolver, handler, requestedModel: "zhipu-model", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
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
            _ => Status(HttpStatusCode.BadGateway), // primary and backup1 both 5xx
        });

        var capturing = new CapturingPublisher();
        var context = await RunAsync(resolver, handler, capturing, requestedModel: "primary", requestAborted: TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("served-by-backup2", await ReadBodyAsync(context));

        var telemetry = await capturing.WaitAsync();
        Assert.True(telemetry.IsFallback);
        Assert.Equal("prov-c", telemetry.Provider);
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
            if (request.RequestUri!.Host == BackupHost)
            {
                backupCalled = true;
            }

            // A genuine client abort: cancellation requested on the inbound request, surfaced as an OCE.
            throw new OperationCanceledException();
        });

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        // The client going away is not an outage - it must propagate, not silently fail over to a backup.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(resolver, handler, requestedModel: "primary", requestAborted: aborted.Token));

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
            if (request.RequestUri!.Host == PrimaryHost)
            {
                primaryAttempts++;
            }

            return request.RequestUri!.Host == PrimaryHost
                ? Status(HttpStatusCode.ServiceUnavailable)
                : Ok("served-by-backup");
        });

        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver, circuitBreaker: circuitBreaker);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            circuitBreaker: circuitBreaker);

        // Default FailureThreshold is 3: the first three requests each attempt (and fail on) the primary
        // before falling over to the backup within the same request.
        for (var i = 0; i < 3; i++)
        {
            var context = await RunWithSharedMiddleware(middleware, requestedModel: "primary");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        Assert.Equal(3, primaryAttempts);
        Assert.True(circuitBreaker.IsOpen(new CircuitBreakerTargetKey("prov-a", $"https://{PrimaryHost}/", "primary-upstream")));

        // A fourth request: the primary's circuit is now open, so RequestInterceptor substitutes the backup
        // as the primary candidate outright - no attempt against "primary" is ever made.
        var finalContext = await RunWithSharedMiddleware(middleware, requestedModel: "primary");

        Assert.Equal(StatusCodes.Status200OK, finalContext.Response.StatusCode);
        Assert.Equal("served-by-backup", await ReadBodyAsync(finalContext));
        Assert.Equal(3, primaryAttempts); // unchanged - the 4th request never touched the primary at all
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

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        return context;
    }

    // -- helpers -------------------------------------------------------------

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage GeminiEmbeddedAuthErrorResponse() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"UNAUTHENTICATED"}}""",
            Encoding.UTF8,
            "application/json"),
    };

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpContext> RunAsync(
        IModelRouteResolver resolver,
        RoutingHandlerStub handler,
        ITelemetryPublisher? telemetryPublisher = null,
        string requestedModel = "primary",
        CancellationToken requestAborted = default)
    {
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            new HttpClient(handler),
            telemetryPublisher: telemetryPublisher);

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

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        return context;
    }

    private sealed class RoutingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
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

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<RoutingTelemetryEvent> WaitAsync()
        {
            var completed = await Task.WhenAny(_tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(completed, _tcs.Task), "Timed out waiting for a routing telemetry event.");
            return await _tcs.Task;
        }
    }
}

