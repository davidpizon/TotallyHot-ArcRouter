namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// No-op <see cref="IBodyLogController"/> for tests and hosts that never wire a body File sink.
/// </summary>
public sealed class NullBodyLogController : IBodyLogController
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullBodyLogController Instance = new();

    private NullBodyLogController()
    {
    }

    /// <inheritdoc/>
    public void ClearBodyFiles()
    {
    }
}
