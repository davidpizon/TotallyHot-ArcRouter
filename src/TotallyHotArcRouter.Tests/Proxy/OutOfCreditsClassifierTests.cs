using System.Text;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="OutOfCreditsClassifier"/> directly, in particular its fail-closed contract when the
/// upstream body has an unexpected shape (malformed JSON, or a <c>message</c>/<c>code</c> field that isn't
/// a JSON string).
/// </summary>
public sealed class OutOfCreditsClassifierTests
{
    [Fact]
    public void IsOutOfCredits_TypedInsufficientQuotaCode_ClassifiesTrue()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"You exceeded your quota.","code":"insufficient_quota"}}""");

        var result = OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message);

        Assert.True(result);
        Assert.Equal("You exceeded your quota.", message);
    }

    [Fact]
    public void IsOutOfCredits_MessageKeywordMatch_ClassifiesTrue()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"Your credit balance is too low."}}""");

        var result = OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message);

        Assert.True(result);
        Assert.Equal("Your credit balance is too low.", message);
    }

    [Fact]
    public void IsOutOfCredits_UnrelatedClientFaultMessage_ClassifiesFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"model not found"}}""");

        var result = OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message);

        Assert.False(result);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void IsOutOfCredits_MalformedJson_FailsClosed()
    {
        var body = Encoding.UTF8.GetBytes("not json");

        var result = OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message);

        Assert.False(result);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void IsOutOfCredits_NonStringMessageField_FailsClosed_DoesNotThrow()
    {
        // The upstream review case: JsonNode.GetValue<string>() throws on a type mismatch - a numeric
        // "message" must be treated as "field absent," not crash the request path.
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":12345}}""");

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body, null, out _));

        Assert.Null(exception);
        Assert.False(OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void IsOutOfCredits_NonStringCodeField_FailsClosed_DoesNotThrow()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"quota exceeded","code":429}}""");

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body, null, out _));

        Assert.Null(exception);
        // Falls back to the message-keyword path since the typed code isn't a string - "quota" still matches.
        Assert.True(OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message));
        Assert.Equal("quota exceeded", message);
    }

    [Fact]
    public void IsOutOfCredits_ObjectShapedMessageField_FailsClosed_DoesNotThrow()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":{"nested":"shape"}}}""");

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body, null, out _));

        Assert.Null(exception);
        Assert.False(OutOfCreditsClassifier.IsOutOfCredits(body, null, out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void IsOutOfCredits_EmbeddedMessageSupplied_KeywordMatchesWithoutParsingBody()
    {
        var result = OutOfCreditsClassifier.IsOutOfCredits([], "Your account is out of credits.", out var message);

        Assert.True(result);
        Assert.Equal("Your account is out of credits.", message);
    }

    [Fact]
    public void IsOutOfCredits_EmbeddedMessageSupplied_NoKeywordMatch_ClassifiesFalse()
    {
        var result = OutOfCreditsClassifier.IsOutOfCredits([], "messages: at least one message is required", out var message);

        Assert.False(result);
        Assert.Equal(string.Empty, message);
    }
}
