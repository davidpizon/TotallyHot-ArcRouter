using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Cache;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Cache;

/// <summary>
/// Covers hit, miss, expiry, incompatibility, and the disabled no-lookup/no-store path for
/// <see cref="SemanticResponseCache"/>.
/// </summary>
public class SemanticResponseCacheTests
{
    private static readonly byte[] Answer = """{"choices":[{"message":{"role":"assistant","content":"four"}}]}"""u8
        .ToArray();

    /// <summary>A cosine equal to the configured threshold is a hit, not a miss.</summary>
    [Fact]
    public void Lookup_AtThreshold_ReturnsTheSavedAnswer()
    {
        var stored = new[] { 1f, 0f };
        var query = UnitVectorAtCosine(0.92);
        var similarity = EmbeddingMemory.CosineSimilarity(left: stored, right: query);
        var cache = CreateCache(threshold: similarity);
        var scope = ScopeFor(temperature: 0.2);

        Assert.True(cache.Remember(probe: Probe(embedding: stored, scope: scope), statusCode: 200,
            responseBody: Answer, contentType: "application/json"));

        var result = cache.Lookup(Probe(embedding: query, scope: scope));

        Assert.True(result.IsHit);
        Assert.Equal(expected: "hit", actual: result.Reason);
        Assert.Equal(expected: Answer, actual: result.Body);
        Assert.True(result.Similarity >= similarity);
    }

    /// <summary>Identical vectors are above a 0.92 threshold and reuse the stored body.</summary>
    [Fact]
    public void Lookup_AboveThreshold_ReturnsTheSavedAnswer()
    {
        var cache = CreateCache(threshold: 0.92);
        var scope = ScopeFor(temperature: 0.2);
        var vector = new[] { 1f, 0f };
        cache.Remember(probe: Probe(embedding: vector, scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        var result = cache.Lookup(Probe(embedding: vector, scope: scope));

        Assert.True(result.IsHit);
        Assert.Equal(1, actual: result.Similarity);
    }

    /// <summary>A cosine below the threshold does not reuse the answer.</summary>
    [Fact]
    public void Lookup_BelowThreshold_Misses()
    {
        var cache = CreateCache(threshold: 0.92);
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        var result = cache.Lookup(Probe(embedding: [0f, 1f], scope: scope));

        Assert.False(result.IsHit);
        Assert.Equal(expected: "below-threshold", actual: result.Reason);
        Assert.Null(result.Body);
        Assert.True(result.Similarity < 0.92);
    }

    /// <summary>An entry older than the current TTL is a miss and is dropped.</summary>
    [Fact]
    public void Lookup_ExpiredEntry_Misses()
    {
        var clock = new ManualTimeProvider { UtcNow = DateTimeOffset.Parse("2026-09-28T00:00:00Z") };
        var cache = CreateCache(threshold: 0.92, timeToLive: TimeSpan.FromMinutes(30), time: clock);
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        var result = cache.Lookup(Probe(embedding: [1f, 0f], scope: scope));

        Assert.False(result.IsHit);
        Assert.Equal(expected: "expired", actual: result.Reason);
        Assert.Equal(0, actual: cache.Count);
    }

    /// <summary>A different upstream model, or different generation settings, never reuses an answer.</summary>
    [Fact]
    public void Lookup_IncompatibleScope_Misses()
    {
        var cache = CreateCache(threshold: 0.92);
        var storedScope = ScopeFor(temperature: 0.2, providerModelId: "gpt-real");
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: storedScope), statusCode: 200,
            responseBody: Answer, contentType: "application/json");

        var otherModel = cache.Lookup(Probe(embedding: [1f, 0f],
            scope: ScopeFor(temperature: 0.2, providerModelId: "gpt-other")));
        var otherTemperature = cache.Lookup(Probe(embedding: [1f, 0f],
            scope: ScopeFor(temperature: 0.9, providerModelId: "gpt-real")));

        Assert.Equal(expected: "incompatible-scope", actual: otherModel.Reason);
        Assert.Equal(expected: "incompatible-scope", actual: otherTemperature.Reason);
        Assert.Equal(1, actual: cache.Count);
    }

