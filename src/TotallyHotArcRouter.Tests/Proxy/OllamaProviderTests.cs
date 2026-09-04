using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Regression coverage for the Ollama slice of unified API translation (see
/// <c>docs/router/unified-api-translation.md</c> §4.1): confirms the proxy's *existing* pass-through
/// forwarding already round-trips correctly against Ollama's own OpenAI-compatible endpoint shape
/// (verified against Ollama's docs, not assumed), with zero
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.IPayloadTranslator"/> involvement - these are
/// regression tests proving no translator is needed for Ollama, not tests of new translation logic
/// (there isn't any yet).
/// </summary>
public class OllamaProviderTests
{
    // Matches appsettings.json's configured "ollama" provider: local, unauthenticated, with a
    // "/v1" path segment - Ollama's OpenAI-compatible routes only exist under "/v1", not at the
    // origin root.
    private const string OllamaBaseUrl = "http://localhost:11434/v1";

    [Fact]
    public async Task
        InvokeAsync_OllamaProvider_NonStreamingResponse_ForwardsUnmodified_AndPreservesBaseUrlPathSegment()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: OllamaBaseUrl,
            providerName: "ollama",
            apiKey: null);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(async request =>
        {
            // Regression coverage for the ProxyMiddleware.InvokeAsync URL-combining fix this PR makes:
            // OllamaBaseUrl's own "/v1" path segment must survive being combined with the client's
            // request path, not get silently replaced by it (the bug Uri(Uri, string) combining has
            // whenever the relative path starts with "/", which every ASP.NET Core request path does).
            //
            // Note the request path below is "/chat/completions" - this covers only the half of the join
            // where the base supplies "/v1". A real OpenAI-shaped client sends "/v1/chat/completions",
            // where the *same* base must instead collapse against it rather than forward "/v1/v1/...";
            // that half is covered by ProxyMiddlewareTests'
            // InvokeAsync_PassthroughProviderWithVersionedBaseUrl_DoesNotForwardTheVersionSegmentTwice.
            Assert.Equal(expected: "http://localhost:11434/v1/chat/completions",
                actual: request.RequestUri!.ToString());

            // No auth header configured for "ollama" - no Authorization header should ever be injected.
            Assert.False(request.Headers.Contains("Authorization"));

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(expected: "llama3", actual: document.RootElement.GetProperty("model").GetString());

            // Ollama's own OpenAI-compatible response shape (docs.ollama.com/api/openai-compatibility):
            // choices[].message + usage.prompt_tokens/completion_tokens - identical to OpenAI's own shape.
            const string ollamaResponse = """
                                          {
                                            "id": "chatcmpl-123",
                                            "object": "chat.completion",
                                            "model": "llama3",
                                            "choices": [
                                              { "index": 0, "message": { "role": "assistant", "content": "Hi from Ollama." }, "finish_reason": "stop" }
                                            ],
                                            "usage": { "prompt_tokens": 12, "completion_tokens": 5, "total_tokens": 17 }
                                          }
                                          """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: ollamaResponse, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat/completions";
        var requestBody = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}"""u8.ToArray();
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var responseJson = JsonDocument.Parse(responseBody);
        Assert.Equal(
            expected: "Hi from Ollama.",
            actual: responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString());
    }

    [Fact]
    public async Task InvokeAsync_OllamaProvider_StreamingResponse_ForwardsSseChunksUnmodified()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: OllamaBaseUrl,
            providerName: "ollama",
            apiKey: null);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        // Ollama's OpenAI-compatible streaming shape: SSE "data: {...}" chunks framed exactly like
        // OpenAI's chat.completion.chunk stream, terminated by "data: [DONE]".
        const string sseBody =
            "data: {\"id\":\"chatcmpl-123\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-123\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" there.\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: sseBody, encoding: Encoding.UTF8, mediaType: "text/event-stream")
            };
            return Task.FromResult(response);
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat/completions";
        var requestBody =
            """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}"""u8.ToArray();
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // No translation happens (nothing consults IPayloadTranslator yet), so every SSE byte Ollama
        // sent must reach the client completely unmodified - this is what "no translator needed"
        // actually means in practice, not just an assertion about the JSON content.
        Assert.Equal(expected: sseBody, actual: responseBody);
    }

    [Fact]
    public async Task InvokeAsync_OllamaProvider_StreamingResponse_FlushesEachChunkToTheClient()
    {
        // A pass-through (no-translator) SSE stream must flush every chunk as it's written to the client
        // - production symptom without this: a slow local model's token-by-token stream sat in Kestrel's
        // internal write buffer instead of reaching the client promptly, so the client saw literally
        // nothing until the connection eventually closed and reported "no response was returned" despite
        // the router itself having gotten a genuine 200 OK. Simulates multiple separate upstream reads
        // (MultiChunkContent) rather than one big buffered read, so each loop iteration in
        // CopyAndCaptureAsync is actually exercised.
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: OllamaBaseUrl,
            providerName: "ollama",
            apiKey: null);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new MultiChunkContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n",
                    "data: [DONE]\n\n")
            };
            return Task.FromResult(response);
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat/completions";
        var requestBody =
            """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}"""u8.ToArray();
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        var flushCounting = new FlushCountingStream(new MemoryStream());
        context.Response.Body = flushCounting;

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(3, actual: flushCounting.FlushCount);
    }

    /// <summary>
    /// Simulates an upstream body delivered across several discrete reads (one per constructor argument), unlike
    /// <see cref="StringContent"/> which always hands back its whole payload in a single read.
    /// </summary>
    private sealed class MultiChunkContent(params string[] chunks) : HttpContent
    {
        private readonly byte[][] _chunks = [.. chunks.Select(Encoding.UTF8.GetBytes)];

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new ChunkedReadStream(_chunks));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new NotSupportedException("This stub is only read via CreateContentReadStreamAsync.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class ChunkedReadStream(byte[][] chunks) : Stream
        {
            private int _index;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                if (_index >= chunks.Length) return Task.FromResult(0);

                var chunk = chunks[_index++];
                Buffer.BlockCopy(src: chunk, 0, dst: buffer, dstOffset: offset, count: chunk.Length);
                return Task.FromResult(chunk.Length);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadAsync(buffer: buffer, offset: offset, count: count, default).GetAwaiter().GetResult();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }

    /// <summary>
    /// Wraps a stream and counts <see cref="FlushAsync(CancellationToken)"/> calls, to prove the proxy actually
    /// flushes each streamed write rather than letting it sit buffered.
    /// </summary>
    private sealed class FlushCountingStream(Stream inner) : Stream
    {
        public int FlushCount { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
            inner.Flush();
            FlushCount++;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer: buffer, offset: offset, count: count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset: offset, origin: origin);
        }

        public override void SetLength(long value)
        {
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer: buffer, offset: offset, count: count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return inner.WriteAsync(buffer: buffer, offset: offset, count: count, cancellationToken: cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.WriteAsync(buffer: buffer, cancellationToken: cancellationToken);
        }
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}