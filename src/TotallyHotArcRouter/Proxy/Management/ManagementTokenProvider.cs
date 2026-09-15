namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Default <see cref="IManagementTokenProvider"/>: wraps <see cref="ManagementAccessToken"/>'s persisted
/// secret-store entry and adds the in-memory rotation generation Phase P4's session tickets key off. A
/// process-lifetime singleton (see <see cref="ProxyServiceCollectionExtensions.AddManagement"/>) - every
/// management surface in this process shares one instance, so <see cref="Regenerate"/> is visible
/// everywhere immediately.
/// </summary>
public sealed class ManagementTokenProvider : IManagementTokenProvider
{
    private readonly Lock _lock = new();
    private readonly ProtectedSecretStore? _store;
    private string _currentToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenProvider"/> class, loading (or
    /// creating) the token in <paramref name="store"/>.
    /// </summary>
    /// <param name="store">The secret store to read/write, or <see langword="null"/> for the default store.</param>
    public ManagementTokenProvider(ProtectedSecretStore? store = null)
    {
        _store = store;
        _currentToken = ManagementAccessToken.GetOrCreate(store);
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
        var token = ManagementAccessToken.Regenerate(_store);
        lock (_lock)
        {
            _currentToken = token;
            Generation++;
            return _currentToken;
        }
    }
}
