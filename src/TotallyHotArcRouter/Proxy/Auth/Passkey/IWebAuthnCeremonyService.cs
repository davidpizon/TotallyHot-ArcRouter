namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// WebAuthn registration and assertion ceremonies for the passkey gate (ADR-0020). Implementations wrap
/// Fido2NetLib and coordinate with <see cref="ChallengeStore"/> so every challenge is consumed before
/// verification proceeds.
/// </summary>
public interface IWebAuthnCeremonyService
{
    /// <summary>
    /// Starts registration after enrollment-code validation elsewhere; returns create-options JSON for
    /// <c>navigator.credentials.create</c>.
    /// </summary>
    string BeginRegistration();

    /// <summary>Completes registration and persists a new <see cref="PasskeyCredentialRecord"/>.</summary>
    /// <param name="attestationJson">Browser attestation response JSON.</param>
    /// <param name="name">Operator-chosen credential name.</param>
    PasskeyCredentialRecord FinishRegistration(string attestationJson, string name);

    /// <summary>
    /// Starts an assertion bound to <paramref name="operation"/> and <paramref name="parameters"/>; returns
    /// request-options JSON for <c>navigator.credentials.get</c>.
    /// </summary>
    string BeginAssertion(string operation, string? parameters);

    /// <summary>
    /// Verifies an assertion and returns the credential display name used. Updates the stored signature
    /// counter when the authenticator reports a non-zero value.
    /// </summary>
    string FinishAssertion(string assertionJson, string operation, string? parameters);
}
