# Trusting the router's local CA

The router generates its own name-constrained local certificate authority (CA) and issues short-lived
leaf certificates under it for every TLS listener it binds: the web dashboard (`47104`), MCP (`47103`),
and the LLM proxy (`47101`, HTTPS by default since the web GUI migration plan's Phase P7 - see
[ADR-0013](../adr/0013-name-constrained-local-ca-for-router-tls.md)). The CA is trusted once per
machine; every leaf rotation after that is silent and needs no re-trust.

This doc covers two things: how to trust the CA in a browser/OS, and how to point an LLM client (Claude
Code, the OpenAI/Anthropic SDKs, curl, Ollama-API clients, etc.) at the HTTPS proxy port.

## Exporting the CA certificate

```
TotallyHotArcRouter.exe --export-ca
```

Writes the CA's public certificate (PEM, no private key) to
`<machine-shared data directory>/router-ca.crt` and prints the resolved path. On Windows that is
`%ProgramData%\TotallyHotArcRouter\router-ca.crt`; see `AppDataPaths` for the Linux/macOS equivalents.

## Trusting the CA in the OS

```
TotallyHotArcRouter.exe --install-certificate
```

Adds the CA to this OS's system-wide trust store:

- **Windows**: `LocalMachine\Root` (the same store `certmgr.msc`'s "Trusted Root Certification
  Authorities" shows). Requires an elevated (Administrator) process.
- **Linux**: writes a PEM copy under `/usr/local/share/ca-certificates/` and runs
  `update-ca-certificates`. Requires root.
- **macOS**: adds the certificate to the System keychain via `security add-trusted-cert -d -r trustRoot`.
  Requires an administrator prompt.

`TotallyHotArcRouter.exe --uninstall-certificate` reverses this - it removes the router's CA from
whichever store `--install-certificate` added it to, matched by thumbprint, and is safe to run even if
no CA has ever been installed (a no-op).

Both flags are meant to be run interactively (elevated) or by an installer/service-account script - never
as an automatic side effect of the router process itself, since installing a trust anchor is a
security-relevant, machine-wide change an operator should take deliberately.

## The Chrome-on-Linux and Firefox gap

Chrome on Linux and Firefox on every platform maintain their own certificate databases (NSS) separate
from the OS-wide trust store `--install-certificate` populates on Linux and macOS. Trusting the CA
system-wide is **not** sufficient for those two browsers specifically. Until this repo ships a
first-run helper for NSS databases, trust the CA in each of them directly:

- **Firefox**: Settings → Privacy & Security → Certificates → View Certificates → Authorities → Import,
  and select `router-ca.crt`. Check "Trust this CA to identify websites."
- **Chrome on Linux**: `certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n "TotallyHot Arc Router Local CA" -i router-ca.crt`
  (requires the `libnss3-tools`/`nss-tools` package for `certutil`).
- **Windows and macOS**: Chrome (and every other Chromium-based browser) reads the OS trust store
  directly, so `--install-certificate` alone is sufficient there.

Enterprise-managed Firefox deployments can instead push the CA via the
[`Certificates` enterprise policy](https://mozilla.github.io/policy-templates/#certificates) rather than
importing it per profile.

## Pointing an LLM client at the HTTPS proxy port

Once the CA is trusted (system-wide, or per-tool below), point your client at
`https://localhost:47101` instead of the old plain `http://` scheme.

| Client | How it discovers trust |
|---|---|
| Claude Code / any Node-based CLI | System trust store by default. If it ignores that, set `NODE_EXTRA_CA_CERTS=<path to router-ca.crt>` (or Node ≥18's `--use-system-ca`). |
| OpenAI/Anthropic Python SDKs | `httpx`'s default `truststore`/`certifi` bundle; point it at the CA with `SSL_CERT_FILE=<path to router-ca.crt>` or `REQUESTS_CA_BUNDLE=<path to router-ca.crt>`. |
| curl | System trust store by default (Windows: Schannel, reads the OS store directly). To point at the CA explicitly without installing it: `curl --cacert router-ca.crt https://localhost:47101/v1/models`. |
| .NET clients | System trust store (`X509Store`) automatically once `--install-certificate` has run. |
| Rust/Go CLIs | Most use the OS trust store by default; check for a `--cacert`/`SSL_CERT_FILE`-equivalent flag if the tool vendors its own root bundle instead. |
| Ollama-API-compatible clients pointed at the router | Same as above - these are ordinary HTTPS clients once pointed at `https://localhost:47101`, with no protocol difference from talking to a real Ollama server over HTTP. |

## When to use the opt-in plain-HTTP listener instead

Some tools hard-code `http://` or otherwise cannot be configured to trust a custom CA at all. For those,
set `Proxy:PlainHttp:Enabled=true` (default port `47105`, always loopback-only, LLM-proxy routes only -
never gRPC, the dashboard, auth, or MCP). The router logs a Warning at startup whenever this listener is
enabled, since traffic to it is unencrypted on the wire to that first hop. Prefer the HTTPS port
(`47101`) for every other client.

## Verifying trust worked

```
curl https://localhost:47104        # the dashboard - should succeed with no -k
curl https://localhost:47101/v1/models   # the LLM proxy - should succeed with no -k
curl http://localhost:47101          # should fail outright - the plain listener is off by default
```

A certificate warning in a browser, or `curl` requiring `-k`, means the CA is not yet trusted by that
specific client - re-check the section above for the client you're using.
