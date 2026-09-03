using System.Net;
using System.Text;
using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Pins the HTTP-client ownership contract the GUI singleton stores follow: a store disposes the
/// <see cref="HttpClient"/> it built for itself, and never the transport it was handed.
/// <para>
/// Both halves matter and they pull in opposite directions. Leaking is what these stores used to do -
/// each built an <see cref="HttpClient"/> and implemented no <see cref="IDisposable"/> at all, unlike
/// their sibling <see cref="UpdateStore"/>, which had the pattern right. Over-disposing is what the
/// obvious fix would have introduced: <see cref="HttpClient"/>'s single-argument constructor disposes
/// its handler, so a store disposing its own client would have reached past it and disposed a
/// caller-supplied transport - and these stores are registered as DI singletons, so bUnit's
/// <c>TestContext</c> disposes them at the end of every test that registers one.
/// </para>
/// </summary>
public sealed class HttpClientOwnershipTests
{
    [Fact]
    public void UsageStore_Dispose_LeavesACallerSuppliedTransportAlone()
    {
        var handler = new TrackingHandler();
        var store = new UsageStore(transport: handler);

        store.Dispose();

        handler.Disposed.Should().BeFalse(
            "the store owns the HttpClient it built, not the transport it was handed");
    }

    [Fact]
    public void ProviderAdminStore_Dispose_LeavesACallerSuppliedTransportAlone()
    {
        var handler = new TrackingHandler();
        var store = new ProviderAdminStore(transport: handler);

        store.Dispose();

        handler.Disposed.Should().BeFalse(
            "the store owns the HttpClient it built, not the transport it was handed");
    }

    [Fact]
    public void UsageStore_IsDisposable_SoTheDiContainerReclaimsItsHttpClient()
    {
        // Registered via AddSingleton<UsageStore>() in MauiProgram; the container only disposes what
        // advertises IDisposable, so losing this interface would silently reintroduce the leak.
        new UsageStore(transport: new TrackingHandler()).Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void ProviderAdminStore_IsDisposable_SoTheDiContainerReclaimsItsHttpClient()
    {
        new ProviderAdminStore(transport: new TrackingHandler()).Should().BeAssignableTo<IDisposable>();
    }

    /// <summary>
    /// A transport that records whether it was disposed. Answers every request with 200/empty-JSON so a
    /// store constructed over it is usable, though these tests never send through it.
    /// </summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "{}", encoding: Encoding.UTF8,
                    mediaType: "application/json")
            });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}