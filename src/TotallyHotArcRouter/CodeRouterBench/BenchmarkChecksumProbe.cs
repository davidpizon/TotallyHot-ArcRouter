using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>One entry of the Hugging Face dataset tree API's response array.</summary>
internal sealed class HuggingFaceTreeEntry
{
    /// <summary>Gets or sets the entry kind, e.g. <c>"file"</c> or <c>"directory"</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Gets or sets the entry's path relative to the dataset root.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Gets or sets the entry's git blob SHA-1.</summary>
    [JsonPropertyName("oid")]
    public string? Oid { get; set; }

    /// <summary>Gets or sets the entry's size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>The published git blob SHA-1 and size of one CodeRouterBench file.</summary>
/// <param name="PublishedOid">The git blob SHA-1 Hugging Face publishes for this file.</param>
/// <param name="Size">The file's published size in bytes.</param>
public sealed record BenchmarkPublishedFile(string PublishedOid, long Size);

/// <summary>The result of one <see cref="BenchmarkChecksumProbe.FetchAsync"/> call.</summary>
/// <param name="Files">Every published file's checksum and size, keyed by its dataset-relative path.</param>
/// <param name="RepoCommit">The dataset commit the tree was resolved to.</param>
public sealed record BenchmarkChecksumProbeResult(
    IReadOnlyDictionary<string, BenchmarkPublishedFile> Files,
    string RepoCommit);

/// <summary>
/// Fetches the published git blob SHA-1 and size of every CodeRouterBench file in one HTTP call, via
/// Hugging Face's dataset tree API (docs/router/coderouterbench-sqlite-migration-plan.md's "Checksums"
/// section). This is the network call Phase 3's startup health check and Phase 4's "Recheck" action use
/// to decide the corpus's <c>Current</c>/<c>Update</c>/<c>CheckFailed</c> state.
/// </summary>
public sealed class BenchmarkChecksumProbe
{
    /// <summary>The Hugging Face dataset repository id CodeRouterBench is published under.</summary>
    public const string DatasetRepo = "Lance1573/CodeRouterBench";

    /// <summary>The named <see cref="HttpClient"/> registered for CodeRouterBench Hugging Face calls.</summary>
    public const string HttpClientName = "CodeRouterBenchHuggingFace";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BenchmarkChecksumProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="BenchmarkChecksumProbe"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Used to create a fresh <see cref="HttpClientName"/> client per call, mirroring
    /// <c>OnnxEmbeddingClient</c>'s pattern rather than capturing one factory-created client for the
    /// singleton's lifetime (which would opt this probe out of the factory's handler rotation).
    /// </param>
    /// <param name="logger">The logger.</param>
    public BenchmarkChecksumProbe(IHttpClientFactory httpClientFactory, ILogger<BenchmarkChecksumProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the tree of <paramref name="datasetRef"/> (a branch, tag, or commit - typically
    /// <c>"main"</c>) and returns every published file's checksum plus the resolved commit.
    /// </summary>
    /// <param name="datasetRef">The dataset ref to resolve, e.g. <c>"main"</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <exception cref="HttpRequestException">The request failed or returned a non-success status.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response body was not a valid tree entry array.</exception>
    public async Task<BenchmarkChecksumProbeResult> FetchAsync(string datasetRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRef);

        var url = $"https://huggingface.co/api/datasets/{DatasetRepo}/tree/{Uri.EscapeDataString(datasetRef)}";
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var repoCommit = response.Headers.TryGetValues("X-Repo-Commit", out var values)
            ? values.FirstOrDefault() ?? datasetRef
            : datasetRef;

        var entries = await response.Content
            .ReadFromJsonAsync<List<HuggingFaceTreeEntry>>(cancellationToken)
            .ConfigureAwait(false) ?? [];

        Dictionary<string, BenchmarkPublishedFile> files = [];
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "file", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.Path) ||
                string.IsNullOrWhiteSpace(entry.Oid))
            {
                continue;
            }

            files[entry.Path] = new BenchmarkPublishedFile(entry.Oid, entry.Size);
        }

        _logger.LogInformation(
            "CodeRouterBench checksum probe found {FileCount} published file(s) at commit {RepoCommit}.",
            files.Count,
            repoCommit);

        return new BenchmarkChecksumProbeResult(files, repoCommit);
    }
}
