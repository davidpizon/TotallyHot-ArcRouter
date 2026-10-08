namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Runs a WebAuthn ceremony in the host's authenticator API - in the browser, <c>navigator.credentials</c>
/// (ADR-0020). An interface so <c>PasskeyAdminStore</c> and the dialogs are testable without a browser and so
/// the Razor library stays free of host specifics, mirroring <see cref="IClipboardService"/>. The router
/// produces the options and verifies the response; this seam only moves the JSON between them and the
/// authenticator, always with user verification required.
/// </summary>
public interface IWebAuthnCeremony
{
    /// <summary>Registers a new credential from the router's creation options.</summary>
    /// <param name="optionsJson">The <c>PublicKeyCredentialCreationOptions</c> JSON the router issued.</param>
    /// <param name="cancellationToken">Cancels the wait for the authenticator.</param>
    /// <returns>The attestation response JSON the router's registration verification expects.</returns>
    /// <exception cref="WebAuthnCeremonyException">The ceremony was cancelled, timed out, or is unsupported here.</exception>
    Task<string> CreateCredentialAsync(string optionsJson, CancellationToken cancellationToken = default);

    /// <summary>Asserts an existing credential from the router's request options.</summary>
    /// <param name="optionsJson">The <c>PublicKeyCredentialRequestOptions</c> JSON the router issued.</param>
    /// <param name="cancellationToken">Cancels the wait for the authenticator.</param>
    /// <returns>The assertion response JSON the router's assertion verification expects.</returns>
    /// <exception cref="WebAuthnCeremonyException">The ceremony was cancelled, timed out, or is unsupported here.</exception>
    Task<string> GetAssertionAsync(string optionsJson, CancellationToken cancellationToken = default);
}

/// <summary>
/// A WebAuthn ceremony did not complete on the client: the operator dismissed the prompt, the prompt timed
/// out, no authenticator was available, or the page is not a secure context. Distinct from
/// <see cref="TotallyHot.ArcRouter.Gui.Telemetry.GrpcAdminException"/>, which is the router rejecting a request:
/// nothing was sent to the router, so the gate state is unchanged.
/// </summary>
public sealed class WebAuthnCeremonyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WebAuthnCeremonyException"/> class.</summary>
    /// <param name="message">A plain-language description fit to render inline.</param>
    /// <param name="innerException">The underlying host error, if any.</param>
    public WebAuthnCeremonyException(string message, Exception? innerException = null)
        : base(message: message, innerException: innerException)
    {
    }
}
