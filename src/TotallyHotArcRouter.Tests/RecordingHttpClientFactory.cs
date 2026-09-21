namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// An <see cref="IHttpClientFactory"/> that records every requested client name and answers every request
/// with <paramref name="respond"/>. Production wiring hands long-lived services the factory rather than a
/// client, so tests that pin the factory path need both halves: that the right named client was asked for
/// (a name/registration mismatch would silently drop that registration's headers and handlers), and that
/// the request really went through it.
/// </summary>
/// <param name="respond">Builds the response for each request that reaches a factory-created client.</param>
internal sealed class RecordingHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : IHttpClientFactory
{
    private readonly StubHandler _handler = new(respond);

    /// <summary>Gets every name passed to <see cref="CreateClient"/>, in call order.</summary>
    internal List<string> RequestedNames { get; } = [];

    /// <summary>Gets every request sent through a client this factory created, in send order.</summary>
    internal List<HttpRequestMessage> Requests => _handler.Requests;

    /// <inheritdoc/>
    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        // disposeHandler: false - callers dispose each leased client, and the handler is shared across them.
        return new HttpClient(handler: _handler, disposeHandler: false);
    }

    /// <summary>Records each request and answers it with the factory's response delegate.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
