using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Cache;

/// <summary>
/// The inputs a lookup needs: the routing task embedding, the compatibility scope, and the model name
/// used only for logs.
/// </summary>
internal sealed class SemanticCacheProbe
{
    /// <summary>Initializes a new instance of the <see cref="SemanticCacheProbe"/> class.</summary>
    /// <param name="embedding">The task embedding already computed for routing.</param>
    /// <param name="scopeKey">The compatibility scope from <see cref="SemanticCacheScope"/>.</param>
    /// <param name="modelName">The client-facing model name that will serve, for logs.</param>
    public SemanticCacheProbe(float[] embedding, string scopeKey, string modelName)
    {
        Embedding = embedding;
        ScopeKey = scopeKey;
        ModelName = modelName;
    }

    /// <summary>Gets the task embedding.</summary>
    public float[] Embedding { get; }

    /// <summary>Gets the compatibility scope key.</summary>
    public string ScopeKey { get; }

    /// <summary>Gets the model name used in log lines.</summary>
    public string ModelName { get; }
}

/// <summary>
/// The outcome of one lookup. <see cref="Reason"/> is a stable code: <c>hit</c>, <c>disabled</c>,
/// <c>below-threshold</c>, <c>expired</c>, <c>incompatible-scope</c>, <c>embedding-model</c>, or
/// <c>no-entry</c>.
/// </summary>
internal sealed class SemanticCacheLookupResult
{
    private SemanticCacheLookupResult(bool isHit, string reason, double? similarity, byte[]? body, string? contentType)
    {
        IsHit = isHit;
        Reason = reason;
        Similarity = similarity;
        Body = body;
        ContentType = contentType;
    }

    /// <summary>Gets whether a saved answer should be written to the client.</summary>
    public bool IsHit { get; }

    /// <summary>Gets the stable reason code.</summary>
    public string Reason { get; }

    /// <summary>Gets the winning cosine similarity when one was computed.</summary>
    public double? Similarity { get; }

    /// <summary>Gets the saved response bytes on a hit.</summary>
    public byte[]? Body { get; }

    /// <summary>Gets the saved content type on a hit.</summary>
    public string? ContentType { get; }

    /// <summary>Builds a hit.</summary>
    public static SemanticCacheLookupResult Hit(double similarity, byte[] body, string contentType)
    {
        return new SemanticCacheLookupResult(isHit: true, reason: "hit", similarity: similarity, body: body,
            contentType: contentType);
    }

    /// <summary>Builds a miss or a disabled result. Disabled results have not scanned stored entries.</summary>
    public static SemanticCacheLookupResult Miss(string reason, double? similarity = null)
    {
        return new SemanticCacheLookupResult(isHit: false, reason: reason, similarity: similarity, body: null,
            contentType: null);
    }
}

/// <summary>
/// In-process store of saved completions, keyed by the routing embedding plus a compatibility scope.
/// Entries expire by <see cref="SemanticCacheOptions.TimeToLive"/> at lookup time and are trimmed to
/// <see cref="SemanticCacheOptions.MaxEntries"/> on store.
/// </summary>
/// <remarks>
/// The cache does not embed text. Callers pass the vector
/// <see cref="TotallyHot.ArcRouter.Proxy.RequestInterceptor"/> already computed with
/// <see cref="IEmbeddingClient"/>, and similarity is
/// <see cref="EmbeddingMemory.CosineSimilarity"/>. A second embedding stack is intentionally absent.
/// The proxy talks to this type directly: lookup inputs are internal, so a separate interface would only
/// have been a second name for the same class. Entries live in memory: a process restart clears them,
/// as does <see cref="Clear"/> and a change to <see cref="SemanticCacheOptions.CacheEpoch"/>.
/// </remarks>
public sealed class SemanticResponseCache
{
    private readonly IEmbeddingClient? _embeddingClient;
    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly IOptionsMonitor<SemanticCacheOptions> _options;
    private readonly TimeProvider _time;
    private int _appliedEpoch;
    private int _comparisonCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticResponseCache"/> class.
    /// </summary>
    /// <param name="options">Live cache settings. <see cref="SemanticCacheOptions.Enabled"/> is read on every call.</param>
    /// <param name="embeddingClient">
    /// The same client the router uses, read only for <see cref="IEmbeddingClient.ModelIdentity"/> so a
    /// vector produced by a different model is never compared. This type does not call
    /// <see cref="IEmbeddingClient.EmbedAsync"/>.
    /// </param>
    /// <param name="timeProvider">Clock used for TTL. Defaults to <see cref="TimeProvider.System"/>.</param>
    public SemanticResponseCache(
        IOptionsMonitor<SemanticCacheOptions> options,
        IEmbeddingClient? embeddingClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _embeddingClient = embeddingClient;
        _time = timeProvider ?? TimeProvider.System;
        _appliedEpoch = options.CurrentValue.CacheEpoch;
    }

