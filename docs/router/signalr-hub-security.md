# Secure SignalR Communication (TotallyHotArcRouter ↔ TotallyHotArcRouter.Gui)

> **Status: Proposed — not yet implemented, AND the transport this doc was written against no longer
> exists.** [`grpc-migration.md`](grpc-migration.md) has since shipped: `Telemetry/TelemetryHub.cs` is
> deleted, and `Proxy/ProxyServer.cs` now serves telemetry over `TelemetryGrpcService`
> (`TelemetryService.StreamEvents`), not SignalR. The underlying gap this doc describes is still real
> and unaddressed on the new transport - the gRPC endpoint is still plain, unencrypted HTTP/2 (h2c)
> with no authentication, bound to loopback only, so any local process can still connect and receive
> everything, including real prompt/response text (see "Why this matters" below, and
> [`telemetry.md`](telemetry.md#transport-grpc) for the gRPC transport as it exists today) - but every
> C# code sample below (`HubConnectionBuilder`, `MapHub<TelemetryHub>`, `options.AccessTokenProvider`,
> SignalR's `JsonHubProtocol`) is written against the removed SignalR API and needs translating to a
> gRPC equivalent (channel credentials for TLS, a server interceptor plus per-call metadata for the
> auth token) before any of it is actionable. That translation is itself proposed and not done - see
> "If `grpc-migration.md` ships..." immediately below, now updated to reflect that it did.

## Why this matters

The hub carries routing metadata (session id, model, token counts, cost, latency) - see
`telemetry.md`'s field table - **and**, since `RequestTextExtractor`/`ResponseTextExtractor` shipped
(see [`backlog.md`](../gui/backlog.md)'s "Turn card request/response text" item), actual prompt/response
text: the newest user message and the assistant's reply, for every turn, truncated but otherwise real.
That's potentially proprietary code, credentials pasted into a chat, customer data, etc., flowing over
this unauthenticated, unencrypted channel to any local process that connects - not a hypothetical
future exposure, current behavior today.

Two separate concerns, both covered below:

- **Encryption in transit** - stop another local process from reading hub traffic off the loopback
  interface (packet capture).
- **Authentication** - stop another local process from simply connecting to the hub and being handed
  everything, which encryption alone does not prevent.

**[`grpc-migration.md`](grpc-migration.md) has shipped, so this doc's SignalR-specific code needs
translating, not its concerns.** That doc's own "Scope" section anticipated this: the authentication
piece (section 2) should carry over almost unchanged as a gRPC interceptor checking call metadata
instead of a SignalR `AccessTokenProvider`/`?access_token=` query string; the encryption piece
(section 1) should get *simpler* under gRPC, since HTTP/2 + TLS is a standard ALPN negotiation rather
than the self-signed-cert-plus-client-bypass dance section 1 below has to do specifically because
SignalR (via `HubConnectionBuilder`) didn't otherwise give an easy hook for trusting a non-CA-issued
certificate. This doc itself is still proposed and unimplemented - none of section 1/2's code below
was ever built, SignalR or otherwise - so what's left is authoring the gRPC-native version of
sections 1 and 2 (a `GrpcChannel`/`ServerCredentials` equivalent of section 1, an
`Interceptor`/`CallCredentials` equivalent of section 2) from scratch, not migrating working code.
Don't build section 1/2's code exactly as written below (`HubConnectionBuilder`,
`options.AccessTokenProvider`, `RequireAuthorization()` on `MapHub<TelemetryHub>`) - none of those
APIs exist in this codebase anymore.

---

## 1. Encryption: Programmatic Self-Signed Certificates for SignalR (Localhost)

Generate, save, and reload a self-signed TLS certificate entirely within the proxy process, and
configure the GUI's SignalR client to trust it, without requiring a real CA-issued certificate (there
is no public hostname to issue one for - this is loopback-only).

### Server-side: generate, save, and load the certificate

A utility class creates the certificate in memory using native .NET cryptography, and saves it as a
password-protected `.pfx` file so it persists across proxy restarts instead of regenerating (and thus
requiring the GUI to re-trust a new one) every launch:

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class CertificateManager
{
    private const string CertFileName = "server_cert.pfx";
    private const string CertPassword = "SecurePassword123!"; // Store securely in production

    public static X509Certificate2 GetOrCreateCertificate()
    {
        // 1. If the certificate already exists locally, load it and return
        if (File.Exists(CertFileName))
        {
            return new X509Certificate2(CertFileName, CertPassword, X509KeyStorageFlags.Exportable);
        }

        // 2. Generate a new cryptographic key pair (RSA 2048-bit)
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName("CN=localhost");

        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        // 3. Add Server Authentication extension (critical for TLS/SSL servers)
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                false
            )
        );

        // 4. Sign the certificate (valid for 1 year)
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), // Slight backdate to prevent immediate timing issues
            DateTimeOffset.UtcNow.AddYears(1)
        );

        // 5. Export to a PFX byte array with a password
        byte[] certBytes = certificate.Export(X509ContentType.Pkcs12, CertPassword);

        // 6. Save the certificate file to disk
        File.WriteAllBytes(CertFileName, certBytes);

        // 7. Load it back with exportable flags to tie the private key correctly on Windows
        return new X509Certificate2(certBytes, CertPassword, X509KeyStorageFlags.Exportable);
    }
}
```

**Note for this codebase:** `CertFileName` as a bare relative path is illustrative only - a real
implementation should write it under a per-user directory (e.g. `%LOCALAPPDATA%\TotallyHotArcRouter\`,
matching the file-based shared-secret approach in [section 2](#2-authentication-shared-secrettoken)
below) rather than the process's working directory, and `CertPassword` must not be a hardcoded
literal - see "Generate a random runtime password" below.

#### Generate a random runtime password

We don't care about the `.pfx` password being human-readable - nobody needs to open this file
manually outside the app. So instead of a fixed literal, generate a random string in memory every
time the certificate is created, and save it alongside the `.pfx` (same per-user-ACL'd directory as
`CertFileName` above) so the same password is available to reload the certificate on the next launch:

```csharp
// Generate a 32-character random string on the fly
string runtimePassword = Guid.NewGuid().ToString("N");

