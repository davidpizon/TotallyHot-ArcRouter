using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Pins the disposal contract the GUI singleton stores follow: a store disposes the client it built
/// for itself, and never a caller-injected client's. A caller-injected
/// <see cref="ProviderAdminClient"/>/<see cref="UsageQueryClient"/> built over a fake generated client owns
/// no channel, so <see cref="AdminStoreBase{TClient}.Dispose"/> must not throw. Both stores stay
/// <see cref="IDisposable"/> so the DI container reclaims anything they own.
/// </summary>
public sealed class AdminStoreDisposalTests
{
    [Fact]
    public void UsageStore_Dispose_DoesNotThrow_WhenGivenAnInjectedClient()
    {
        var store = new UsageStore(client: new UsageQueryClient(new StubUsageAdminServiceClient()));

        var act = store.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void ProviderAdminStore_Dispose_DoesNotThrow_WhenGivenAnInjectedClient()
    {
        var store = new ProviderAdminStore(client: new ProviderAdminClient(new StubProviderAdminServiceClient()));

        var act = store.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void UsageStore_IsDisposable_SoTheDiContainerReclaimsItsClient()
    {
        new UsageStore(client: new UsageQueryClient(new StubUsageAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void ProviderAdminStore_IsDisposable_SoTheDiContainerReclaimsItsClient()
    {
        new ProviderAdminStore(client: new ProviderAdminClient(new StubProviderAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }
}
