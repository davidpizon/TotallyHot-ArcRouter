using System.Text.Json.Serialization;
using TotallyHot.ArcRouter.Checksums;

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

    /// <summary>
    /// Gets or sets the entry's git blob SHA-1. For a Git LFS-tracked entry (<see cref="Lfs"/> non-null),
    /// this is the SHA-1 of the small LFS pointer text file, not of the real content - see
    /// <see cref="PublishedChecksumAlgorithm.LfsSha256"/>.
    /// </summary>
    [JsonPropertyName("oid")]
    public string? Oid { get; set; }

    /// <summary>Gets or sets the entry's size in bytes. For an LFS-tracked entry, prefer <see cref="Lfs"/>'s size instead.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets the entry's Git LFS metadata, present only when this file is LFS-tracked.</summary>
    [JsonPropertyName("lfs")]
    public HuggingFaceTreeEntryLfs? Lfs { get; set; }
}

/// <summary>The Git LFS metadata of one Hugging Face dataset tree entry, present only for an LFS-tracked file.</summary>
internal sealed class HuggingFaceTreeEntryLfs
{
    /// <summary>Gets or sets the file's real content SHA-256, unlike the enclosing entry's git-blob <c>oid</c>.</summary>
    [JsonPropertyName("oid")]
    public string? Oid { get; set; }

    /// <summary>Gets or sets the file's real (LFS-resolved) size in bytes.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary>The published checksum and size of one CodeRouterBench file.</summary>
/// <param name="PublishedOid">
/// The checksum Hugging Face publishes for this file, in the format <paramref name="Algorithm"/> names -
/// a git blob SHA-1 for a regular file, or a content SHA-256 for a Git LFS-tracked one.
/// </param>
/// <param name="Size">The file's published size in bytes.</param>
/// <param name="Algorithm">Which hash <paramref name="PublishedOid"/> is, and so which algorithm a downloaded/cached copy must be verified with.</param>
public sealed record BenchmarkPublishedFile(string PublishedOid, long Size, PublishedChecksumAlgorithm Algorithm);

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
    /// <exception cref="NotSupportedException">The response's content type is not supported for JSON deserialization.</exception>
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

            // An entry with a non-empty Lfs.Oid is Git LFS-tracked: the real content hash lives there
            // (SHA-256), not in the entry's own git-blob oid (which is only the small pointer file's
            // hash). Prefer Lfs.Size too - it's the real, LFS-resolved size a download actually transfers.
            files[entry.Path] = entry.Lfs is { Oid: { Length: > 0 } lfsOid } lfs
                ? new BenchmarkPublishedFile(lfsOid, lfs.Size ?? entry.Size, PublishedChecksumAlgorithm.LfsSha256)
                : new BenchmarkPublishedFile(entry.Oid, entry.Size, PublishedChecksumAlgorithm.GitBlobSha1);
        }

        _logger.LogInformation(
            "CodeRouterBench checksum probe found {FileCount} published file(s) at commit {RepoCommit}.",
            files.Count,
            repoCommit);

        return new BenchmarkChecksumProbeResult(files, repoCommit);
    }
}
