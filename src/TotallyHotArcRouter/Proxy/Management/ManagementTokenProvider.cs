namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Default <see cref="IManagementTokenProvider"/>: wraps <see cref="ManagementAccessToken"/>'s persisted
/// file and adds the in-memory rotation generation Phase P4's session tickets key off. A process-lifetime
/// singleton (see <see cref="ProxyServiceCollectionExtensions.AddManagement"/>) - every management surface
/// in this process shares one instance, so <see cref="Regenerate"/> is visible everywhere immediately.
/// </summary>
public sealed class ManagementTokenProvider : IManagementTokenProvider
{
    private readonly Lock _lock = new();
    private readonly string? _path;
    private string _currentToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenProvider"/> class, loading (or
    /// creating) the token at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The token file path, or <see langword="null"/> for the default location.</param>
    public ManagementTokenProvider(string? path = null)
    {
        _path = path;
        _currentToken = ManagementAccessToken.GetOrCreate(path);
    }

    /// <inheritdoc/>
    public string CurrentToken
    {
        get
        {
            lock (_lock)
            {
                return _currentToken;
            }
        }
    }

    /// <inheritdoc/>
    public int Generation { get; private set; }

    /// <inheritdoc/>
    public bool Verify(string? presented)
    {
        return ManagementAccessToken.Verify(presented: presented, expected: CurrentToken);
    }

    /// <inheritdoc/>
    public string Regenerate()
    {
        var token = ManagementAccessToken.Regenerate(_path);
        lock (_lock)
        {
            _currentToken = token;
            Generation++;
            return _currentToken;
        }
    }
}
