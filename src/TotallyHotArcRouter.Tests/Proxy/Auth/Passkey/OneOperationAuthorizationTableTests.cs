using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

public sealed class OneOperationAuthorizationTableTests
{
    [Fact]
    public void TryConsume_SingleUse_SecondCallFails()
    {
        var table = new OneOperationAuthorizationTable();
        var token = table.Issue(GatedOperation.GetManagementToken, GatedOperation.GetManagementTokenParameters());

        Assert.True(table.TryConsume(token, GatedOperation.GetManagementToken,
            GatedOperation.GetManagementTokenParameters()));
        Assert.False(table.TryConsume(token, GatedOperation.GetManagementToken,
            GatedOperation.GetManagementTokenParameters()));
    }

    [Fact]
    public void TryConsume_WrongBinding_FailsAndConsumes()
    {
        var table = new OneOperationAuthorizationTable();
        var token = table.Issue(GatedOperation.Export, GatedOperation.ExportParameters());

        Assert.False(table.TryConsume(token, GatedOperation.Import, GatedOperation.ImportParameters("abc")));
        Assert.False(table.TryConsume(token, GatedOperation.Export, GatedOperation.ExportParameters()));
    }
}