    /// <summary>
    /// Gets whether lookup and store are active. When <see langword="false"/>, <see cref="Count"/> may
    /// still reflect entries from an earlier enabled period, but they are not read or added to.
    /// </summary>
    public bool IsEnabled => _options.CurrentValue.Enabled;

    /// <summary>Gets how many answers are currently stored, including ones a disabled cache will not serve.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Gets how many cosine comparisons lookups have performed. Stays at 0 across disabled lookups, which
    /// return before scanning. Useful for tests and for confirming a disabled cache is not doing work.
    /// </summary>
    public int ComparisonCount
    {
        get
        {
            lock (_gate)
            {
                return _comparisonCount;
            }
        }
    }

    /// <summary>Drops every stored answer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Finds a saved answer for <paramref name="probe"/> when the cache is enabled, the entry is inside
    /// the current TTL, the embedding model identity matches, the scope key matches, and cosine
    /// similarity is at or above the current threshold. Disabled calls return before any comparison.
    /// </summary>
    /// <param name="probe">The routing embedding and compatibility scope.</param>
    /// <returns>A hit, or a miss that names why.</returns>
    internal SemanticCacheLookupResult Lookup(SemanticCacheProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!_options.CurrentValue.Enabled)
            return SemanticCacheLookupResult.Miss("disabled");

        if (probe.Embedding.Length == 0)
            return SemanticCacheLookupResult.Miss("no-embedding");

        lock (_gate)
        {
            if (!_options.CurrentValue.Enabled)
                return SemanticCacheLookupResult.Miss("disabled");

            ApplyEpochLocked();
            var options = _options.CurrentValue;
            var now = _time.GetUtcNow();
            var identity = CurrentIdentity();
            double best = double.NegativeInfinity;
            Entry? bestEntry = null;
            var sawExpiredSameScope = false;
            var sawScopeMismatch = false;
            var sawIdentityMismatch = false;

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (now - entry.StoredAtUtc >= options.TimeToLive)
                {
                    if (string.Equals(a: entry.ScopeKey, b: probe.ScopeKey, comparisonType: StringComparison.Ordinal))
                        sawExpiredSameScope = true;

                    _entries.RemoveAt(i);
                    continue;
                }

                if (!string.Equals(a: entry.ScopeKey, b: probe.ScopeKey, comparisonType: StringComparison.Ordinal))
                {
                    sawScopeMismatch = true;
                    continue;
                }

                if (!string.Equals(a: entry.EmbeddingModelIdentity, b: identity,
                        comparisonType: StringComparison.Ordinal))
                {
                    sawIdentityMismatch = true;
                    continue;
                }

                if (entry.Embedding.Length != probe.Embedding.Length)
                    continue;

                var similarity = EmbeddingMemory.CosineSimilarity(left: probe.Embedding, right: entry.Embedding);
                _comparisonCount++;
                if (similarity > best)
                {
                    best = similarity;
                    bestEntry = entry;
                }
            }

            if (bestEntry is not null && best >= options.SimilarityThreshold)
                return SemanticCacheLookupResult.Hit(similarity: best, body: bestEntry.ResponseBody,
                    contentType: bestEntry.ContentType);

