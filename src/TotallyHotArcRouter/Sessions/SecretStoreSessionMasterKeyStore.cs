using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Keeps the session master key as two named entries in the protected secret store (ADR-0015), so the
/// key inherits that store's machine-scoped encryption and administrator-only file. Only the router
/// process reads it back: the management surface is handed <see cref="ISecretWriter"/>, which has no
/// reader, and this type is not reachable from it.
/// </summary>
public sealed class SecretStoreSessionMasterKeyStore : ISessionMasterKeyStore
{
    private const string CurrentName = "sessions.master-key";
    private const string NextName = "sessions.master-key.next";
    private const string RotationRequiredName = "sessions.rotation-required";
    private const string RotationRequiredValue = "1";

    private readonly ProtectedSecretStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStoreSessionMasterKeyStore"/> class.
    /// </summary>
    /// <param name="store">The protected secret store that holds the key entries.</param>
    public SecretStoreSessionMasterKeyStore(ProtectedSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc/>
    public byte[] GetOrCreateCurrent() =>
        Decode(_store.GetOrAdd(CurrentName, () => Convert.ToBase64String(SessionKeyMaterial.CreateMasterKey())));

    /// <inheritdoc/>
    public byte[]? TryGetCurrent() => TryGet(CurrentName);

    /// <inheritdoc/>
    public byte[]? TryGetNext() => TryGet(NextName);

    /// <inheritdoc/>
    public void StageNext(byte[] nextKey)
    {
        SessionKeyMaterial.ValidateKeyLength(nextKey, nameof(nextKey));
        _store.Write(NextName, Convert.ToBase64String(nextKey));
    }

    /// <inheritdoc/>
    public void PromoteNext()
    {
        if (!_store.TryRead(NextName, out var next)) return;

        // Current first, then the staged copy: a crash between the two leaves both entries holding the
        // same key, which startup recovery resolves by discarding the staged one.
        _store.Write(CurrentName, next);
        _store.Delete(NextName);
    }

    /// <inheritdoc/>
    public void DiscardNext() => _store.Delete(NextName);

    /// <inheritdoc/>
    public void RequireRotation() => _store.Write(RotationRequiredName, RotationRequiredValue);

    /// <inheritdoc/>
    public bool IsRotationRequired() => _store.TryRead(RotationRequiredName, out _);

    /// <inheritdoc/>
    public void ClearRotationRequired() => _store.Delete(RotationRequiredName);

    /// <inheritdoc/>
    public void DestroyAll()
    {
        _store.Delete(NextName);
        _store.Delete(CurrentName);
        _store.Delete(RotationRequiredName);
    }

    private byte[]? TryGet(string name) => _store.TryRead(name, out var value) ? Decode(value) : null;

    private static byte[] Decode(string base64)
    {
        var key = Convert.FromBase64String(base64);
        SessionKeyMaterial.ValidateKeyLength(key, nameof(base64));
        return key;
    }
}
