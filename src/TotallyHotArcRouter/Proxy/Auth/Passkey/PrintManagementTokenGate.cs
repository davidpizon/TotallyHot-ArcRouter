using System.Runtime.InteropServices;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Gates the router's <c>--print-management-token</c> CLI (ADR-0020 phase 4). Conversation-content and
/// token-disclosure operations need a person's WebAuthn gesture; the dashboard runs that ceremony in the
/// browser. On Windows 10 1903+ the tray and CLI can use <c>webauthn.dll</c>; until a native ceremony is
/// wired here, and on every platform without that API, this gate refuses and sends the operator to the
/// dashboard — matching ADR-0020's macOS/Linux and Windows 1809 rules.
/// </summary>
public static class PrintManagementTokenGate
{
    /// <summary>
    /// Checks whether <c>--print-management-token</c> may print. Returns <see langword="null"/> when
    /// allowed, or a refusal message for stderr when the operator must enroll or use the dashboard.
    /// </summary>
    /// <remarks>
    /// Every current path refuses because the native WebAuthn CLI ceremony is not wired yet; the nullable
    /// return stays so <c>Program</c>'s allow-path remains once that ceremony lands (ADR-0020).
    /// </remarks>
    /// <param name="credentialStore">Reads whether any passkey exists.</param>
    /// <param name="storeProtected">
    /// Result of <see cref="SecretStoreAclProbe.Check"/> for the secrets file; fail closed when false.
    /// </param>
    // ReSharper disable once ReturnTypeCanBeNotNullable
    public static string? TryGetRefusalMessage(IPasskeyCredentialStore credentialStore, bool storeProtected)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);

        if (!storeProtected)
        {
            return "Cannot print the management token until the protected secret store ACL is repaired " +
                   "(only SYSTEM and Administrators may access it).";
        }

        if (credentialStore.List().Count == 0)
            return ContentGate.EnrollmentRequiredDetail;

        // Native webauthn.dll ceremony for this CLI is not wired yet; ADR-0020 already sends Windows 1809,
        // macOS, and Linux operators to the dashboard. Until the native path lands, every CLI print goes
        // there too so token disclosure cannot bypass the passkey gate.
        if (!OperatingSystem.IsWindows() || !IsWebAuthnApiAvailable())
        {
            return "Printing the management token requires passkey verification in the dashboard " +
                   "(System Settings → Copy MCP token). This platform or Windows build cannot run a " +
                   "CLI WebAuthn ceremony.";
        }

        return "Printing the management token requires passkey verification in the dashboard " +
               "(System Settings → Copy MCP token). Use Unlock / Copy MCP token there; the CLI native " +
               "ceremony is not available in this build.";
    }

    /// <summary>
    /// Returns whether <c>webauthn.dll</c> can be loaded — present from Windows 10 1903 onward. Used to
    /// choose the ADR-0020 refusal wording when the API is missing (1809).
    /// </summary>
    private static bool IsWebAuthnApiAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return NativeLibrary.TryLoad("webauthn.dll", out var handle) && handle != 0;
    }
}
