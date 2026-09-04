using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Covers end-to-end proxy interception behavior against a real local upstream endpoint.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public class ProxyInterceptionTests
{
    [Fact(Skip = "Integration testing disabled")]
    public async Task Proxy_InterceptsAndForwards_Request_ToUpstream()
    {
        using var upstream = new HttpListener();
        var upstreamPort = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{upstreamPort}/";
        upstream.Prefixes.Add(prefix);
        upstream.Start();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var upstreamTask = Task.Run(function: async () =>
        {
            try
            {
                // upstreamTask is awaited below, before this scope exits.
                // ReSharper disable once AccessToDisposedClosure
                var context = await upstream.GetContextAsync().WaitAsync(TestContext.Current.CancellationToken);
                using var reader = new StreamReader(stream: context.Request.InputStream,
                    encoding: context.Request.ContentEncoding);
                var requestBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Headers.Add(name: "X-Upstream", value: "ok");
                await using var writer =
                    new StreamWriter(stream: context.Response.OutputStream, encoding: Encoding.UTF8);
                await writer.WriteAsync($"upstream:{requestBody}");
                context.Response.Close();
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred; upstream listener was cancelled
            }
        }, cancellationToken: timeoutCts.Token);

        var host = Program.CreateHostBuilder([]).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var interceptor = host.Services.GetRequiredService<RequestInterceptor>();
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var request = new HttpRequestMessage(method: HttpMethod.Post,
                requestUri: "http://127.0.0.1:5001/v1/chat/completions");
            request.Content =
                new StringContent(content: "payload", encoding: Encoding.UTF8, mediaType: "text/plain");

            request.Headers.Host = $"127.0.0.1:{upstreamPort}";

            var response = await client.SendAsync(request: request,
                cancellationToken: TestContext.Current.CancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            Assert.Equal(expected: "ok", actual: response.Headers.GetValues("X-Upstream").Single());
            Assert.Contains(expectedSubstring: "upstream:payload", actualString: responseBody,
                comparisonType: StringComparison.Ordinal);
            Assert.True(interceptor.InterceptedRequestCount >= 1);

            await upstreamTask.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            upstream.Stop();
            await timeoutCts.CancelAsync();
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(localaddr: IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}