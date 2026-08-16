# Routing Telemetry

> **Status: Implemented, but unverified in this repo's environment.** This repo's Linux CI/agent
> environment has no .NET SDK and cannot install one (network policy blocks the installer), so
> everything below was written by careful manual review against the existing, presumably-working
> code — it has never been compiled or run here. Treat it as review-verified, not test-verified,
> until it's built on a machine with the .NET 10 SDK. The server-side pieces do have unit test
> coverage (see "Tests" below) that should be run there to confirm.

## Purpose

`src/TotallyHotArcRouter/Telemetry/` captures per-request routing telemetry from the live traffic path
and pushes it to connected clients (currently `TotallyHot.ArcRouter.Gui`) over gRPC, so the dashboard's
Live Stream and Cost Analytics tabs can show real conversations instead of only `MockData`. See
[`../gui/dashboard.md`](../gui/dashboard.md) for how the GUI consumes this, and
[`../gui/backlog.md`](../gui/backlog.md) for the backlog items this closed out. The transport was
SignalR until [`grpc-migration.md`](grpc-migration.md) shipped (see "Transport: gRPC" below) - that
migration changed only the transport, not any of the capture logic this doc otherwise describes.

This is purely additive: every dependency it introduces into existing classes
(`ProxyServer`, `ProxyHostedService`, `ProxyMiddleware`) is an appended optional constructor
parameter with an internal default, so none of the proxy's existing request-forwarding behavior or
its existing tests change. Every telemetry operation (usage extraction, publishing) is wrapped in
try/catch so a telemetry failure can never affect the forwarded client response.

## What gets captured, per request

`ProxyMiddleware.InvokeAsync` builds and publishes one `RoutingTelemetryEvent` per forwarded
request, after the response has already been fully forwarded to the client:

