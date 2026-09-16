using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// A purely in-memory <see cref="IManagementTokenProvider"/> for tests that need a token gate without
/// touching the filesystem - unlike <see cref="ManagementTokenProvider"/>, nothing here is persisted.
/// </summary>
public sealed class FakeManagementTokenProvider : IManagementTokenProvider
{
    private readonly string _initialToken;

    /// <summary>Initializes a new instance of the <see cref="FakeManagementTokenProvider"/> class.</summary>
    /// <param name="initialToken">The value <see cref="CurrentToken"/> starts as before any <see cref="Regenerate"/> call.</param>
    public FakeManagementTokenProvider(string initialToken)
    {
        _initialToken = initialToken;
        CurrentToken = initialToken;
    }

    /// <inheritdoc/>
    public string CurrentToken { get; private set; }

    /// <inheritdoc/>
    public int Generation { get; private set; }

    /// <inheritdoc/>
    public bool Verify(string? presented)
    {
        return ManagementAccessToken.Verify(presented: presented, expected: CurrentToken);
    }

    /// <inheritdoc/>
    public string Regenerate()
    {
        CurrentToken = $"{_initialToken}-rotated-{Generation + 1}";
        Generation++;
        return CurrentToken;
    }
}
