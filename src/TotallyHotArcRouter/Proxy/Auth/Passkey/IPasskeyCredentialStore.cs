namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Read/write surface for enrolled passkey credentials in the protected secret store. Split from
/// <see cref="PasskeyCredentialStore"/> so tests can substitute an in-memory fake without touching
/// encrypted persistence.
/// </summary>
public interface IPasskeyCredentialStore
{
    /// <summary>Returns every enrolled credential, oldest first.</summary>
    IReadOnlyList<PasskeyCredentialRecord> List();

    /// <summary>Persists a newly enrolled credential.</summary>
    void Add(PasskeyCredentialRecord record);

    /// <summary>Replaces an existing credential matched by <see cref="PasskeyCredentialRecord.Id"/>.</summary>
    void Update(PasskeyCredentialRecord record);

    /// <summary>Removes the credential whose id matches <paramref name="credentialIdBase64Url"/>.</summary>
    /// <returns><see langword="true"/> when a credential was removed.</returns>
    bool Remove(string credentialIdBase64Url);
}
