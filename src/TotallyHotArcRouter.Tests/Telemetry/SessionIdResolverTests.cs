using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="SessionIdResolver"/>'s header and body-field conventions, which follow the
/// session-id convention established by the <c>claude-code-router</c> project so that clients
/// already emitting those fields resolve correctly.
/// </summary>
public class SessionIdResolverTests
{
    private readonly SessionIdResolver _resolver = new();

    private static IHeaderDictionary EmptyHeaders()
    {
        return new HeaderDictionary();
    }

    [Fact]
    public void Resolve_NullHeaders_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(headers: null!, null));
    }

    [Fact]
    public void Resolve_NoHeaderAndNoBody_ReturnsNull()
    {
        var result = _resolver.Resolve(headers: EmptyHeaders(), null);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ClaudeCodeSessionIdHeader_IsUsed()
    {
        var headers = new HeaderDictionary { ["x-claude-code-session-id"] = "sess-abc" };

        var result = _resolver.Resolve(headers: headers, null);

        Assert.Equal(expected: "sess-abc", actual: result);
    }

    [Fact]
    public void Resolve_ClaudeSessionIdHeader_UsedWhenPrimaryHeaderAbsent()
    {
        var headers = new HeaderDictionary { ["x-claude-session-id"] = "sess-fallback" };

        var result = _resolver.Resolve(headers: headers, null);

        Assert.Equal(expected: "sess-fallback", actual: result);
    }

    [Fact]
    public void Resolve_ClaudeCodeSessionIdHeader_TakesPriorityOverClaudeSessionIdHeader()
    {
        var headers = new HeaderDictionary
        {
            ["x-claude-code-session-id"] = "primary",
            ["x-claude-session-id"] = "secondary"
        };

        var result = _resolver.Resolve(headers: headers, null);

        Assert.Equal(expected: "primary", actual: result);
    }

    [Fact]
    public void Resolve_BlankHeaderValue_FallsThroughToNextSource()
    {
        var headers = new HeaderDictionary
        {
            ["x-claude-code-session-id"] = new StringValues("   "),
            ["x-claude-session-id"] = "sess-real"
        };

        var result = _resolver.Resolve(headers: headers, null);

        Assert.Equal(expected: "sess-real", actual: result);
    }

    [Theory]
    [InlineData("session_id")]
    [InlineData("sessionId")]
    [InlineData("conversation_id")]
    [InlineData("conversationId")]
    [InlineData("chat_id")]
    [InlineData("chatId")]
    [InlineData("thread_id")]
    [InlineData("threadId")]
    public void Resolve_EachBodyFieldName_IsRecognized(string fieldName)
    {
        var body = new JsonObject { [fieldName] = "sess-from-body" };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Equal(expected: "sess-from-body", actual: result);
    }

    [Fact]
    public void Resolve_HeadersTakePriorityOverBodyFields()
    {
        var headers = new HeaderDictionary { ["x-claude-code-session-id"] = "from-header" };
        var body = new JsonObject { ["session_id"] = "from-body" };

        var result = _resolver.Resolve(headers: headers, body: body);

        Assert.Equal(expected: "from-header", actual: result);
    }

    [Fact]
    public void Resolve_EarlierBodyFieldName_TakesPriorityOverLater()
    {
        var body = new JsonObject { ["session_id"] = "primary", ["thread_id"] = "secondary" };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Equal(expected: "primary", actual: result);
    }

    [Fact]
    public void Resolve_MetadataUserIdWithSessionMarker_ExtractsSessionSuffix()
    {
        var body = new JsonObject
        {
            ["metadata"] = new JsonObject { ["user_id"] = "user-42_session_sess-xyz" }
        };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Equal(expected: "sess-xyz", actual: result);
    }

    [Fact]
    public void Resolve_MetadataUserIdWithoutSessionMarker_ReturnsNull()
    {
        var body = new JsonObject
        {
            ["metadata"] = new JsonObject { ["user_id"] = "user-42" }
        };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_BodyFieldsTakePriorityOverMetadataUserId()
    {
        var body = new JsonObject
        {
            ["session_id"] = "from-field",
            ["metadata"] = new JsonObject { ["user_id"] = "user-42_session_from-metadata" }
        };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Equal(expected: "from-field", actual: result);
    }

    [Fact]
    public void Resolve_EmptyBodyObject_ReturnsNull()
    {
        var result = _resolver.Resolve(headers: EmptyHeaders(), body: new JsonObject());

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NonStringBodyField_IsIgnored()
    {
        var body = new JsonObject { ["session_id"] = 12345 };

        var result = _resolver.Resolve(headers: EmptyHeaders(), body: body);

        Assert.Null(result);
    }
}