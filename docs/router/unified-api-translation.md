# Unified API Translation

> **Status: all four providers implemented - Ollama (§4.1), Google Gemini (§4.3), the Anthropic
> retrofit (§4.4), and AWS Bedrock (§4.2).** This document originally recorded the scope, sequencing,
> and API research agreed for this pillar before any code was written; all four sections have since
> been updated to reflect what was actually built (including a real pre-existing bug found and fixed in
> §4.1, the interface additions Gemini forced in §4.3, the request-shape-detection design §4.4 left
> open, and the SDK-based architecture §4.2 landed on after its originally-planned hand-rolled SigV4
> signing turned out to be the wrong call). This was one pillar of an earlier, broader parity
> workstream (unified API translation, local proxy CLI, simple local fallbacks, basic token/cost
> tracking) tracked at the time in an earlier, fuller revision of [`src/PLAN.md`](../../src/PLAN.md);
> that revision's pillar/TODO breakdown is not reproduced in the current, trimmed
> `src/PLAN.md`, which tracks only unfinished work. Unified API Translation itself is fully closed.

## 1. Purpose

This pillar's original goal: *"normalize request/response payloads to the OpenAI format so the same
client code can call Anthropic, AWS Bedrock, or local Ollama interchangeably."* Google Gemini was
added to scope during planning for this document (see §4.3) — it was not part of that original
goal's text but is now part of this plan.

### 1.1 Current state — verified against the code, not assumed

`ProxyMiddleware.InvokeAsync` (`src/TotallyHotArcRouter/Proxy/ProxyMiddleware.cs`) forwards the client's
request body essentially byte-for-byte: `RequestInterceptor.ResolveModelRouteAsync` parses the JSON
body only far enough to read and rewrite the top-level `model` field, then the rest of the body is
forwarded unchanged to whichever provider `ModelRouteResolver` resolves it to. There is **no
request/response payload translation for any provider today** — including the two providers that
already work, OpenAI and Anthropic:

- The **OpenAI** path works because OpenAI's API already matches the shape TotallyHotArcRouter forwards.
- The **Anthropic** path works today only because the real client (Claude Code) already sends
  Anthropic-shaped requests (`x-api-key` auth, `messages` array, Anthropic's own tool-use
  conventions) — the proxy never converts an OpenAI-shaped request into Anthropic's shape or back.
  `OpenAiUsageParser`/`OpenAiResponseTextParser`/`AnthropicUsageParser`/`AnthropicResponseTextParser`
  (`src/TotallyHotArcRouter/Telemetry/`) parse **responses already in each provider's native shape** to
  extract usage/text for telemetry — this is response *parsing* for observability, not request/response
  *translation* for the forwarded call itself. Do not confuse the two when picking this back up.

So "Unified API Translation" is genuinely unbuilt end-to-end: no provider today lets a client send
one OpenAI-shaped request and reach an arbitrary configured backend.

## 2. Scope agreed for this plan

Decided via user clarification during planning (not inferred):

| Item | Decision |
|---|---|
| Providers in scope | Ollama, AWS Bedrock, Google Gemini, **and** retrofitting Anthropic to accept OpenAI-shaped requests |
| Build order | Ollama (own PR) → **Gemini (own PR)** → **Anthropic retrofit (own PR)** → **Bedrock (own PR)**, actual shipped order. *Gemini and Bedrock were swapped from the plan's original order by user decision: Gemini is fully verifiable against a mock harness and exercises the whole translator seam first. Anthropic and Bedrock then swapped again from that revised order in practice — Anthropic shipped before Bedrock, which turned out to need its own architectural pivot mid-implementation (raw HTTP + hand-rolled SigV4 → the AWSSDK.BedrockRuntime client; see §4.2) once AWSSDK.Core's signer proved not to be the clean standalone utility it was assumed to be.* |
| Streaming (SSE) | In scope for every provider, not deferred to a later pass |
| Verification | Local mock/stub HTTP harness with real provider-shaped fixture payloads — either an in-process `HttpMessageHandler` stub (`ProxyMiddlewareTests`' `DelegatingHandlerStub` pattern) or a real-socket `HttpListener` mock upstream (`ProxyInterceptionTests`' pattern) — **not** a live provider or real credentials, mirroring why `litellm-sidecar/`'s Docker pull was never verified end-to-end in this environment |

Each provider gets its own PR, mirroring how Workstream A (Phase 8 sandbox hardening) shipped as
four focused PRs rather than one, and how TODO 4's other pillars (spend tracking, single-model CLI)
each shipped separately.

## 3. Proposed architecture: `IPayloadTranslator`

A new seam, not yet implemented:

```csharp
namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Translates one provider's native request/response payload shape to/from the OpenAI-compatible
/// shape TotallyHotArcRouter's proxy speaks by default. A provider with no registered translator (every
/// provider today) is forwarded unchanged, exactly as ProxyMiddleware already does - this interface
/// only needs to be consulted for providers whose native shape actually differs from OpenAI's.
/// </summary>
public interface IPayloadTranslator
{
    /// <summary>The provider key this translator applies to (matches ModelRouteEntry.Provider).</summary>
    string Provider { get; }

    /// <summary>Rewrites an OpenAI-shaped request body into this provider's native shape.</summary>
    byte[] TranslateRequest(byte[] openAiShapedBody);

    /// <summary>Rewrites this provider's native (non-streaming) response body into OpenAI's shape.</summary>
    byte[] TranslateResponse(byte[] nativeShapedBody);

    /// <summary>
    /// Rewrites one native streaming chunk into an OpenAI-shaped SSE "chat.completion.chunk" data
    /// line, or null if the chunk carries no client-visible content (e.g. a provider-specific
    /// heartbeat/metadata event with nothing to translate).
    /// </summary>
    string? TranslateStreamingChunk(string nativeChunk);
}
```

