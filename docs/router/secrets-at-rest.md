# Secrets at rest

Reference documentation for the protected secret store implemented per
[`secrets-at-rest-plan.md`](secrets-at-rest-plan.md), all six phases. Phases 4-6 (Anthropic Admin-key
sourcing and the reported-usage card) are functional for any account with a Console/Enterprise Admin API
key; an account without one (a Claude Pro/Max subscription, say - see the plan's §2 eligibility check)
simply has nothing to store, every reconciliation cycle involving it is a no-op, and Phase 6's card
renders its empty state. The rest of the application works fully either way.

## 1. What is protected today

| Secret | Where it lives | Protection |
|---|---|---|
| Provider header literals (inference API keys) with `Locked = true` | Protected store, referenced from `model-routing.json` by `ProviderHeader.ValueSecretRef` | DPAPI-encrypted, ACL-restricted (`ProtectedSecretStore`) |
| Telemetry certificate password | Protected store (`telemetry:cert-password`) | DPAPI-encrypted, ACL-restricted |
| Management token | `%LOCALAPPDATA%\TotallyHotArcRouter\management-token.txt` | ACL-restricted via `ManagementAccessToken.WriteRestricted`; contents plaintext (deliberately left as-is - see §5) |
| Admin API keys (OpenAI/Anthropic cost reconcilers) | Protected store (`reconciliation:{provider}:admin-key`), stored secret preferred, environment variable fallback | DPAPI-encrypted, ACL-restricted; optional - an account with neither configured simply has no reconciler/usage report for that provider |
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
| A reconciler's Admin API key | `reconciliation:{provider}:admin-key` (only `openai`/`anthropic` are recognized) |

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

`PUT`/`DELETE /admin/secrets/{name}` (`ManagementFacade.SetSecret`/`DeleteSecret`) is the one write path
into the store from outside a provider's own headers, and it is intentionally not a generic secret
store endpoint: `name` must match `reconciliation:{openai|anthropic}:admin-key` exactly, or the request
is rejected with 400 before it ever reaches `ISecretWriter`. `GET /admin/providers` reports only a
`HasStoredAdminKey` boolean per provider - never the key.

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

## 7. Admin key sourcing and reported usage (Phases 4-6)

`ServiceCollectionExtensions.TryResolveAdminApiKey` resolves a provider's Admin API key stored secret
first, then `CostTracking:Reconciliation:Providers:{provider}:AdminApiKeyEnvVar` - so a key saved through
the GUI takes priority over (and needs no change to) an existing environment-variable deployment. This
runs fresh on every reconciliation cycle (`BuildCostReconcilers`, called again each cycle via the
registered `Func<IReadOnlyList<IProviderCostReconciler>>` rather than once at DI construction), and
`AnthropicUsageReportService` resolves the same way for the reported-usage fetch - so a key pasted into
the GUI takes effect on the very next hourly cycle, no restart required.

Anthropic's own reported per-model daily token usage (`GET /v1/organizations/usage_report/messages`) is
fetched on that same cycle and stored raw (no derived totals) in `provider_reported_usage_snapshot`,
keyed `(provider_key, usage_day, model)`; see `agent-cost-tracking.md` §4 for the resolution order and
`secrets-at-rest-plan.md` §8 for the full fetch/storage/GUI design. An account with no Admin API key
configured simply never populates this table, and `ManagementFacade.ProviderView.ReportedUsage` reads
back `null`.
