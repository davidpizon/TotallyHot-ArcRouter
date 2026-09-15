using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Pins the gRPC-channel ownership contract the GUI singleton stores follow: a store disposes the
/// <see cref="Grpc.Net.Client.GrpcChannel"/> it built for itself, and never a caller-injected client's
/// (docs/router/tracked-todos.md #7 - this used to pin an analogous <see cref="HttpClient"/> contract
/// before the provider/usage admin surface moved to gRPC; a caller-injected
/// <see cref="ProviderAdminClient"/>/<see cref="UsageQueryClient"/> built over a fake generated client owns
/// no channel, so there is nothing left for these stores to over-dispose - only the "still IDisposable, and
/// Dispose still doesn't throw" half of the original contract still applies here).
/// </summary>
public sealed class HttpClientOwnershipTests
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
    public void UsageStore_IsDisposable_SoTheDiContainerReclaimsItsChannel()
    {
        // Registered via AddSingleton<UsageStore>() in MauiProgram; the container only disposes what
        // advertises IDisposable, so losing this interface would silently reintroduce a channel leak.
        new UsageStore(client: new UsageQueryClient(new StubUsageAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void ProviderAdminStore_IsDisposable_SoTheDiContainerReclaimsItsChannel()
    {
        new ProviderAdminStore(client: new ProviderAdminClient(new StubProviderAdminServiceClient()))
            .Should().BeAssignableTo<IDisposable>();
    }
}
