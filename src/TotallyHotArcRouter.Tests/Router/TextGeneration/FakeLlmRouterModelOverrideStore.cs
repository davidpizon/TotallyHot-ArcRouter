using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Tests.Router.TextGeneration;

/// <summary>
/// A fixed, never-changing override, for tests that don't exercise
/// <see cref="ILlmRouterModelOverrideStore.SetBaseUrlAsync"/>.
/// </summary>
internal sealed class FakeLlmRouterModelOverrideStore(LlmRouterModelOverride overrideValue) : ILlmRouterModelOverrideStore
{
    public LlmRouterModelSnapshot Snapshot { get; } = new(Override: overrideValue, 0);

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}