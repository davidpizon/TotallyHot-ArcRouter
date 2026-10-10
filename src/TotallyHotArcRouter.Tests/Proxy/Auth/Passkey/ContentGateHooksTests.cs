using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Pins <see cref="ContentGateHooks"/> so #165/#176 can call the binding helpers without re-deriving
/// operation names. The hooks have no production callers yet; these tests are the live callers that keep
/// the helpers from reading as dead code in the Qodana scan.
/// </summary>
public sealed class ContentGateHooksTests
{
    private const string Destination = @"C:\exports\conversations.zip";

    private static readonly ConversationExportFilter Filter = new(
        From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        Harness: "claude-code");

    [Fact]
    public void RequireExport_ConsumesATokenBoundToTheFilterAndDestination()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters(Filter, Destination));

        ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination);

        Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination));
    }

    [Fact]
    public void RequireExport_WithoutAToken_IsRefused()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);

        var noToken = Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, null, Filter, Destination));
        var emptyToken = Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, "", Filter, Destination));

        Assert.Equal(StatusCode.Unauthenticated, noToken.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, emptyToken.StatusCode);
    }

    [Fact]
    public void RequireExport_ASpentToken_IsRefused()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters(Filter, Destination));
        ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination);

        var spent = Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination));

        Assert.Equal(StatusCode.Unauthenticated, spent.StatusCode);
    }

    [Fact]
    public void RequireExport_ATokenForADifferentFilter_IsRefused_AndTheMismatchBurnsIt()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters(Filter, Destination));

        var wider = Filter with { Harness = null };
        var mismatch = Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token, wider, Destination));

        Assert.Equal(StatusCode.Unauthenticated, mismatch.StatusCode);

        // A failed attempt spends the token (OneOperationAuthorizationTable), so the approved export cannot follow.
        Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination));
    }

    [Fact]
    public void RequireExport_ATokenForADifferentDestination_IsRefused()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters(Filter, Destination));

        var mismatch = Assert.Throws<RpcException>(
            () => ContentGateHooks.RequireExport(harness.Gate, token, Filter, @"C:\exports\other.zip"));

        Assert.Equal(StatusCode.Unauthenticated, mismatch.StatusCode);
    }

    [Fact]
    public void RequireExport_ATokenForAnotherOperation_IsRefused()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(
            GatedOperation.GetManagementToken, GatedOperation.ExportParameters(Filter, Destination));

        Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token, Filter, Destination));
    }

    [Fact]
    public void RequireExport_WithNoPasskeyEnrolled_IsAFailedPreconditionWithTheEnrollmentMessage()
    {
        var harness = PasskeyGateHarness.Create(enrolled: false);

        var refused = Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, "token", Filter, Destination));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(ContentGate.EnrollmentRequiredDetail, refused.Status.Detail);
    }

    [Fact]
    public void RequireImport_ConsumesATokenBoundToTheArchiveDigest()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var token = harness.OneOperations.Issue(GatedOperation.Import, GatedOperation.ImportParameters(digest));

        ContentGateHooks.RequireImport(harness.Gate, token, digest);

        Assert.Throws<RpcException>(() => ContentGateHooks.RequireImport(harness.Gate, token, digest));
    }

    [Fact]
    public void RequireTurnTexts_AcceptsAValidContentGrant()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var context = PasskeyGateHarness.Context(
            grantToken: harness.Grants.IssueGrant(),
            cancellationToken: TestContext.Current.CancellationToken);

        ContentGateHooks.RequireTurnTexts(harness.Gate, context);
    }
}
