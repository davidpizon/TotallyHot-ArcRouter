using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="RateLimitHeaderCapture"/> and <see cref="NullRateLimitHeaderCapture"/>.</summary>
public class RateLimitHeaderCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CaptureAsync_CapturesStandardAndUnifiedAndUnknownAnthropicHeaders()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        using var capture = new RateLimitHeaderCapture(repository);

        var headers = BuildHeaders(
            ("anthropic-ratelimit-tokens-remaining", "1000"),
            ("anthropic-ratelimit-unified-status", "allowed"),
            ("anthropic-ratelimit-future-header", "value"));

        await capture.CaptureAsync(providerKey: "anthropic", headers: headers, cancellationToken: Ct);

        var (snapshot, _) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Equal(3, actual: snapshot.Count);
    }

    [Fact]
    public async Task CaptureAsync_CapturesOpenAiXRateLimitHeaders()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        using var capture = new RateLimitHeaderCapture(repository);

        var headers = BuildHeaders(
            ("x-ratelimit-limit-requests", "5000"),
            ("x-ratelimit-remaining-tokens", "159975"),
            ("content-type", "application/json"));

        await capture.CaptureAsync(providerKey: "openai", headers: headers, cancellationToken: Ct);

        var (snapshot, observedAt) = repository.GetRateLimitSnapshot("openai");
        Assert.Equal(2, actual: snapshot.Count);
        Assert.NotNull(observedAt);
    }

    [Fact]
    public async Task CaptureAsync_BothAnthropicAndOpenAiFamiliesCapturedWhenBothAppear()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        using var capture = new RateLimitHeaderCapture(repository);

        var headers = BuildHeaders(
            ("anthropic-ratelimit-tokens-remaining", "1000"),
            ("x-ratelimit-remaining-tokens", "2000"));

        await capture.CaptureAsync(providerKey: "mixed-provider", headers: headers, cancellationToken: Ct);

        var (snapshot, _) = repository.GetRateLimitSnapshot("mixed-provider");
        Assert.Equal(2, actual: snapshot.Count);
    }

    [Fact]
    public async Task CaptureAsync_IgnoresHeadersWithoutThePrefix()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        using var capture = new RateLimitHeaderCapture(repository);

        var headers = BuildHeaders(
            ("content-type", "application/json"),
            ("x-request-id", "abc-123"));

        await capture.CaptureAsync(providerKey: "anthropic", headers: headers, cancellationToken: Ct);

        var (snapshot, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Empty(snapshot);
        Assert.Null(observedAt);
    }

    [Fact]
    public async Task CaptureAsync_UnknownProvider_IsNoOp()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        using var capture = new RateLimitHeaderCapture(repository);

        await capture.CaptureAsync(providerKey: string.Empty,
            headers: BuildHeaders(("anthropic-ratelimit-tokens-remaining", "1")), cancellationToken: Ct);

        var (snapshot, _) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task CaptureAsync_RepositoryThrows_IsSwallowed()
    {
        // Best-effort, same contract as ProviderBudgetStore.RecordUsageAsync: a storage failure here must
        // never propagate and fail a request that already succeeded upstream.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var brokenDatabase = new PriceCatalogDatabase(
            Options.Create(new StorageOptions
            { DatabasePath = Path.Combine(path1: temp.DatabasePath, path2: "does", path3: "not", path4: "exist.db") }));
        var repository = new RateLimitRepository(brokenDatabase);
        using var capture = new RateLimitHeaderCapture(repository);

        var exception = await Record.ExceptionAsync(() =>
            capture.CaptureAsync(providerKey: "anthropic",
                headers: BuildHeaders(("anthropic-ratelimit-tokens-remaining", "1")), cancellationToken: Ct));

        Assert.Null(exception);
    }

    [Fact]
    public async Task NullRateLimitHeaderCapture_NeverThrowsAndDoesNothing()
    {
        var exception = await Record.ExceptionAsync(() =>
            NullRateLimitHeaderCapture.Instance.CaptureAsync(providerKey: "anthropic",
                headers: BuildHeaders(("anthropic-ratelimit-tokens-remaining", "1")), cancellationToken: Ct));

        Assert.Null(exception);
    }

    private static HttpResponseHeaders BuildHeaders(params (string Name, string Value)[] entries)
    {
        // Not disposed: HttpResponseHeaders is owned by its HttpResponseMessage, so disposing here before
        // returning would hand callers a headers collection whose parent is already gone. The message has
        // no content and holds no unmanaged resources, so leaving it undisposed is harmless in a test.
        var response = new HttpResponseMessage();
        foreach (var (name, value) in entries) response.Headers.TryAddWithoutValidation(name: name, value: value);

        return response.Headers;
    }
}