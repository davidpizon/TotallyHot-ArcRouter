# Secrets at rest

Reference documentation for the protected secret store implemented per
[`secrets-at-rest-plan.md`](secrets-at-rest-plan.md), all six phases. Phases 4-6 (Anthropic Admin-key
sourcing and the reported-usage card) are functional for any account with a Console/Enterprise Admin API
key; an account without one (a Claude Pro/Max subscription, say - see the plan's §2 eligibility check)
simply has nothing to store, every reconciliation cycle involving it is a no-op, and Phase 6's card
renders its empty state. The rest of the application works fully either way.

## 1. What is protected today

> **Updated for the web GUI migration plan's P11 docs close-out (2026-09-15).** Two things changed since
> this document was written: Phase P3 gave `ProtectedSecretStore` a cross-platform, non-Windows protector
> (ASP.NET Data Protection) instead of refusing to write outside Windows, and Phase P9 moved the
> management token itself into the store, deleting the plaintext `management-token.txt` file this
> document originally described as deliberately-plaintext. §2 and §4 below are rewritten; the rest of the
> document (naming convention, write-only invariant) is unchanged.

| Secret | Where it lives | Protection |
|---|---|---|
| Provider header literals (inference API keys) with `Locked = true` | Protected store, referenced from `model-routing.json` by `ProviderHeader.ValueSecretRef` | Encrypted (DPAPI on Windows, ASP.NET Data Protection elsewhere - see §2), ACL-restricted (`ProtectedSecretStore`) |
| Telemetry certificate password / local CA + leaf key material | Protected store (`telemetry:cert-password`, and the local-CA/leaf secrets ADR-0013 added) | Same as above |
| Management token | Protected store (moved off the plaintext `management-token.txt` file in Phase P9; the legacy file is imported once on first startup, then deleted) | Same as above |
| Admin API keys (OpenAI/Anthropic cost reconcilers) | Protected store (`reconciliation:{provider}:admin-key`), stored secret preferred, environment variable fallback | Same as above; optional - an account with neither configured simply has no reconciler/usage report for that provider |
| AWS credentials | Environment variable only, by design | n/a - no change |

A provider header that predates this store, or was written while the store was unavailable, may still
hold a plaintext `Value` in `model-routing.json` - see §4's non-Windows behavior.

## 2. Storage

One file, `secrets.dat`, under the machine-shared data directory
(`%ProgramData%\TotallyHotArcRouter\` on Windows; see
[`AppDataPaths`](../../src/TotallyHotArcRouter/Hosting/AppDataPaths.cs) for every other platform - the
same directory the management token, telemetry/CA certificates, and operational databases all use),
holding an encrypted JSON map of name → value. The whole blob is encrypted as a unit, not per-value, so
secret *names* are hidden too. Writes go through the same create-then-restrict-then-write sequence
`ManagementAccessToken` uses (factored into `SecureFile`), then an atomic temp-file-then-`File.Move`
overwrite, and are serialized by a path-scoped named `Mutex` so two processes (router + Tray) editing at
once cannot interleave and lose an entry. `ProtectedSecretStore.GetOrAdd` additionally holds one mutex
across an entire check-then-write sequence (not two separately-mutex-guarded calls), closing a real race
a composed `TryRead`+`Write` reintroduced during Phase P9 - see that method's remarks.

**Encryption is platform-appropriate, not Windows-only** (web GUI migration plan Phase P3 - this section
originally described a DPAPI-only, Windows-required implementation that has since been replaced):
- **Windows**: DPAPI, `CurrentUser` scope, with a fixed application-specific `optionalEntropy` - the
  bytes are decryptable only by the account that wrote them.
- **Elsewhere**: ASP.NET Core Data Protection, with its own key ring persisted to a `keys/` subdirectory
  of the same machine-shared directory (`PersistKeysToFileSystem`, a fixed application name and purpose
  string so the same key ring is found again across restarts). That directory is created at mode `0700`
  if missing, and a write **refuses outright** (rather than proceeding insecurely) if it already exists
  with broader permissions - see `ProtectedSecretStore.EnsureKeyDirectorySecure`.

Implementation: [`ProtectedSecretStore.cs`](../../src/TotallyHotArcRouter/Proxy/Management/ProtectedSecretStore.cs),
split into `ISecretReader` (the router's own resolution paths only) and `ISecretWriter` (what
`ManagementFacade` is injected with).

Only the router process ever opens this file directly - the Windows Tray and the browser dashboard both
go through the router's gRPC/gRPC-Web management surface instead, never touching `secrets.dat` on disk.

### 2.1 What the machine-wide move exposes

`%ProgramData%`'s inherited ACL grants `BUILTIN\Users` read. Nothing encrypted is affected — `secrets.dat`
and the management token are both encrypted/hardened at rest regardless — but two databases that are
per-machine rather than per-user are readable by **every local account**:

| File | Contents newly readable by local `Users` |
|---|---|
| `agent_telemetry.db` | Usage ledger, provider spend, price catalog. Token counts and cost, no credentials. |
| `transcripts.db` | **Raw prompt and response text**, when `TranscriptOptions.Enabled` turns capture on. |

`transcripts.db` is the one that matters. It is opt-in and retention-bounded precisely because of what it
holds, and on a single-user machine — the deployment this tool targets — "readable by local Users" and
"readable by me" are the same set. On a shared or multi-user machine they are not: leave transcripts off,
or point `Storage:TranscriptDatabasePath` at a directory you have ACL'd yourself. See
`docs/router/security-hardening-plan.md` T-07.

## 3. Naming convention

Deterministic, so prefix cascades (e.g. "delete everything for this provider") work without enumerating
the store:

| Secret | Name |
|---|---|
| A provider header value | `provider:{providerKey}:header:{headerName}` |
| The telemetry certificate password | `telemetry:cert-password` |
| A reconciler's Admin API key | `reconciliation:{provider}:admin-key` (only `openai`/`anthropic` are recognized) |

## 4. Non-Windows behavior: encrypted, not refused (Phase P3)

`ProtectedSecretStore` no longer requires Windows DPAPI - this section originally described a
non-Windows platform refusing every write with `PlatformNotSupportedException`, which the web GUI
migration plan's Phase P3 replaced with real cross-platform encryption (§2's ASP.NET Data Protection
path), specifically so the router could run as a genuine cross-platform service rather than degrading on
Linux/macOS. The one case that still refuses is a key directory whose on-disk permissions are already
broader than `0700` (§2's `EnsureKeyDirectorySecure`) - that is a "don't proceed insecurely" refusal, not
a platform gate, and it can happen on Windows too if an operator manually loosens the directory's ACL.

## 5. The management surface is write-only for secrets

**There is no read RPC for a secret's value** - only `SetSecret`/`DeleteSecret` on
`ProviderAdminService`, and this is a deliberate non-goal, not an oversight (there is likewise no REST
`/admin/secrets/{name}` any more - that whole REST surface was deleted in Phase P2). `ManagementFacade`
receives `ISecretWriter` for its write path and therefore cannot read a secret's value back even by
mistake; a separate, narrowly-scoped `ISecretReader` is injected only for the router's own outbound
requests (the real proxied traffic in `ModelRouteResolver`, and the "Discover models" probe in
`ManagementFacade.DiscoverModelsCoreAsync`) and is never consulted while building any value returned to
a caller. `ListProviders` and the MCP `list_providers` tool report a header's source as `"protected"`
and nothing else - see `HeaderValueSource.Protected`.

`SetSecret`/`DeleteSecret` (`ManagementFacade.SetSecret`/`DeleteSecret`) is the one write path into the
store from outside a provider's own headers, and it is intentionally not a generic secret store surface:
`name` must match `reconciliation:{openai|anthropic}:admin-key` exactly, or the request is rejected
before it ever reaches `ISecretWriter`. `ListProviders` reports only a `HasStoredAdminKey` boolean per
provider - never the key.

A known scope limit: the endpoint-capability scanner and tool-call dialect resolver
(`ProviderEndpointScanner`, `ModelDialectResolver`) do not currently take a secret reader, so a
best-effort capability scan or dialect detection for a provider whose credential lives only in the
store may report the endpoint as unreachable rather than probing it with the real key. This does not
affect real request routing, which always resolves through the store.

## 6. The management token is deliberately not covered

`management-token.txt` stays as it is. See
[`signalr-hub-security.md`](signalr-hub-security.md) for the reasoning: `TotallyHot.ArcRouter.Gui.Admin`
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
