using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

public sealed class EnrollmentCodeServiceTests
{
    [Fact]
    public void Mint_ThenTryConsume_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");
        var store = new ProtectedSecretStore(path);
        var service = new EnrollmentCodeService(store);
        var code = service.Mint();

        Assert.True(service.TryConsume(code));
    }

    [Fact]
    public void TryConsume_SecondUseOfTheSameCode_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");
        var service = new EnrollmentCodeService(new ProtectedSecretStore(path));
        var code = service.Mint();

        Assert.True(service.TryConsume(code));
        Assert.False(service.TryConsume(code));
    }

    [Fact]
    public void TryConsume_FifthFailure_Invalidates()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");
        var store = new ProtectedSecretStore(path);
        var service = new EnrollmentCodeService(store);
        service.Mint();

        for (var i = 0; i < 5; i++) Assert.False(service.TryConsume("WRONG-CODE"));

        Assert.False(service.TryConsume("WRONG-CODE"));
    }

    [Fact]
    public void TryConsume_AfterExpiry_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");
        var store = new ProtectedSecretStore(path);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new EnrollmentCodeService(store, clock);
        var code = service.Mint();

        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        Assert.False(service.TryConsume(code));
    }
}
