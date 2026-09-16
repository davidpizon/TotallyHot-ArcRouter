# MCP Management Endpoint

ArcRouter exposes an MCP (Model Context Protocol) server so an MCP client (Claude Desktop/Code, or any
other agent) can manage the router the same way the web dashboard does - add/edit/remove providers and
model routes, set budgets, manage price sources, and read spend/budget aggregates - without going through
the browser. This is one of two management surfaces the router runs; see the table below for how they
relate.

> **This document was rewritten for the web GUI migration plan's P11 docs close-out**, reflecting the
> architecture after P1-P10 shipped (2026-09-15): REST `/admin/*` is gone (deleted in Phase P2), the
> native gRPC-only port is retired (Phase P9), the management token lives in the encrypted secret store
> rather than a plaintext file (Phase P9, ADR-0012), and every listener moved off the 5000s port range
> (see the plan doc's port-renumbering addendum). The dashboard itself is now a browser-hosted Blazor
> WebAssembly app served by the router (ADR-0011), not the retired MAUI GUI this document originally
> described.

## Transport and ports

| Port | Transport | Purpose | Auth |
|------|-----------|---------|------|
| 47101 | HTTPS | LLM forwarding (`/v1/*`) | none (forwarding itself is never gated) |
| 47104 | HTTPS (HTTP/1.1 + HTTP/2) | Web dashboard, gRPC-Web, and native gRPC (telemetry, price-source, provider/usage, management-token admin) | Session cookie (ADR-0012, loopback) or the shared management token |
| 47103 | HTTPS (Streamable HTTP) | MCP management endpoint | Bearer token (the same shared management token) |
| 47105 (opt-in, off by default) | plain HTTP, loopback-only | LLM forwarding only, for clients that cannot trust a custom CA | none |

There is no longer a REST `/admin/*` API and no longer a separate native-gRPC-only port: every gRPC admin
service (`ProviderAdminGrpcService`, `UsageAdminGrpcService`, `PriceSourceAdminGrpcService`,
`ManagementTokenAdminGrpcService`, telemetry streaming) is mapped on the single web port (47104) alongside
the dashboard and gRPC-Web, since Phase P2/P9. See `docs/router/client-tls-setup.md` for trusting the
router's local CA (every listener above is HTTPS using a leaf issued by it - ADR-0013) and for when to use
the opt-in plain-HTTP fallback.

The MCP endpoint (`TotallyHot.ArcRouter.Mcp.McpServer`, started by `McpHostedService`) is a small,
standalone Kestrel host using the same local-CA-issued leaf certificate as every other listener, so there
is one trust story for the whole router, not a separate one for MCP. A certificate failure is
non-essential: it's logged as a warning and the port simply doesn't bind, rather than failing the whole
process.

Configuration lives under the `Mcp` section in `appsettings.json`:

```json
"Mcp": { "Enabled": true, "Port": 47103, "BindAddress": "loopback" }
```

Set `Enabled: false` to turn the endpoint off entirely. `BindAddress` widens to `"any"`/`"0.0.0.0"` for a
Docker deployment - see the Dockerfile's own header comment.

## Authentication: one shared management token, in the encrypted secret store

`TotallyHot.ArcRouter.Proxy.Management.ManagementAccessToken` generates (or loads) a single 32-byte random
token on first use and persists it through `ProtectedSecretStore` - DPAPI-protected on Windows, ASP.NET
Data Protection-protected elsewhere (web GUI migration plan Phase P3/P9) - not a plaintext file. The
legacy plaintext `management-token.txt` is imported once on first startup if found, then the router never
writes plaintext again.

This is the **same token** that gates the MCP endpoint and acts as the non-loopback (Docker, remote
tunnel) fallback credential for the web dashboard - one credential, every surface, all authenticated by
it or by the loopback session cookie ADR-0012 issues in its place for a same-machine browser session.

- **MCP** presents it as `Authorization: Bearer <token>` (`McpBearerAuthMiddleware`, checked before
  `MapMcp()` - there is no unauthenticated route on this host).
- **The web dashboard/gRPC-Web/native gRPC** on port 47104 accepts either a loopback session cookie
  (`TelemetryAuthInterceptor`/`WebPortRequestGuardMiddleware`, ADR-0012 - issued automatically to a
  same-machine browser, no credential prompt) or the same bearer token, for a non-loopback client (a
  Docker deployment, or an operator who set `WebInterface:TrustLoopback=false` behind a loopback tunnel).

Verification is constant-time (`CryptographicOperations.FixedTimeEquals`) so a caller probing the endpoint
can't learn anything from response timing.

**Retrieving the token:**
- From the dashboard: System Settings → **Copy MCP token** (also has **Regenerate**, which invalidates the
  old token immediately and rotates the running MCP/gRPC-Web sessions' bearer credential).
- Headless or scripted (Docker, CI, an install script): `TotallyHotArcRouter --print-management-token`
  prints the same token an already-running instance is enforcing, without needing filesystem access to
  the now-encrypted secret store.

## Shared core: one facade, two surfaces

Both the web dashboard's gRPC-Web calls and the MCP provider tools call the same
`TotallyHot.ArcRouter.Proxy.Management.ManagementFacade` - the single place that projects, merges, and
validates provider/model/budget state. This matters for two reasons:

1. **One masking rule everywhere.** Credentials are write-only on both surfaces, expressed purely as
   custom headers: a caller can set a header's value, but no read - dashboard or MCP - ever returns a
   locked header's literal value. Each header reports a `source` (`literal` / `envVar` / `none`) and, for
   an env-var header, the variable's name - never the literal value of a locked one.
2. **One write rule everywhere.** Since no surface ever returns a locked header's literal value, a
   caller can't resend one it never received. Sending a custom header with `Value` and `ValueEnvVar`
   both blank preserves whatever is already stored under that name, rather than clearing it.

## Tool surface

All tools are read/write through the facade except price-source tools (which touch no credential material,
so they call the underlying stores directly) and the telemetry tools (read-only aggregates).

**Providers / models / budgets** (`TotallyHot.ArcRouter.Mcp.Tools.ProviderMcpTools`):
`list_providers`, `upsert_provider`, `remove_provider`, `upsert_model`, `remove_model`,
`set_provider_budget`, `discover_models`.

**Price sources & catalog** (`PriceSourceMcpTools`):
`list_price_sources`, `set_price_source_enabled`, `reorder_price_sources`, `refresh_price_sources`,
`get_model_price`.

**Telemetry & spend, read-only** (`TelemetryMcpTools`):
`get_budget_status`, `get_spend_summary`.

There is deliberately **no** tool that streams raw routing telemetry or request/response text: that
traffic can carry a user-pasted secret (the same concern `signalr-hub-security.md` raises about the
telemetry gRPC stream), so only spend/budget aggregates are exposed here.

## Out of scope

- Runtime mutation of `RoutingOptions`, `RouterMemory`, or `QualityOptions` - these remain appsettings-only.
- Hardening the telemetry gRPC stream's own authentication beyond the session-cookie/token gate the web
  port now applies to it (Phase P2/P4) - any remaining gap is tracked in `signalr-hub-security.md`.
