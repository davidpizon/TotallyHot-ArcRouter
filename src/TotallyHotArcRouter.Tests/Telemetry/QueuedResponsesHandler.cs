using System.Net;
using System.Text;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// A stub <see cref="HttpMessageHandler"/> that returns the given JSON bodies in order, one per request,
/// recording each request's URI and (for the last request) its Authorization header - used by the
/// provider cost-reconciler tests to verify pagination and auth without a real HTTP call.
/// </summary>
internal sealed class QueuedResponsesHandler(IReadOnlyList<string> jsonBodies) : HttpMessageHandler
{
    private int _index;

    public List<string> RequestUris { get; } = [];

    public string? LastRequestAuthScheme { get; private set; }

    public string? LastRequestAuthParameter { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri!.ToString());
        LastRequestAuthScheme = request.Headers.Authorization?.Scheme;
        LastRequestAuthParameter = request.Headers.Authorization?.Parameter;

        var body = jsonBodies[_index];
        _index++;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
