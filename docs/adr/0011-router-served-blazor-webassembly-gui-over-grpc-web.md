# 0011. Router-served Blazor WebAssembly GUI over gRPC-Web; retire port 5002 and REST `/admin`

**Status:** accepted <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-14
**Deciders:** David Pizon

> **Accepted 2026-09-15**, on completion of the web GUI migration plan's Phases P1-P10: the router serves
> the WASM dashboard, port 5002 and REST `/admin` are both retired, and the plan's P11 "Docs close-out"
> phase is what marks this ADR accepted rather than leaving it perpetually "proposed" after the fact.

## Context and Problem Statement

The dashboard (`src/TotallyHotArcRouter.Gui`) is a Windows-only .NET MAUI Blazor Hybrid app: a
`BlazorWebView`/WebView2 host that ships in the system tray and talks native gRPC to the router over
`https://localhost:5002`. This makes the entire product Windows-bound, carries WebView2-specific
failure modes (blank dashboards, user-data-folder hacks, `SetWebViewVisible` workarounds — see
`MainPage.cs`, `WebViewUserData.cs`), and its tests can't run on Linux CI (the
`windows-gui-build-and-test` job in `.github/workflows/dotnet-ci.yml` is disabled).

The goal ([`docs/archive/gui/web-gui-migration-plan.md`](../archive/gui/web-gui-migration-plan.md)) is for the router
(`src/TotallyHotArcRouter`, already `Microsoft.NET.Sdk.Web`) to serve the existing Razor dashboard to
any browser, cross-platform, on a port configured in `appsettings.json`. This ADR covers the transport
and hosting shape; ADR-0012 covers auth, ADR-0013 covers TLS trust, ADR-0014 covers cross-platform
paths/secrets.

This also supersedes **ADR-0007** (`ProviderAdminClient` stays on HTTP): that decision predates the
migration to gRPC that has since happened in code — `ProviderAdminClient` already extends the gRPC
plumbing pattern — so 0007's premise (an inconsistent HTTP holdout) no longer describes the codebase.

## Decision Drivers

- **Reuse over rewrite.** ~85 files of Razor components, stores, and gRPC clients
  (`Components/*.razor`, `Services/*Store.cs`, `Gui.Telemetry/GrpcAdminClientBase`) already work; the
  migration should move them, not rewrite them against a new protocol.
- **No CORS, one origin.** Serving the WASM bundle and the API from the same router process avoids a
  second network boundary to secure.
- **Browsers can't dial native gRPC.** HTTP/2 trailers-based gRPC isn't reachable from
  `fetch`/`XMLHttpRequest`; gRPC-Web is the standard bridge and needs no new IDL or codegen — it reuses
  `telemetry.proto`/`admin.proto` as-is.
- **Fewer listeners, fewer ports to secure and document.** Every extra Kestrel listener is another
  surface for auth, TLS trust and firewall docs to describe.
- **Nothing production-side calls REST `/admin` any more.** `ProviderAdminEndpoints`/
  `UsageAdminEndpoints` (port 5001) were CodeGraph-verified to have zero production callers: the GUI
  uses gRPC, and MCP calls `ManagementFacade` in-process. Carrying an unused authenticated surface is
  pure liability.

## Considered Options

- Option A — Blazor Server (interactive server render mode): components run in the router process over
  a SignalR circuit; the browser is a thin renderer.
- Option B — Blazor WebAssembly over gRPC-Web on a new router-hosted web port (chosen).
- Option C — Blazor WebAssembly calling the existing REST `/admin` API instead of gRPC.
- Option D — Leave port 5002 as a second listener indefinitely, and just add a browser-facing gRPC-Web
  listener alongside it.

## Decision Outcome

Chosen option: "Option B", because it keeps every store's data-access code (`GrpcAdminClientBase`, the
generated gRPC clients, `IAdminServiceModule`) unchanged in *shape* — only the channel becomes
gRPC-Web — while giving every state-heavy singleton (`LiveDataStore`, `ToastService`, admin stores) a
natural per-tab lifetime for free, which Option A (Blazor Server) does not: those singletons would need
to become per-circuit or be rewritten as shared state with cross-tab-bleed guards (see the "state model"
research: `ToastService`'s toasts, `LiveDataStore`'s single stream, `UpdateStore.Environment.Exit` are
all singleton-shared problems under Server). Option C would mean re-plumbing the Governance/Providers
domain that already migrated off HTTP for no defect it fixes — the same "no confirmed benefit"
reasoning ADR-0007 itself used, just pointed the other direction now that the transport question is
being revisited anyway. Option D was rejected because a second live listener is a second thing to keep
in TLS/auth/firewall lockstep with the first, and CodeGraph shows nothing production-side still needs
5002 once the web port carries gRPC too.

