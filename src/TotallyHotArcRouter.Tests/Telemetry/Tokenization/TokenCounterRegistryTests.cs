using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry.Tokenization;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TokenCounterRegistry"/>'s precedence: the real tokenizer wins when it can serve the
/// model, the labelled heuristic answers when it cannot, and a refusal survives only when neither can.
/// </summary>
public class TokenCounterRegistryTests
{
    private static readonly ModelKey AnyModel = new(ModelName: "some-model", Provider: "somewhere");

    [Fact]
    public void TryCountPromptTokens_PrimaryServes_FallbackIsNotConsulted()
    {
        var fallback = new StubCounter(tokens: 999, source: TokenCountSource.Heuristic);
        var registry = new TokenCounterRegistry(
            primary: new StubCounter(tokens: 42, source: TokenCountSource.LocalCalibrated),
            fallback: fallback);

        var counted = registry.TryCountPromptTokens(text: "text", key: AnyModel, tokens: out var tokens,
            source: out var source);

        Assert.True(counted);
        Assert.Equal(expected: 42, actual: tokens);
        Assert.Equal(expected: TokenCountSource.LocalCalibrated, actual: source);
        Assert.Equal(expected: 0, actual: fallback.CallCount);
    }

    [Fact]
    public void TryCountPromptTokens_PrimaryCannotServe_FallsBackToLabelledHeuristic()
    {
        // Closing ADR-0009's cold-start hole: a worse answer that says so beats no answer at all.
        var registry = new TokenCounterRegistry(
            primary: new StubCounter(serves: false),
            fallback: new StubCounter(tokens: 25, source: TokenCountSource.Heuristic));

        var counted = registry.TryCountPromptTokens(text: "text", key: AnyModel, tokens: out var tokens,
            source: out var source);

        Assert.True(counted);
        Assert.Equal(expected: 25, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Heuristic, actual: source);
    }

    [Fact]
    public void TryCountPromptTokens_NeitherServes_Refuses()
    {
        var registry = new TokenCounterRegistry(
            primary: new StubCounter(serves: false),
            fallback: new StubCounter(serves: false));

        var counted = registry.TryCountPromptTokens(text: null, key: AnyModel, tokens: out var tokens,
            source: out var source);

        Assert.False(counted);
        Assert.Equal(expected: 0, actual: tokens);
        Assert.Equal(expected: TokenCountSource.Unavailable, actual: source);
    }

    [Fact]
    public void DefaultComposition_CountsAnOrdinaryPromptWithoutCalibration()
    {
        // The production wiring, exercised end to end: no calibration source configured is the default,
        // and must still yield a real tokenizer count rather than falling through to the heuristic.
        var registry = new TokenCounterRegistry();

        var counted = registry.TryCountPromptTokens(
            text: "Refactor the retry policy so it backs off exponentially.",
            key: new ModelKey(ModelName: "claude-opus-5", Provider: "anthropic"),
            tokens: out var tokens,
            source: out var source);

        Assert.True(counted);
        Assert.True(tokens > 0);
        Assert.Equal(expected: TokenCountSource.LocalProxy, actual: source);
    }

    /// <summary>A counter that returns a fixed answer and records how often it was asked.</summary>
    private sealed class StubCounter : ITokenCounter
    {
        private readonly bool _serves;
        private readonly TokenCountSource _source;
        private readonly int _tokens;

        /// <summary>Initializes a stub returning the given count and source.</summary>
        /// <param name="tokens">The count to report.</param>
        /// <param name="source">The source to report.</param>
        /// <param name="serves">Whether the stub reports success at all.</param>
        public StubCounter(int tokens = 0, TokenCountSource source = TokenCountSource.Unavailable, bool serves = true)
        {
            _tokens = tokens;
            _source = source;
            _serves = serves;
        }

        /// <summary>Gets the number of times this stub was consulted.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc/>
        public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
        {
            CallCount++;
            tokens = _serves ? _tokens : 0;
            source = _serves ? _source : TokenCountSource.Unavailable;
            return _serves;
        }
    }
}
