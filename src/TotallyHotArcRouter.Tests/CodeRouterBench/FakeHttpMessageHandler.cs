using System.Net;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// A <see cref="HttpMessageHandler"/> that answers every request from a caller-supplied delegate, so
/// <see cref="TotallyHot.ArcRouter.CodeRouterBench.BenchmarkChecksumProbe"/> and
/// <see cref="TotallyHot.ArcRouter.CodeRouterBench.BenchmarkSyncService"/> can be exercised against fixture
/// bytes with no real network I/O.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);

        _respond = respond;
    }

    /// <summary>
    /// Builds a handler that always returns <paramref name="statusCode"/> with an empty body - for probe-failure
    /// fixtures.
    /// </summary>
    public static FakeHttpMessageHandler AlwaysFails(HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = _respond(request);
        return Task.FromResult(response);
    }
}