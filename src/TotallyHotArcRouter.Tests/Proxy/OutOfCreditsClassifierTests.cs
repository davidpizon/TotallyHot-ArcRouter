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
        var body = """{"error":{"message":"You exceeded your quota.","code":"insufficient_quota"}}"""u8.ToArray();

        var result = OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message);

        Assert.True(result);
        Assert.Equal(expected: "You exceeded your quota.", actual: message);
    }

    [Fact]
    public void IsOutOfCredits_MessageKeywordMatch_ClassifiesTrue()
    {
        var body = """{"error":{"message":"Your credit balance is too low."}}"""u8.ToArray();

        var result = OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message);

        Assert.True(result);
        Assert.Equal(expected: "Your credit balance is too low.", actual: message);
    }

    [Fact]
    public void IsOutOfCredits_UnrelatedClientFaultMessage_ClassifiesFalse()
    {
        var body = """{"error":{"message":"model not found"}}"""u8.ToArray();

        var result = OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message);

        Assert.False(result);
        Assert.Equal(expected: string.Empty, actual: message);
    }

    [Fact]
    public void IsOutOfCredits_MalformedJson_FailsClosed()
    {
        var body = "not json"u8.ToArray();

        var result = OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message);

        Assert.False(result);
        Assert.Equal(expected: string.Empty, actual: message);
    }

    [Fact]
    public void IsOutOfCredits_NonStringMessageField_FailsClosed_DoesNotThrow()
    {
        // The upstream review case: JsonNode.GetValue<string>() throws on a type mismatch - a numeric
        // "message" must be treated as "field absent," not crash the request path.
        var body = """{"error":{"message":12345}}"""u8.ToArray();

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out _));

        Assert.Null(exception);
        Assert.False(OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message));
        Assert.Equal(expected: string.Empty, actual: message);
    }

    [Fact]
    public void IsOutOfCredits_NonStringCodeField_FailsClosed_DoesNotThrow()
    {
        var body = """{"error":{"message":"quota exceeded","code":429}}"""u8.ToArray();

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out _));

        Assert.Null(exception);
        // Falls back to the message-keyword path since the typed code isn't a string - "quota" still matches.
        Assert.True(OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message));
        Assert.Equal(expected: "quota exceeded", actual: message);
    }

    [Fact]
    public void IsOutOfCredits_ObjectShapedMessageField_FailsClosed_DoesNotThrow()
    {
        var body = """{"error":{"message":{"nested":"shape"}}}"""u8.ToArray();

        var exception = Record.Exception(() => OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out _));

        Assert.Null(exception);
        Assert.False(OutOfCreditsClassifier.IsOutOfCredits(body: body, null, message: out var message));
        Assert.Equal(expected: string.Empty, actual: message);
    }

    [Fact]
    public void IsOutOfCredits_EmbeddedMessageSupplied_KeywordMatchesWithoutParsingBody()
    {
        var result = OutOfCreditsClassifier.IsOutOfCredits(body: [], embeddedMessage: "Your account is out of credits.",
            message: out var message);

        Assert.True(result);
        Assert.Equal(expected: "Your account is out of credits.", actual: message);
    }

    [Fact]
    public void IsOutOfCredits_EmbeddedMessageSupplied_NoKeywordMatch_ClassifiesFalse()
    {
        var result = OutOfCreditsClassifier.IsOutOfCredits(body: [],
            embeddedMessage: "messages: at least one message is required", message: out var message);

        Assert.False(result);
        Assert.Equal(expected: string.Empty, actual: message);
    }
}