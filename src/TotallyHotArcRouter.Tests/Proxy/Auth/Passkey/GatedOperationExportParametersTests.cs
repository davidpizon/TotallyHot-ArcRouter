using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Pins <see cref="GatedOperation.ExportParameters"/>, the digest a passkey approval for an export is bound to
/// (#165 phase 3). The golden digest is the contract with the dashboard's own copy in
/// <c>PasskeyOperations.ExportParameters</c>: the GUI test project asserts the same value for the same inputs, so
/// a change to either encoding fails a test instead of making every export approval unusable.
/// </summary>
public sealed class GatedOperationExportParametersTests
{
    /// <summary>The digest for <see cref="GoldenFilter"/> and <see cref="GoldenDestination"/>; the GUI's golden test asserts the same value.</summary>
    private const string GoldenDigest = "1170fb9a0612c1edf2167fff51996745ae54ed63f81d82ca72dbb14672384157";

    private const string GoldenDestination = @"C:\exports\conversations.zip";

    private static readonly ConversationExportFilter GoldenFilter = new(
        From: new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
        To: new DateTimeOffset(2026, 10, 9, 17, 45, 30, 500, TimeSpan.Zero),
        SessionId: "session-1",
        Harness: "claude-code",
        Provider: "anthropic",
        Model: "claude-x");

    [Fact]
    public void ExportParameters_MatchesTheGoldenDigest()
    {
        Assert.Equal(GoldenDigest, GatedOperation.ExportParameters(GoldenFilter, GoldenDestination));
    }

    [Fact]
    public void ExportParameters_IsLowercaseHexSha256()
    {
        var digest = GatedOperation.ExportParameters(new ConversationExportFilter(), @"C:\a.zip");

        Assert.Equal(64, digest.Length);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void ExportParameters_IsDeterministic()
    {
        Assert.Equal(
            GatedOperation.ExportParameters(GoldenFilter, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { }, GoldenDestination));
    }

    [Fact]
    public void ExportParameters_ChangingAnyFieldChangesTheDigest()
    {
        var baseline = GatedOperation.ExportParameters(GoldenFilter, GoldenDestination);

        var variants = new[]
        {
            GatedOperation.ExportParameters(GoldenFilter with { From = GoldenFilter.From!.Value.AddTicks(1) }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { To = null }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { SessionId = "session-2" }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { Harness = "codex" }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { Provider = "openai" }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter with { Model = "gpt-x" }, GoldenDestination),
            GatedOperation.ExportParameters(GoldenFilter, @"C:\exports\other.zip"),
        };

        Assert.DoesNotContain(baseline, variants);
        Assert.Equal(variants.Length, variants.Distinct().Count());
    }

    [Fact]
    public void ExportParameters_FieldValuesCannotShiftBoundaries()
    {
        // Length prefixes keep "ab"+"c" and "a"+"bc" apart, which a plain delimiter-free concatenation would not.
        var first = GatedOperation.ExportParameters(new ConversationExportFilter(Harness: "ab", Provider: "c"), @"C:\a.zip");
        var second = GatedOperation.ExportParameters(new ConversationExportFilter(Harness: "a", Provider: "bc"), @"C:\a.zip");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ExportParameters_NullEmptyAndWhitespaceFiltersAreTheSameAbsence()
    {
        var absent = GatedOperation.ExportParameters(new ConversationExportFilter(), @"C:\a.zip");
        var blank = GatedOperation.ExportParameters(
            new ConversationExportFilter(SessionId: "", Harness: "  ", Provider: "\t", Model: null), @"C:\a.zip");

        Assert.Equal(absent, blank);
    }

    [Fact]
    public void ExportParameters_DoesNotTrimOrFoldCase()
    {
        var plain = GatedOperation.ExportParameters(new ConversationExportFilter(Harness: "codex"), @"C:\a.zip");

        Assert.NotEqual(plain, GatedOperation.ExportParameters(new ConversationExportFilter(Harness: " codex"), @"C:\a.zip"));
        Assert.NotEqual(plain, GatedOperation.ExportParameters(new ConversationExportFilter(Harness: "Codex"), @"C:\a.zip"));
        Assert.NotEqual(plain, GatedOperation.ExportParameters(new ConversationExportFilter(Harness: "codex"), @"C:\a.zip "));
    }

    [Fact]
    public void ExportParameters_AnInstantIsTheSameInEveryOffset()
    {
        var utc = new ConversationExportFilter(From: new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero));
        var local = new ConversationExportFilter(From: new DateTimeOffset(2026, 10, 1, 1, 30, 0, TimeSpan.FromHours(-7)));

        Assert.Equal(
            GatedOperation.ExportParameters(utc, @"C:\a.zip"),
            GatedOperation.ExportParameters(local, @"C:\a.zip"));
    }

    [Fact]
    public void ExportParameters_NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => GatedOperation.ExportParameters(null!, @"C:\a.zip"));
        Assert.Throws<ArgumentNullException>(() => GatedOperation.ExportParameters(new ConversationExportFilter(), null!));
    }
}