Concretely:
- New `WebInterface` router config section: `Port` (default `5004`), `BindAddress` (default loopback).
- The web port serves: the WASM static bundle, `UseGrpcWeb()`-wrapped gRPC services (every
  `IAdminServiceModule`, telemetry, provider/usage admin), and the auth endpoints from ADR-0012.
- Port 5002 (native gRPC) is retired once the cutover (`web-gui-migration-plan.md` P9) completes.
- REST `/admin/*` and `/admin/usage/*` (`ProviderAdminEndpoints.cs`, `UsageAdminEndpoints.cs`) are
  deleted in the same phase that introduces gRPC-Web (P2), after moving any test assertion not already
  covered onto the gRPC service tests, so `ManagementFacade` coverage does not regress.
- Endpoints are scoped per listening port by `HttpContext.Connection.LocalPort` (not `RequireHost`,
  which matches only the `Host:` header and is not a real port boundary): the LLM proxy terminal
  middleware maps only to the proxy port(s); gRPC/GUI endpoints map only to the web port.

### Consequences

- Good, because the router becomes a single deployable that serves its own management UI on every
  platform .NET 10 runs on.
- Good, because deleting REST `/admin` removes an entire authenticated attack surface and a duplicate
  code path (`ManagementFacade` already has one caller-facing shape: gRPC) with no loss of function.
- Bad, because every store's default-constructed `GrpcChannel` (~15 of them) and the
  `TelemetryChannelFactory` cert-validation callback need reworking for a browser-safe transport — this
  is real, scoped refactor work (`web-gui-migration-plan.md` P5).
- Neutral, because gRPC-Web restricts client-streaming RPCs (none exist in this codebase today) and
  requires the router to run HTTP/2 (already true for the native gRPC listener).

## Pros and Cons of the Options

### Option A — Blazor Server

- Good, because component code needs no browser-safety pass (no WASM trimming, no `PlatformNotSupportedException`
  landmines) and startup is instant (no WASM download).
- Bad, because every long-lived singleton service becomes multi-circuit shared state with no natural
  isolation boundary, requiring a rewrite of `LiveDataStore`, `ToastService`, and `UpdateStore` to be
  circuit-scoped or explicitly reasoned about as shared.
- Bad, because it keeps a persistent SignalR connection alive per browser tab against the router
  process itself, adding a second streaming transport next to gRPC-Web/native gRPC rather than
  replacing one.

### Option B — Blazor WebAssembly over gRPC-Web (chosen)

- Good, because per-tab component/service state is free (a fresh WASM app domain per tab), matching
  today's single-window-per-launch MAUI behavior closely.
- Good, because the existing generated gRPC client code and `GrpcAdminClientBase` pattern carries over
  almost unchanged — only the channel transport changes.
- Bad, because the WASM payload must be downloaded and JIT/interpreted client-side (mitigated by
  fingerprinted caching), and every dependency (Serilog, Grpc.Net.Client.Web) must survive .NET
  trimming under `TreatWarningsAsErrors` (spike S4 in the plan doc).

### Option C — WASM over REST `/admin`

- Good, because `fetch`-based REST needs no gRPC-Web bridge library.
- Bad, because it reintroduces two parallel admin transports (REST for the browser, gRPC for
  MCP/whatever else) with two different error-shape contracts, exactly the inconsistency ADR-0007
  documented and chose not to fix — now with an added browser-facing surface instead of a removed one.

### Option D — Add gRPC-Web alongside the existing native gRPC listener

- Good, because it's the smallest single-PR change.
- Bad, because it leaves two live TLS listeners (5002 and the web port) needing identical trust, auth,
  and firewall treatment indefinitely, for no caller that still needs native gRPC once the tray and GUI
  both move to gRPC-Web/HTTP.

## More Information

See [`docs/archive/gui/web-gui-migration-plan.md`](../archive/gui/web-gui-migration-plan.md) phases P1–P2 (listener and
port-scoping work) and P9 (retiring 5002 and REST `/admin`). Supersedes
[ADR-0007](0007-provider-admin-client-stays-on-http.md).