    /// <summary>
    /// Two client-facing names that resolve to the same provider model id and the same settings share
    /// a scope, so an alias reuses the answer.
    /// </summary>
    [Fact]
    public void ScopeKey_AliasesWithEquivalentSettings_Match()
    {
        var fast = Body(model: "fast", temperature: 0.2, user: "What is two plus two?");
        var alias = Body(model: "fast-alias", temperature: 0.2, user: "What's 2 + 2?");

        Assert.True(SemanticCacheScope.TryCreateScopeKey(body: fast, provider: "prov", providerModelId: "gpt-real",
            scopeKey: out var fastKey, refusalReason: out _));
        Assert.True(SemanticCacheScope.TryCreateScopeKey(body: alias, provider: "Prov", providerModelId: "gpt-real",
            scopeKey: out var aliasKey, refusalReason: out _));

        Assert.Equal(expected: fastKey, actual: aliasKey);
    }

    /// <summary>A different system prompt is not equivalent, even when the newest user text matches.</summary>
    [Fact]
    public void ScopeKey_DifferentSystemPrompt_DoesNotMatch()
    {
        var polite = Body(model: "fast", temperature: 0.2, user: "hi", system: "be brief");
        var loud = Body(model: "fast", temperature: 0.2, user: "hi", system: "be verbose");

        SemanticCacheScope.TryCreateScopeKey(body: polite, provider: "prov", providerModelId: "gpt-real",
            scopeKey: out var politeKey, refusalReason: out _);
        SemanticCacheScope.TryCreateScopeKey(body: loud, provider: "prov", providerModelId: "gpt-real",
            scopeKey: out var loudKey, refusalReason: out _);

        Assert.NotEqual(expected: politeKey, actual: loudKey);
    }

    /// <summary>Disabled lookups do not compare vectors and do not return a stored answer.</summary>
    [Fact]
    public void Lookup_WhenDisabled_DoesNotLookUp()
    {
        var monitor = new MutableOptionsMonitor(EnabledOptions());
        var cache = new SemanticResponseCache(monitor, embeddingClient: new NamedEmbeddingClient("model-a"));
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");
        var comparisonsBefore = cache.ComparisonCount;

        monitor.Current = DisabledOptions();
        var result = cache.Lookup(Probe(embedding: [1f, 0f], scope: scope));

        Assert.Equal(expected: "disabled", actual: result.Reason);
        Assert.Null(result.Body);
        Assert.Equal(expected: comparisonsBefore, actual: cache.ComparisonCount);
        Assert.Equal(1, actual: cache.Count);
    }

    /// <summary>Disabled stores do not add entries.</summary>
    [Fact]
    public void Remember_WhenDisabled_DoesNotStore()
    {
        var cache = CreateCache(threshold: 0.92, enabled: false);

        var stored = cache.Remember(probe: Probe(embedding: [1f, 0f], scope: ScopeFor(temperature: 0.2)),
            statusCode: 200, responseBody: Answer, contentType: "application/json");

        Assert.False(stored);
        Assert.Equal(0, actual: cache.Count);
        Assert.Equal(0, actual: cache.ComparisonCount);
    }

    /// <summary>Clear drops stored answers.</summary>
    [Fact]
    public void Clear_DropsStoredAnswers()
    {
        var cache = CreateCache(threshold: 0.92);
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        cache.Clear();

        Assert.Equal(0, actual: cache.Count);
        Assert.Equal(expected: "no-entry", actual: cache.Lookup(Probe(embedding: [1f, 0f], scope: scope)).Reason);
    }

    /// <summary>Raising the cache epoch invalidates answers that a config change would make stale.</summary>
    [Fact]
    public void CacheEpochChange_DropsStoredAnswers()
    {
        var monitor = new MutableOptionsMonitor(EnabledOptions());
        var cache = new SemanticResponseCache(monitor);
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        monitor.Current = new SemanticCacheOptions
        {
            Enabled = true,
            SimilarityThreshold = 0.92,
            TimeToLive = TimeSpan.FromHours(1),
            MaxEntries = 256,
            CacheEpoch = 1
        };

        var result = cache.Lookup(Probe(embedding: [1f, 0f], scope: scope));

        Assert.Equal(expected: "no-entry", actual: result.Reason);
        Assert.Equal(0, actual: cache.Count);
    }

    /// <summary>Vectors from a different embedding model are not comparable and are not served.</summary>
    [Fact]
    public void Lookup_DifferentEmbeddingModel_Misses()
    {
        var client = new NamedEmbeddingClient("model-a");
        var monitor = new MutableOptionsMonitor(EnabledOptions());
        var cache = new SemanticResponseCache(options: monitor, embeddingClient: client);
        var scope = ScopeFor(temperature: 0.2);
        cache.Remember(probe: Probe(embedding: [1f, 0f], scope: scope), statusCode: 200, responseBody: Answer,
            contentType: "application/json");

        client.Identity = "model-b";
        var result = cache.Lookup(Probe(embedding: [1f, 0f], scope: scope));

        Assert.Equal(expected: "embedding-model", actual: result.Reason);
        Assert.False(result.IsHit);
    }

