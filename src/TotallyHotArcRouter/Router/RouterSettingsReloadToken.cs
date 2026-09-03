using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The live-reload signal for the three settings types backed by <c>router_settings</c> -
/// <see cref="Models.RoutingOptions"/>, <see cref="Judge.JudgeOptions"/>, and
/// <see cref="Transcripts.TranscriptOptions"/>
/// (docs/router/self-organizing-classification-plan.md Phase T6): a swappable <see cref="IChangeToken"/>
/// source that <see cref="RouterSettingsAdminGrpcService"/> triggers after a successful
/// <c>router_settings</c> write, so each <c>IOptionsMonitor</c> recomputes <c>CurrentValue</c> - re-running
/// every registered configure step, including <see cref="RouterSettingsConfigureOptions"/>,
/// <see cref="Judge.JudgeSettingsConfigureOptions"/>, and <see cref="Transcripts.TranscriptSettingsConfigureOptions"/>
/// - without a process restart.
/// </summary>
/// <remarks>
/// Registered as both a plain singleton (so <see cref="RouterSettingsAdminGrpcService"/> can call
/// <see cref="Trigger"/> directly) and as <see cref="IOptionsChangeTokenSource{TOptions}"/> (so the options
/// system subscribes to it). Built on a swappable <see cref="CancellationTokenSource"/> rather than the
/// internal <c>ConfigurationReloadToken</c> type <c>Microsoft.Extensions.Configuration</c> uses internally
/// for the same purpose - this achieves the identical "cancel the old token, hand out a fresh one" pattern
/// with only a public, documented primitive.
/// </remarks>
public sealed class RouterSettingsReloadToken
    : IOptionsChangeTokenSource<RoutingOptions>,
        IOptionsChangeTokenSource<JudgeOptions>,
        IOptionsChangeTokenSource<TranscriptOptions>
{
    private CancellationTokenSource _cts = new();

    /// <inheritdoc cref="IOptionsChangeTokenSource{TOptions}.Name"/>
    /// <remarks>
    /// <see langword="null"/>: this source applies to the default (unnamed) instance only. One
    /// implementation satisfies all three interfaces - their members are identical in signature - so a
    /// single <see cref="Trigger"/> reloads <see cref="Models.RoutingOptions"/>, <see cref="Judge.JudgeOptions"/>,
    /// and <see cref="Transcripts.TranscriptOptions"/> together. That is exactly what the one Save button
    /// behind all three wants: the settings share a table (<see cref="RouterSettingsStore"/>) and are
    /// written in one transaction, so reloading them in lockstep is simpler than, and indistinguishable
    /// from, three independent sources.
    /// </remarks>
    public string? Name => null;

    /// <inheritdoc cref="IOptionsChangeTokenSource{TOptions}.GetChangeToken"/>
    public IChangeToken GetChangeToken()
    {
        return new CancellationChangeToken(_cts.Token);
    }

    /// <summary>
    /// Signals every current <see cref="IChangeToken"/> handed out by <see cref="GetChangeToken"/>, then
    /// replaces the underlying source so the next call returns a fresh, un-signaled token. Idempotent to
    /// call repeatedly; each call produces exactly one more reload.
    /// </summary>
    public void Trigger()
    {
        var previous = Interlocked.Exchange(location1: ref _cts, value: new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }
}