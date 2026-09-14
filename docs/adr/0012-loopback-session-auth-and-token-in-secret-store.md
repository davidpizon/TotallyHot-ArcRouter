# 0012. Loopback session-cookie auth for the web GUI; management token moves into the encrypted secret store

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-14
**Deciders:** David Pizon

## Context and Problem Statement

Every management surface (REST `/admin`, native gRPC, MCP) is gated today by one shared 32-byte token
(`ManagementAccessToken`), persisted in plaintext at
`%ProgramData%\TotallyHotArcRouter\management-token.txt` with a Users-readable ACL (0644 off Windows).
The MAUI GUI reads this file directly (`Gui.Admin/ManagementTokenReader.cs`) because it runs as the
same logged-in user as the router's interactive session.

A browser cannot read that file. It has no filesystem access, so the WASM GUI (ADR-0011) needs a way to
authenticate that doesn't require shipping a local file's contents to client-side JavaScript. This ADR
also decides whether the token file survives at all, since a browser-reachable credential source
changes what "management-token.txt" is protecting.

## Decision Drivers

- **No worse than today's boundary.** The current token file is readable by any local account on the
  machine (Users ACL / 0644). A browser-based scheme should not narrow that boundary in a way that
  breaks existing workflows, but it also must not widen it to the network.
- **Cross-platform.** Windows-only mechanisms (e.g. reading a file via a Windows-specific API) don't
  work for the WASM GUI on Linux/macOS.
- **Zero required user action for the common case.** A developer opening `https://localhost:5004`
  should not have to hunt down and paste a token on every browser/profile.
- **Docker changes the trust model.** Inside a container, "loopback" is the container's own network
  namespace — a host user connecting through a published port is not loopback from the container's
  point of view, so a non-loopback path must exist.
- **secrets-at-rest §4 says the management surface is write-only for secrets** (no RPC ever returns
  secret material). Any "show me the token" affordance is a deliberate, documented exception to that
  rule, not a silent violation of it.

## Considered Options

- Option A — Loopback session cookie: the router issues a signed, HttpOnly, Secure, SameSite=Strict
  cookie to any request that looks like it came from this machine (Host header + Origin + remote IP all
  check out); non-loopback callers fall back to a token login page (chosen).
- Option B — One-time launch link: the tray (or a CLI command on Linux/macOS) mints a single-use nonce
  that the browser exchanges for a session; nothing works without going through that launcher first.
- Option C — Paste-the-token: first visit prompts for the token (read from the file by the user),
  stored in `localStorage` and sent as a header on every call.
- Option D — No auth on the web port at all; rely entirely on loopback binding as the boundary.

## Decision Outcome

Chosen option: "Option A", because it is the only option that works identically, with zero setup step,
on every platform the router runs on (Windows, Linux, macOS, and — via the token-login fallback —
Docker), and it does not narrow today's trust boundary: anyone who could already read
`management-token.txt` could already reach every management RPC, and Option A grants a session to
exactly that same population (anyone with local-process/browser access on this machine), gated by the
same Host/Origin checks that also defeat DNS rebinding. Option B is strictly stronger against a
shared-machine adversary, but every non-Windows, non-tray environment (a Linux server or a Docker host)
has no launcher to run the ceremony through, degrading to "run a CLI command before every fresh
browser/profile" — a real support cost for a security property the token file didn't offer either.
Option C works everywhere but requires the user to go find and copy a value on every browser/profile
and leaves that secret sitting in page-readable storage. Option D is rejected outright: even under a
loopback bind, another local process (or a script run by another local user) could otherwise call every
admin RPC with no credential at all, which is strictly weaker than today.

**Auth mechanics:**
- `POST /auth/session` on the web port: issued only when the request's `Host` is
  `localhost`/`127.0.0.1`/`[::1]` (or an explicitly configured allowed host), `Origin` matches the web
  origin (or is absent, for native/CLI callers with no `Sec-Fetch-Site`), and the remote IP is loopback
  (IPv4-mapped IPv6 normalized). Returns a `__Host-`-prefixed, HttpOnly, Secure, SameSite=Strict cookie
  signed with an in-memory HMAC key plus a rotation generation — no persistent key ring needed for
  sessions.
