using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="RequestInterceptor"/> with the same fake defaults its constructor supplies for
/// omitted collaborators. Exists because the interceptor's constructor is the hub other code keeps growing
/// parameters onto: constructing it by hand at every test site meant one new parameter touched dozens of
/// files. Tests that need only a resolver go through <see cref="For"/>; a test needing another collaborator
/// should add the specific option here rather than constructing the interceptor by hand.
/// </summary>
public static class RequestInterceptorBuilder
{
    /// <summary>Builds an interceptor over <paramref name="resolver"/> with every other collaborator at its default.</summary>
    /// <param name="resolver">The model route resolver, the one collaborator every interceptor needs.</param>
    /// <returns>The interceptor.</returns>
    public static RequestInterceptor For(IModelRouteResolver resolver)
    {
        return new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
    }
}
