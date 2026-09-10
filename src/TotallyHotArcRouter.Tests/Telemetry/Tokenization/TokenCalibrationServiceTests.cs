using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry.Tokenization;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TokenCalibrationService"/>: that it records samples when enabled, and - the claim
/// ADR-0009 rests on - that it opens no socket at all when it is not.
/// </summary>
public class TokenCalibrationServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunCycleAsync_NoCountClientConfigured_MakesNoCallAndRecordsNothing()
    {
        // The ADR's central safety claim: with calibration off, the composition root supplies no client and
        // this service is completely inert. No egress, no samples.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = BuildStore(temp);
        var service = Build(temp: temp, store: store, countClient: null,
            rows: [Row(model: "claude-opus-5", prompt: "a prompt worth counting")]);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 0, actual: recorded);
    }

    [Fact]
    public async Task RunCycleAsync_RequestsInFlight_SkipsTheCycleEntirely()
    {
        // Calibration must never contend with served traffic; under load the backlog simply lags.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var handler = new CountingHandler(120);
        var gauge = new InFlightRequestGauge();
        using var tracked = gauge.Track();

        var service = Build(temp: temp, store: BuildStore(temp), countClient: BuildClient(handler),
            rows: [Row(model: "claude-opus-5", prompt: "a prompt worth counting")], gauge: gauge);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 0, actual: recorded);
        Assert.Equal(expected: 0, actual: handler.CallCount);
    }

    [Fact]
    public async Task RunCycleAsync_Enabled_RecordsASampleAndLearnsAFactor()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = BuildStore(temp: temp, minSamplesForTrust: 1);
        var service = Build(temp: temp, store: store, countClient: BuildClient(new CountingHandler(500)),
            rows: [Row(model: "claude-opus-5", prompt: "a prompt worth counting")]);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 1, actual: recorded);
        Assert.True(store.TryGetTrustedFactor(
            key: new ArcRouter.PriceCatalog.ModelKey(ModelName: "claude-opus-5", Provider: "anthropic"),
            factor: out var factor));
        Assert.True(factor > 0d);
    }

    [Fact]
    public async Task RunCycleAsync_RowsWithoutCapturedPrompts_AreSkipped()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var handler = new CountingHandler(500);
        var service = Build(temp: temp, store: BuildStore(temp), countClient: BuildClient(handler),
            rows: [Row(model: "claude-opus-5", prompt: null), Row(model: "claude-opus-5", prompt: "  ")]);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 0, actual: recorded);
        Assert.Equal(expected: 0, actual: handler.CallCount);
    }

    [Fact]
    public async Task RunCycleAsync_NonAnthropicModels_AreSkipped()
    {
        // Only Anthropic has a counting client implemented; an OpenAI model's local count is already
        // correct for its own encoding, so there is nothing to calibrate.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var handler = new CountingHandler(500);
        var service = Build(temp: temp, store: BuildStore(temp), countClient: BuildClient(handler),
            rows: [Row(model: "gpt-4o", prompt: "a prompt worth counting")]);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 0, actual: recorded);
        Assert.Equal(expected: 0, actual: handler.CallCount);
    }

    [Fact]
    public async Task RunCycleAsync_HonorsTheMaxSamplesPerCycleBound()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var handler = new CountingHandler(500);
        IReadOnlyList<SessionTranscript> rows =
        [
            .. Enumerable.Range(start: 0, count: 20)
                .Select(i => Row(model: "claude-opus-5", prompt: $"prompt number {i}"))
        ];

        var service = Build(temp: temp, store: BuildStore(temp), countClient: BuildClient(handler), rows: rows,
            maxSamplesPerCycle: 3);

        var recorded = await service.RunCycleAsync(Ct);

        Assert.Equal(expected: 3, actual: recorded);
        Assert.Equal(expected: 3, actual: handler.CallCount);
    }

    /// <summary>Builds one recent-transcript row carrying the given model and prompt.</summary>
    /// <param name="model">The routed model name.</param>
    /// <param name="prompt">The captured prompt text, or <see langword="null"/>.</param>
    /// <returns>The row.</returns>
    private static SessionTranscript Row(string model, string? prompt)
    {
        return new SessionTranscript(
            Id: 1, SessionId: "s", CorrelationId: "s:1", CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: model, RoutedModel: model, PromptText: prompt, ResponseText: null, Cost: null,
            InputTokens: null, OutputTokens: null, MemoryEntryId: null);
    }

    /// <summary>Builds a calibration store over the temp database.</summary>
    /// <param name="temp">The temp database fixture.</param>
    /// <param name="minSamplesForTrust">Samples required before a factor is applied.</param>
    /// <returns>The store.</returns>
    private static TokenCalibrationStore BuildStore(TempDatabase temp, int minSamplesForTrust = 10)
    {
        return new TokenCalibrationStore(database: temp.Database,
            options: new StaticMonitor(new TokenizationOptions { MinSamplesForTrust = minSamplesForTrust }));
    }

    /// <summary>Builds a count client over a stub transport.</summary>
    /// <param name="handler">The stub transport.</param>
    /// <returns>The client.</returns>
    private static AnthropicTokenCountClient BuildClient(CountingHandler handler)
    {
        return new AnthropicTokenCountClient(httpClient: new HttpClient(handler), apiKey: "test-key");
    }

    /// <summary>Builds the service under test over stub collaborators.</summary>
    /// <param name="temp">The temp database fixture.</param>
    /// <param name="store">The calibration store.</param>
    /// <param name="countClient">The count client, or <see langword="null"/> for the disabled case.</param>
    /// <param name="rows">The recent transcript rows the fake store returns.</param>
    /// <param name="gauge">The in-flight gauge, or <see langword="null"/> for no hard pause.</param>
    /// <param name="maxSamplesPerCycle">The per-cycle sample bound.</param>
    /// <returns>The service.</returns>
    private static TokenCalibrationService Build(
        TempDatabase temp,
        TokenCalibrationStore store,
        AnthropicTokenCountClient? countClient,
        IReadOnlyList<SessionTranscript> rows,
        InFlightRequestGauge? gauge = null,
        int maxSamplesPerCycle = 20)
    {
        return new TokenCalibrationService(
            logger: NullLogger<TokenCalibrationService>.Instance,
            transcriptStore: new FakeTranscriptStore(rows),
            store: store,
            routeResolver: new FakeRouteResolver(),
            options: new StaticMonitor(new TokenizationOptions
            {
                CalibrationEnabled = true,
                MaxSamplesPerCycle = maxSamplesPerCycle
            }),
            countClient: countClient,
            inFlightGauge: gauge);
    }

    /// <summary>A stub transport returning a fixed token count and recording how often it was called.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly int _tokens;

        /// <summary>Initializes the stub with the count it reports.</summary>
        /// <param name="tokens">The <c>input_tokens</c> value to return.</param>
        public CountingHandler(int tokens)
        {
            _tokens = tokens;
        }

        /// <summary>Gets how many requests reached this handler.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: $$"""{"input_tokens": {{_tokens}}}""",
                    encoding: Encoding.UTF8, mediaType: "application/json")
            });
        }
    }

    /// <summary>A transcript store that serves a fixed list of recent rows and nothing else.</summary>
    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        private readonly IReadOnlyList<SessionTranscript> _rows;

        /// <summary>Initializes the fake with the rows it returns.</summary>
        /// <param name="rows">The recent rows.</param>
        public FakeTranscriptStore(IReadOnlyList<SessionTranscript> rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SessionTranscript>> ListSessionsAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SessionTranscript>>([.. _rows.Take(limit)]);
        }

        /// <inheritdoc/>
        public Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(string scorerVersion, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task UpdateOutcomeAsync(string correlationId, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task MarkQualityRescannedAsync(long transcriptId, string scorerVersion, double? score,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>A route resolver mapping <c>claude-*</c> to Anthropic and <c>gpt-*</c> to OpenAI.</summary>
    private sealed class FakeRouteResolver : IModelRouteResolver
    {
        /// <inheritdoc/>
        public bool TryResolve(string? modelName, [NotNullWhen(true)] out ResolvedModelRoute? route)
        {
            route = null;
            if (string.IsNullOrWhiteSpace(modelName)) return false;

            var provider = modelName.StartsWith(value: "claude", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? "anthropic"
                : "openai";

            route = new ResolvedModelRoute(
                ModelName: modelName, Provider: provider, ProviderModelId: modelName,
                UpstreamBaseUrl: new Uri("https://example.invalid"), AuthHeaderName: "x-api-key",
                ExtraHeaders: []);
            return true;
        }

        /// <inheritdoc/>
        public IReadOnlyList<AvailableModel> ListModels()
        {
            return [];
        }

        /// <inheritdoc/>
        public bool IsProviderEnabled(string provider)
        {
            return true;
        }

        /// <inheritdoc/>
        public bool IsModelEnabled(string modelName)
        {
            return true;
        }
    }

    /// <summary>A minimal <see cref="IOptionsMonitor{TOptions}"/> returning one fixed value.</summary>
    private sealed class StaticMonitor : IOptionsMonitor<TokenizationOptions>
    {
        /// <summary>Initializes the monitor with the value it always returns.</summary>
        /// <param name="value">The options value.</param>
        public StaticMonitor(TokenizationOptions value)
        {
            CurrentValue = value;
        }

        /// <inheritdoc/>
        public TokenizationOptions CurrentValue { get; }

        /// <inheritdoc/>
        public TokenizationOptions Get(string? name)
        {
            return CurrentValue;
        }

        /// <inheritdoc/>
        public IDisposable? OnChange(Action<TokenizationOptions, string?> listener)
        {
            return null;
        }
    }
}
