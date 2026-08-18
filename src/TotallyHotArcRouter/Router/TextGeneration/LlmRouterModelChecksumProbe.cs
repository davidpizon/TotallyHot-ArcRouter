using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Checksums;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>One entry of the Hugging Face models tree API's response array.</summary>
internal sealed class LlmRouterModelTreeEntry
{
    /// <summary>Gets or sets the entry kind, e.g. <c>"file"</c> or <c>"directory"</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Gets or sets the entry's path relative to the model repository root.</summary>
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
    public LlmRouterModelTreeEntryLfs? Lfs { get; set; }
}

/// <summary>The Git LFS metadata of one Hugging Face models tree entry, present only for an LFS-tracked file.</summary>
internal sealed class LlmRouterModelTreeEntryLfs
{
    /// <summary>Gets or sets the file's real content SHA-256, unlike the enclosing entry's git-blob <c>oid</c>.</summary>
    [JsonPropertyName("oid")]
    public string? Oid { get; set; }

    /// <summary>Gets or sets the file's real (LFS-resolved) size in bytes.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary>The published checksum and size of one llm_router model file.</summary>
/// <param name="PublishedOid">
/// The checksum Hugging Face publishes for this file, in the format <paramref name="Algorithm"/> names -
/// a git blob SHA-1 for a regular file, or a content SHA-256 for a Git LFS-tracked one (model.onnx and
/// model.onnx.data almost always are; tokenizer.json sometimes is).
/// </param>
/// <param name="Size">The file's published size in bytes.</param>
/// <param name="Algorithm">Which hash <paramref name="PublishedOid"/> is, and so which algorithm a downloaded/cached copy must be verified with.</param>
public sealed record LlmRouterModelPublishedFile(string PublishedOid, long Size, PublishedChecksumAlgorithm Algorithm);

/// <summary>The result of one successful <see cref="LlmRouterModelChecksumProbe.TryFetchAsync"/> call.</summary>
/// <param name="Files">Every published file's checksum and size, keyed by its file name (not full repo path).</param>
public sealed record LlmRouterModelChecksumProbeResult(IReadOnlyDictionary<string, LlmRouterModelPublishedFile> Files);

/// <summary>
/// Fetches the published git blob SHA-1 and size of every file in a llm_router model's folder, via
/// Hugging Face's <em>models</em> tree API - distinct from <see cref="CodeRouterBench.BenchmarkChecksumProbe"/>,
/// which calls the <em>datasets</em> tree API for one fixed, hardcoded repository. This probe instead
/// parses an arbitrary user-supplied <c>https://huggingface.co/{owner}/{repo}/resolve/{ref}/{path...}</c>
/// URL, since the Governance panel's "Local Voter Model" section lets the operator switch to any model
/// folder by URL.
/// </summary>
/// <remarks>
/// Unlike <see cref="CodeRouterBench.BenchmarkChecksumProbe.FetchAsync"/>, this never throws for a
/// probe-side failure: an arbitrary, operator-supplied URL is not guaranteed to be a Hugging Face URL at
/// all, let alone one the API answers successfully, so <see cref="LlmRouterModelSyncService"/> must be
/// able to fall back to existence-only verification rather than failing the whole sync over an
/// unverifiable model source. Caller cancellation is the one exception - it still propagates as
/// <see cref="OperationCanceledException"/>, matching <see cref="LlmRouterModelSyncService.SyncAsync"/>'s
/// cancellation contract.
/// </remarks>
public sealed class LlmRouterModelChecksumProbe
{
    /// <summary>The named <see cref="HttpClient"/> registered for llm_router model Hugging Face calls.</summary>
    public const string HttpClientName = "LlmRouterModelHuggingFace";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmRouterModelChecksumProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmRouterModelChecksumProbe"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Used to create a fresh <see cref="HttpClientName"/> client per call, mirroring
    /// <see cref="CodeRouterBench.BenchmarkChecksumProbe"/>'s pattern rather than capturing one
    /// factory-created client for the singleton's lifetime.
    /// </param>
    /// <param name="logger">The logger.</param>
    public LlmRouterModelChecksumProbe(IHttpClientFactory httpClientFactory, ILogger<LlmRouterModelChecksumProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to fetch every published file's checksum under <paramref name="baseUrl"/>'s model folder.
    /// Returns <see langword="null"/> - rather than throwing - when <paramref name="baseUrl"/> is not a
    /// recognized Hugging Face model resolve URL, or the API call fails for any reason other than caller
    /// cancellation, which still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="baseUrl">The model folder URL, e.g. <c>https://huggingface.co/{owner}/{repo}/resolve/{ref}/{path}</c>.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    public async Task<LlmRouterModelChecksumProbeResult?> TryFetchAsync(string baseUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!TryParseResolveUrl(baseUrl, out var owner, out var repo, out var modelRef, out var pathPrefix))
        {
            _logger.LogInformation(
                "llm_router checksum probe skipped for {BaseUrl}: not a recognized Hugging Face model resolve URL.",
                baseUrl);
            return null;
        }

        var escapedRef = EscapeUriSegment(modelRef);
        var apiUrl = string.IsNullOrEmpty(pathPrefix)
            ? $"https://huggingface.co/api/models/{owner}/{repo}/tree/{escapedRef}"
            : $"https://huggingface.co/api/models/{owner}/{repo}/tree/{escapedRef}/{EscapePathPrefix(pathPrefix)}";

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var entries = await response.Content
                .ReadFromJsonAsync<List<LlmRouterModelTreeEntry>>(cancellationToken)
                .ConfigureAwait(false) ?? [];

            // The tree API is called scoped to pathPrefix already, but its response paths are relative to
            // the repository root, not the folder - so an entry can legitimately live directly in the
            // requested folder (no further '/') or, if a same-named file also exists in a subdirectory of
            // that folder, one level deeper. Only the former is a real match for this fixed, flat
            // llm_router artifact set; keying by leaf name alone (ignoring where in the tree it came from)
            // would let a same-named file from an unrelated subdirectory silently overwrite the real one
            // and verify downloads against the wrong OID.
            //
            // pathPrefix itself is a slice of Uri.AbsolutePath, so it's percent-encoded (e.g. "sub%20dir"),
            // but the tree API's JSON path values are repo paths - decoded characters (e.g. "sub dir").
            // Comparing the encoded prefix against decoded entry paths would never match for any folder
            // name containing URL-escaped characters, silently dropping every file and disabling checksum
            // verification for that model. Decode once here for comparison; EscapePathPrefix above already
            // re-escapes pathPrefix separately when composing the API request URL.
            var decodedPathPrefix = Uri.UnescapeDataString(pathPrefix);
            var folderPrefix = string.IsNullOrEmpty(decodedPathPrefix) ? string.Empty : decodedPathPrefix + "/";
            Dictionary<string, LlmRouterModelPublishedFile> files = [];
            foreach (var entry in entries)
            {
                if (!string.Equals(entry.Type, "file", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(entry.Path) ||
                    string.IsNullOrWhiteSpace(entry.Oid) ||
                    !entry.Path.StartsWith(folderPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relativePath = entry.Path[folderPrefix.Length..];
                if (relativePath.Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }

                // An entry with a non-empty Lfs.Oid is Git LFS-tracked: the real content hash lives there
                // (SHA-256), not in the entry's own git-blob oid (which is only the small pointer file's
                // hash). Prefer Lfs.Size too - it's the real, LFS-resolved size a download actually
                // transfers. model.onnx and model.onnx.data are almost always LFS-tracked given their
                // size; tokenizer.json sometimes is too.
                files[relativePath] = entry.Lfs is { Oid: { Length: > 0 } lfsOid } lfs
                    ? new LlmRouterModelPublishedFile(lfsOid, lfs.Size ?? entry.Size, PublishedChecksumAlgorithm.LfsSha256)
                    : new LlmRouterModelPublishedFile(entry.Oid, entry.Size, PublishedChecksumAlgorithm.GitBlobSha1);
            }

            _logger.LogInformation(
                "llm_router checksum probe found {FileCount} published file(s) at {BaseUrl}.", files.Count, baseUrl);
            return new LlmRouterModelChecksumProbeResult(files);
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or JsonException ||
            (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(
                ex,
                "llm_router checksum probe failed for {BaseUrl}; falling back to existence-only verification.",
                baseUrl);
            return null;
        }
    }

    /// <summary>
    /// Re-escapes a single URI path segment taken from <see cref="Uri.AbsolutePath"/> for use in a
    /// freshly composed URL. <see cref="Uri.AbsolutePath"/> segments are already percent-encoded, so
    /// escaping them again (as the original code did for <c>modelRef</c>) would double-escape - e.g. a
    /// ref containing an encoded slash (<c>%2F</c>) would become <c>%252F</c> and the API call would
    /// 404. Unescaping first undoes that existing encoding before re-escaping exactly once.
    /// </summary>
    private static string EscapeUriSegment(string segment) => Uri.EscapeDataString(Uri.UnescapeDataString(segment));

    /// <summary>Re-escapes every slash-separated segment of a model-relative path prefix, individually.</summary>
    private static string EscapePathPrefix(string pathPrefix) =>
        string.Join('/', pathPrefix.Split('/').Select(EscapeUriSegment));

    /// <summary>
    /// Parses <c>https://huggingface.co/{owner}/{repo}/resolve/{ref}/{path...}</c> into its components.
    /// <paramref name="pathPrefix"/> is empty when the folder is the repository root.
    /// </summary>
    private static bool TryParseResolveUrl(string url, out string owner, out string repo, out string modelRef, out string pathPrefix)
    {
        owner = repo = modelRef = pathPrefix = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !string.Equals(segments[2], "resolve", StringComparison.Ordinal))
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1];
        modelRef = segments[3];
        pathPrefix = segments.Length > 4 ? string.Join('/', segments[4..]) : string.Empty;
        return true;
    }
}