- `POST /auth/login` accepts the management token for non-loopback callers (Docker, remote access
  behind an explicit `WebInterface:TrustLoopback=false`/reverse proxy setup), rate-limited.
- `TelemetryAuthInterceptor` (gRPC) and the web listener accept either the cookie or the token.
- **The token itself moves into `ProtectedSecretStore`** (ADR-0014's cross-platform backend), replacing
  the plaintext file. The existing file is imported once on first startup after upgrade, then deleted,
  so already-configured MCP clients keep working without reconfiguration.
- **Explicit carve-out from secrets-at-rest §4:** a new, narrowly-scoped `ManagementTokenAdminGrpcService`
  (Get/Regenerate) — not a `ManagementFacade` method, so `ManagementFacade`'s frozen public surface is
  untouched — lets an already-authenticated session (loopback cookie or existing token) read the
  current token for copying into an MCP client config, and rotate it. This is the one place the
  write-only rule is knowingly broken, and it is broken for the same credential the file already
  exposed to any local reader.
- A CLI flag `--print-management-token` covers headless/Docker bootstrap without a browser.
- Named tunnels/forwarders (`ngrok`, `tailscale serve`, VS Code port forwarding, WSL2 mirrored
  networking) can make a genuinely remote origin appear to arrive over what looks like a loopback
  socket; `WebInterface:TrustLoopback=false` disables the loopback-cookie fast path entirely so every
  session goes through token login in that setup.

### Consequences

- Good, because opening the dashboard in a browser on the same machine needs no manual credential step,
  matching the zero-friction experience the MAUI GUI had.
- Good, because deleting the plaintext token file removes a Windows-ACL/Unix-mode-dependent secret from
  disk entirely; the only readable-by-any-user artifact left in this design is the session cookie a
  same-machine caller can already mint for themselves.
- Bad, because the loopback-cookie fast path is, by design, available to any process/browser profile on
  the machine — this is a documented continuation of today's boundary, not a new weakness, but it must
  be stated plainly for anyone auditing this decision later.
- Bad, because `ManagementTokenAdminGrpcService` is a deliberate, narrow exception to the write-only
  secrets rule and must be reviewed whenever that rule is revisited.
- Neutral, because token-login sessions need throttling and rotation-invalidation that loopback sessions
  do not, adding a small amount of session-state bookkeeping.

## Pros and Cons of the Options

### Option A — Loopback session cookie (chosen)

- Good, because it requires no companion app (tray, CLI) to be running for the common desktop-browser
  case.
- Good, because Host/Origin/remote-IP checks together defeat the two concrete network attacks this
  needs to defeat: DNS rebinding and cross-site request forgery from an unrelated tab.
- Bad, because "any local account" remains the effective boundary for the fast path, same as today.

### Option B — One-time launch link

- Good, because only processes the user explicitly launched (via the tray or a CLI command) can ever
  obtain a session — stronger against another local account on a shared machine.
- Bad, because it requires a launcher to exist and run before every fresh browser/profile visit; no
  launcher exists on a headless Linux server or inside Docker without extra plumbing.

### Option C — Paste-the-token

- Good, because it is the simplest to implement and works identically everywhere, including Docker,
  with no Host/Origin/IP logic at all.
- Bad, because the token sits in `localStorage`, readable by any script that runs in that origin
  (including a future XSS bug in a vendored JS dependency), and the user must go find the file/value
  manually on every new browser or profile.

### Option D — Loopback bind only, no auth

- Good, because it is zero code.
- Bad, because it is a strict regression from today: any unauthenticated local process could call every
  admin RPC, whereas today's file-based token at least requires reading a specific file.

## More Information

See [`docs/gui/web-gui-migration-plan.md`](../gui/web-gui-migration-plan.md) phase P4 (auth
implementation and its DNS-rebinding/CSRF test matrix) and P9 (token-file import/delete, Copy/Regenerate
UI). Builds on [`docs/adr/0011-router-served-blazor-webassembly-gui-over-grpc-web.md`](0011-router-served-blazor-webassembly-gui-over-grpc-web.md)
for the web port this auth model gates, and on
[ADR-0014](0014-cross-platform-service-layout-and-secret-backend.md) for the secret store the token
moves into. Relevant existing doc: `docs/router/secrets-at-rest.md` §4 (write-only management surface).
