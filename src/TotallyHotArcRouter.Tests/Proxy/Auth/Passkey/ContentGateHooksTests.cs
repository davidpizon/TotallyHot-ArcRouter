using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Pins <see cref="ContentGateHooks"/> so #165/#176 can call the binding helpers without re-deriving
/// operation names. The hooks have no production callers yet; these tests are the live callers that keep
/// the helpers from reading as dead code in the Qodana scan.
/// </summary>
public sealed class ContentGateHooksTests
{
    [Fact]
    public void RequireExport_ConsumesAMatchingOneOperationToken()
    {
        var harness = PasskeyGateHarness.Create(enrolled: true);
        var token = harness.OneOperations.Issue(GatedOperation.Export, GatedOperation.ExportParameters());

        ContentGateHooks.RequireExport(harness.Gate, token);

        Assert.Throws<RpcException>(() => ContentGateHooks.RequireExport(harness.Gate, token));
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
