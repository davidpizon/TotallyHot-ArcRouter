using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// AES-256-GCM key material for one session file, plus wrap/unwrap against the machine master key
/// stored in the protected secret store (ADR-0019 / ADR-0015 custody). Wrapped keys live in
/// <c>transcripts.db</c>; the plaintext session key exists only in memory while a file is open.
/// </summary>
public static class SessionKeyMaterial
{
    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeyLengthBytes = 32;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceLengthBytes = 12;

    /// <summary>AES-GCM authentication tag length in bytes.</summary>
    public const int TagLengthBytes = 16;

    /// <summary>
    /// Generates a fresh random session key. Callers wrap it before persisting the wrap blob.
    /// </summary>
    /// <returns>A 32-byte AES key.</returns>
    public static byte[] CreateSessionKey()
    {
        var key = new byte[KeyLengthBytes];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Generates a fresh random master key for wrapping session keys. Persisted once via the secret store.
    /// </summary>
    /// <returns>A 32-byte AES key, typically Base64-encoded at rest.</returns>
    public static byte[] CreateMasterKey()
    {
        var key = new byte[KeyLengthBytes];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Wraps <paramref name="sessionKey"/> under <paramref name="masterKey"/> as
    /// <c>nonce || tag || ciphertext</c> so SQLite can store a single blob per session.
    /// </summary>
    /// <param name="masterKey">The 32-byte machine master key.</param>
    /// <param name="sessionKey">The 32-byte per-session key.</param>
    /// <returns>The wrap blob.</returns>
    public static byte[] WrapSessionKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> sessionKey)
    {
        ValidateKey(masterKey, nameof(masterKey));
        ValidateKey(sessionKey, nameof(sessionKey));

        var nonce = new byte[NonceLengthBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[sessionKey.Length];
        var tag = new byte[TagLengthBytes];

        using var aes = new AesGcm(masterKey, TagLengthBytes);
        aes.Encrypt(nonce, sessionKey, ciphertext, tag);

        var wrapped = new byte[NonceLengthBytes + TagLengthBytes + ciphertext.Length];
        nonce.CopyTo(wrapped.AsSpan(0, NonceLengthBytes));
        tag.CopyTo(wrapped.AsSpan(NonceLengthBytes, TagLengthBytes));
        ciphertext.CopyTo(wrapped.AsSpan(NonceLengthBytes + TagLengthBytes));
        return wrapped;
    }

    /// <summary>
    /// Unwraps a blob produced by <see cref="WrapSessionKey"/>.
    /// </summary>
    /// <param name="masterKey">The 32-byte machine master key.</param>
    /// <param name="wrapped">The <c>nonce || tag || ciphertext</c> blob.</param>
    /// <returns>The 32-byte session key.</returns>
    /// <exception cref="CryptographicException">When authentication fails.</exception>
    public static byte[] UnwrapSessionKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> wrapped)
    {
        ValidateKey(masterKey, nameof(masterKey));
        var minLength = NonceLengthBytes + TagLengthBytes + KeyLengthBytes;
        if (wrapped.Length != minLength)
        {
            throw new CryptographicException("Wrapped session key has an unexpected length.");
        }

        var nonce = wrapped[..NonceLengthBytes];
        var tag = wrapped.Slice(NonceLengthBytes, TagLengthBytes);
        var ciphertext = wrapped[(NonceLengthBytes + TagLengthBytes)..];
        var sessionKey = new byte[KeyLengthBytes];

        using var aes = new AesGcm(masterKey, TagLengthBytes);
        aes.Decrypt(nonce, ciphertext, tag, sessionKey);
        return sessionKey;
    }

    /// <summary>
    /// Ensures <paramref name="key"/> is exactly <see cref="KeyLengthBytes"/> long.
    /// </summary>
    /// <param name="key">Candidate key bytes.</param>
    /// <param name="paramName">Argument name for exceptions.</param>
    public static void ValidateKeyLength(ReadOnlySpan<byte> key, string paramName = "sessionKey")
    {
        if (key.Length != KeyLengthBytes)
        {
            throw new ArgumentException($"Expected a {KeyLengthBytes}-byte key.", paramName);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, string paramName) =>
        ValidateKeyLength(key, paramName);
}
