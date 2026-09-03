namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Thrown by <see cref="ProviderAdminClient"/> when the management API rejects a request (e.g. a
/// validation 400 from <c>EnsureValid</c>, a 401 for a missing/invalid management token, or a 404).
/// The <see cref="Exception.Message"/> carries the server's own error text so the Governance UI can
/// surface it directly.
/// </summary>
public sealed class ProviderAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderAdminException"/> class.</summary>
    /// <param name="message">The error message, ideally taken from the server's error envelope.</param>
    public ProviderAdminException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProviderAdminException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ProviderAdminException(string message, Exception innerException)
        : base(message: message, innerException: innerException)
    {
    }
}