using Microsoft.AspNetCore.Http;
using TotallyHot.ArcRouter.Mcp;

namespace TotallyHot.ArcRouter.Tests.Mcp;

/// <summary>Covers <see cref="McpBearerAuthMiddleware"/>: the MCP endpoint's token gate.</summary>
public sealed class McpBearerAuthMiddlewareTests
{
    private const string Token = "s3cret-token";

    [Fact]
    public async Task InvokeAsync_MissingAuthorizationHeader_Returns401AndDoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new McpBearerAuthMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, expectedToken: Token);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(expected: StatusCodes.Status401Unauthorized, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WrongToken_Returns401AndDoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new McpBearerAuthMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, expectedToken: Token);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(expected: StatusCodes.Status401Unauthorized, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_MissingBearerPrefix_Returns401()
    {
        var middleware = new McpBearerAuthMiddleware(next: _ => Task.CompletedTask, expectedToken: Token);
        var context = new DefaultHttpContext();
        // The raw token without the "Bearer " scheme prefix must not be accepted.
        context.Request.Headers.Authorization = Token;

        await middleware.InvokeAsync(context);

        Assert.Equal(expected: StatusCodes.Status401Unauthorized, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_LowercaseBearerScheme_IsAccepted()
    {
        // The "Bearer" scheme name is case-insensitive per RFC 6750/9110.
        var nextCalled = false;
        var middleware = new McpBearerAuthMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, expectedToken: Token);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"bearer {Token}";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_TokenWithTrailingWhitespace_IsTrimmedAndAccepted()
    {
        var nextCalled = false;
        var middleware = new McpBearerAuthMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, expectedToken: Token);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {Token}  ";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_CorrectToken_CallsNextAndLeavesResponseUntouched()
    {
        var nextCalled = false;
        var middleware = new McpBearerAuthMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, expectedToken: Token);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }
}