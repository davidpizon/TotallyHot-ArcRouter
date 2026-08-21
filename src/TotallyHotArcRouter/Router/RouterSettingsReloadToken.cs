using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The <see cref="Models.RoutingOptions"/> live-reload signal (docs/router/self-organizing-classification-plan.md
/// Phase T6): a swappable <see cref="IChangeToken"/> source that <see cref="RouterSettingsAdminGrpcService"/>
/// triggers after a successful <c>router_settings</c> write, so <c>IOptionsMonitor&lt;RoutingOptions&gt;</c>
/// recomputes <c>CurrentValue</c> - re-running every registered
/// <c>IConfigureOptions&lt;RoutingOptions&gt;</c> step, including <see cref="RouterSettingsConfigureOptions"/>
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
public sealed class RouterSettingsReloadToken : IOptionsChangeTokenSource<Models.RoutingOptions>
{
    private CancellationTokenSource _cts = new();

    /// <inheritdoc />
    /// <remarks><see langword="null"/>: this source applies to the default (unnamed) <see cref="Models.RoutingOptions"/> instance only.</remarks>
    public string? Name => null;

    /// <inheritdoc />
    public IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

    /// <summary>
    /// Signals every current <see cref="IChangeToken"/> handed out by <see cref="GetChangeToken"/>, then
    /// replaces the underlying source so the next call returns a fresh, un-signaled token. Idempotent to
    /// call repeatedly; each call produces exactly one more reload.
    /// </summary>
    public void Trigger()
    {
        var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }
}
