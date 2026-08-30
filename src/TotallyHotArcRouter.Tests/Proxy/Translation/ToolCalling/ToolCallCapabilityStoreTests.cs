using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Coverage for the tool-call capability store (<c>docs/router/tool-call-normalization.md</c> Phase 1):
/// round-trip persistence, the confidence gate that decides which classification wins, and the cache
/// invalidation that keeps the request path from serving a stale dialect.
///
/// <para>
/// The confidence cases are the ones that carry real weight. If a cheap startup heuristic could overwrite
/// something learned from a live response, detection would oscillate; if any automatic tier could overwrite
/// an operator's manual pin, the escape hatch for a misfiring model would be worthless.
/// </para>
/// </summary>
public class ToolCallCapabilityStoreTests
{
    // ----- Model capabilities: round trip -----

    [Fact]
    public void GetModelCapability_ReturnsNull_WhenNeverClassified()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        // Null is the signal Phase 4 uses to mean "forward natively and arm the union scanner", so an
        // unknown model must read as null rather than as some default dialect.
        Assert.Null(store.GetModelCapability("lmstudio", "qwen2.5.1-coder-7b-instruct"));
    }

    [Fact]
    public void TryRecordModelCapability_RoundTripsEveryField()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        var detectedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio",
            "qwen2.5.1-coder-7b-instruct",
            "hermes",
            DetectionConfidence.Observed,
            Evidence: "matched <tool_call>",
            ObservationCount: 3,
            DetectedAtUtc: detectedAt)));

        var stored = store.GetModelCapability("lmstudio", "qwen2.5.1-coder-7b-instruct");

        Assert.NotNull(stored);
        Assert.Equal("hermes", stored!.Dialect);
        Assert.Equal(DetectionConfidence.Observed, stored.Confidence);
        Assert.Equal("matched <tool_call>", stored.Evidence);
        Assert.Equal(3, stored.ObservationCount);
        Assert.Equal(detectedAt, stored.DetectedAtUtc);
    }

    [Fact]
    public void GetModelCapability_IsCaseInsensitiveOnBothHalves()
    {
        // Provider keys and model names are matched OrdinalIgnoreCase throughout the routing path
        // (ModelRoutingOptions.Providers, ModelRouteResolver._routes), so the capability lookup - and the
        // COLLATE NOCASE on the backing table - must agree, or one casing silently misses the other's row.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "LMStudio", "Qwen2.5-Coder", "hermes", DetectionConfidence.Template));

        Assert.NotNull(store.GetModelCapability("lmstudio", "qwen2.5-coder"));
        Assert.NotNull(store.GetModelCapability("LMSTUDIO", "QWEN2.5-CODER"));
    }

    [Fact]
    public void ModelsAreKeyedPerProvider_NotByModelNameAlone()
    {
        // The same model served by two providers is two independent classifications - the whole reason the
        // key is composite. A regression keying on the model alone would collapse these into one winner.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "shared-model", "hermes", DetectionConfidence.Observed));
        store.TryRecordModelCapability(new ModelToolCapability(
            "ollama", "shared-model", "mistral", DetectionConfidence.Observed));

        Assert.Equal("hermes", store.GetModelCapability("lmstudio", "shared-model")!.Dialect);
        Assert.Equal("mistral", store.GetModelCapability("ollama", "shared-model")!.Dialect);
    }

    // ----- The confidence gate -----

    [Theory]
    [InlineData(DetectionConfidence.Heuristic, DetectionConfidence.Template)]
    [InlineData(DetectionConfidence.Heuristic, DetectionConfidence.Observed)]
    [InlineData(DetectionConfidence.Template, DetectionConfidence.Observed)]
    [InlineData(DetectionConfidence.Observed, DetectionConfidence.Operator)]
    public void HigherConfidence_OverwritesLower(DetectionConfidence existing, DetectionConfidence incoming)
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", existing));

        Assert.True(store.TryRecordModelCapability(new ModelToolCapability("p", "m", "mistral", incoming)));
        Assert.Equal("mistral", store.GetModelCapability("p", "m")!.Dialect);
        Assert.Equal(incoming, store.GetModelCapability("p", "m")!.Confidence);
    }

    [Theory]
    [InlineData(DetectionConfidence.Observed, DetectionConfidence.Heuristic)]
    [InlineData(DetectionConfidence.Observed, DetectionConfidence.Template)]
    [InlineData(DetectionConfidence.Template, DetectionConfidence.Heuristic)]
    public void LowerConfidence_IsRejected_AndLeavesTheStoredRowIntact(
        DetectionConfidence existing,
        DetectionConfidence incoming)
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", existing));

        Assert.False(store.TryRecordModelCapability(new ModelToolCapability("p", "m", "mistral", incoming)));
        Assert.Equal("hermes", store.GetModelCapability("p", "m")!.Dialect);
        Assert.Equal(existing, store.GetModelCapability("p", "m")!.Confidence);
    }

    [Theory]
    [InlineData(DetectionConfidence.Heuristic)]
    [InlineData(DetectionConfidence.Template)]
    [InlineData(DetectionConfidence.Observed)]
    public void NothingAutomatic_OverwritesAnOperatorPin(DetectionConfidence automatic)
    {
        // The escape hatch: an operator who corrects a misdetected model must not have the next request
        // silently undo it.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "p", "m", "openai-native", DetectionConfidence.Operator));

        Assert.False(store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", automatic)));
        Assert.Equal("openai-native", store.GetModelCapability("p", "m")!.Dialect);
    }

    [Fact]
    public void AnOperatorPin_CanBeReplacedByAnotherOperatorPin()
    {
        // >= not >: an operator changing their mind must still work, and a fresh observation must be able
        // to correct an earlier observation (a model reloaded with a different template).
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Operator));

        Assert.True(store.TryRecordModelCapability(new ModelToolCapability(
            "p", "m", "mistral", DetectionConfidence.Operator)));
        Assert.Equal("mistral", store.GetModelCapability("p", "m")!.Dialect);
    }

    [Fact]
    public void EqualConfidence_Overwrites_SoAFreshObservationCanCorrectAnEarlierOne()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Observed));

        Assert.True(store.TryRecordModelCapability(new ModelToolCapability(
            "p", "m", "mistral", DetectionConfidence.Observed)));
        Assert.Equal("mistral", store.GetModelCapability("p", "m")!.Dialect);
    }

    // ----- Cache behavior -----

    [Fact]
    public void AStoreThatNeverReloaded_ReportsEverythingUnclassified()
    {
        // Mirrors the real lifecycle: the singleton is constructed before the schema exists, so it starts
        // empty on purpose. "Unknown" is the safe direction - it means forward natively and observe.
        using var temp = new TempDatabase();
        var seeded = temp.CreateToolCallCapabilityStore();
        seeded.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Observed));

        var fresh = new ToolCallCapabilityStore(
            new ToolCallCapabilityRepository(temp.Database),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolCallCapabilityStore>.Instance);

        Assert.Null(fresh.GetModelCapability("p", "m"));

        fresh.Reload();
        Assert.NotNull(fresh.GetModelCapability("p", "m"));
    }

    [Fact]
    public void ARejectedWrite_DoesNotLeaveTheCacheShowingTheRejectedValue()
    {
        // The cache is refreshed from what was persisted, not from what was requested - which matters
        // precisely because a write here can be silently rejected by the confidence gate.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Operator));
        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "mistral", DetectionConfidence.Heuristic));

        Assert.Equal("hermes", store.GetModelCapability("p", "m")!.Dialect);
    }

    [Fact]
    public void ARejectedWrite_RefreshesTheCache_SoAnOutOfBandOperatorPinBecomesVisible()
    {
        // The scenario this guards is not hypothetical: until the Phase 6 GUI exists, writing the database
        // directly is the *only* way to create an operator pin. Without a refresh on rejection the pin is
        // invisible to a already-running process - GetModelCapability keeps returning null, so the request
        // path keeps treating the model as unclassified, re-observing it and re-attempting a write that is
        // rejected again on every request, forever. The correction silently does nothing until a restart.
        using var temp = new TempDatabase();

        // Pin written through one store, standing in for an out-of-band edit.
        var operatorSession = temp.CreateToolCallCapabilityStore();
        operatorSession.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen2.5-coder", "openai-native", DetectionConfidence.Operator));

        // A second store that has never seen that write - its snapshot is empty, exactly like a running
        // process whose cache predates the edit.
        var running = new ToolCallCapabilityStore(
            new ToolCallCapabilityRepository(temp.Database),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolCallCapabilityStore>.Instance);

        Assert.Null(running.GetModelCapability("lmstudio", "qwen2.5-coder"));

        // The request path observes the model and tries to record it; the gate rejects the write.
        Assert.False(running.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen2.5-coder", "hermes", DetectionConfidence.Observed)));

        // The rejection must have taught the cache about the pin, so the caller stops re-recording.
        var visible = running.GetModelCapability("lmstudio", "qwen2.5-coder");
        Assert.NotNull(visible);
        Assert.Equal("openai-native", visible!.Dialect);
        Assert.Equal(DetectionConfidence.Operator, visible.Confidence);
    }

    [Fact]
    public void Changed_IsRaisedOnAnAcceptedWrite_ButNotOnARejectedOne()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        var raised = 0;
        store.Changed += () => raised++;

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Operator));
        Assert.Equal(1, raised);

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "mistral", DetectionConfidence.Heuristic));
        Assert.Equal(1, raised);
    }

    // ----- Evidence handling -----

    [Fact]
    public void OverlongEvidence_IsTruncated()
    {
        // Evidence must never carry raw model output - a matched tool call contains the user's own
        // arguments. Callers are told to pass a description, and this cap is the backstop for when they
        // don't.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "p", "m", "hermes", DetectionConfidence.Observed, Evidence: new string('x', 5_000)));

        Assert.Equal(200, store.GetModelCapability("p", "m")!.Evidence!.Length);
    }

    [Fact]
    public void DefaultDetectedAt_IsStampedRatherThanStoredAsMinValue()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Observed));

        Assert.True(store.GetModelCapability("p", "m")!.DetectedAtUtc >= before);
    }

    // ----- Provider endpoint capabilities -----

    [Fact]
    public void GetProviderCapabilities_ReturnsNull_WhenNeverScanned()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        Assert.Null(store.GetProviderCapabilities("lmstudio"));
    }

    [Fact]
    public void SetProviderCapabilities_RoundTripsEveryFlag()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetProviderCapabilities(new ProviderEndpointCapabilities(
            "lmstudio",
            OpenAiCompatible: true,
            LmStudioNative: true,
            OllamaNative: false,
            AnthropicCompatible: false,
            JsonSchemaResponseFormat: true,
            ScannedAtUtc: DateTimeOffset.UtcNow));

        var stored = store.GetProviderCapabilities("LMSTUDIO");

        Assert.NotNull(stored);
        Assert.True(stored!.OpenAiCompatible);
        Assert.True(stored.LmStudioNative);
        Assert.False(stored.OllamaNative);
        Assert.False(stored.AnthropicCompatible);
        Assert.True(stored.JsonSchemaResponseFormat);
        Assert.Null(stored.ScanError);
    }

    [Fact]
    public void SetProviderCapabilities_IsUnconditional_UnlikeTheModelPath()
    {
        // Endpoint flavors are a direct observation of what the server answered, so there is no
        // weaker-source problem for a confidence gate to solve - a rescan simply wins.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetProviderCapabilities(new ProviderEndpointCapabilities(
            "p", true, true, true, true, true, DateTimeOffset.UtcNow));
        store.SetProviderCapabilities(new ProviderEndpointCapabilities(
            "p", false, false, false, false, false, DateTimeOffset.UtcNow, ScanError: "connection refused"));

        var stored = store.GetProviderCapabilities("p");
        Assert.False(stored!.OpenAiCompatible);
        Assert.False(stored.JsonSchemaResponseFormat);
        Assert.Equal("connection refused", stored.ScanError);
    }

    // ----- ObservationCount is the database's to increment, not the caller's -----

    [Fact]
    public void EachObservation_IncrementsTheCount_EvenThoughEveryCallerPassesOne()
    {
        // The caller never reads-then-writes: it passes a flat 1 every time and SQLite adds it to whatever
        // is already stored. That is what makes concurrent first-responses for the same model - the exact
        // case tier-4 observation exists for - count once each rather than all landing on 1.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(store.TryRecordModelCapability(new ModelToolCapability(
                "lmstudio", "qwen", "hermes", DetectionConfidence.Observed, ObservationCount: 1)));
        }

        Assert.Equal(3, store.GetModelCapability("lmstudio", "qwen")!.ObservationCount);
    }

    [Fact]
    public void AMetadataWrite_LeavesTheObservationCountAlone()
    {
        // A template or model-id classification is metadata read off the provider, not an observation of a
        // response, so a rescan must neither bump the counter nor reset it to whatever it happened to pass.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen", "hermes", DetectionConfidence.Heuristic, ObservationCount: 5));
        store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen", "mistral", DetectionConfidence.Heuristic, ObservationCount: 0));

        var stored = store.GetModelCapability("lmstudio", "qwen")!;
        Assert.Equal("mistral", stored.Dialect);
        Assert.Equal(5, stored.ObservationCount);
    }

    // ----- ObservationCount at both trust boundaries -----

    [Fact]
    public void ANegativeObservationCountFromACaller_Throws()
    {
        // A programming error, surfaced loudly - the same call ProviderBudgetStore.SetBudget makes for a
        // negative cap. Contrast with the on-disk case below, which must not take the proxy down.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.TryRecordModelCapability(
            new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Observed, ObservationCount: -1)));
    }

    [Theory]
    [InlineData(-5L, 0)]                    // corrupted negative
    [InlineData(3_000_000_000L, int.MaxValue)] // beyond int range; an unchecked cast would wrap negative
    [InlineData(7L, 7)]                     // ordinary value passes through
    public void AnOutOfRangeObservationCountOnDisk_IsClampedRatherThanWrapped(long stored, int expected)
    {
        // SQLite INTEGER is 64-bit, so a hand-edited row can hold what no int can represent. Degrading here
        // matches how ParseTimestamp and ParseConfidence already treat unparseable stored values.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        store.TryRecordModelCapability(new ModelToolCapability("p", "m", "hermes", DetectionConfidence.Observed));

        using (var connection = temp.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE model_tool_capabilities SET observation_count = $count WHERE provider_key = 'p';";
            command.Parameters.AddWithValue("$count", stored);
            command.ExecuteNonQuery();
        }

        store.Reload();

        Assert.Equal(expected, store.GetModelCapability("p", "m")!.ObservationCount);
    }

    // ----- The composite key -----

    [Fact]
    public void DefaultModelCapabilityKey_IsUsableInADictionary_WithoutThrowing()
    {
        // Every struct has an unsuppressable `default` whose reference fields are null, which nullable
        // reference types cannot prevent - and StringComparer.GetHashCode(null) throws. A public key type
        // has to be total here, or merely inserting a default into a dictionary crashes.
        var key = default(ModelCapabilityKey);

        // The exact hash is an implementation detail of HashCode.Combine and deliberately not pinned -
        // what matters is that computing it, and using the key, complete without throwing.
        _ = key.GetHashCode();

        var map = new Dictionary<ModelCapabilityKey, string> { [key] = "value" };

        Assert.Equal("value", map[default]);
        Assert.True(key.Equals(default));
        Assert.Equal(key.GetHashCode(), default(ModelCapabilityKey).GetHashCode());
    }

    [Fact]
    public void ModelCapabilityKey_EqualityAndHashing_AreCaseInsensitiveOnBothHalves()
    {
        var lower = new ModelCapabilityKey("lmstudio", "qwen");
        var upper = new ModelCapabilityKey("LMStudio", "QWEN");

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void ModelCapabilityKey_DoesNotConflateATransposedPair()
    {
        // The two halves are not interchangeable; a key that hashed them symmetrically would collapse
        // provider "a"/model "b" into provider "b"/model "a".
        Assert.NotEqual(new ModelCapabilityKey("a", "b"), new ModelCapabilityKey("b", "a"));
    }

    // ----- Cross-check against the Phase 0 registry -----

    [Fact]
    public void AnUnknownPersistedDialect_DoesNotResolve_AndSoDegradesToNoScanning()
    {
        // A row written by a newer build naming a dialect this one has never heard of must degrade to
        // "don't scan", not throw on load. The store stores the raw string; the registry is what decides
        // whether it means anything.
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.TryRecordModelCapability(new ModelToolCapability(
            "p", "m", "dialect-from-the-future", DetectionConfidence.Observed));

        var stored = store.GetModelCapability("p", "m");
        Assert.Equal("dialect-from-the-future", stored!.Dialect);
        Assert.False(ToolCallDialectRegistry.TryGet(stored.Dialect, out var resolved));
        Assert.False(resolved.IsScannable);
    }

    [Fact]
    public void EveryBuiltInDialectName_RoundTripsThroughTheStore()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        foreach (var dialect in ToolCallDialectRegistry.All)
        {
            store.TryRecordModelCapability(new ModelToolCapability(
                "p", dialect.Name, dialect.Name, DetectionConfidence.Operator));

            var stored = store.GetModelCapability("p", dialect.Name);
            Assert.NotNull(stored);
            Assert.True(ToolCallDialectRegistry.TryGet(stored!.Dialect, out var resolved));
            Assert.Equal(dialect.Name, resolved.Name);
        }
    }

    // ----- Context windows: the separation invariants from ADR-0002 -----

    [Fact]
    public void GetModelContextWindow_ReturnsNull_WhenNeverProbed()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        Assert.Null(store.GetModelContextWindow("lmstudio", "qwen2.5.1-coder-7b-instruct"));
    }

    [Fact]
    public void SetModelContextWindow_RoundTripsEveryField()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();
        var detectedAt = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        store.SetModelContextWindow(new ModelContextWindow(
            "lmstudio", "qwen-local", 32768, "qwen2", "LM Studio /api/v0/models.", detectedAt));

        var stored = store.GetModelContextWindow("lmstudio", "qwen-local");

        Assert.NotNull(stored);
        Assert.Equal(32768, stored!.ContextLength);
        Assert.Equal("qwen2", stored.Architecture);
        Assert.Equal("LM Studio /api/v0/models.", stored.Evidence);
        Assert.Equal(detectedAt, stored.DetectedAtUtc);
    }

    // Proves the COLLATE NOCASE key columns and ModelCapabilityKey's comparer carried over to the new
    // table - a lookup that missed on casing would silently report every window as unknown.
    [Fact]
    public void GetModelContextWindow_IsCaseInsensitiveOnBothHalves()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetModelContextWindow(new ModelContextWindow("LMStudio", "Qwen-Local", 8192));

        Assert.NotNull(store.GetModelContextWindow("lmstudio", "qwen-local"));
    }

    // The first corruption path ADR-0002 exists to prevent: ToolCallObservationRecorder builds a fresh
    // capability from the request path with no window to supply. Sharing a row would null the window on the
    // first live tool call.
    [Fact]
    public void RecordingADialectObservation_DoesNotDisturbTheContextWindow()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetModelContextWindow(new ModelContextWindow("lmstudio", "qwen-local", 32768, "qwen2"));
        store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen-local", "hermes", DetectionConfidence.Observed, ObservationCount: 1));

        Assert.Equal(32768, store.GetModelContextWindow("lmstudio", "qwen-local")?.ContextLength);
    }

    // The second: ClearModelCapability DELETEs the dialect row. An operator resetting a dialect override
    // must not destroy an unrelated probed window as a side effect.
    [Fact]
    public void ClearModelCapability_LeavesTheContextWindowIntact()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetModelContextWindow(new ModelContextWindow("lmstudio", "qwen-local", 32768, "qwen2"));
        store.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen-local", "hermes", DetectionConfidence.Template));

        store.ClearModelCapability("lmstudio", "qwen-local");

        Assert.Null(store.GetModelCapability("lmstudio", "qwen-local"));
        Assert.Equal(32768, store.GetModelContextWindow("lmstudio", "qwen-local")?.ContextLength);
    }

    // No confidence ladder applies to a context length - a model reloaded under a different num_ctx
    // genuinely has a different window, so a gate would freeze the first reading forever.
    [Fact]
    public void SetModelContextWindow_LastWriteWins_WithNoConfidenceGate()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        store.SetModelContextWindow(new ModelContextWindow("lmstudio", "qwen-local", 32768, "qwen2"));
        store.SetModelContextWindow(new ModelContextWindow("lmstudio", "qwen-local", 8192, "qwen2"));

        Assert.Equal(8192, store.GetModelContextWindow("lmstudio", "qwen-local")?.ContextLength);
    }

    // Rejected at the caller boundary rather than coerced, mirroring TryRecordModelCapability's treatment
    // of a negative observation count. A corrupt row on disk is skipped instead; see the repository.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetModelContextWindow_NonPositiveLength_Throws(int contextLength)
    {
        using var temp = new TempDatabase();
        var store = temp.CreateToolCallCapabilityStore();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.SetModelContextWindow(new ModelContextWindow("lmstudio", "qwen-local", contextLength)));
    }
}
