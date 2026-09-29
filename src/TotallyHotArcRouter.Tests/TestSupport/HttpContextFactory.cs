using Microsoft.AspNetCore.Http;
using System.Text;

namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// Creates the <see cref="DefaultHttpContext"/> shape proxy tests keep redefining privately: a POST with a
/// UTF-8 JSON body on a loopback host.
/// </summary>
public static class HttpContextFactory
{
    /// <summary>Creates a POST context to <paramref name="path"/> carrying <paramref name="jsonBody"/>.</summary>
    /// <param name="jsonBody">The request body text.</param>
    /// <param name="path">The request path; defaults to the OpenAI-style chat route.</param>
    /// <returns>The context, with a writable in-memory response body.</returns>
    public static DefaultHttpContext CreateJsonPost(string jsonBody, string path = "/v1/chat/completions")
    {
        var bytes = Encoding.UTF8.GetBytes(jsonBody);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
