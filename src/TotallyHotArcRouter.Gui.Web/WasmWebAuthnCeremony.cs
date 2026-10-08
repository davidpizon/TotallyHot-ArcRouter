using Microsoft.JSInterop;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Web;

/// <summary>
/// The browser <see cref="IWebAuthnCeremony"/> implementation (ADR-0020): <c>navigator.credentials.create</c>
/// and <c>navigator.credentials.get</c> via JS interop (<c>wwwroot/js/webauthn-interop.js</c>), the browser
/// counterpart to the router's Fido2 verification. The script always requests
/// <c>userVerification: "required"</c>, whatever the options say, because ADR-0020 makes presence-only
/// approval unacceptable.
/// </summary>
public sealed class WasmWebAuthnCeremony : IWebAuthnCeremony
{
    private readonly IJSRuntime _jsRuntime;

    /// <summary>Initializes a new instance of the <see cref="WasmWebAuthnCeremony"/> class.</summary>
    /// <param name="jsRuntime">Used to invoke the browser's WebAuthn API.</param>
    public WasmWebAuthnCeremony(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _jsRuntime = jsRuntime;
    }

    /// <inheritdoc/>
    public Task<string> CreateCredentialAsync(string optionsJson, CancellationToken cancellationToken = default)
    {
        return InvokeAsync(identifier: "webAuthnInterop.create", optionsJson: optionsJson,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> GetAssertionAsync(string optionsJson, CancellationToken cancellationToken = default)
    {
        return InvokeAsync(identifier: "webAuthnInterop.get", optionsJson: optionsJson,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Calls one <c>webAuthnInterop</c> function and converts any browser-side failure into a
    /// <see cref="WebAuthnCeremonyException"/>. The script rejects with a plain-language message for the
    /// failures an operator can act on (dismissed prompt, insecure context, no authenticator), so that
    /// message is forwarded as is.
    /// </summary>
    private async Task<string> InvokeAsync(string identifier, string optionsJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(optionsJson);

        try
        {
            return await _jsRuntime.InvokeAsync<string>(identifier: identifier, cancellationToken: cancellationToken,
                args: optionsJson).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new WebAuthnCeremonyException(message: ex.Message, innerException: ex);
        }
    }
}