    /// <summary>Tool-call answers, error envelopes, and non-JSON bodies are not stored.</summary>
    [Theory]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"1"}]}}]}""")]
    [InlineData("""{"error":{"message":"no"}}""")]
    [InlineData("not-json")]
    public void Remember_RejectsUnsafeAnswers(string body)
    {
        var cache = CreateCache(threshold: 0.92);

        var stored = cache.Remember(probe: Probe(embedding: [1f, 0f], scope: ScopeFor(temperature: 0.2)),
            statusCode: 200, responseBody: System.Text.Encoding.UTF8.GetBytes(body),
            contentType: "application/json");

        Assert.False(stored);
        Assert.Equal(0, actual: cache.Count);
    }

    /// <summary>Streaming requests and tool-bearing requests are refused before a scope key exists.</summary>
    [Theory]
    [InlineData("""{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}]}""", "streaming")]
    [InlineData("""{"model":"m","tools":[{"type":"function"}],"messages":[{"role":"user","content":"hi"}]}""", "tools")]
    [InlineData("""{"model":"m","messages":[{"role":"tool","content":"x"},{"role":"user","content":"hi"}]}""", "tool-history")]
    public void TryCreateScopeKey_RefusesUnsafeRequests(string json, string reason)
    {
        var body = JsonNode.Parse(json)!.AsObject();

        var created = SemanticCacheScope.TryCreateScopeKey(body: body, provider: "prov", providerModelId: "m",
            scopeKey: out _, refusalReason: out var refusal);

        Assert.False(created);
        Assert.Equal(expected: reason, actual: refusal);
    }

    /// <summary>A unit vector whose cosine with <c>[1, 0]</c> is about <paramref name="cosine"/>.</summary>
    private static float[] UnitVectorAtCosine(double cosine)
    {
        var y = Math.Sqrt(1 - (cosine * cosine));
        return [(float)cosine, (float)y];
    }

    private static SemanticResponseCache CreateCache(double threshold, bool enabled = true,
        TimeSpan? timeToLive = null, TimeProvider? time = null)
    {
        var options = new SemanticCacheOptions
        {
            Enabled = enabled,
            SimilarityThreshold = threshold,
            TimeToLive = timeToLive ?? TimeSpan.FromHours(1),
            MaxEntries = 256
        };

        return new SemanticResponseCache(options: new MutableOptionsMonitor(options), timeProvider: time);
    }

    private static SemanticCacheOptions EnabledOptions()
    {
        return new SemanticCacheOptions
        {
            Enabled = true,
            SimilarityThreshold = 0.92,
            TimeToLive = TimeSpan.FromHours(1),
            MaxEntries = 256
        };
    }

    private static SemanticCacheOptions DisabledOptions()
    {
        return new SemanticCacheOptions
        {
            Enabled = false,
            SimilarityThreshold = 0.92,
            TimeToLive = TimeSpan.FromHours(1),
            MaxEntries = 256
        };
    }

    private static string ScopeFor(double temperature, string providerModelId = "gpt-real")
    {
        var body = Body(model: "fast", temperature: temperature, user: "ignored by the fingerprint");
        Assert.True(SemanticCacheScope.TryCreateScopeKey(body: body, provider: "prov",
            providerModelId: providerModelId, scopeKey: out var scopeKey, refusalReason: out var reason),
            reason);
        return scopeKey;
    }

    private static SemanticCacheProbe Probe(float[] embedding, string scope)
    {
        return new SemanticCacheProbe(embedding: embedding, scopeKey: scope, modelName: "fast");
    }

    private static JsonObject Body(string model, double temperature, string user, string? system = null)
    {
        var messages = new JsonArray();
        if (system is not null)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
        return new JsonObject
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = messages
        };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }

    private sealed class MutableOptionsMonitor(SemanticCacheOptions initial) : IOptionsMonitor<SemanticCacheOptions>
    {
        public SemanticCacheOptions Current { get; set; } = initial;

        public SemanticCacheOptions CurrentValue => Current;

        public SemanticCacheOptions Get(string? name)
        {
            return Current;
        }

        public IDisposable OnChange(Action<SemanticCacheOptions, string?> listener)
        {
            return new Noop();
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NamedEmbeddingClient(string identity) : IEmbeddingClient
    {
        public string Identity { get; set; } = identity;

        public string ModelIdentity => Identity;

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
