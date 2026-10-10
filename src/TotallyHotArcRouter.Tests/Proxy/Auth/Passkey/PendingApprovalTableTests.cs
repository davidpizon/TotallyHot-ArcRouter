using AwesomeAssertions;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Covers <see cref="PendingApprovalTable"/> (#165 phase 3, PR 3c): a request is bound to the digest of exactly its
/// filter and destination, the table is bounded and lapses after five minutes, only the first decision counts, and a
/// decision is handed to exactly one waiting caller.
/// </summary>
public sealed class PendingApprovalTableTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Dashboard = new("https://localhost:47104");

    private static ConversationExportFilter Filter(string? model = "gpt-5") => new(Model: model);

    private static (PendingApprovalTable Table, SteppingTimeProvider Clock) NewTable()
    {
        var clock = new SteppingTimeProvider(Start);
        return (new PendingApprovalTable(Dashboard, clock), clock);
    }

    [Fact]
    public void CreateExport_BindsTheRequestToTheDigestOfItsFilterAndDestination()
    {
        var (table, _) = NewTable();

        var request = table.CreateExport(Filter(), @"C:\exports\a.zip");

        request.Operation.Should().Be(GatedOperation.Export);
        request.Parameters.Should().Be(GatedOperation.ExportParameters(Filter(), @"C:\exports\a.zip"));
        request.Parameters.Should().NotBe(GatedOperation.ExportParameters(Filter("other"), @"C:\exports\a.zip"));
        request.Parameters.Should().NotBe(GatedOperation.ExportParameters(Filter(), @"C:\exports\b.zip"));
        request.ExpiresAtUtc.Should().Be(Start + PendingApprovalTable.RequestTtl);
        PendingApprovalTable.RequestTtl.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void CreateExport_GivesEveryRequestItsOwnUnguessableId()
    {
        var (table, _) = NewTable();

        var first = table.CreateExport(Filter(), "/tmp/a.zip");
        var second = table.CreateExport(Filter(), "/tmp/a.zip");

        first.Id.Should().NotBe(second.Id);
        first.Id.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void CreateExport_RefusesOnceTheTableIsFull_AndFreesRoomWhenOneLapses()
    {
        var (table, clock) = NewTable();
        for (var i = 0; i < PendingApprovalTable.MaxPending; i++) table.CreateExport(Filter(), $"/tmp/{i}.zip");

        var refused = Assert.Throws<RpcException>(() => table.CreateExport(Filter(), "/tmp/overflow.zip"));
        refused.StatusCode.Should().Be(StatusCode.ResourceExhausted);

        clock.Advance(PendingApprovalTable.RequestTtl);

        table.CreateExport(Filter(), "/tmp/after.zip").Should().NotBeNull();
        table.ListPending().Should().ContainSingle();
    }

    [Fact]
    public void DashboardUrlFor_PointsAtTheDashboardOriginWithTheRequestId()
    {
        var (table, _) = NewTable();

        table.DashboardUrlFor("abc123").Should().Be("https://localhost:47104/?approval=abc123");
    }

    [Fact]
    public void ListPending_ReturnsOpenRequestsOldestFirst_AndOmitsDecidedAndLapsedOnes()
    {
        var (table, clock) = NewTable();
        var first = table.CreateExport(Filter(), "/tmp/1.zip");
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = table.CreateExport(Filter(), "/tmp/2.zip");
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = table.CreateExport(Filter(), "/tmp/3.zip");

        table.TryDeny(second.Id);

        table.ListPending().Select(r => r.Id).Should().Equal(first.Id, third.Id);

        clock.Advance(PendingApprovalTable.RequestTtl - TimeSpan.FromSeconds(1));

        table.ListPending().Select(r => r.Id).Should().Equal(third.Id);
    }

    [Fact]
    public void OnlyTheFirstDecisionCounts()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");

        table.TryApprove(request.Id, "laptop").Should().BeTrue();

        table.TryApprove(request.Id, "phone").Should().BeFalse();
        table.TryDeny(request.Id).Should().BeFalse();
        table.TryGetPending(request.Id).Should().BeNull();
    }

    [Fact]
    public void ADecisionOnAnUnknownOrBlankIdChangesNothing()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");

        table.TryApprove("unknown", "laptop").Should().BeFalse();
        table.TryApprove(null, "laptop").Should().BeFalse();
        table.TryDeny(" ").Should().BeFalse();

        table.TryGetPending(request.Id).Should().NotBeNull();
    }

    [Fact]
    public void ALapsedRequestCannotBeApproved()
    {
        var (table, clock) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");

        clock.Advance(PendingApprovalTable.RequestTtl);

        table.TryApprove(request.Id, "laptop").Should().BeFalse();
        table.TryGetPending(request.Id).Should().BeNull();
    }

    [Fact]
    public async Task WaitAsync_ReturnsTheApprovalToTheWaiterAndNamesThePasskey()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        var waiting = table.WaitAsync(request.Id, TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();

        table.TryApprove(request.Id, "laptop");
        var decision = await waiting;

        decision.Should().NotBeNull();
        decision!.Outcome.Should().Be(PendingApprovalOutcome.Approved);
        decision.CredentialName.Should().Be("laptop");
        decision.Request.Should().Be(request);
    }

    [Fact]
    public async Task WaitAsync_ReportsADenial()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        var waiting = table.WaitAsync(request.Id, TestContext.Current.CancellationToken);

        table.TryDeny(request.Id);
        var decision = await waiting;

        decision!.Outcome.Should().Be(PendingApprovalOutcome.Denied);
        decision.CredentialName.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAsync_ReportsExpiryWhenNobodyDecidesInFiveMinutes()
    {
        var (table, clock) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        var waiting = table.WaitAsync(request.Id, TestContext.Current.CancellationToken);

        clock.Advance(PendingApprovalTable.RequestTtl);
        var decision = await waiting;

        decision!.Outcome.Should().Be(PendingApprovalOutcome.Expired);
    }

    [Fact]
    public async Task WaitAsync_ForARequestAlreadyDecided_ReturnsAtOnce()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        table.TryApprove(request.Id, "laptop");

        var decision = await table.WaitAsync(request.Id, TestContext.Current.CancellationToken);

        decision!.Outcome.Should().Be(PendingApprovalOutcome.Approved);
    }

    [Fact]
    public async Task WaitAsync_HandsTheDecisionToExactlyOneCaller()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        var first = table.WaitAsync(request.Id, TestContext.Current.CancellationToken);
        var second = table.WaitAsync(request.Id, TestContext.Current.CancellationToken);

        table.TryApprove(request.Id, "laptop");
        var decisions = await Task.WhenAll(first, second);

        decisions.Count(d => d is not null).Should().Be(1);
        (await table.WaitAsync(request.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task WaitAsync_ForAnUnknownId_ReturnsNull()
    {
        var (table, _) = NewTable();

        (await table.WaitAsync("unknown", TestContext.Current.CancellationToken)).Should().BeNull();
        (await table.WaitAsync(null, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task WaitAsync_WhenTheCallerGoesAway_LeavesTheRequestOpenUntilItLapses()
    {
        var (table, _) = NewTable();
        var request = table.CreateExport(Filter(), "/tmp/a.zip");
        using var cancellation = new CancellationTokenSource();
        var waiting = table.WaitAsync(request.Id, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        table.TryGetPending(request.Id).Should().NotBeNull();
    }
}
