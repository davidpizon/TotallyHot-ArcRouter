using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="AnthropicTokenCountClient"/>: the request it builds, the field it parses, and its
/// refusal to surface a transport or protocol failure as anything but "no sample this time".
/// </summary>
public class AnthropicTokenCountClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryCountTokensAsync_SuccessfulResponse_ReturnsInputTokens()
    {
        var handler = new RecordingHandler("""{"input_tokens": 2095}""");
        var client = Build(handler);

        var tokens = await client.TryCountTokensAsync(model: "claude-opus-5", text: "hello", cancellationToken: Ct);

        Assert.Equal(expected: 2095, actual: tokens);
    }

    [Fact]
    public async Task TryCountTokensAsync_SendsModelAndTextAsAUserMessage_WithTheApiKeyAndVersionHeaders()
    {
        var handler = new RecordingHandler("""{"input_tokens": 10}""");
        var client = Build(handler);

        await client.TryCountTokensAsync(model: "claude-opus-5", text: "count me", cancellationToken: Ct);

        Assert.Equal(expected: "https://api.anthropic.com/v1/messages/count_tokens", actual: handler.LastUri);
        Assert.Contains(expectedSubstring: "\"model\":\"claude-opus-5\"", actualString: handler.LastBody!);
        Assert.Contains(expectedSubstring: "\"role\":\"user\"", actualString: handler.LastBody!);
        Assert.Contains(expectedSubstring: "count me", actualString: handler.LastBody!);
        Assert.Equal(expected: "test-key", actual: handler.LastApiKey);
        Assert.Equal(expected: "2023-06-01", actual: handler.LastAnthropicVersion);
    }

    [Fact]
    public async Task TryCountTokensAsync_ErrorStatus_ReturnsNullRatherThanThrowing()
    {
        // A provider outage must degrade calibration to "no new sample", never fault the hosting service.
        var handler = new RecordingHandler(body: """{"error": "nope"}""", status: HttpStatusCode.BadRequest);
        var client = Build(handler);

        var tokens = await client.TryCountTokensAsync(model: "claude-opus-5", text: "hello", cancellationToken: Ct);

        Assert.Null(tokens);
    }

    [Theory]
    [InlineData("""{"something_else": 5}""")]
    [InlineData("""{"input_tokens": 0}""")]
    [InlineData("""{"input_tokens": "not a number"}""")]
    [InlineData("[]")]
    public async Task TryCountTokensAsync_UnusableBody_ReturnsNull(string body)
    {
        var client = Build(new RecordingHandler(body));

        var tokens = await client.TryCountTokensAsync(model: "claude-opus-5", text: "hello", cancellationToken: Ct);

        Assert.Null(tokens);
    }

    [Fact]
    public async Task TryCountTokensAsync_BlankText_MakesNoCallAtAll()
    {
        var handler = new RecordingHandler("""{"input_tokens": 10}""");
        var client = Build(handler);

        var tokens = await client.TryCountTokensAsync(model: "claude-opus-5", text: "   ", cancellationToken: Ct);

        Assert.Null(tokens);
        Assert.Equal(expected: 0, actual: handler.CallCount);
    }

    /// <summary>Builds a client over the given handler with a fixed test key.</summary>
    /// <param name="handler">The stub transport.</param>
    /// <returns>The client under test.</returns>
    private static AnthropicTokenCountClient Build(RecordingHandler handler)
    {
        return new AnthropicTokenCountClient(httpClient: new HttpClient(handler), apiKey: "test-key");
    }

    /// <summary>A stub transport returning one fixed response and recording what it was sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        /// <summary>Initializes the stub with the response it should return.</summary>
        /// <param name="body">The response body.</param>
        /// <param name="status">The response status.</param>
        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        /// <summary>Gets how many requests reached this handler.</summary>
        public int CallCount { get; private set; }

        /// <summary>Gets the last request's URI.</summary>
        public string? LastUri { get; private set; }

        /// <summary>Gets the last request's serialized body.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Gets the last request's <c>x-api-key</c> header.</summary>
        public string? LastApiKey { get; private set; }

        /// <summary>Gets the last request's <c>anthropic-version</c> header.</summary>
        public string? LastAnthropicVersion { get; private set; }

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastUri = request.RequestUri!.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LastApiKey = request.Headers.TryGetValues(name: "x-api-key", values: out var keys)
                ? keys.FirstOrDefault()
                : null;
            LastAnthropicVersion = request.Headers.TryGetValues(name: "anthropic-version", values: out var versions)
                ? versions.FirstOrDefault()
                : null;

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(content: _body, encoding: Encoding.UTF8, mediaType: "application/json")
            };
        }
    }
}
