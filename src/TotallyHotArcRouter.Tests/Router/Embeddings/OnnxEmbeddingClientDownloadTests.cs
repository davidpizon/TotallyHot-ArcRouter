using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.Embeddings;

/// <summary>
/// Covers ADR-0024 rule 6 on <see cref="OnnxEmbeddingClient"/>'s first-use download: a downloaded artifact
/// is hashed before it is renamed into the cache, and one that does not match its configured SHA-256 is
/// deleted and reported instead of loaded. Only the download path is exercised; the model is never loaded.
/// </summary>
public sealed class OnnxEmbeddingClientDownloadTests
{
    [Fact]
    public async Task DownloadWithTheWrongHash_IsDeleted_AndNeverCached()
    {
        var cache = Path.Combine(TestScratchDirectory.RunRoot, "embedding-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var options = new EmbeddingOptions
            {
                ModelCacheDirectory = cache,
                ModelUrl = "https://example.invalid/model.onnx",
                TokenizerJsonUrl = "https://example.invalid/tokenizer.json"
            };
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("planted weights"u8.ToArray())
            });
            await using var client = new OnnxEmbeddingClient(Options.Create(options), new FakeHttpClientFactory(handler),
                NullLogger<OnnxEmbeddingClient>.Instance);

            await Assert.ThrowsAsync<InvalidDataException>(() => client.EmbedAsync("hello"));

            Assert.False(File.Exists(Path.Combine(cache, "model.onnx")));
            Assert.Empty(Directory.EnumerateFiles(cache));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadMatchingTheConfiguredHash_IsCached()
    {
        var cache = Path.Combine(TestScratchDirectory.RunRoot, "embedding-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var modelBytes = "model bytes"u8.ToArray();
            var options = new EmbeddingOptions
            {
                ModelCacheDirectory = cache,
                ModelUrl = "https://example.invalid/model.onnx",
                ModelSha256 = Convert.ToHexStringLower(SHA256.HashData(modelBytes)),
                TokenizerJsonUrl = "https://example.invalid/tokenizer.json"
            };
            var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".onnx")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
            await using var client = new OnnxEmbeddingClient(Options.Create(options), new FakeHttpClientFactory(handler),
                NullLogger<OnnxEmbeddingClient>.Instance);

            // The tokenizer 404s, so EmbedAsync still fails - but only after the verified model was cached.
            await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync("hello"));

            Assert.Equal(modelBytes, await File.ReadAllBytesAsync(Path.Combine(cache, "model.onnx")));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }
}
