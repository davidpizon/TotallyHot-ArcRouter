using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Pins the disposal contract the GUI singleton stores follow: a store disposes only what it built for
/// itself (see <see cref="AdminStoreBase{TClient}.Dispose()"/>), never its client - the admin clients own
/// no channel, since every one is built over the shared call invoker or a caller-supplied generated client.
/// Both stores stay <see cref="IDisposable"/> so the DI container reclaims anything they do own.
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
    public void UsageStore_IsDisposable_SoTheDiContainerReclaimsWhatItOwns()
    {
        new UsageStore(client: new UsageQueryClient(new StubUsageAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void ProviderAdminStore_IsDisposable_SoTheDiContainerReclaimsWhatItOwns()
    {
        new ProviderAdminStore(client: new ProviderAdminClient(new StubProviderAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }
}
