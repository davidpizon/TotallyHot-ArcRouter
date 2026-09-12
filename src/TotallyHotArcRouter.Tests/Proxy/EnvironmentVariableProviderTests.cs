using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>Covers <see cref="EnvironmentVariableProvider"/>'s pass-through to <see cref="Environment"/>.</summary>
public class EnvironmentVariableProviderTests
{
    [Fact]
    public void GetVariable_VariableIsSet_ReturnsItsValue()
    {
        var name = $"ARCROUTER_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "some-value");
        try
        {
            IEnvironmentVariableProvider provider = new EnvironmentVariableProvider();

            Assert.Equal(expected: "some-value", actual: provider.GetVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void GetVariable_VariableIsNotSet_ReturnsNull()
    {
        var name = $"ARCROUTER_TEST_{Guid.NewGuid():N}";
        IEnvironmentVariableProvider provider = new EnvironmentVariableProvider();

        Assert.Null(provider.GetVariable(name));
    }
}