| Field | Source |
|---|---|
| `SessionId`, `IsSessionSynthesized` | `SessionIdResolver`, falling back to `MessageHistoryContinuityMatcher` (see below) |
| `TurnNumber` | `IConversationTurnTracker` - `PersistentConversationTurnTracker` (ledger-seeded, app default) or `ConversationTurnTracker` (in-memory only, tests/no-ledger callers) |
| `RequestedModel`, `ResolvedModel`, `Provider` | From the existing `ModelRouteResolver` resolution already computed for routing |
| `IsFallback` | Hardcoded `false` — `ModelRouteResolver` has no fallback-routing concept today, unlike the GUI's mock data which simulates one |
| `PromptTokens`, `CompletionTokens` | `UsageExtractor`, parsed from the captured response body (see below); `null` if extraction fails or the provider is unrecognized |
| `EstimatedCostUsd` | `0` when the route's provider is free (`ProviderOptions.IsFree`); a real catalog-priced estimate when the price catalog holds a fresh (≤24h) resolved price for `(route.ModelName, route.Provider)` (`PriceCatalogModelPriceLookup` → `ModelPrice.EstimateCost`, cache-aware); otherwise `null` — an unresolved or stale price yields no cost estimate rather than a fabricated one. Also `null` if usage wasn't extracted. See [Pricing](#pricing) |
| `IsStreaming` | Whether the upstream response's `Content-Type` was `text/event-stream` |
| `LatencyToHeadersMs` | Time from sending the upstream request to receiving response headers |
| `TotalDurationMs` | Time from sending the upstream request to finishing forwarding the full body |
| `StatusCode`, `TimestampUtc` | The forwarded response's status code; capture time |
| `RequestSummary` | `RequestTextExtractor`, the newest user message's text from the request body's `messages` array (not the whole resent history); `null` if there's no user message or its content isn't text |
| `ResponseSummary` | `ResponseTextExtractor` (see below), the assistant's reply text; `null` if the provider is unsupported or no text was extractable (e.g. a tool-only response) |
| `RouterTokens`, `RouterCostUsd` | The router's *own* consumption for this request: the embedding model's tokenized sequence length (`EmbeddingResult.TokenCount`, threaded through `ModelRouteResolutionResult.RouterTokens`), priced at `Routing:SelfHostedRouterPricePerMillionTokens`. Never `null` — `0` means the router genuinely spent nothing (no embedding client, still warming up, budget exceeded, or no extractable task text), which is a measurement rather than a gap. See [Router overhead](#router-overhead) |

Both `RequestSummary` and `ResponseSummary` are truncated via `TextTruncator` (2,000 characters, with a
trailing "…" marker) before being placed on the event, so a pathological input (a huge pasted file, a
very long generated response) can't produce an outsized gRPC message. See
[`signalr-hub-security.md`](signalr-hub-security.md) for the shipped `ManagementAccessToken` gating
that makes this safe, since this is real prompt/response text flowing over the stream.

### Session/conversation identification

`SessionIdResolver` mirrors the convention established by the upstream `claude-code-router`
TypeScript project (`resolveSessionId`/`extractSessionIdFromPayload`) rather than inventing a new
one, in priority order. That project is external to this repository; the convention is reimplemented
here in .NET so clients that already emit these headers keep working unchanged.

1. Header `x-claude-code-session-id`, then `x-claude-session-id`.
2. Body field `session_id`, `sessionId`, `conversation_id`, `conversationId`, `chat_id`, `chatId`,
   `thread_id`, or `threadId` (first match wins, in that order).
3. `metadata.user_id`, split on the literal `"_session_"` marker.
4. If none of the above match, `MessageHistoryContinuityMatcher.MatchOrTrack` (see below) is tried
   before giving up.

`ConversationTurnTracker` is a `ConcurrentDictionary<string, int>` counting turns per session,
process-lifetime only (not persisted, resets on restart). It remains in the codebase and is still what
a directly-constructed `ProxyMiddleware` (tests, or any no-ledger caller) falls back to.

> **Superseding decision (2026-08-07): implemented.** The "no persistence beyond the process" model for
> turn tracking has been abandoned as part of adopting
> [`token-tracking-improvements.md`](token-tracking-improvements.md) §5.5 — once a durable per-request
> ledger exists (§5.2 there), a turn counter that restarts at 1 after a proxy restart corrupts every
> durable `(sessionId, turnNumber)` ordering built on it. As of Phase 2 of
> [`token-tracking-implementation-plan.md`](token-tracking-implementation-plan.md),
> `PersistentConversationTurnTracker` is the app's default `IConversationTurnTracker` registration: on
> first sight of a session it seeds its in-memory counter from `IUsageLedger.GetMaxTurnNumber`, then
> counts purely in memory, evicting idle sessions after 12h (safe because a resumed session simply
> re-seeds from the ledger).

**Client integration note:** plain OpenAI-compatible clients hitting `/v1/chat/completions` (as
opposed to Claude Code CLI or another client that already follows one of `SessionIdResolver`'s
conventions) commonly send none of the above by default - GitHub Copilot's VS Code OpenAI-compatible
model providers, for example, send only standard HTTP/auth headers (`Accept`, `Authorization`,
`Content-Type`, `User-Agent`, etc.) and a bare `{model, messages, stream, tools, ...}` body, with no
identifiable session/conversation field anywhere, and no extension setting to add one dynamically per
conversation (VS Code's `LanguageModelChatProvider` API itself gives extensions no session/conversation
id to work with - only the `messages` array). For a client that *can* send a stable per-conversation
value, the cleanest fix is still to send it as `x-claude-code-session-id` on every request in that
conversation (any stable string works, e.g. a client-generated GUID) - that takes priority over
everything below. For clients that can't, `MessageHistoryContinuityMatcher` is the fallback:

### Message-history continuity matching (`MessageHistoryContinuityMatcher`)

When `SessionIdResolver` finds nothing, `ProxyMiddleware` calls
`IConversationContinuityMatcher.MatchOrTrack` with the request body's `messages` array (or
`null`/empty if there isn't one). It fingerprints each message (a SHA-256 hash of its canonical JSON,
so full prompt/response text isn't retained in memory) and compares the sequence against every
currently-tracked conversation:

- If some tracked conversation's fingerprint sequence is a **proper prefix** of the incoming one (i.e.
  this request's messages are that conversation's messages plus one or more new ones appended), it's
  treated as the next turn of that session: the tracked entry is replaced with the fuller sequence and
  its session id is reused.
- Otherwise, a fresh session id is synthesized and this conversation starts being tracked (unless the
  messages array was empty, in which case nothing is tracked - there's nothing meaningful to match
  future requests against).
- Tracked conversations are evicted after 30 minutes of inactivity, so (a) memory doesn't grow
  unboundedly in a long-running proxy process and (b) an old conversation can't get accidentally
  matched against an unrelated new one hours later.

This always returns a session id - `MatchOrTrack` owns synthesis for the "no explicit id" path
entirely, so `ProxyMiddleware` no longer generates its own fallback GUID directly.

**This is a heuristic, not a real session concept.** Two known limitations, by design (see the class
remarks on `MessageHistoryContinuityMatcher` for the reasoning):

- **False positive**: two genuinely unrelated conversations that happen to open with an identical
  exchange (e.g. the same fixed system prompt plus a coincidentally identical first message) get
  merged into one tracked session.
- **False negative**: a client that edits or regenerates earlier messages, rather than only ever
  appending new ones, won't match its own prior turns (the prefix no longer matches exactly) and
  starts a new tracked conversation instead. A same-length resend (e.g. a client retry) also doesn't
  self-match, for the same reason - there's nothing new appended to confirm continuation from.

`ProxyMiddleware`'s Debug-level logging distinguishes the three outcomes for a request with no
explicit session id: a brand-new tracked session (with the request's header names and top-level body
keys, to help identify a client's actual conventions), a message-history match (with the matched
session id and turn number), and - unchanged from before - a fully resolved explicit id.

### Token usage extraction

`UsageExtractor` dispatches on `provider` to `OpenAiUsageParser` or `AnthropicUsageParser`;
unrecognized providers (Alibaba, Zhipu, Moonshot, MiniMax) return no usage rather than throwing.
Both parsers handle streaming and non-streaming responses:

- **OpenAI**: `usage.prompt_tokens`/`usage.completion_tokens` from the non-streaming body, or from
  the final SSE `data:` chunk before `[DONE]` when streaming (only present if the client requested
  `stream_options.include_usage=true` — many real client requests won't have it, so a `null` usage
  on a streaming OpenAI response is an expected, common case, not a bug).
- **Anthropic**: `usage.input_tokens`/`usage.output_tokens` from the non-streaming body, or the
  `message_start` event's `message.usage.input_tokens` (fixed) combined with the *last*
  `message_delta` event's `usage.output_tokens` (cumulative) when streaming. Also reads
  `cache_creation_input_tokens`/`cache_read_input_tokens` when present (absent on older responses ⇒
  0, never a parse failure); the final `message_delta`'s cache fields win over `message_start`'s when
  both are present, since a newer API version's cumulative delta is the final value.

**Usage-field provenance** (`UsageInfo`, `docs/router/anthropic-reported-usage-plan.md` Phase 1):

| Field | Meaning |
|---|---|
| `PromptTokens` | Standard input tokens. For Anthropic, this is `input_tokens` - tokens **after** the last cache breakpoint, not the request's full input. |
| `CompletionTokens` | Output tokens. |
| `CacheCreationTokens` | Input tokens written to a new prompt cache entry. Parsed natively from Anthropic responses; on OpenAI-shaped bodies it appears only via the `cache_creation_input_tokens` extension field an enriched translated-Anthropic response carries (see the normalization block below). `0` when absent. |
| `CacheReadTokens` | Input tokens served from an existing cache entry. Parsed natively from Anthropic responses, and normalized out of OpenAI's inclusive `prompt_tokens_details.cached_tokens` by `OpenAiUsageParser` (see below). `0` when absent. |
| `TotalInputTokens` (computed) | `PromptTokens + CacheCreationTokens + CacheReadTokens` - the true total input size a request carried. This is the *only* place this formula is defined; nothing else should re-derive it. |

`ModelPrice.EstimateCost(UsageInfo)` prices all four components, falling back to the standard input
rate for either cache dimension when the price catalog has no published rate for it - a deliberate
conservative overestimate (see `ModelPrice`'s remarks) rather than a guessed discount multiplier.

**`UsageInfo` is always additive** (Anthropic's own convention: cache tokens are separate from
`PromptTokens`, summed by `TotalInputTokens`). OpenAI's shape is instead **inclusive**
(`usage.prompt_tokens_details.cached_tokens` is a subset of `usage.prompt_tokens`), so
`OpenAiUsageParser` normalizes it at parse time - the one place this normalization happens
(`docs/router/openai-format-usage-accuracy-plan.md` §1.1/§6.1):

```text
CacheReadTokens     = cached_tokens                               (0 when absent)
CacheCreationTokens = cache_creation_input_tokens                 (0 when absent; extension field)
PromptTokens        = max(0, prompt_tokens − CacheReadTokens − CacheCreationTokens)
```

`cache_creation_input_tokens` only ever appears on an *enriched* translated-Anthropic body (see below);
a real OpenAI response has no cache-write concept and never sets it.

**Native telemetry tap for translated Anthropic traffic** (`openai-format-usage-accuracy-plan.md` §4):
when an OpenAI-format client is routed to Anthropic, `ProxyMiddleware` translates the request/response
through `AnthropicPayloadTranslator`/`AnthropicStreamTranslator` so the client sees OpenAI's shape. Usage
extraction does **not** depend on that translation being lossless: `UsageExtractor.SupportsNativeShape`
gates a second, capped capture of the pre-translation native Anthropic bytes (`CapturedResponse.NativeBytes`
in `ProxyMiddleware`), and `PublishTelemetryAsync` prefers those bytes (parsed under `route.Provider`,
i.e. `AnthropicUsageParser`) over the translated ones whenever they were captured. Today only Anthropic
has a registered native parser, so this only affects Anthropic-routed traffic; a provider with no native
parser (Gemini) keeps parsing the translated bytes exactly as before. The client-visible translated
response is *also* enriched (`AnthropicPayloadTranslator.BuildEnrichedUsage`, shared by the non-streaming
`TranslateUsage` and the streaming terminal chunk) so a client reading the OpenAI-shaped `usage` field
directly sees the same cache-aware numbers the ledger records - `prompt_tokens` becomes the inclusive
total, with `prompt_tokens_details.cached_tokens` broken out and the raw Anthropic components riding
along as `cache_creation_input_tokens`/`cache_read_input_tokens` extension fields (LiteLLM's convention).
A cache-free response is unaffected: both paths emit exactly today's legacy two-field-plus-total shape.

To extract usage without disrupting true streaming pass-through timing, `ProxyMiddleware` no longer
does a plain `Content.CopyToAsync(Response.Body)`. Instead `CopyAndCaptureAsync` loops
`ReadAsync`/`WriteAsync` manually, writing every chunk to the client immediately (same timing as
before) while separately appending a capped copy (4 MiB, `ArrayPool<byte>.Shared` buffer) to an
in-memory buffer for parsing after the response has finished forwarding.

### Request/response text extraction

Powers the Live Stream turn cards' Request/Response sections (`TurnCard.razor`).

**Request text** (`RequestTextExtractor.ExtractNewestUserMessage`) scans the already-parsed request
body's `messages` array from the end and returns the text of the most recent `role: "user"` message -
not necessarily the last array element, since a user message can be followed by tool-call/tool-result
messages in agentic workflows, and not the whole array, since OpenAI-/Anthropic-style clients resend
the full growing conversation history on every request (using the whole array would repeat every
prior turn's content in every subsequent turn's summary).

**Response text** mirrors `UsageExtractor`'s dispatch design exactly: `ResponseTextExtractor`
dispatches on `provider` to `OpenAiResponseTextParser` or `AnthropicResponseTextParser`, both handling
streaming and non-streaming responses (unrecognized providers return no text, same as usage
extraction):

- **OpenAI**: `choices[0].message.content` from the non-streaming body, or every SSE `data:` chunk's
  `choices[0].delta.content` concatenated in stream order when streaming.
- **Anthropic**: the top-level `content` block array from the non-streaming body, or every
  `content_block_delta` event's `delta.text` concatenated in stream order (only when
  `delta.type == "text_delta"` - other delta types, e.g. `input_json_delta` for tool-use argument
  streaming, are skipped) when streaming.

Both request and response `content` can be a plain string or an array of parts/blocks (OpenAI
multimodal parts, Anthropic content blocks); `MessageContentTextExtractor` (shared by the request
extractor and both response parsers) concatenates only `type: "text"` parts, skipping images,
`tool_use`, etc., rather than failing.

### Pricing

**There is no hand-maintained price table — prices come from the auto-refreshed catalog.**
TotallyHotArcRouter used to carry a hand-maintained `Pricing` section in `appsettings.json` whose own
comment admitted the numbers were illustrative placeholders; it was deleted rather than maintained
(see [`pricing-seed-removal.md`](pricing-seed-removal.md)). A fabricated cost is indistinguishable from
a real one at the point someone reads it, which makes it worse than no cost at all. The replacement —
the multi-source SQLite catalog in [`model-price-catalog.md`](model-price-catalog.md), fed by LiteLLM
and OpenRouter and resolved onto configured model names by
[`d3-alias-resolution.md`](d3-alias-resolution.md)'s exact auto-match — is now live and wired into this
pipeline via `PriceCatalogModelPriceLookup` (a 24-hour freshness floor; stale is treated as unknown).

So `EstimatedCostUsd` today is exactly one of three things:

| Value | When |
|---|---|
| `0` | The resolved route's provider is flagged free (`ProviderOptions.IsFree`) — a *known* price of zero |
| a positive estimate | The catalog holds a fresh (≤24h) price resolved to `(route.ModelName, route.Provider)`; `ModelPrice.EstimateCost` prices all four token dimensions (cache rates fall back to the standard input rate when unpublished — a documented conservative overestimate) |
| `null` | Everything else: no fresh resolved price for this model, or usage couldn't be extracted |

`ProviderOptions.IsFree` marks a provider that genuinely costs nothing — a local Ollama runtime, say.
That is a fact about the deployment rather than an estimate, which is why it's allowed to produce a
number when nothing else is. It's set per provider in the Governance → Providers pane (or seeded from
`appsettings.json` on a fresh install; once `model-routing.json` exists, that file owns provider
config, so an existing install must tick the box). Zero and unknown are different answers and the code
keeps them apart: `ModelPrice.Free.EstimateCost(...)` produces the zero, and everything else declines
to guess.

`EstimatedCostUsd` is always a **local estimate** — token counts × a catalog price. Nothing in this
repo queries a provider's own billing API for real, provider-reported spend. What *is* persisted today
is aggregate, not per-request: per-provider monthly spend rows (`provider_spend`, backing the
Governance budget bars — see [`anthropic-reported-usage-plan.md`](anthropic-reported-usage-plan.md)),
the append-only `spend_log.jsonl` (written by `SpendTracker`, read by nothing), and per-provider
rate-limit header snapshots/history. A **per-request** usage ledger still doesn't exist — each
`RoutingTelemetryEvent` is broadcast once and gone when no dashboard is listening. See
[`agent-cost-tracking.md`](agent-cost-tracking.md) for the original ledger/reconciliation design and
[`token-tracking-improvements.md`](token-tracking-improvements.md) §5.2/§5.8 (executed by
[`token-tracking-implementation-plan.md`](token-tracking-implementation-plan.md)) for the adopted,
current version of it.

### Router overhead

`EstimatedCostUsd` is what the **upstream provider** charged. `RouterCostUsd` is what **routing itself**
cost, and the two are deliberately separate fields rather than one summed number.

The research doc charges the router's own consumption against the router: §5.1 defines `TotTok` as
"total input + output token consumption (**router** + model)", and prices locally-served router tokens
at `$0.054/M` from H100 amortization and measured throughput (§B.3.2). That rate is the default of
`Routing:SelfHostedRouterPricePerMillionTokens`; an operator running different hardware overrides it.
It deliberately does **not** come from the price catalog — that catalog holds published *provider*
prices, and locally-served inference has no provider to publish one.

Today the only metered router-side token consumer is `OnnxEmbeddingClient`: the BGE forward pass on the
request path (`docs/router/live-feedback-learning-plan.md` Phase 2b). `LlmRouterVoter` now runs its own
local ONNX GenAI generation whenever it has task text to route on, but that generation's token cost is
not yet metered into `RouterCostUsd` - a further, still-open deferral (distinct from the earlier "no
model artifact at all" gap this paragraph used to describe). When that metering lands, its tokens belong
in the same field, which is why the field is named for the router rather than for embeddings.

Keeping the halves separate is what lets a savings figure be reported **net**: a router that saves
$0.08 by downshifting a turn but burns $0.01 deciding has saved $0.07, and only a consumer that can see
both numbers can say so. Summing them at the source would make that unrecoverable downstream.

> **Not yet wired:** nothing computes a baseline cost, so no savings figure exists to be net *of*. The
> Always-*m* reference point that would supply one is configured by `Routing:AlwaysBaselineModel`
> (research-doc Table 4's "Single-Model (Always-*m*) … Reference performance floor"), which is declared
> but not yet read by any consumer — it is deliberately operator-set rather than auto-derived from the
> priciest configured model. `RouterCostUsd` is recorded now so that the accounting is already correct
> whenever that consumer lands. See [`../gui/backlog.md`](../gui/backlog.md)'s Routing ROI item.

## Transport: gRPC

> This was SignalR until [`grpc-migration.md`](grpc-migration.md) shipped (full clean-swap rewrite,
> not a coexistence period - SignalR is fully gone from this codebase). That doc's `GetModelSpend` RPC
> and `ModelListEvent` stream case were **not** part of what shipped - see its status banner. Everything
> in this section describes what's actually implemented today.

`ProxyServer` adds a second Kestrel listener, on a **separate, dedicated port**
(`ProxyServer.DefaultGrpcPort`, 5002) from the LLM-forwarding proxy's port (5001) - not a second
protocol sharing the same port. `app.UseRouting()` + `app.UseEndpoints(e => e.MapGrpcService<TelemetryGrpcService>())`
is registered via `services.AddGrpc()` on the **inner** Kestrel host's DI container (not the outer
application container `ProxyMiddleware` is constructed in — these are deliberately separate; see the
code comment on `ProxyHostedService` warning about a prior unbounded-recursion bug), same pipeline
shared across both ports. `TelemetryGrpcService.StreamEvents` (the sole RPC on `TelemetryService`,
defined in `src/Protos/telemetry.proto`) is push-only in effect - the client sends an empty
`StreamEventsRequest` and then only ever receives - though unlike a SignalR hub it's technically a
server-streaming RPC the client calls once, not a persistent hub connection with named methods.

**Transport is TLS (HTTPS/2 via ALPN), not unencrypted h2c.** An earlier version of this shipped as
unencrypted HTTP/2 (h2c) on the *same* port as the proxy, per the original design in
[`grpc-migration.md`](grpc-migration.md); that had to be reverted after real-world use found it
unreliable - on at least one managed/corporate Windows machine, every connection attempt failed with
the HTTP/2-level `HTTP_1_1_REQUIRED` error, consistent with something on the network path (VPN
client, endpoint security agent, TLS-inspecting proxy) not understanding or mangling the h2c
connection preface, even on loopback. `Telemetry/TelemetryTlsCertificate.cs` generates and persists a
self-signed certificate (`%LOCALAPPDATA%\TotallyHotArcRouter\telemetry-cert.pfx`, random per-installation
password stored alongside it) bound to the dedicated gRPC port via Kestrel's `UseHttps(certificate)`;
the client trusts it by subject name (`CN=localhost`) rather than a blanket accept-all or a pinned
thumbprint - see [`grpc-migration.md`](grpc-migration.md)'s section 2 for the full story.

**Security note:** authentication is enforced — every call carries the `ManagementAccessToken` as
per-call metadata, checked server-side by `TelemetryAuthInterceptor` and attached client-side by
`TelemetryAuthClientInterceptor`, so a local process without the token cannot connect and receive
broadcast events (including the real request/response text described above). See
[`signalr-hub-security.md`](signalr-hub-security.md) for the design rationale that motivated this,
kept there as historical SignalR-era context even though that doc's code samples predate the
migration to gRPC.

The same stream also carries a second, unrelated event stream for the GUI's Console tab: every
Serilog log event, sent as the `log_line` oneof case by `TelemetryLogEventSink` (a custom
`ILogEventSink` wired into `Program.cs`, see `docs/gui/console-tab-plan.md`; renamed from
`SignalRLogEventSink` when the transport changed). It reuses `TelemetryPublisher`/
`ITelemetryPublisher` (via `PublishLogLineAsync`) rather than a separate publisher, so it shares the
same fault-isolation behavior described below.

`TelemetryBroadcaster` is the fan-out registry every connected `StreamEvents` call registers a
`System.Threading.Channels.Channel`'s writer with, for that call's lifetime; `TelemetryPublisher`
(registered as both `TelemetryPublisher` and `ITelemetryPublisher`, resolving to the same singleton
instance) is a thin wrapper that maps a domain `RoutingTelemetryEvent`/`LogLineEvent` to the wire
`TelemetryEvent` envelope and writes it to every registered channel. Unlike the SignalR-era
`IHubContext<TelemetryHub>` (only available after the inner Kestrel host started, requiring
`ProxyServer.StartAsync` to call `AttachHubContext(...)` post-start), `TelemetryBroadcaster` has no
hosting dependency at all — it's constructed once in the outer container and the same instance is
registered into the inner host's DI container too, so `TelemetryGrpcService` can receive it via plain
constructor injection. No post-start attachment step, and no narrow startup-race window where an
early request's telemetry event is silently dropped: publishing before any client has connected is
still a safe no-op, simply because nothing is registered to write to yet.

The wire message (`TotallyHot.ArcRouter.Telemetry.Contract.RoutingTelemetryEvent`, generated from
`src/Protos/telemetry.proto`) is compiled independently into both `TotallyHotArcRouter`
(`GrpcServices="Server"`) and `TotallyHot.ArcRouter.Gui.Telemetry` (`GrpcServices="Client"`) from the same
file, so the two sides can never structurally drift the way the old hand-synced SignalR DTOs could.
The client-side compile happens in `TotallyHot.ArcRouter.Gui.Telemetry` - a plain, non-MAUI project - rather
than in `TotallyHot.ArcRouter.Gui` itself (which is where the generated types are actually *used*, via a
`ProjectReference`): .NET MAUI's `SingleProject` build doesn't reliably run Grpc.Tools' codegen (no
`protoc` invocation happens at all, confirmed empirically - restore succeeds, the `.proto`'s path
resolves, but nothing is generated), while `TotallyHot.ArcRouter.Gui.Telemetry`'s plain `Microsoft.NET.Sdk`
build has no such problem. See [`grpc-migration.md`](grpc-migration.md)'s section 4 for the full story.

The GUI side still keeps `TotallyHot.ArcRouter.Gui.Telemetry.RoutingTelemetryEventDto`/
`TotallyHot.ArcRouter.Gui.Console.LogLineDto` as separate, hand-written types, though — `TotallyHot.ArcRouter.Gui`'s
`LiveDataStore` maps the generated proto messages into them (handling proto3 `optional` field
presence, the decimal-as-string cost encoding, and `Timestamp`↔`DateTimeOffset` conversion) rather
than passing proto types straight through, specifically so `ConversationAggregator`/`LogBuffer` (the
actual aggregation/buffering logic, still Windows-independent and unit-tested) stay decoupled from
the wire message shape. `TotallyHot.ArcRouter.Gui.Console` still has no `Grpc`/`Google.Protobuf` dependency
of its own; `TotallyHot.ArcRouter.Gui.Telemetry` now does, for the codegen reason above, not because its
aggregation logic needs it.

## GUI consumption

**Architecture principle: the GUI only ever talks to the TotallyHotArcRouter proxy.** `TotallyHot.ArcRouter.Gui`
has no other integration surface, by design - it never calls an upstream provider (OpenAI, Anthropic,
etc.) directly, and never reads proxy-side storage directly (e.g. opening a SQLite file on disk),
even when both processes happen to run on the same machine as the same user and doing so would be
technically possible. Every capability the GUI has goes through the proxy - today that's exclusively
the `TelemetryService.StreamEvents` gRPC stream described below; any future GUI-facing surface (a new
RPC, a new REST endpoint) must be served *by the proxy*, not bypass it. This keeps the proxy as the
single point that holds credentials, talks to providers, and owns persistence, and the GUI as a thin,
credential-free client of it. Proposed features that add new GUI-facing surfaces -
[`agent-cost-tracking.md`](agent-cost-tracking.md) (SQLite ledger + provider reconciliation) and
[`../gui/governance-model-cards.md`](../gui/governance-model-cards.md) (model pricing/spend cards) -
both call this out explicitly at the point they introduce a new surface, precisely because it would be
easy to accidentally design around the proxy instead of through it (e.g. having the GUI open the
ledger's `.db` file directly, since it's "just a file on the same machine").

`TotallyHot.ArcRouter.Gui.Telemetry.ConversationAggregator.Aggregate` (pure, unit-tested) groups a flat
list of `RoutingTelemetryEventDto`s into `LiveConversation`/`LiveConversationTurn` records by
`SessionId`, ordering turns by `TurnNumber` and conversations by most-recently-active first.

`TotallyHot.ArcRouter.Gui`'s `Services/LiveDataStore.cs` owns a `GrpcChannel` to `https://localhost:5002`
(`ProxyServer.DefaultGrpcPort`, the dedicated TLS gRPC port - not the plain-HTTP proxy port 5001;
configurable via `GuiSettingsStore`, editable from `SettingsModal.razor`) and a
`TelemetryService.TelemetryServiceClient` over it, accumulates every received
event, and re-runs `ConversationAggregator.Aggregate` on the full accumulated list after each new
event. It's registered as a singleton in `MauiProgram.cs`, started once from `Dashboard.razor`'s
`OnInitializedAsync`, and connection failures (e.g. the proxy isn't running) are logged and
swallowed — the dashboard just shows no live conversations until a connection succeeds. Unlike
SignalR's `WithAutomaticReconnect()`, `Grpc.Net.Client` has no built-in reconnect policy, so
`LiveDataStore` hand-rolls a simple retry loop (fixed 2-second delay) around the stream call - see
[`grpc-migration.md`](grpc-migration.md)'s "Known gap: no built-in reconnect".

`Services/LiveConversationMapper.cs` then maps `LiveConversation`/`LiveConversationTurn` onto the
dashboard's existing `Models.Conversation`/`ConversationTurn` view-model shape. Several
`ConversationTurn` fields have no live-data source given this telemetry event's scope and are set
to **honest, explicit defaults rather than fabricated values**:

| Field | Default | Why |
|---|---|---|
| `RoutingRoi` | `0` | No "worst case" baseline cost is computed for live requests |
| `ToolExecutionSteps` | `0` | The proxy doesn't introspect tool calls within a turn |
| `ContextBufferPercent` | `0` | No per-model context-window-size configuration exists |

`RequestSummary`/`ResponseSummary` are **not** in this table - they're real, mapped straight through
from `LiveConversationTurn.RequestSummary`/`ResponseSummary` (see "Request/response text extraction"
above), null only when there's genuinely nothing extractable for that turn.

`CacheHitRate` is **also not** in this table anymore: `RoutingTelemetryEvent` now carries
`CacheCreationTokens`/`CacheReadTokens` (parsed the same way as `PromptTokens`/`CompletionTokens`,
see the table above) through the wire contract, `LiveConversationTurn`, and
`RoutingTelemetryEventDto`. `LiveConversationMapper` derives `CacheHitRate` from them via
`CostChartBuilder.CacheHitRate(prompt, cacheCreation, cacheRead)`, dividing by the additive total
(`prompt + cacheCreation + cacheRead`) rather than by `prompt` alone - dividing by the provider's own
`input_tokens` (which excludes cached tokens) could otherwise push the rate over 100%.

`ConversationTurn.TimeToFirstTokenMs` **is** real — it's `LatencyToHeadersMs` from the event. The
Razor components already render these defaults gracefully (e.g. `TurnCard.razor` shows "—" for a
zero ROI or cache rate, `ConversationSummary.razor`'s avg-ROI does the same), so no component
changes were needed to consume live data safely.

`Dashboard.razor` uses `LiveDataStore.Conversations` for the Live Stream tab and the Cost Analytics
tab's `Conversations` parameter (feeding its token-compounding chart); `CostData`, `AgentRoi`,
`TokenBuckets`, `ModelShares`, and `Providers` remain `MockData` — those have no telemetry source at
all yet (no cumulative-savings baseline, no per-agent ROI concept, no token-bucket/model-share
aggregation, no provider-budget tracking). See [`../gui/backlog.md`](../gui/backlog.md) for what
that would take.

## Tests

`src/TotallyHotArcRouter.Tests/Telemetry/` covers every server-side class (session resolution
priority/fallback, message-history continuity matching including staleness eviction, turn-counter
concurrency via 200 parallel calls, both providers' streaming and non-streaming usage and response-text
extraction, request-text extraction from the newest user message, shared content-part extraction,
truncation, SSE event parsing edge cases, pricing math, `TelemetryBroadcaster`'s fan-out/field-mapping/
fault-isolation behavior, `TelemetryGrpcService.StreamEvents`'s register/forward/unregister behavior
(via `Grpc.Core.Testing`'s `TestServerCallContext` and an in-memory stream-writer fake), and
`TelemetryPublisher`'s forwarding to a real `TelemetryBroadcaster`), plus integration-style cases
appended to `ProxyMiddlewareTests.cs` covering the full request → telemetry event path (including
request/response summaries), turn-number persistence across requests, session synthesis, and fault
isolation from the client response. `TotallyHotArcRouter.Gui.Telemetry.Tests/` covers
`ConversationAggregator`'s grouping/ordering/summation, including null-token/cost handling, unsorted
input, and request/response summary pass-through - unaffected by the SignalR→gRPC transport change,
since it still operates on `RoutingTelemetryEventDto`, unchanged in shape (see "Transport: gRPC"
above).

`TotallyHot.ArcRouter.Gui`'s own `Services/LiveDataStore.cs` and `Services/LiveConversationMapper.cs` are
**not** unit-tested: like the rest of `TotallyHot.ArcRouter.Gui` (Razor components, `MauiProgram.cs`,
`TrayWindowManager.cs`), they depend on Windows-only MAUI/Blazor types (or, for `LiveDataStore`,
live `GrpcChannel` networking) and can't be built or tested in this repo's Linux environment. The
logic they wrap is tested where it's actually portable (`ConversationAggregator`, above).