            if (bestEntry is not null)
                return SemanticCacheLookupResult.Miss(reason: "below-threshold", similarity: best);

            if (sawExpiredSameScope)
                return SemanticCacheLookupResult.Miss("expired");

            if (sawIdentityMismatch)
                return SemanticCacheLookupResult.Miss("embedding-model");

            if (sawScopeMismatch && !sawExpiredSameScope)
                return SemanticCacheLookupResult.Miss("incompatible-scope");

            return SemanticCacheLookupResult.Miss("no-entry");
        }
    }

    /// <summary>
    /// Stores a successful answer for <paramref name="probe"/> when the cache is enabled and the body
    /// passes the size and tool-call checks. Disabled calls return before the list is touched.
    /// </summary>
    /// <param name="probe">The routing embedding and compatibility scope.</param>
    /// <param name="statusCode">The HTTP status written to the client. Only 200 is stored.</param>
    /// <param name="responseBody">The complete client-facing body.</param>
    /// <param name="contentType">The content type to replay.</param>
    /// <returns><see langword="true"/> when an entry was added.</returns>
    internal bool Remember(SemanticCacheProbe probe, int statusCode, byte[] responseBody, string contentType)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(responseBody);
        ArgumentNullException.ThrowIfNull(contentType);

        var options = _options.CurrentValue;
        if (!options.Enabled)
            return false;

        if (statusCode != StatusCodes.Status200OK)
            return false;

        if (probe.Embedding.Length == 0 || responseBody.Length == 0 ||
            responseBody.Length > options.MaxStoredResponseBytes ||
            responseBody.Length >= UpstreamResponseWriter.MaxCapturedResponseBytes)
            return false;

        if (!SemanticCacheScope.IsReusableAnswer(responseBody: responseBody, refusalReason: out _))
            return false;

        lock (_gate)
        {
            if (!_options.CurrentValue.Enabled)
                return false;

            ApplyEpochLocked();
            options = _options.CurrentValue;
            if (responseBody.Length > options.MaxStoredResponseBytes)
                return false;

            _entries.Add(new Entry(
                embedding: probe.Embedding.ToArray(),
                scopeKey: probe.ScopeKey,
                embeddingModelIdentity: CurrentIdentity(),
                responseBody: responseBody.ToArray(),
                contentType: contentType,
                storedAtUtc: _time.GetUtcNow()));

            var overflow = _entries.Count - options.MaxEntries;
            if (overflow > 0)
            {
                _entries.Sort(static (left, right) => left.StoredAtUtc.CompareTo(right.StoredAtUtc));
                _entries.RemoveRange(index: 0, count: overflow);
            }

            return true;
        }
    }

    /// <summary>Drops every entry when <see cref="SemanticCacheOptions.CacheEpoch"/> changes.</summary>
    private void ApplyEpochLocked()
    {
        var epoch = _options.CurrentValue.CacheEpoch;
        if (epoch == _appliedEpoch) return;

        _entries.Clear();
        _appliedEpoch = epoch;
    }

    /// <summary>The embedding model's identity, or the unknown sentinel when no client was injected.</summary>
    private string CurrentIdentity()
    {
        return _embeddingClient?.ModelIdentity ?? IEmbeddingClient.UnknownModelIdentity;
    }

    /// <summary>One stored answer and the vector that retrieved it.</summary>
    private sealed class Entry(
        float[] embedding,
        string scopeKey,
        string embeddingModelIdentity,
        byte[] responseBody,
        string contentType,
        DateTimeOffset storedAtUtc)
    {
        public float[] Embedding { get; } = embedding;
        public string ScopeKey { get; } = scopeKey;
        public string EmbeddingModelIdentity { get; } = embeddingModelIdentity;
        public byte[] ResponseBody { get; } = responseBody;
        public string ContentType { get; } = contentType;
        public DateTimeOffset StoredAtUtc { get; } = storedAtUtc;
    }
}