// Export using this temporary password
byte[] certBytes = certificate.Export(X509ContentType.Pkcs12, runtimePassword);
File.WriteAllBytes("server_cert.pfx", certBytes);

// Save 'runtimePassword' to a local text file with strict Windows permissions (ACLs)
// so only your app's Windows account can read it when booting up next time.
File.WriteAllText("cert_pwd.txt", runtimePassword);
```

This removes the hardcoded-literal problem entirely rather than just relocating it: there's no fixed
password checked into source or compiled into the binary to leak in the first place, only a
per-installation random value gated by the same filesystem ACLs already doing the real work in
[section 2's Option A](#option-a---file-based-per-user-secret-stronger).

### Binding to Kestrel

In `Proxy/ProxyServer.cs`'s `UseKestrel` configuration (currently just `options.ListenLocalhost(port)`
- see [`telemetry.md`](telemetry.md#transport-signalr)), pass the certificate into the listen options:

```csharp
webBuilder.UseKestrel(options =>
{
    options.ListenLocalhost(port, listenOptions =>
    {
        var certificate = CertificateManager.GetOrCreateCertificate();
        listenOptions.UseHttps(certificate);
    });
});
```

### Client-side: bypassing the trust issue

Because this certificate is generated programmatically rather than issued by a globally recognized
Certificate Authority, Windows marks it as untrusted. When `TotallyHotArcRouter.Gui`'s
`Services/LiveDataStore.cs` connects via `https://localhost:5001/telemetry/hub`, the underlying
HTTP/WebSocket client throws an `AuthenticationException` and aborts the handshake to protect against
tampering - unless the client is explicitly told to trust this specific certificate.

Since both processes run on the same standalone machine over the loopback interface, traffic never
leaves the machine, which makes it reasonable to have the client bypass standard CA validation for
this connection specifically:

