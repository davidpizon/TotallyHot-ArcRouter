# Secure MCP endpoint + hardened REST over one shared ManagementFacade

> **Status: implemented.** This plan had no status banner for some time, which left it reading as a
> proposal after it had shipped. What exists today: `src/TotallyHotArcRouter/Mcp/` (`McpServer`,
> `McpHostedService`, `McpBearerAuthMiddleware`, `McpOptions`, and the `Tools/` surface),
> `Proxy/Management/ManagementFacade.cs` as the single shared core, and
> `Proxy/Management/ManagementAccessToken.cs` backing the `X-Admin-Token` header that
> `ProviderAdminEndpoints`/`UsageAdminEndpoints` now require on every `/admin/*` request. See
> [`mcp-endpoint.md`](mcp-endpoint.md) for the as-built reference.

## Context

ArcRouter runs as a long-lived loopback proxy daemon. Its management surfaces today are: the GUI's REST `/admin/*` API (providers, models, budgets) on plain-HTTP port 5001, and a TLS gRPC service on port 5002 (telemetry stream + price-source admin). We are adding an **MCP (Model Context Protocol) endpoint** so an MCP client (Claude Desktop/Code or any agent) can manage the router — and, in the same pass, **hardening the existing REST admin API** so both surfaces share one secure core.

Two hard requirements drive the design:
1. **No caller may ever retrieve API-key / credential material.** Credentials (API keys *and* literal header/bearer values) are *write-only*: settable, never returned by any surface.
2. **The interaction must be highly secure** — TLS on the MCP loopback port, a required per-user bearer token, constant-time verified; the REST admin API gains the same token, required by default.

