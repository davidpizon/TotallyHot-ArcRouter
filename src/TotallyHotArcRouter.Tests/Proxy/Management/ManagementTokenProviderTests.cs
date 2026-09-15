using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementTokenProvider"/>: it loads/creates the same way
/// <see cref="ManagementAccessToken.GetOrCreate"/> does, <see cref="IManagementTokenProvider.Regenerate"/>
/// persists a new value and bumps the generation, and a second provider over the same store picks up the
/// rotated value.
/// </summary>
public sealed class ManagementTokenProviderTests
{
    private static ProtectedSecretStore NewStore()
    {
        return new ProtectedSecretStore(Path.Combine(Path.GetTempPath(), "arcrouter-tests",
            Guid.NewGuid().ToString("N"), "secrets.dat"));
    }

    [Fact]
    public void CurrentToken_OnFirstConstruction_MatchesTheStoredSecret()
    {
        var store = NewStore();

        var provider = new ManagementTokenProvider(store);

        store.TryRead(name: ManagementAccessToken.SecretName, value: out var stored);
        Assert.Equal(expected: stored, actual: provider.CurrentToken);
    }

    [Fact]
    public void Generation_StartsAtZero()
    {
        var provider = new ManagementTokenProvider(NewStore());

        Assert.Equal(expected: 0, actual: provider.Generation);
    }

    [Fact]
    public void Regenerate_ChangesTheTokenAndPersistsIt()
    {
        var store = NewStore();
        var provider = new ManagementTokenProvider(store);
        var original = provider.CurrentToken;

        var regenerated = provider.Regenerate();

        Assert.NotEqual(expected: original, actual: regenerated);
        Assert.Equal(expected: regenerated, actual: provider.CurrentToken);
        store.TryRead(name: ManagementAccessToken.SecretName, value: out var stored);
        Assert.Equal(expected: regenerated, actual: stored);
    }

    [Fact]
    public void Regenerate_IncrementsGeneration()
    {
        var provider = new ManagementTokenProvider(NewStore());

        provider.Regenerate();
        provider.Regenerate();

        Assert.Equal(expected: 2, actual: provider.Generation);
    }

    [Fact]
    public void Verify_OldTokenAfterRegenerate_ReturnsFalse()
    {
        var provider = new ManagementTokenProvider(NewStore());
        var original = provider.CurrentToken;

        provider.Regenerate();

        Assert.False(provider.Verify(original));
        Assert.True(provider.Verify(provider.CurrentToken));
    }

    [Fact]
    public void SecondProvider_AfterRegenerate_ObservesTheRotatedToken()
    {
        var store = NewStore();
        var first = new ManagementTokenProvider(store);
        var rotated = first.Regenerate();

        var second = new ManagementTokenProvider(store);

        Assert.Equal(expected: rotated, actual: second.CurrentToken);
    }
}
