using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Generated-client test doubles shared by the GUI store tests that need a canned
/// <c>ProviderAdminService</c>/<c>UsageAdminService</c> response instead of a live proxy
/// (docs/router/tracked-todos.md #7's gRPC migration replaced the earlier
/// <c>HttpMessageHandler</c>-based stubs these tests used). Each overrides only the <c>CallOptions</c>
/// overload of the RPCs its own tests exercise: the generated convenience overloads delegate to it, so
/// this intercepts both call shapes, the same seam
/// <c>TotallyHot.ArcRouter.Gui.Telemetry.PriceSourceAdminClientTests</c> established for gRPC clients in
/// this codebase.
/// </summary>
internal sealed class StubProviderAdminServiceClient : Contract.ProviderAdminService.ProviderAdminServiceClient
{
    public Contract.ProviderListResponse ListProvidersResponse { get; init; } = new();

    public RpcException? Failure { get; init; }

    public override AsyncUnaryCall<Contract.ProviderListResponse> ListProvidersAsync(
        Contract.ListProvidersRequest request, CallOptions options)
    {
        return new AsyncUnaryCall<Contract.ProviderListResponse>(
            responseAsync: Failure is null
                ? Task.FromResult(ListProvidersResponse)
                : Task.FromException<Contract.ProviderListResponse>(Failure),
            responseHeadersAsync: Task.FromResult(new Metadata()),
            getStatusFunc: () => Status.DefaultSuccess,
            getTrailersFunc: () => [],
            disposeAction: () => { });
    }
}

/// <summary>See <see cref="StubProviderAdminServiceClient"/>'s remarks - the <c>UsageAdminService</c> counterpart.</summary>
internal sealed class StubUsageAdminServiceClient : Contract.UsageAdminService.UsageAdminServiceClient
{
    public Contract.UsageRollupResponse RollupResponse { get; init; } = new();

    public RpcException? Failure { get; init; }

    public override AsyncUnaryCall<Contract.UsageRollupResponse> GetUsageRollupAsync(
        Contract.GetUsageRollupRequest request, CallOptions options)
    {
        return new AsyncUnaryCall<Contract.UsageRollupResponse>(
            responseAsync: Failure is null
                ? Task.FromResult(RollupResponse)
                : Task.FromException<Contract.UsageRollupResponse>(Failure),
            responseHeadersAsync: Task.FromResult(new Metadata()),
            getStatusFunc: () => Status.DefaultSuccess,
            getTrailersFunc: () => [],
            disposeAction: () => { });
    }
}
