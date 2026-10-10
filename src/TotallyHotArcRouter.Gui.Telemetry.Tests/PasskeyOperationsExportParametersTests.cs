using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Pins <see cref="PasskeyOperations.ExportParameters"/>, the dashboard's copy of the router's
/// <c>GatedOperation.ExportParameters</c> (#165 phase 3). The golden digest below is asserted verbatim by the
/// router's <c>GatedOperationExportParametersTests</c> for the same inputs: if either encoding drifts, one of the
/// two tests fails instead of every export approval being refused at runtime.
/// </summary>
public class PasskeyOperationsExportParametersTests
{
    private const string GoldenDigest = "1170fb9a0612c1edf2167fff51996745ae54ed63f81d82ca72dbb14672384157";
    private const string GoldenDestination = @"C:\exports\conversations.zip";

    private static readonly ConversationExportFilterInfo GoldenFilter = new(
        From: new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
        To: new DateTimeOffset(2026, 10, 9, 17, 45, 30, 500, TimeSpan.Zero),
        SessionId: "session-1",
        Harness: "claude-code",
        Provider: "anthropic",
        Model: "claude-x");

    [Fact]
    public void ExportOperation_IsTheRoutersOperationName()
    {
        PasskeyOperations.Export.Should().Be("export");
    }

    [Fact]
    public void ExportParameters_MatchesTheRoutersGoldenDigest()
    {
        PasskeyOperations.ExportParameters(GoldenFilter, GoldenDestination).Should().Be(GoldenDigest);
    }

    [Fact]
    public void ExportParameters_ChangingAnyFieldChangesTheDigest()
    {
        var variants = new[]
        {
            PasskeyOperations.ExportParameters(GoldenFilter with { From = null }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter with { To = GoldenFilter.To!.Value.AddTicks(1) }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter with { SessionId = "session-2" }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter with { Harness = "codex" }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter with { Provider = "openai" }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter with { Model = "gpt-x" }, GoldenDestination),
            PasskeyOperations.ExportParameters(GoldenFilter, @"C:\exports\other.zip"),
        };

        variants.Should().NotContain(GoldenDigest);
        variants.Distinct().Should().HaveCount(variants.Length);
    }

    [Fact]
    public void ExportParameters_NullAndBlankFiltersAreTheSameAbsence()
    {
        var absent = PasskeyOperations.ExportParameters(new ConversationExportFilterInfo(), @"C:\a.zip");
        var blank = PasskeyOperations.ExportParameters(
            new ConversationExportFilterInfo(SessionId: "", Harness: "  ", Provider: "\t"), @"C:\a.zip");

        blank.Should().Be(absent);
    }

    [Fact]
    public void ExportParameters_AnInstantIsTheSameInEveryOffset()
    {
        var utc = new ConversationExportFilterInfo(From: new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero));
        var local = new ConversationExportFilterInfo(From: new DateTimeOffset(2026, 10, 1, 1, 30, 0, TimeSpan.FromHours(-7)));

        PasskeyOperations.ExportParameters(local, @"C:\a.zip")
            .Should().Be(PasskeyOperations.ExportParameters(utc, @"C:\a.zip"));
    }

    [Fact]
    public void ExportParameters_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PasskeyOperations.ExportParameters(null!, @"C:\a.zip"));
        Assert.Throws<ArgumentNullException>(() => PasskeyOperations.ExportParameters(new ConversationExportFilterInfo(), null!));
    }
}