`ProxyMiddleware` would look up an `IPayloadTranslator` by `route.Provider` (optional dictionary,
empty by default) and, when one exists, run the request body through `TranslateRequest` before
forwarding and the response through `TranslateResponse`/`TranslateStreamingChunk` before returning it
to the client. Providers with no translator registered behave exactly as they do today — this is
additive, not a rewrite of the existing forwarding path.

> **As-built note:** the interface above is the original sketch. Implementing Gemini (§4.3) grew it —
> `TranslateStreamingChunk(string)` became a stateful per-request `IStreamTranslator` from
> `CreateStreamTranslator()`, and a `BuildRequestUri(...)` method was added because Gemini encodes the
> model id and streaming choice in the URL path, not the body. See §4.3 and
> `src/TotallyHotArcRouter/Proxy/Translation/IPayloadTranslator.cs` for the real shape. The dictionary
> lookup + empty-default pass-through described here is exactly what shipped.

## 4. Provider-by-provider plan

### 4.1 Ollama (PR 1 — implemented)

**Verified finding: Ollama needs no `IPayloadTranslator` implementation at all.** Ollama ships its
own OpenAI-compatible endpoint, `POST http://localhost:11434/v1/chat/completions`
([Ollama docs](https://docs.ollama.com/api/openai-compatibility)), which already accepts the same
request shape TotallyHotArcRouter forwards (`model`, `messages[].role/content`), returns the same response
shape (`choices[].message`, `usage.prompt_tokens`/`completion_tokens`), and streams via the same SSE
framing (`data: {...}` chunks terminated by `data: [DONE]`). No API key is required.

Authentication is an ordinary entry in `ProviderOptions.Headers`, so a provider with no such entry
forwards no auth header at all — a no-auth local provider is already a fully-supported configuration
shape today, not a gap.

**What PR 1 did:**
1. Added `ollama` to `ModelRoutingOptions:Providers` (`appsettings.json`), base URL
   `http://localhost:11434/v1`, no auth header configured.
2. Added an example `ModelRouting:ModelList` entry (`llama3`, `Provider: ollama`).
3. Added `IPayloadTranslator` (`src/TotallyHotArcRouter/Proxy/Translation/IPayloadTranslator.cs`) as an
   interface only, not yet consumed by anything — the real seam PR 2/3/4 will implement against.
4. Tests (`src/TotallyHotArcRouter.Tests/Proxy/OllamaProviderTests.cs`): an in-process `HttpMessageHandler`
   stub (matching `ProxyMiddlewareTests`' `DelegatingHandlerStub` pattern) returning Ollama-shaped
   fixture responses — both a non-streaming JSON body and an SSE stream — confirming the *existing*
   pass-through forwarding path round-trips correctly end-to-end with zero `IPayloadTranslator`
   involvement. A regression test proving no translator is needed, not a test of new translation logic.
5. Closed out the "Unified API Translation" gap entry this pillar tracked at the time.

**Implementation notes — a real bug found while wiring this up, not anticipated when this plan was
first written:**

`ProxyMiddleware.InvokeAsync` built the forwarded request's URL as
`new Uri(route.UpstreamBaseUrl, $"{context.Request.Path}{context.Request.QueryString}")`. Since an
ASP.NET Core request path always starts with `/`, the `Uri(Uri, string)` combining constructor treats
it as an RFC 3986 §5.3 absolute-path reference — which **replaces** `UpstreamBaseUrl`'s own path
entirely instead of appending to it. Every provider configured before this PR happens to have a
path-less `BaseUrl` (`https://api.openai.com`, etc.), so this was never exercised — but Ollama's
`BaseUrl` genuinely needs its `/v1` path segment preserved (Ollama's OpenAI-compatible routes only
exist under `/v1`, not at the origin root), which surfaced the bug immediately.

Fixed by building the target URL via string concatenation instead
(`UpstreamBaseUrl.ToString().TrimEnd('/') + requestPath + queryString`), which is provably
byte-identical to the old behavior for every path-less `BaseUrl` (verified against
`ProxyMiddlewareTests`' existing exact-URL assertions, which still pass unchanged) and now correctly
preserves a `BaseUrl` with its own path segment.

**Superseded — concatenation was half the rule, and the other half was a silent failure.** Two
claims above did not survive contact with a real client. They are corrected here rather than edited
away, because the reasoning that produced them is the reasoning that would reintroduce the bug:

- *"Ollama's `BaseUrl` genuinely needs its `/v1` path segment preserved"* holds only for the request
  path this PR's own test sends, `/chat/completions`. An OpenAI-shaped client does not send that —
  it sends `/v1/chat/completions`, version segment already included. Against a real client,
  concatenation forwards `/v1/v1/chat/completions`. Verified against a live LM Studio on
  `127.0.0.1:1234` (configured with the `/v1` base `appsettings.json` ships): that URL returns HTTP
  **200** with body `{"error":"Unexpected endpoint or method. (POST /v1/v1/chat/completions)"}`.
  A 200 means `ProxyMiddleware` books it as a success — the circuit breaker never trips, telemetry
  records a healthy provider, and the client receives an error-shaped body it cannot distinguish
  from a completion. A 404 would at least have failed loudly.
- *"Gemini's real API also lives under its own path prefix (`/v1beta/...`), so this fix is a
  prerequisite for §4.3"* — it is not. `GeminiPayloadTranslator.BuildRequestUri` composes
  `/v1beta/models/{model}:generateContent` from a host-only `BaseUrl`
  (`https://generativelanguage.googleapis.com`), and `ProxyMiddleware` forwards to the
  translator-built URL without consulting the client's path at all. Gemini never reaches the
  passthrough join and depends on nothing in it. Likewise Anthropic (`BuildRequestUri` →
  `/v1/messages`) and Bedrock (invoked through the AWS SDK, with no forwarded URL to build).

What was actually missing is that the two provider styles have opposite `BaseUrl` contracts and
neither was written down: a passthrough provider's path comes from the client, a translated
provider's from its translator. `src/README.md` showed a path-less `BaseUrl` while this section
called Ollama's `/v1` load-bearing, and an operator had no way to reconcile the two.

**Resolved** by extracting the join into `ProviderUrlBuilder.BuildPassthroughUrl`, which keeps every
base segment and then appends only the request segments the base did not already supply.
Concatenation and `Uri(Uri, string)` combining are each lossy in one direction; overlap-collapsing is
a superset of both, so this section's preservation behavior is strengthened rather than weakened — a
gateway prefix like `https://gw.corp/openai` still survives, and `http://localhost:11434/v1` now
forwards a real client's `/v1/chat/completions` to `/v1/chat/completions` instead of doubling it.
Matching is ordinal and anchored at the base/request boundary, so nothing collapses on a case
difference or on a segment that merely recurs deeper in the path. Covered by
`ProviderUrlBuilderTests` — including the gateway-prefix and near-miss cases that a naive "just strip
a trailing `/v1`" normalization would regress — plus an end-to-end exact-URL assertion in
`ProxyMiddlewareTests`. The operator-facing contract now lives in `src/README.md` under "Provider
base URLs".

### 4.2 AWS Bedrock (PR 5 — implemented)

Real translation work across three model families (Anthropic Claude, Amazon Titan, Meta Llama — scoped
via user decision to go beyond this section's original "Anthropic-on-Bedrock only" candidate; see
below), each with its own request/response envelope, plus a genuinely different invocation model from
every other translated provider.

**Architectural pivot from the original plan (a real correction, not a guess that happened to work):**
this section originally flagged AWS SigV4 request signing as "the most security-sensitive piece of this
entire pillar" and planned to hand-roll it, or sign a raw `HttpRequestMessage` via `AWSSDK.Core`. Neither
survived contact with the actual SDK: `AWSSDK.Core`'s `AWS4Signer` turned out to be an internal,
undocumented class (`Amazon.Runtime.Internal.Auth.AWS4Signer`) built to sign the SDK's own
`IRequest`/`AmazonWebServiceRequest` objects, not a clean standalone utility for an arbitrary
`HttpRequestMessage` — using it would have meant reaching into SDK internals and adapting our request
into its own abstractions. Once that was discovered (mid-implementation, via targeted research, not
assumed), the user's revised decision was to use the full, official `AWSSDK.BedrockRuntime` client
(`IAmazonBedrockRuntime`) instead of raw HTTP forwarding for Bedrock specifically. This eliminates the
SigV4 risk entirely (the SDK signs every request itself) and, as a direct consequence, also eliminates
the originally-planned need to hand-roll AWS's binary `application/vnd.amazon.eventstream` framing for
streaming — the SDK decodes that too, handing this codebase discrete, complete native JSON chunks one at
a time. The tradeoff: Bedrock providers don't flow through `ProxyMiddleware`'s raw-`HttpRequestMessage`-
via-`HttpClient` forwarding path the way every other provider does; they get their own SDK-based
invocation path (`ProxyMiddleware.InvokeBedrockAsync`), forked on by a new `IBedrockPayloadTranslator`
marker interface.

**Credential resolution ("env vars? `~/.aws/credentials`? both?", this section's original open
question) — answered for free by the SDK pivot:** `BedrockRuntimeClientFactory` uses an explicit
access-key/secret-key/session-token override when `ProviderOptions.AwsAccessKeyIdEnvVar`/
`AwsSecretAccessKeyEnvVar`/`AwsSessionTokenEnvVar` resolve to values; otherwise it constructs the SDK
client with only a region, letting `IAmazonBedrockRuntime`'s own default AWS credential chain apply
(environment variables, `~/.aws/credentials` profiles, instance/container role, etc.) — both sources are
covered without this codebase needing to implement either credential-resolution mechanism itself.

**Model family scope (user decision, explicitly widened beyond this section's original
"Anthropic-on-Bedrock only" candidate):** Anthropic Claude, Amazon Titan, and Meta Llama — three
translators, `src/TotallyHotArcRouter/Proxy/Bedrock/`:
- **`AnthropicOnBedrockPayloadTranslator`**: Bedrock's Claude `InvokeModel` body is nearly identical to
  native Anthropic's Messages API (verified against AWS's own docs) — no top-level `model` (the SDK's
  `ModelId` parameter carries it instead) and `anthropic_version` is the fixed literal
  `bedrock-2023-05-31` rather than an HTTP header. Reuses `AnthropicPayloadTranslator`'s message/tool
  translation helpers directly (widened from `private` to `internal` specifically to enable this) and
  its `TranslateResponse` verbatim (Bedrock's Claude response envelope is byte-for-byte the same shape),
  rather than reimplementing any of it. Streaming reuses `AnthropicStreamTranslator`'s per-event-type
  handling too: that class was split into SSE-parsing (`TranslateEvent`, unchanged for the direct
  Anthropic path) and a shared `DispatchEvent` a new `TranslateNativeJsonChunk` entry point calls
  directly — Bedrock's Claude streaming events carry the identical JSON shape as native Anthropic's SSE
  events (confirmed via AWS's own Java/JS code samples), just without the `event:`/`data:` framing
  around them, since the SDK has already stripped that.
- **`TitanPayloadTranslator`**: Titan's `InvokeModel` API has no structured multi-turn message concept
  at all — a single `inputText` string and an optional `textGenerationConfig` — so OpenAI's `messages`
  are folded into AWS's own documented `"User: ...\nBot:"` transcript convention. No tool-calling
  support exists in Titan's native Bedrock API (a genuine capability absence, not a translation gap); a
  prior `role: "tool"` result is folded into the transcript as a `Tool:`-labeled line rather than
  silently dropped, documented as best-effort rather than faithful.
- **`LlamaPayloadTranslator`**: Llama's `InvokeModel` API takes a single `prompt` string, built from
  Meta's own documented Llama 3 chat template (`<|begin_of_text|>`/`<|start_header_id|>{role}<|end_header_id|>`/
  `<|eot_id|>`), verified against AWS's and Meta's published prompt-format docs. Same no-tool-calling,
  no-`stop`-parameter absences as Titan (Llama's documented Bedrock request parameters have neither),
  same best-effort `Tool result: ...` folding for a prior tool-result turn.

**Streaming architecture (a genuinely new pattern in this codebase, not `IStreamTranslator`):** unlike
Gemini/Anthropic's raw-SSE-byte streaming (which needs event-boundary buffering because bytes can arrive
mid-event), the AWS SDK already delivers complete, individually-decoded native JSON chunks one at a time
via `IAsyncEnumerable<IEventStreamEvent>` (concrete type `PayloadPart`, `.Bytes` a `MemoryStream`) — so
`IBedrockPayloadTranslator.CreateBedrockStreamChunkTranslator()`/`IBedrockStreamChunkTranslator` is a
simpler, framing-free sibling to `IStreamTranslator`: `TranslateChunk(byte[] completeChunk)` per SDK
item, `Flush()` once at the end. `IPayloadTranslator.BuildRequestUri`/`CreateStreamTranslator` are never
called for a Bedrock translator (they throw `NotSupportedException` if they ever are, since that would
mean `ProxyMiddleware`'s dispatch fork failed) — the SDK computes the endpoint and decodes streaming
itself, so neither member has anything to do for this provider family.

**A real discrepancy found and fixed while writing the streaming test, not assumed correct:** the AWS
SDK's own published .NET/Java/JS code samples for `InvokeModelWithResponseStream` show
`chunk.bytes().asUtf8String()`/`JSON.parse(...event.chunk.bytes)` being fed directly to a JSON parser as
if the frame payload *is* the native chunk. Building a genuine, spec-correct
`application/vnd.amazon.eventstream` binary-frame encoder for the streaming test (prelude + headers +
payload + CRC32s, verified against the Smithy 2.0 Amazon Event Stream Specification) and feeding it
straight to the real, installed `AWSSDK.BedrockRuntime` 4.0.100.5's own `ResponseStream(Stream)`
decoder — not a fake shortcut — immediately surfaced that `PayloadPart.Bytes` came back `null`. Tracing
into `PayloadPartUnmarshaller` (via reflection, not guessing) showed it parses the frame payload as JSON
and reads a base64-encoded `bytes` field to populate `PayloadPart.Bytes` — the actual wire protocol
nests the native chunk one level deeper (`{"bytes": "<base64>"}`) than those code samples' variable
naming suggests. This is a test-fixture-only finding (production code's `part.Bytes.ToArray()` was
always correct once fed real SDK-decoded output; only the test's hand-built binary frames needed the
extra `{"bytes": base64(...)}` wrapper), but it's exactly the kind of "verified against the real
decoder, not assumed from documentation" check this pillar has tried to hold to throughout — recorded
here the way §4.1 recorded its URL-combining bug and §4.3 recorded its interface-shape corrections.

**Tests:** `src/TotallyHotArcRouter.Tests/Proxy/BedrockProviderTests.cs` — non-streaming request/response
translation for all three families driven through the real `ProxyMiddleware` with a mocked
`IAmazonBedrockRuntime` (`Mock<IAmazonBedrockRuntime>`, substituted via a fake
`IBedrockRuntimeClientFactory`, so no live AWS call or real credentials are needed); a genuine
event-stream-encoded streaming test for Claude that exercises the real SDK decoder end-to-end (the
fixture builder described above); direct unit coverage of the Titan/Llama request-building and
streaming-chunk translators; an SDK-exception-to-502-error-envelope test; and a
`BedrockRuntimeClientFactory` region-validation test. `ModelRouteResolverTestFactory.Create` gained an
optional `awsRegion` parameter to support this.

**Verification honesty, unchanged from the original plan's caveat:** no live Bedrock call or real AWS
credentials were used in this environment (same class of limitation as `litellm-sidecar/`'s blocked
Docker pull and Gemini's mock-only verification) — but unlike a pure JSON-shape mock, the streaming path
specifically was verified against the actual installed AWS SDK's real binary-protocol decoder, not just
against this codebase's own assumptions about what that decoder does.

### 4.3 Google Gemini (PR 3 — implemented)

Added to scope during planning (not in this pillar's original wording, above). This is the **first
provider that needed a real `IPayloadTranslator`** — Ollama (§4.1) turned out not to. Google AI Studio
(`generativelanguage.googleapis.com`), not Vertex AI. Field mappings mirror LiteLLM's pinned
`vertex_ai/gemini` transformation (read directly out of the running parity sidecar container, the same
version the parity tests pin), scoped to the surface this pillar needs.

**What PR 3 did:**
1. Added the `gemini` provider to `appsettings.json` (`https://generativelanguage.googleapis.com`,
   `AuthHeaderName: x-goog-api-key` with a `Headers` entry sourcing it from `GEMINI_API_KEY` — the raw
   key in the header, **not** the `?key=` query form, so the secret never lands in a URL), two
   `ModelList` entries (`gemini-2.5-pro`, `gemini-2.5-flash`), and both to
   `RouterConstants.SupportedModels`.
2. `GeminiPayloadTranslator` + `GeminiStreamTranslator`
   (`src/TotallyHotArcRouter/Proxy/Translation/`), registered in DI as an `IPayloadTranslator`; a
   provider-keyed map is injected into `ProxyMiddleware`, which consults it per request and forwards
   every other provider byte-for-byte exactly as before.
3. `UsageExtractor`/`ResponseTextExtractor` map `"gemini"` to the OpenAI parsers, since the response is
   already translated to OpenAI's shape by the time telemetry captures it.
4. Tests: `src/TotallyHotArcRouter.Tests/Proxy/GeminiProviderTests.cs` — mock-harness (`DelegatingHandlerStub`)
   with real-Gemini-shaped fixtures: non-streaming text + usage, tool-calling, SSE streaming, and the
   embedded-error termination case.

**Interface additions this PR forced (deviation from §3's original sketch, recorded here the way §4.1
recorded its URL bug):** the sketched `IPayloadTranslator` was pure body-in/body-out. Gemini needed
two more things, so the interface grew:
- `BuildRequestUri(baseUrl, providerModelId, isStreaming)` — Gemini puts the model id **and** the
  streaming choice in the URL path (`POST …/v1beta/models/{model}:generateContent`, or
  `:streamGenerateContent?alt=sse`), not the body. `ProxyMiddleware` now forwards to the
  translator-built URL for translated providers instead of appending the client's request path, and
  detects streaming from the request body's `stream` field (it previously learned streaming only from
  the *response* content-type).
- `CreateStreamTranslator()` → a per-request, stateful `IStreamTranslator` (`Push`/`Flush`). Streaming
  needs state: tool-call index continuity, and buffered accumulation of fragmented SSE events. The
  original single-method `TranslateStreamingChunk(string)` couldn't hold that safely on a shared
  singleton.

**Streaming-error semantics — "mimic LiteLLM" (the user's explicit call):** verified against LiteLLM's
`ModelResponseIterator`, not guessed. An embedded provider error in a chunk (e.g. a 429
`RESOURCE_EXHAUSTED` delivered as an HTTP 200 SSE body with an `error` field) **terminates** the
stream; a fragmented event stays buffered until complete rather than being dropped or forwarded raw
(the translator's byte buffer split on the `\n\n` event delimiter *is* that accumulation); a
finish-only chunk still emits so `finish_reason` is never lost. One honest proxy-vs-library difference:
because TotallyHotArcRouter has already committed a `200 OK` and earlier chunks to the wire before a
mid-stream error appears, it can't retroactively change the status — it truncates the stream (no
`[DONE]`, after emitting the valid prefix) rather than surfacing a 429, and logs it.

**Field mappings (in scope):** system messages → `system_instruction`; `messages` →
`contents[].role/parts` (`user`/`system` → `user`, `assistant` → `model`, consecutive same-role turns
merged as Gemini requires, `tool`/`function` → `functionResponse`, assistant `tool_calls` →
`functionCall`); OpenAI `tools[].function` → a single `{functionDeclarations: [...]}` tool, with
JSON-Schema-only keywords (`additionalProperties`, `strict`, `$schema`) stripped from `parameters`
(mirroring LiteLLM's `_build_vertex_schema` — note the schema is passed through, **not** type-uppercased;
modern Gemini accepts lowercase JSON-Schema types, and the pinned LiteLLM reference does not uppercase
them, so this doc's original "must be uppercased" claim was wrong); generation params (`temperature`,
`top_p`→`topP`, `top_k`→`topK`, `max_tokens`/`max_completion_tokens`→`maxOutputTokens`,
`stop`→`stopSequences`, `n`→`candidateCount`, `response_format`→`responseMimeType`/`responseSchema`) →
`generationConfig`; `tool_choice`→`toolConfig.functionCallingConfig.mode`. Response: `candidates[]` →
`choices[]` (`parts` text → `message.content`, `functionCall` → `tool_calls`, finishReason mapped
`STOP`→`stop`/`MAX_TOKENS`→`length`/`SAFETY`|`RECITATION`→`content_filter`, `tool_calls` when a
functionCall is present); `usageMetadata` → `usage`.

**Deliberately out of scope (documented, not silently dropped):** image/audio/file content blocks,
reasoning/thinking blocks and thought signatures, context caching, safety settings, response schema
beyond a plain JSON mime type, and Gemini's built-in tools (googleSearch, codeExecution, etc.). A
request carrying those still translates — the unsupported parts are ignored rather than erroring — but
faithful translation of them is future work, scoped honestly rather than over-claimed.

**Verification:** mock harness only, per §2 — no live Gemini call or real `GEMINI_API_KEY` in this
environment (same class of limitation as `litellm-sidecar/`'s and Bedrock's). The fixtures are real
Gemini response shapes traced from LiteLLM's transformation and Google's `generateContent` docs.

### 4.4 Anthropic retrofit (PR 4 — highest risk, implemented)

Explicitly confirmed in scope by the user despite being flagged as the highest-risk item: this is
the **one path real Claude Code production traffic depends on today**. Today's Anthropic support
works precisely because nothing touches the request/response shape — Claude Code already sends
Anthropic-native requests and the proxy forwards them unchanged.

**Detection strategy (resolved via user clarification before implementation, not guessed):**
request path, not a body-shape heuristic and not an explicit client mode. A request to Anthropic's own
`POST /v1/messages` is treated as already-native and passes through byte-for-byte, exactly as before
this translator existed; a request routed to an anthropic-backed model on any other path (e.g.
`/v1/chat/completions`) is treated as OpenAI-shaped and translated. This is the lowest-risk option
because it changes zero bytes of behavior for the one path real Claude Code traffic depends on — no
body sniffing, no new client/config requirement. `IPayloadTranslator` grew a new member for this,
`ShouldTranslate(HttpRequest)` (default `true`, so Gemini/Ollama/future translators need no change);
`AnthropicPayloadTranslator.ShouldTranslate` returns `false` for `/v1/messages` (case-insensitive,
trailing slash tolerated). `ProxyMiddleware` nulls out the resolved translator for the request when
`ShouldTranslate` vetoes it, before any URL-building or streaming-detection happens, so the rest of the
method's existing pass-through path runs completely unchanged.

**What PR 4 did:**
1. `AnthropicPayloadTranslator` + `AnthropicStreamTranslator`
   (`src/TotallyHotArcRouter/Proxy/Translation/`), registered in DI alongside Gemini's, sharing the same
   provider-keyed map `ProxyMiddleware` already consults.
2. **System prompt extraction**: `role: "system"` messages are pulled out of `messages` and
   concatenated (`\n\n`-joined, for multiple system messages) into Anthropic's top-level `system`
   string field.
3. **Role-alternation enforcement**: consecutive same-role turns are merged into one Anthropic message
   (mirrors `GeminiPayloadTranslator`'s `AppendMergedContent` pattern exactly), covering the
   tool-calling-loop case Anthropic would otherwise reject with a 400.
4. **Tool call/response translation**: OpenAI `tool_calls` → Anthropic `tool_use` content blocks
   (assistant turn); OpenAI `role: "tool"` → Anthropic `tool_result` content blocks carried on a `user`
   turn referencing the same id (`tool_call_id` ↔ `tool_use_id`), preserving the id end-to-end so a
   later turn round-trips correctly. `tools[].function` → Anthropic's `{name, description,
   input_schema}` (no JSON-Schema-keyword stripping needed, unlike Gemini — Anthropic's `input_schema`
   is plain JSON Schema). `tool_choice` → Anthropic's `{type: auto|any|none|tool, name}`.
5. **Extended thinking round-trip** (the scope decision made explicitly when Gemini's scope-parity
   question was asked for this PR): Anthropic's `thinking`/`redacted_thinking` content blocks map to
   OpenAI's de facto `reasoning_content` (plain string) + `thinking_blocks` (raw blocks, signature
   included) fields — LiteLLM's own standardized convention for this, confirmed via targeted research
   (not guessed) against LiteLLM's `docs/reasoning_content` page and its Anthropic
   thinking-block-support PRs. `thinking_blocks` must be resent verbatim (including the opaque
   `signature`) on a later turn for Anthropic to accept it back; when present, they're placed first in
   the assistant turn's content blocks, as Anthropic requires. A client that kept only the plain
   `reasoning_content` text (no `thinking_blocks`) gets a best-effort reconstructed `thinking` block
   without a signature — not a substitute for a verifiable one, but keeps the text from being silently
   dropped. A client opting into extended thinking sends Anthropic's own already-shaped `thinking:
   {type, budget_tokens}` object as a pass-through extension field (mirroring LiteLLM's `**kwargs`
   passthrough), forwarded verbatim since OpenAI has no standard equivalent field.
6. **`max_tokens`**: Anthropic requires it on every request, unlike OpenAI's optional
   `max_tokens`/`max_completion_tokens`; when absent from the incoming request, a documented default
   floor (4096) is used rather than guessing at intent or leaving it unset (which Anthropic rejects).
7. **Telemetry keying fix (a design gap this PR's dual-mode nature exposed, not present before it)**:
   `UsageExtractor`/`ResponseTextExtractor` previously assumed one native shape per provider string
   (`route.Provider`), which broke once `"anthropic"` could be *either* translated (OpenAI-shaped by
   the time telemetry sees it) *or* passed through (still Anthropic-native) on a per-request basis.
   `ProxyMiddleware.InvokeAsync` now computes a `telemetryShapeProvider` per request — `"openai"` when a
   translator actually ran (Gemini always; Anthropic only when `ShouldTranslate` allowed it), the real
   `route.Provider` otherwise — and passes that (not `route.Provider`) to both extractors, while the
   `RoutingTelemetryEvent.Provider` field itself still reports the real provider. No change was needed
   inside the extractors' own provider-name switch statements: the existing `"openai"` case already
   parses OpenAI-shaped bytes correctly regardless of which provider originally produced them.
8. Tests: `src/TotallyHotArcRouter.Tests/Proxy/AnthropicProviderTests.cs` — a native-`/v1/messages`
   passthrough regression test (the highest-risk assertion in this PR: today's Claude Code traffic
   shape must be provably untouched), OpenAI-shaped non-streaming translation (system extraction, role
   merge, usage telemetry), tool-calling round trip (both directions, id preservation),
   extended-thinking round trip (both directions), SSE streaming (text deltas, tool-call argument
   accumulation across `input_json_delta` fragments, usage/finish_reason on the terminal chunk), and
   the embedded-error stream-termination case (mirrors Gemini's, using Anthropic's own `error` event
   type).

**Verification:** mock harness only, per §2 — no live Anthropic call in this environment (same class of
limitation as Gemini's and Bedrock's). The fixtures are real Anthropic Messages API response/event
shapes traced from Anthropic's own API docs and LiteLLM's Anthropic transformation.

**Deliberately out of scope (documented, not silently dropped):** image/document content blocks,
prompt caching (`cache_control`), and Anthropic's built-in tools (web search, code execution, computer
use, etc.). A request carrying those still translates — the unsupported parts are ignored rather than
erroring — but faithful translation of them is future work.

### 4.5 Tool-call echo guard for OpenAI-shaped local passthrough providers (implemented)

> **Replaced in code by [`tool-call-normalization.md`](tool-call-normalization.md) Phase 4.** The
> incident and root-cause investigation recorded below remain the authoritative account, and the
> design points still describe how normalization works — but the classes named here no longer exist.
> `ToolCallEchoGuardTranslator`/`ToolCallEchoGuardStreamTranslator` are now
> `ToolCallNormalizingTranslator`/`ToolCallNormalizingStreamTranslator` under
> `Proxy/Translation/ToolCalling/`, `ToolCallEchoScanner` is `JsonObjectScanner`, and the test suite
> named at the end of this section is now
> `Proxy/Translation/ToolCalling/ToolCallNormalizingTranslatorTests.cs`, which carries every one of
> its cases forward as the regression contract for this incident.
>
> The fix described here was narrower than the problem in two ways, both corrected by Phase 4: it
> handled only the Qwen/Hermes `<tools>`/`<tool_call>` dialect with a `name`+`arguments` payload, and
> it was armed by a **provider-wide** flag even though this section's own analysis concludes the cause
> is *"model quality, not one specific provider."* One LM Studio process serves many GGUFs with
> different chat templates, so provider-scoped arming both missed models needing another dialect and
> exposed capable models to false positives. Arming is now per (provider, model) and per request.
> `ProviderOptions.EnableToolCallGuard` survives one release as a forced-on override and is removed in
> Phase 6.

Not part of the original four-pillar scope above (that was closed 2026-07-23) — this is a small,
separately-motivated addition prompted by a real production incident: VS Code Copilot
Chat, routed through a local LM Studio-served `qwen2.5.1-coder-7b-instruct`, showed "Sorry, no response
was returned" whenever the request carried `tools`/`tool_choice`.

**Root cause, confirmed live, not guessed:** curling LM Studio's own `:1234` endpoint directly (bypassing
this proxy entirely) reproduced the identical malformed output, proving TotallyHotArcRouter was forwarding
correctly. LM Studio's Qwen2.5 chat template — inspected directly (Load → Advanced Load Params → Chat
Template) and confirmed to be the standard, correct one — wraps the tool schema documentation in the
system prompt with `<tools>...</tools>` and instructs the model to reply with `<tool_call>{"name": ...,
"arguments": ...}</tool_call>`. This particular 7B model sometimes blends the two and echoes the tag
back as literal assistant `content` instead of a real OpenAI `tool_calls` delta/field — a model
capability limitation, not a config problem or something any chat-template fix resolves. Since the
underlying cause is model quality, not one specific provider, and llama.cpp-backed servers generally
(Ollama included) could in principle serve an equally weak model, the fix targets the *symptom* — the
proxy is the one universal place able to intercept it regardless of which local server or model produces
it.

**Design — opt-in flag, not a new provider-name binding.** Unlike every translator in §4.1–4.4, this one
is not selected by a fixed `Provider` key:
- `ProviderOptions.EnableToolCallGuard` (bool, default `false`) — settable on *any* provider with no
  registered `IPayloadTranslator` (an `lmstudio` entry, the existing `ollama` entry, or a future local
  OpenAI-compatible server), since the misbehavior isn't tied to one provider name.
- `ToolCallEchoGuardTranslator` (`src/TotallyHotArcRouter/Proxy/Translation/`) implements `IPayloadTranslator`
  but is deliberately **not** registered in the provider-keyed `IReadOnlyDictionary<string,
  IPayloadTranslator>` every other translator joins — `ProxyMiddleware` selects it directly, from
  `ResolvedModelRoute.EnableToolCallGuard`, when no other translator is already registered for the route.
  Its `Provider` property is a placeholder, never consulted for routing.
- Its request side (`BuildRequestUri`/`TranslateRequest`) is pure identity — LM Studio/Ollama-shaped
  requests are already OpenAI's own shape, so only the *response* needs scanning. Because
  `BuildRequestUri` has no access to the client's original request path (unlike this translator's own
  no-translator pass-through logic, which preserves it via string concatenation — see §4.1's URL-combining
  bug), `ProxyMiddleware` treats this translator as "no translator" for request-forwarding purposes only,
  while still routing the *response* through it (streaming/buffered dispatch, `Content-Length`/
  `Content-Encoding` stripping, etc., all apply exactly as they do for a real translator).
- `TranslateResponse` (non-streaming) and `ToolCallEchoGuardStreamTranslator` (streaming, per-request,
  reusing the same `\n\n`-delimited SSE event framing every existing stream translator independently
  implements) both scan `content` for a `<tools>`/`<tool_call>` tag pair and, on finding one, extract any
  JSON object shaped like a real call (a string `name` plus an `arguments` value) via the shared
  `ToolCallEchoScanner` helper, rewriting it into a proper `tool_calls` delta/field and
  `finish_reason: "tool_calls"`. The streaming side additionally has to buffer across many separate SSE
  events, since a slow local model streams one token per event and a tag can be split arbitrarily across
  chunk boundaries — this cross-chunk accumulation (distinct from the SSE-event framing itself) has no
  precedent among the existing translators.
- **Fail open, always** (a deliberate difference from Gemini/Anthropic's embedded-error-terminates-the-
  stream behavior): a tag whose body doesn't parse as a real call (invalid JSON, or the tag's own
  *schema*-documentation shape echoed instead of a call) is forwarded as ordinary plain text, unchanged,
  with a logged warning — this is a heuristic best-effort rewrite, not a real upstream protocol error, so
  it must never drop or truncate a response the way a genuine `GeminiStreamException` legitimately does.

**Tests:** `src/TotallyHotArcRouter.Tests/Proxy/Translation/ToolCallEchoGuardTranslatorTests.cs` — direct
`Push`/`Flush` coverage of both tag variants, the exact token-by-token fragment sequence captured live
from the real LM Studio repro, content correctly preserved before/after a tag, multiple sequential tool
calls, the fail-open path (malformed JSON and the schema-echo shape) with a verified logged warning, an
unterminated-tag-at-stream-end case, the non-streaming `TranslateResponse` equivalent, and one end-to-end
test through the real `ProxyMiddleware` proving the opt-in flag actually selects this translator.
`ModelRouteResolverTests` covers `EnableToolCallGuard` propagating from `ProviderOptions` onto
`ResolvedModelRoute` (mirroring the existing `IsFree` propagation test).

## 5. Open questions to resolve before each PR starts

- **Bedrock** *(resolved, PR 5)*: model families widened to Anthropic Claude + Amazon Titan + Meta Llama
  (user's call, beyond the original "Anthropic-on-Bedrock only" candidate). Credentials come from either
  source — an explicit per-provider override when configured, else the AWS SDK's own default credential
  chain (which itself covers both env vars and `~/.aws/credentials`) — see §4.2.
- **Gemini** *(resolved, PR 3)*: added `gemini-2.5-pro` and `gemini-2.5-flash`, and — unlike the Ollama
  precedent — they **were** added to `RouterConstants.SupportedModels` (user's call, so Gemini can
  compete in autonomous routing, not just be reachable as a pass-through target).
- **Anthropic retrofit** *(resolved, PR 4)*: request-path detection (`/v1/messages` = native, anything
  else = translate) — see §4.4.
- **Streaming translators** *(resolved for Gemini, PR 3)*: the error-handling semantics were settled by
  mirroring LiteLLM (the user's explicit "mimic LiteLLM" call) — embedded error terminates, fragmented
  chunk accumulates, finish-only chunk still emits (see §4.3). Future providers' stream translators
  should follow the same reference rather than re-deciding.

## 6. Non-goals

This pillar does not add virtual keys, admin UI, per-team budgets, SSO, Redis caching, or audit logs
— TotallyHotArcRouter remains a single-developer tool, not a multi-tenant platform. Adding
Gemini support is a scope addition agreed during planning, not a signal that this pillar is
expanding toward general-purpose multi-tenant LLM-gateway territory.

