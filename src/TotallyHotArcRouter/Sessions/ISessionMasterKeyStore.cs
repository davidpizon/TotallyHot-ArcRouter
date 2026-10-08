namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Custody of the machine master key that wraps every session key (ADR-0019, ADR-0015). Deleting a
/// session is final only once the master key that wrapped its key is gone, so the store supports a
/// two-step rotation: stage the next key, re-wrap the index, then promote. A crash between the steps is
/// resolved at startup by <see cref="SessionStore.RecoverOnStartup"/>.
/// </summary>
public interface ISessionMasterKeyStore
{
    /// <summary>
    /// Returns the current master key, creating and persisting one the first time. Atomic, so two callers
    /// can never create competing keys.
    /// </summary>
    /// <returns>A 32-byte key. The caller owns the array and should zero it after use.</returns>
    byte[] GetOrCreateCurrent();

    /// <summary>Returns the current master key, or <see langword="null"/> when none has been created.</summary>
    /// <returns>A 32-byte key the caller owns, or <see langword="null"/>.</returns>
    byte[]? TryGetCurrent();

    /// <summary>Returns the staged next key of a rotation in progress, or <see langword="null"/> when none is staged.</summary>
    /// <returns>A 32-byte key the caller owns, or <see langword="null"/>.</returns>
    byte[]? TryGetNext();

    /// <summary>
    /// Persists <paramref name="nextKey"/> beside the current key without replacing it, so the old key
    /// stays usable until the index has been re-wrapped.
    /// </summary>
    /// <param name="nextKey">The 32-byte replacement key.</param>
    void StageNext(byte[] nextKey);

    /// <summary>
    /// Makes the staged key current and destroys the old one. Does nothing when no key is staged.
    /// </summary>
    void PromoteNext();

    /// <summary>Destroys the staged key and keeps the current one. Does nothing when no key is staged.</summary>
    void DiscardNext();

    /// <summary>
    /// Records that a master-key rotation must still run to finalize a deletion. Durable across a crash:
    /// <see cref="SessionStore.DeleteSessions"/> sets it before removing index rows, and only a successful
    /// rotation clears it, so a failed finalization remains retryable even when the deleted ids are gone.
    /// </summary>
    void RequireRotation();

    /// <summary>Whether <see cref="RequireRotation"/> is recorded and not yet cleared by a successful rotation.</summary>
    bool IsRotationRequired();

    /// <summary>Clears the rotation-required marker after a successful promotion.</summary>
    void ClearRotationRequired();

    /// <summary>
    /// Destroys the current and staged keys, which makes every session file unreadable. Used by the
    /// uninstall shred.
    /// </summary>
    void DestroyAll();
}