```csharp
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http;

var connection = new HubConnectionBuilder()
    .WithUrl("https://localhost:5001/telemetry/hub", options =>
    {
        options.HttpMessageHandlerFactory = (HttpMessageHandler handler) =>
        {
            if (handler is HttpClientHandler clientHandler)
            {
                // Bypasses the strict CA authority and thumbprint validation checks.
                // This is safe ONLY because traffic is locked strictly to 'localhost'.
                clientHandler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            return handler;
        };
    })
    .WithAutomaticReconnect()
    .Build();

await connection.StartAsync();
```

#### Alternative: a more targeted validation callback

`DangerousAcceptAnyServerCertificateValidator` trusts *any* certificate on this connection, not just
the proxy's - a more targeted check accepts only a certificate matching what the proxy is expected to
present, falling back to normal OS validation otherwise:

```csharp
clientHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
{
    // Accept the certificate if the server name is exactly localhost
    if (cert is not null && cert.Subject.Contains("CN=localhost"))
    {
        return true;
    }

    // Otherwise, fallback to default operating system validation
    return sslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
};
```

Better still (not shown above): pin the certificate's actual thumbprint (read from the same
`CertificateManager.GetOrCreateCertificate()` output, or a value shared the same way as the
authentication token in section 2) rather than matching on subject name alone, since subject name is
attacker-controllable but a thumbprint is not.

---

## 2. Authentication: shared secret/token

TLS alone does not restrict *who* can connect to the hub - it only encrypts the channel for whoever
does. `TelemetryHub` has no `[Authorize]` attribute and no connection-time credential check today,
so closing that gap is a separate piece of work from section 1. Two options, differing in how strong a
guarantee the secret gives:

### Option A - file-based, per-user secret (stronger)

1. On startup, the proxy generates a random token (if one doesn't already exist) and writes it to a
   file only the current Windows user can read - e.g. `%LOCALAPPDATA%\TotallyHotArcRouter\telemetry-token`.
2. `TotallyHotArcRouter.Gui` reads that same file before connecting (same user, same machine, so it can).
3. Server side: a check runs before the hub is reached - either a small custom middleware comparing
   `context.Request.Query["access_token"]` against the expected value and returning 401 on mismatch,
   or the standard ASP.NET Core auth pipeline with `endpoints.MapHub<TelemetryHub>(...).RequireAuthorization()`.
4. Client side: SignalR's `HubConnectionBuilder` has a built-in hook for this -
   `options.AccessTokenProvider = () => Task.FromResult(token)` - the client automatically attaches it
   as `?access_token=...` on the connection.
5. What this actually buys: "only processes running as the same OS user as the proxy can connect" -
   filesystem ACLs are doing the real work, not the token's secrecy. A meaningful boundary on a shared
   multi-user machine, and a reasonable one for a personal local dev tool.

### Option B - hardcoded shared constant (weaker, simpler)

Same mechanism, but the token is a fixed string compiled into both the proxy and the GUI instead of
generated/read from a file. Much less code, but it is not really a secret - decompiling either binary
reveals it. Stops *accidental* local connections (a stray script, a misconfigured tool), not a
deliberate attacker.

### Combining sections 1 and 2

The token itself travels in plaintext unless the connection is also encrypted (section 1) - another
local process sniffing loopback traffic could capture and replay it, partially undermining the
authentication check. Section 1 + Option A together is the combination that closes both gaps; either
alone is a partial mitigation.

---

## Known limitations of this proposal

- The certificate password is resolved (a random runtime value, not a hardcoded literal - see
  "Generate a random runtime password" above), reusing the same per-user-ACL'd-file pattern as
  section 2's Option A token. That pattern itself is still unimplemented, so this is a design
  decision recorded ahead of the code, not something to treat as already built.
- None of this changes the hub's `Clients.All` broadcast model - every authenticated client still
  receives every event. There's no per-conversation or per-client scoping; that's a separate,
  unrelated design question from encryption/authentication.
- This only secures the `/telemetry/hub` SignalR endpoint. The proxy's actual LLM-forwarding traffic
  (the rest of `Proxy/ProxyMiddleware.cs`) is out of scope for this doc.

