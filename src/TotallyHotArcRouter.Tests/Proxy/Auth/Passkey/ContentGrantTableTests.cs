using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

public sealed class ContentGrantTableTests
{
    [Fact]
    public void IssueGrant_IsValidUntilExpiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var table = new ContentGrantTable(new PasskeyOptions { ContentGrantMinutes = 15 }, clock);
        var token = table.IssueGrant();

        Assert.True(table.IsValid(token));

        clock.UtcNow = clock.UtcNow.AddMinutes(16);
        Assert.False(table.IsValid(token));
    }

    [Fact]
    public void RevokeAll_InvalidatesOutstandingGrants()
    {
        var table = new ContentGrantTable(new PasskeyOptions());
        var token = table.IssueGrant();
        table.RevokeAll();

        Assert.False(table.IsValid(token));
    }

    [Fact]
    public void Revoke_InvalidatesOnlyTheNamedGrant()
    {
        var table = new ContentGrantTable(new PasskeyOptions());
        var kept = table.IssueGrant();
        var revoked = table.IssueGrant();

        Assert.True(table.Revoke(revoked));
        Assert.False(table.IsValid(revoked));
        Assert.True(table.IsValid(kept));
    }
}
