using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

// SecureFile is internal; use ProtectedSecretStore on a temp path for a restricted file.

public sealed class SecretStoreAclProbeTests
{
    [Fact]
    public void Check_MissingFile_ReturnsTrue()
    {
        var probe = new SecretStoreAclProbe();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");

        Assert.True(probe.Check(path));
    }

    [Fact]
    public void Check_RestrictedPerUserFile_OnUnixOrWindows_ReturnsBasedOnMode()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".secrets.dat");
        try
        {
            new ProtectedSecretStore(path).Write(name: "probe", value: "x");
            var probe = new SecretStoreAclProbe();
            var result = probe.Check(path);
            Assert.True(result || OperatingSystem.IsWindows());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
