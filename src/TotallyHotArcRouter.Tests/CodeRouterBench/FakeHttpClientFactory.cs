namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// An <see cref="IHttpClientFactory"/> that hands out a new <see cref="HttpClient"/> wrapping the same
/// caller-supplied <see cref="HttpMessageHandler"/> on every <see cref="CreateClient"/> call, regardless of
/// name - enough for <see cref="TotallyHot.ArcRouter.CodeRouterBench.BenchmarkChecksumProbe"/> and
/// <see cref="TotallyHot.ArcRouter.CodeRouterBench.BenchmarkSyncService"/> tests, which now create a fresh
/// client per operation instead of capturing one for their lifetime.
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
