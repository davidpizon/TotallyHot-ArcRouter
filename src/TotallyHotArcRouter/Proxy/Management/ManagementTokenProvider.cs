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
        // The persistent write and the in-memory update must happen under one held lock, not two
        // separately-locked steps: two concurrent Regenerate() calls could otherwise persist tokens A
        // then B (in that order) but acquire the lock to update _currentToken in the opposite order (B
        // then A), leaving _currentToken at "A" while the store - and every other process reading it -
        // holds "B". The same class of bug this fixes was already found and fixed once in
        // ManagementAccessToken.GetOrCreate (see ProtectedSecretStore.GetOrAdd's remarks).
        lock (_lock)
        {
            _currentToken = ManagementAccessToken.Regenerate(_store);
            Generation++;
            return _currentToken;
        }
    }
}