### Key architectural decision: shared in-process facade, not MCP-over-REST
Rather than have MCP call the REST API over HTTP (a self-directed network hop that puts the admin token in transit and violates the repo's "prefer gRPC over REST internally" rule), **both surfaces call a single in-process `ManagementFacade`**. The facade is the one place that does credential masking, credential-mode resolution, validation, and store delegation. REST and MCP are siblings over that core.

```
   ┌─ REST /admin/* (5001, plain HTTP, token REQUIRED) ─┐
   │                                                    ├─► ManagementFacade ─► ProviderConfigStore
   └─ MCP tools     (5003, TLS, Bearer token)     ──────┘   (mask · resolve · validate)   ProviderBudgetStore
                                                                                          (+ price-source/telemetry
                                                                                           tools call their stores)
   LLM forwarding + /v1/models  (5001, plain HTTP, NO token)  ── unchanged, never touches the facade
```

**Forwarding is untouched.** Port 5001 routes `/admin/*` to mapped endpoints and everything else — all real LLM traffic — falls through to the terminal `ProxyMiddleware` (`app.Run`). The token filter is an endpoint filter scoped to the `/admin` group only, so agent traffic (`/v1/*`, `/v1/models`) never sees it: no token, no auth header, same base URL and port. Agents keep working with zero changes. We do **not** put TLS on 5001 (that would break the plain-HTTP base URL agents expect).

### Decisions locked with the user
- **Transport/hosting:** in-process Streamable-HTTP MCP server on a new dedicated loopback **TLS port 5003**, mirroring the 5002 gRPC pattern.
- **Scope (read + mutate):** providers/models/budgets; price sources & catalog; telemetry & spend (read-only, **aggregates only** — never raw prompt/response text). No new routing/memory/sandbox plumbing.
- **Shared core:** one `ManagementFacade` backs both MCP and the hardened REST `/admin/*`.
- **REST auth default:** the per-user token is **required by default** on `/admin/*`; the **GUI is updated** to read the same token file and send it (in scope).
- **Credential masking:** **write-only everywhere** — no surface returns literal API keys or literal header values; on save, a **blank value preserves the stored one** (mirrors `ApiKey` "literal" mode). One projection, no per-surface leniency.
- **Auth:** one auto-generated per-user token in an ACL-restricted file under `%LOCALAPPDATA%\TotallyHotArcRouter\`, constant-time compared; MCP presents it as `Authorization: Bearer`, REST as `X-Admin-Token`.

## Components & files

### Shared core (new, in `src/TotallyHotArcRouter/Proxy/Management/`)
1. **`ManagementFacade.cs`** — the security boundary and single source of truth. Public surface:
   - `ListProviders()` → masked `ProviderView` records (see below). Reads `IProviderConfigStore.Snapshot` + `ProviderBudgetStore.GetStatus`, reusing the projection logic currently inline in [ProviderAdminEndpoints.cs:164-206](../../src/TotallyHotArcRouter/Proxy/Management/ProviderAdminEndpoints.cs).
   - `UpsertProviderAsync`, `RemoveProviderAsync`, `UpsertModelAsync`, `RemoveModelAsync`, `SetBudget`, `DiscoverModelsAsync` — the write/validate helpers, moved out of `ProviderAdminEndpoints` (credential-mode `literal`/`envVar`/`none` resolution, budget non-negative check, 404/referenced-model checks). Never exposes `Snapshot.Options`/`ProviderOptions` directly.
   - **Write-only + preserve-on-blank:** `MergeProvider` extended so a blank literal **header value** preserves the existing stored value (today only `ApiKey` does this; headers are replaced wholesale). Result records drop secret material.
2. **Masked view records** (`ProviderView` etc., updated): `HasApiKey` (bool) + `ApiKeyEnvVar` (name only); each header becomes `HeaderView(string Name, HeaderSource Source, string? ValueEnvVar)` with `enum HeaderSource { Literal, EnvVar, None }` — **the literal `Value` field is removed** so it can't be returned by any caller. Models/budgets/spend carried as-is.
3. **`ManagementAccessToken.cs`** — persist-or-create per-user token, mirroring [TelemetryTlsCertificate.cs](../../src/TotallyHotArcRouter/Telemetry/TelemetryTlsCertificate.cs):
   - `GetOrCreate(string? path)` → existing token file, else `RandomNumberGenerator.GetBytes(32)` base64url-encoded, written to `%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt`.
   - Restrictive ACL: Windows `FileSecurity` (Full to `WindowsIdentity.GetCurrent().User`, inheritance removed); POSIX `File.SetUnixFileMode(600)`.
   - `Verify(presented, expected)` → `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes (constant-time).

### MCP surface (new, in `src/TotallyHotArcRouter/Mcp/`)
4. **`McpOptions.cs`** — repo options convention (`SectionName = "Mcp"`, `init` props, `EnsureValid()`): `Enabled` (default `true`), `Port` (default `5003`), optional `TokenFilePath`.
5. **`McpBearerAuthMiddleware.cs`** — before `MapMcp()`; reads `Authorization: Bearer <token>`, 401 (JSON error envelope like `/admin`) when missing/invalid via `ManagementAccessToken.Verify`.
6. **`Tools/ProviderMcpTools.cs`**, **`Tools/PriceSourceMcpTools.cs`**, **`Tools/TelemetryMcpTools.cs`** — `[McpServerToolType]` classes, `[McpServerTool, Description]` methods, DI-injected:
   - *Providers/models/budgets (via facade):* `list_providers`, `upsert_provider`, `remove_provider`, `upsert_model`, `remove_model`, `set_provider_budget`, `discover_models`.
   - *Price sources (direct to stores — no secrets):* `list_price_sources`, `set_price_source_enabled`, `reorder_price_sources`, `refresh_price_sources` (one `RunCycleAsync`), `get_model_price` (`IModelPriceLookup`).
   - *Telemetry/spend (read-only, aggregates):* `get_budget_status`/`get_spend_summary` from `ProviderBudgetStore.GetStatus` + `SpendTracker`. **No** raw-event/prompt-text tool.
7. **`McpServer.cs`** + **`McpHostedService.cs`** — minimal web host mirroring [ProxyServer.cs](../../src/TotallyHotArcRouter/Proxy/ProxyServer.cs): `WebApplication.CreateBuilder`, register stores handed across the constructor + `AddMcpServer().WithHttpTransport().WithTools<...>()`, Kestrel `ListenLocalhost(port, o => { o.UseHttps(TelemetryTlsCertificate.GetOrCreate()); })` (reuse the `CN=localhost` loopback cert), then `app.UseMiddleware<McpBearerAuthMiddleware>(); app.MapMcp();`. Cert/token init in try/catch, gated by `McpOptions.Enabled` (non-essential-skip posture).

### REST hardening (modify)
8. **`ProviderAdminEndpoints.cs`** — delegate all logic to `ManagementFacade` (projection/merge/validation move into it). **Token now required by default:** the group filter is wired to the always-present `ManagementAccessToken` value rather than the (usually empty) `Management:Token` config, so `/admin/*` returns 401 without the token. `HeaderView` loses its literal `Value`.

### GUI update (modify, so it keeps working against hardened REST)
9. **`TotallyHotArcRouter.Gui.Admin/ProviderAdminClient.cs` + `TotallyHotArcRouter.Gui/Services/ProviderAdminStore.cs`** — read the shared token file (small path helper duplicated here, as the GUI can't reference the exe — same pattern as `TelemetryChannelFactory`'s duplicated trust logic) and send `X-Admin-Token` on every call.
10. **`TotallyHotArcRouter.Gui.Admin/ProviderAdminModels.cs` + the Blazor provider/header editor** — `HeaderView` DTO drops the literal value; the header editor shows a "(set)" placeholder for `Literal` headers and treats a blank input as "keep existing" (preserve-on-blank), matching the API-key field's existing behavior.

### Wiring / config (modify)
11. **`TotallyHotArcRouter.csproj`** — add `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` (pin current stable; verify at implement time; no central package management, pin inline).
12. **`Hosting/ServiceCollectionExtensions.cs`** — register `ManagementFacade`, the MCP tool types, bind `McpOptions`, register `McpHostedService` (factory, passing stores across like the `ProxyHostedService` block at [ServiceCollectionExtensions.cs:151-171](../../src/TotallyHotArcRouter/Hosting/ServiceCollectionExtensions.cs)); pass the `ManagementAccessToken` value into `ProxyHostedService`/`ProxyServer` as the `managementToken`.
13. **`appsettings.json`** — add `"Mcp": { "Enabled": true, "Port": 5003 }`.
14. **`docs/router/mcp-endpoint.md`** (new) — transport, ports, token file + ACL, the write-only-credential guarantee, tool surface, and the REST-hardening note. (Optionally record the threat model in the stub `SECURITY.md`.)

## Testing (xUnit v3 + Moq + FluentAssertions, in `TotallyHotArcRouter.Tests/`)

Reuse [InMemoryProviderConfigStore.cs](../../src/TotallyHotArcRouter.Tests/Proxy/InMemoryProviderConfigStore.cs); mirror [ServiceCollectionExtensionsTests.cs](../../src/TotallyHotArcRouter.Tests/Hosting/ServiceCollectionExtensionsTests.cs) and [TelemetryGrpcServiceTests.cs](../../src/TotallyHotArcRouter.Tests/Telemetry/TelemetryGrpcServiceTests.cs).

- **`ManagementFacadeTests`** (critical security core — covers both surfaces at once): never returns a literal API key; never returns a literal header value; returns `HasApiKey` + env-var names + `HeaderSource` only; upsert with a literal key/header persists but the projection masks it; **blank value on save preserves the stored secret**; credential-mode `literal`/`envVar`/`none`; budget non-negative; remove-provider 404 / referenced-model rejection.
- **`ManagementAccessTokenTests`**: strong token generated, persisted + reloaded identically, `Verify` accepts match / rejects wrong or truncated; on Windows the file ACL is restricted to the current user.
- **`McpBearerAuthMiddlewareTests`**: missing header → 401, wrong token → 401, correct → passes to next (`DefaultHttpContext` + spy `RequestDelegate`).
- **REST hardening tests** (extend existing `ProviderAdminEndpoints` tests): `/admin/*` returns 401 without the token and 200 with it; `HeaderView` carries no literal value; delegation to the facade produces the masked shape. **Regression guard:** a `/v1/models` (or forwarding) request needs **no** token and is unaffected.
- **MCP tool tests** (`ProviderMcpToolsTests`/`PriceSourceMcpToolsTests`/`TelemetryMcpToolsTests`): each tool delegates correctly and returns masked output (tools are plain methods — instantiate with fakes).
- **`McpServiceRegistrationTests`**: `AddTotallyHotArcRouter` registers facade, tool types, options, `McpHostedService`, and the token is passed to the proxy; provider resolves them.
- **GUI**: `ProviderAdminStore`/`Client` send `X-Admin-Token` (assert via a stub handler); header editor preserve-on-blank unit test.
- *Optional integration:* MCP SDK in-memory client/server — unauthenticated call rejected; `list_providers` round-trip returns masked data.

Each test < 5s; warning-free build (`TreatWarningsAsErrors`); coverage ≥ 80% (AGENTS.md).

## Verification

1. `dotnet build src/TotallyHotArcRouter.slnx` — warning-free.
2. `dotnet test src/TotallyHotArcRouter.slnx` — all pass, ≥ 80% coverage.
3. Run the router: `%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt` created with a restricted ACL; 5003 listening (TLS); 5001 still plain HTTP.
4. **Forwarding unaffected:** an OpenAI-shaped request to `http://localhost:5001/v1/...` (no token) still forwards and returns normally.
5. **REST hardened:** `GET http://localhost:5001/admin/providers` → 401 without the token, 200 with `X-Admin-Token`; response shows `hasApiKey`/env-var names/`headerSource` and **no** literal key or header value. The GUI (updated) still manages providers.
6. **MCP:** point an MCP client (or `curl --http2` + Bearer token) at `https://localhost:5003`: `list_providers` returns masked data; unauthenticated → 401. `upsert_provider` with a literal key, then `list_providers` — key stored (routing still works), never echoed.
7. Store learnings (shared-facade design, header-value write-only tightening, token file, port map, forwarding-untouched) in Qdrant + the Obsidian vault (`ArcRouter/architecture/`), and this project's memory.

## Out of scope
- Runtime mutation of `RoutingOptions`, `RouterMemory`, `SandboxOptions` (remain appsettings-only).
- Streaming raw telemetry/prompt content over MCP (aggregates only).
- Migrating the GUI off REST entirely / consolidating telemetry+price-source gRPC onto MCP (a larger future effort; this work leaves those on gRPC).

