# Secrets at rest

Reference documentation for the protected secret store implemented per
[`secrets-at-rest-plan.md`](secrets-at-rest-plan.md) Phases 1-3. Phases 4-6 (Anthropic Admin-key
sourcing and the reported-usage card) are documented in that plan and in
[`../gui/backlog.md`](../gui/backlog.md) item #3, and remain gated on the eligibility check described
there.

## 1. What is protected today

| Secret | Where it lives | Protection |
|---|---|---|
| Provider header literals (inference API keys) with `Locked = true` | Protected store, referenced from `model-routing.json` by `ProviderHeader.ValueSecretRef` | DPAPI-encrypted, ACL-restricted (`ProtectedSecretStore`) |
| Telemetry certificate password | Protected store (`telemetry:cert-password`) | DPAPI-encrypted, ACL-restricted |
| Management token | `%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt` | ACL-restricted via `ManagementAccessToken.WriteRestricted`; contents plaintext (deliberately left as-is - see §5) |
| Admin API keys (OpenAI/Anthropic cost reconcilers) | Environment variable only | n/a - nothing is stored (Phase 4, not yet built) |
| AWS credentials | Environment variable only, by design | n/a - no change |

A provider header that predates this store, or was written while the store was unavailable, may still
hold a plaintext `Value` in `model-routing.json` - see §4's non-Windows behavior.

## 2. Storage

One file, `%LOCALAPPDATA%\TotallyHotArcRouter\secrets.dat`, holding a DPAPI-encrypted (`CurrentUser`
scope, with a fixed application-specific `optionalEntropy`) JSON map of name → value. The whole blob is
encrypted as a unit, not per-value, so secret *names* are hidden too. Writes go through the same
create-then-restrict-then-write sequence `ManagementAccessToken` uses (factored into `SecureFile`), then
an atomic temp-file-then-`File.Move` overwrite, and are serialized by a path-scoped named `Mutex` so two
processes (router + GUI) editing at once cannot interleave and lose an entry.

Implementation: [`ProtectedSecretStore.cs`](../../src/TotallyHotArcRouter/Proxy/Management/ProtectedSecretStore.cs),
split into `ISecretReader` (the router's own resolution paths only) and `ISecretWriter` (what
`ManagementFacade` is injected with).

## 3. Naming convention

Deterministic, so prefix cascades (e.g. "delete everything for this provider") work without enumerating
the store:

| Secret | Name |
|---|---|
| A provider header value | `provider:{providerKey}:header:{headerName}` |
| The telemetry certificate password | `telemetry:cert-password` |
| A reconciler's Admin API key (Phase 4, not yet built) | `reconciliation:{provider}:admin-key` |

## 4. Non-Windows behavior: refuse, do not degrade

`ProtectedSecretStore` requires Windows DPAPI. On any other platform: `Write` throws
`PlatformNotSupportedException`; `TryRead`/`Exists`/`Delete`/`DeleteByPrefix` all report "nothing here"
rather than throwing. Every caller that writes through the store (`ManagementFacade`'s header upsert,
`ProviderConfigStore`'s migration, `TelemetryTlsCertificate`) catches the exception and falls back to
its pre-Phase-1 behavior - a plaintext literal in `model-routing.json`, or the legacy
`telemetry-cert-pwd.txt` file - rather than silently writing an unencrypted file under a name that
claims to be protected.

## 5. The management surface is write-only for secrets

**There is no `GET /admin/secrets/{name}`.** `PUT`/`DELETE` only, and this is a deliberate non-goal, not
an oversight - it is not to be added later "for debugging". `ManagementFacade` receives `ISecretWriter`
for its write path and therefore cannot read a secret's value back even by mistake; a separate,
narrowly-scoped `ISecretReader` is injected only for the router's own outbound requests (the real
proxied traffic in `ModelRouteResolver`, and the "Discover models" probe in
`ManagementFacade.DiscoverModelsCoreAsync`) and is never consulted while building any value returned to
a caller. `GET /admin/providers` and the MCP `list_providers` tool report a header's source as
`"protected"` and nothing else - see `HeaderValueSource.Protected`.

A known scope limit: the endpoint-capability scanner and tool-call dialect resolver
(`ProviderEndpointScanner`, `ModelDialectResolver`) do not currently take a secret reader, so a
best-effort capability scan or dialect detection for a provider whose credential lives only in the
store may report the endpoint as unreachable rather than probing it with the real key. This does not
affect real request routing, which always resolves through the store.

## 6. The management token is deliberately not covered

`management-token.txt` stays as it is. See
[`signalr-hub-security.md`](signalr-hub-security.md) for the reasoning: `TotallyHotArcRouter.Gui.Admin`
deliberately does not reference the router project, and folding the token into the shared store would
mean either duplicating it into `Gui.Admin` or introducing a shared assembly - a bigger change than
warranted for the one secret in the inventory that is already ACL-restricted.
