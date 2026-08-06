# MCP Management Endpoint

ArcRouter exposes an MCP (Model Context Protocol) server so an MCP client (Claude Desktop/Code, or any
other agent) can manage the router the same way the Governance UI does - add/edit/remove providers and
model routes, set budgets, manage price sources, and read spend/budget aggregates - without going through
the GUI. This is one of three management surfaces the router runs; see the table below for how they relate.

## Transport and ports

| Port | Transport | Purpose | Auth |
|------|-----------|---------|------|
| 5001 | plain HTTP | LLM forwarding (`/v1/*`) + REST `/admin/*` | `/admin/*` requires the management token by default; forwarding never does |
| 5002 | TLS gRPC | Telemetry stream + price-source admin | none (unchanged, out of scope here) |
| 5003 | TLS HTTP (Streamable HTTP) | MCP management endpoint | Bearer token (same token as `/admin/*`) |

The MCP endpoint (`TotallyHotArcRouter.Mcp.McpServer`, started by `McpHostedService`) is a small, standalone
Kestrel host mirroring `TotallyHotArcRouter.Proxy.ProxyServer`'s dedicated TLS gRPC listener: it reuses the same
self-signed `CN=localhost` certificate (`TotallyHotArcRouter.Telemetry.TelemetryTlsCertificate`) so there is one
trust story for every TLS management port, not a second one to configure. Like the gRPC listener, a
certificate failure is non-essential: it's logged as a warning and the port simply doesn't bind, rather
than failing the whole process.

Configuration lives under the `Mcp` section in `appsettings.json`:

```json
"Mcp": { "Enabled": true, "Port": 5003 }
```

Set `Enabled: false` to turn the endpoint off entirely.

## Authentication: one shared per-user token

`TotallyHotArcRouter.Proxy.Management.ManagementAccessToken` generates (or loads) a single 32-byte random token
on first use and persists it to `%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt`. On Windows the file's
ACL is broken from inheritance and rewritten to grant only the current Windows user; on POSIX the mode is
set to `600`. This is the **same token** that now gates the REST `/admin/*` API by default (see below) -
one credential, two surfaces, both authenticated by it.

- **MCP** presents it as `Authorization: Bearer <token>` (`McpBearerAuthMiddleware`, checked before
  `MapMcp()` - there is no unauthenticated route on this host).
- **REST** presents it as `X-Admin-Token` (unchanged header name from before this change).

Verification is constant-time (`CryptographicOperations.FixedTimeEquals`) so a caller probing the endpoint
can't learn anything from response timing. TLS on the MCP port is defense-in-depth on top of the token, not
a substitute for it - the trust model (same OS user, same machine, self-signed loopback cert) is the same
pragmatic one `TelemetryTlsCertificate` documents for the gRPC port; see that class's remarks for the
reasoning and its stated follow-up (thumbprint pinning) if a stronger boundary is ever needed.

The GUI reads this same file (`TotallyHotArcRouter.Gui.Admin.ManagementTokenReader`) and sends it automatically;
no manual configuration is needed on a machine where both processes run as the same user.

## REST `/admin/*` hardening (this change)

Before this change, `/admin/*` was gated only by an optional `Management:Token` config value that was
unset by default - the API was unauthenticated out of the box. It now defaults to **requiring** the shared
management token, sourced from `ManagementAccessToken.GetOrCreate()` rather than a config key. **LLM
forwarding is unaffected**: the token filter is an endpoint filter scoped to the `/admin` route group only,
so `/v1/*` traffic - which never targets `/admin` - never sees it. An IDE or agent pointed at the proxy's
plain-HTTP base URL keeps working with zero changes.

## Shared core: one facade, two surfaces

Both REST `/admin/*` and the MCP provider tools call the same `TotallyHotArcRouter.Proxy.Management.ManagementFacade`
- the single place that projects, merges, and validates provider/model/budget state. This matters for two
reasons:

1. **One masking rule everywhere.** Credentials are write-only on both surfaces, expressed purely as
   custom headers: a caller can set a header's value, but no read - REST or MCP - ever returns a locked
   header's literal value. Each header reports a `source` (`literal` / `envVar` / `none`) and, for an
   env-var header, the variable's name - never the literal value of a locked one. This is stricter than
   the REST API's pre-existing behavior, which used to echo a literal custom-header value back to the
   client unconditionally.
2. **One write rule everywhere.** Since no surface ever returns a locked header's literal value, a
   caller can't resend one it never received. Sending a custom header with `Value` and `ValueEnvVar`
   both blank preserves whatever is already stored under that name, rather than clearing it.

## Tool surface

All tools are read/write through the facade except price-source tools (which touch no credential material,
so they call the underlying stores directly) and the telemetry tools (read-only aggregates).

**Providers / models / budgets** (`TotallyHotArcRouter.Mcp.Tools.ProviderMcpTools`):
`list_providers`, `upsert_provider`, `remove_provider`, `upsert_model`, `remove_model`,
`set_provider_budget`, `discover_models`.

**Price sources & catalog** (`PriceSourceMcpTools`):
`list_price_sources`, `set_price_source_enabled`, `reorder_price_sources`, `refresh_price_sources`,
`get_model_price`.

**Telemetry & spend, read-only** (`TelemetryMcpTools`):
`get_budget_status`, `get_spend_summary`.

There is deliberately **no** tool that streams raw routing telemetry or request/response text: that
traffic can carry a user-pasted secret (the same concern `signalr-hub-security.md` raises about the
unauthenticated gRPC telemetry stream), so only spend/budget aggregates are exposed here.

## Out of scope

- Runtime mutation of `RoutingOptions`, `RouterMemory`, or `SandboxOptions` - these remain appsettings-only.
- Migrating the GUI's telemetry/price-source-admin traffic off gRPC onto MCP, or retiring REST `/admin/*`
  in favor of MCP - a larger future consolidation, not part of this change.
- Hardening the telemetry gRPC stream's own (still absent) authentication - a separate, pre-existing gap
  documented in `signalr-hub-security.md`.

