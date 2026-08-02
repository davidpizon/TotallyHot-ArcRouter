# Tracked TODOs

Working list of open items tracked in-session (not yet promoted to their own dedicated backlog
entries, GitHub issues, or PRs). Complements [`backlog.md`](backlog.md), which records **known
defects** discovered while reading the code; this file also covers forward-looking research and
hardening work. One of the five items below lives in a different repository entirely
(`spark-vscode-extension`) — recorded here anyway because it surfaced from the same investigation
and this is the nearer of the two repos' doc folders to where the work is being tracked.

| # | Status | Repo | Title |
|---|---|---|---|
| [1](#1-make-converttoolstoopenai-degrade-instead-of-throwing-on-toolmoderequired-with-multiple-tools) | Open | `spark-vscode-extension` | Make `convertToolsToOpenAI` degrade instead of throwing on `ToolMode.Required` with multiple tools |
| [2](#2-write-tool-call-normalizationmd-design-doc) | ✅ Done | ArcRouter | Write `tool-call-normalization.md` design doc |
| [3](#3-research-deepseek-tool-call-delimiters-and-register-a-deepseek-dialect) | Open | ArcRouter | Research DeepSeek tool-call delimiters and register a `deepseek` dialect |
| [4](#4-add-test-coverage-for-zero-coverage-classes-in-TotallyHotArcRouter-and-TotallyHotArcRoutersandbox) | Open | ArcRouter | Add test coverage for zero-coverage classes in `TotallyHotArcRouter` and `TotallyHotArcRouter.Sandbox` |
| [5](#5-get-a-human-review-of-phase-5s-three-design-decisions) | Open | ArcRouter | Get a human review of Phase 5's three design decisions |

---

## #1 Make `convertToolsToOpenAI` degrade instead of throwing on `ToolMode.Required` with multiple tools

**Repo:** `spark-vscode-extension` (a separate repository — file paths below are relative to *its*
root, not this one) · **Status:** Open

### Problem

`convertToolsToOpenAI` in `src/utils.ts:74-81` throws when VS Code passes `toolMode ===
LanguageModelChatToolMode.Required` together with more than one tool:

```ts
let tool_choice: "auto" | { type: "function"; function: { name: string } } = "auto";
if (options?.toolMode === vscode.LanguageModelChatToolMode.Required) {
    if (tools.length !== 1) {
        console.error("[OAI Compatible Model Provider] ToolMode.Required but multiple tools:", tools.length);
        throw new Error("LanguageModelChatToolMode.Required is not supported with more than one tool");
    }
    tool_choice = { type: "function", function: { name: tools[0].name } };
}
```

The throw propagates out of `buildRequestBody` and aborts the whole chat request before a single
byte is sent upstream. The user sees a failed request, not a degraded one.

### Why this matters

The restriction is an artifact of an older, narrower reading of the OpenAI spec. Modern
OpenAI-compatible endpoints support `tool_choice: "required"` — "call *some* tool, you pick which" —
which is exactly the semantics `LanguageModelChatToolMode.Required` asks for with N tools. There is
no need to fail.

Blast radius is wide because this is on the shared path: `convertToolsToOpenAI` is called by all four
backends —
- `src/openai/openaiApi.ts:229`
- `src/ollama/ollamaApi.ts:138`
- `src/anthropic/anthropicApi.ts:264`
- `src/gemini/geminiApi.ts:706` (via `openaiToolChoiceToGeminiToolConfig`)
- `src/openai/openaiResponsesApi.ts:268` (via `convertToolsToOpenAIResponses`, `src/utils.ts:103`)

So one Copilot request with `Required` + 2 tools hard-fails on every provider the extension supports.

### Proposed fix

1. Widen the `tool_choice` union in both `convertToolsToOpenAI` (`src/utils.ts:49-51`) and
   `convertToolsToOpenAIResponses` (`src/utils.ts:99-101`) to include the literal `"required"`.
2. Replace the throw: emit `tool_choice: { type: "function", function: { name } }` when exactly one
   tool is present (preserves today's behavior), and `tool_choice: "required"` when more than one is.
3. Map the new value per provider:
   - **OpenAI / Ollama** — passes through unchanged; `"required"` is native.
   - **OpenAI Responses** — `convertToolsToOpenAIResponses` (`src/utils.ts:122-127`) currently
     handles only `"auto"` and the function object. Add a `"required"` branch.
   - **Anthropic** — `src/anthropic/anthropicApi.ts:282-287` maps `"auto"` → `{ type: "auto" }` and
     the function object → `{ type: "tool", name }`. Add `"required"` → `{ type: "any" }`, which is
     Anthropic's exact equivalent.
   - **Gemini** — `openaiToolChoiceToGeminiToolConfig` should map `"required"` →
     `functionCallingConfig.mode: "ANY"` with no `allowedFunctionNames` restriction.
4. Drop the `console.error` — the codebase logs through `src/logger.ts`, not `console`.

### Tests

No existing coverage for this branch. Add cases to `src/test/utils.test.ts`:
- `Required` + 1 tool → `{ type: "function", function: { name } }` (regression guard on current
  behavior)
- `Required` + 3 tools → `"required"`, and **does not throw** (this is the bug)
- `Required` + 0 tools → still returns `{}` early at `src/utils.ts:54`
- Per-provider mapping assertions for the Anthropic `any` and Gemini `ANY` translations

### Notes

Found while diagnosing a separate issue (a model emitting tool-call JSON as prose instead of a native
`tool_calls` delta — the same investigation that produced [`tool-call-normalization.md`](tool-call-normalization.md)
below). Unrelated root cause — this one is latent and has probably never fired, since Copilot rarely
requests `Required` mode with multiple tools. Worth fixing before it does.

---

## #2 Write `tool-call-normalization.md` design doc

**Repo:** ArcRouter · **Status:** ✅ Done

Deliverable: [`tool-call-normalization.md`](tool-call-normalization.md). Per-model tool-call dialect
detection and normalization, so VS Code respects a model's intent to invoke a tool regardless of
which provider and model served it.

Also delivered alongside it:
- Cross-link from `docs/README.md`'s router table.
- A supersession banner on `unified-api-translation.md` §4.5 (kept as the authoritative incident
  record for the original LM Studio/Qwen repro; the new doc explains why that section's fix is
  narrower than the problem it targets).
- [`backlog.md`](backlog.md)'s "Related" note updated now that the provider-vs-model granularity
  question is settled by the new design.

Shipped docs-only as [PR #85](https://github.com/davidpizon/ArcRouter/pull/85).

Phase 0 of the plan (the `ToolCallDialect` model, registry, and matcher — pure code, nothing wired
into `ProxyMiddleware` yet) has since been implemented on branch `feat/tool-call-dialect-registry`.
Items #3 and #4 below are follow-ons from that same workstream.

---

## #3 Research DeepSeek tool-call delimiters and register a `deepseek` dialect

**Repo:** ArcRouter · **Status:** Open

### Why this is open

Phase 0 of the tool-call normalization workstream registered five dialects in
`ToolCallDialectRegistry` (`src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallDialectRegistry.cs`)
— `openai-native`, `hermes`, `mistral`, `llama3-json`, `emulated` — and **deliberately omitted
DeepSeek**.

The reason is recorded in a comment in that file, just below the `ScannableOpenerFirstChars`
property: DeepSeek's tool-call template uses special delimiter tokens built from non-ASCII
full-width characters, and registering a guessed spelling would be worse than registering nothing.
A near-miss delimiter silently never matches, which is indistinguishable from the exact bug this
whole workstream exists to fix — so a wrong entry would look like working code while doing nothing.

### What to do

1. **Get the real tokens from a live model, not from memory or a blog post.** The delimiters
   reportedly use full-width/box-drawing-style characters (something in the shape of
   `<|tool calls begin|>` but with non-ASCII separators), and the exact codepoints matter — a
   visually identical lookalike character will not match. Authoritative sources, in order of
   preference:
   - `POST /api/show` against an Ollama-served DeepSeek model, which returns the literal Go chat
     template. This is the same endpoint Phase 3 of the tool-call normalization plan uses for
     tier-1 detection, so this task and Phase 3 share infrastructure.
   - The model's `tokenizer_config.json` / `chat_template` field on Hugging Face.
   - DeepSeek's official API docs for the function-calling format.
2. **Determine the payload shape**: does it key arguments as `arguments` or `parameters`? Is the
   payload one JSON object per block, or an array? Is there a per-call inner delimiter distinct from
   the outer begin/end pair? DeepSeek reportedly nests (an outer calls-begin/end wrapping per-call
   begin/end), which the current flat `DialectDelimiter(Open, Close)` model may not express — **if
   so, that is a finding that changes the abstraction, not just a missing table row.** Report it
   rather than forcing a fit.
3. **Verify the codepoints survive round-tripping** through the source file. The repo's files are
   UTF-8; confirm the literal compiles and matches against a captured real response, not just
   against a hand-typed test string that may have been normalized by an editor.
4. **Register the dialect** in `ToolCallDialectRegistry` alongside the others, add it to
   `ScannableDialects` in the correct tie-break position, and remove the omission comment.
5. **Add coverage** in `src/TotallyHotArcRouter.Tests/Proxy/Translation/ToolCalling/DialectMatcherTests.cs`
   mirroring the existing per-dialect cases: happy path, multiple sequential calls, argument-shape
   handling, and a `MatchAny` attribution case in the existing `[Theory]`. Add `"deepseek"` to the
   persisted-name stability `[Theory]` in `ToolCallDialectRegistryTests.cs`.

### Acceptance

A captured real DeepSeek tool-call response — not a synthetic string — is parsed by
`DialectMatcher.MatchAny` into a correct `ExtractedToolCall` and attributed to the `deepseek`
dialect. Full suite stays green with zero build warnings.

### Notes

Not urgent and not a blocker. Phase 4's observation path classifies unknown models from their first
live tools-carrying request, and Phase 5's emulation fallback covers anything still unmatched — so a
DeepSeek model is functional without this, just not on the fast path. The value here is skipping
straight to native recognition instead of relying on the fallback.

If a real DeepSeek model isn't reachable to capture from, the honest outcome is to leave it
unregistered and say so, rather than register a guess. That was the original decision and it stands
until real evidence replaces it.

### Attempt 2026-07-31 — `deepseek-r1-distill-qwen-7b` via LM Studio: does not settle it

A DeepSeek-badged model was reachable and was probed against all five
`ToolCallEmulationScenarios`, natively and through the emulation rewriter, buffered and streamed.
**No delimiter tokens were observed, and the recordings cannot be read as evidence that DeepSeek has
none.** The dialect stays unregistered and this item stays open.

Why the probe cannot answer the question:

- **The model was never shown the tools.** LM Studio accepted the `tools` array, returned HTTP 200,
  and never rendered it — `prompt_tokens` was 10/13/14/17/16 across the five scenarios, which is
  token for token what the same five questions cost with no `tools` key at all (measured as a
  control), while the same schemas rendered into the system prompt by emulation cost 138–245. A
  model that is not told a tool exists cannot demonstrate the framing it would use to call one.
  Recorded in `RecordedNativeToolCallProbes`.
- **It is a distill, and the arch confirms it.** LM Studio reports `"arch": "qwen2"` for this model.
  It is Qwen-2.5-7B fine-tuned on R1 reasoning traces, so its chat template is not DeepSeek-V3's, and
  the tool-call template that carries the delimiter tokens is precisely what a distill does not
  inherit. Even a positive result here would have been weak evidence about V3.
- **Under emulation it invented four different framings** — `<function-call>`, a fenced block keying
  the function as `"function"`, a fenced ```xml `<function name=… arguments={…} />` element, and bare
  fenced JSON — and none of them resembles a DeepSeek delimiter. See
  `RecordedModelTranscripts.DeepSeekR1DistillQwen7B` and the second block of
  `ToolCallEmulationCaptureTests`.

What would settle it: a **tool-capable** DeepSeek — V3, or an R1 build whose GGUF carries a
tool-calling template — served by something that actually renders `tools` into the prompt. Confirm
rendering first by comparing `prompt_tokens` with and without the `tools` array; if the two match,
the probe is measuring the server, not the model, and no reply it produces is worth recording as
dialect evidence. That check is now the first step of this task rather than an afterthought, and it
is the one thing this attempt did establish.

---

## #4 Add test coverage for zero-coverage classes in `TotallyHotArcRouter` and `TotallyHotArcRouter.Sandbox`

**Repo:** ArcRouter · **Status:** Open

### Why this is open

Verified locally that both non-GUI production assemblies currently clear AGENTS.md's 80%
line-coverage bar as CI actually checks it (per-assembly via `reportgenerator` merging
`TotallyHotArcRouter.Tests` + `TotallyHotArcRouter.Sandbox.Tests` cobertura reports, matching
`.github/workflows/dotnet-ci.yml`'s "Check coverage threshold" step):

- `TotallyHotArcRouter`: 84%
- `TotallyHotArcRouter.Sandbox`: 80.1%

Both pass today. But `TotallyHotArcRouter.Sandbox` is only 0.1 points above the line, and both assemblies
carry classes sitting at exactly **0%** covered — none of which individually fail the per-assembly
gate yet, but which are the reason the margin is thin rather than comfortable. A modest amount of new
untested code, or a refactor that shifts lines between classes, could tip `TotallyHotArcRouter.Sandbox`
below 80% without anyone touching these classes directly.

### Classes at 0% coverage

**`TotallyHotArcRouter`:**
- `TotallyHotArcRouter.Hosting.PriceCatalogIngestionHostedService`
- `TotallyHotArcRouter.Hosting.StartupHealthCheckHostedService`
- `TotallyHotArcRouter.Mcp.McpHostedService`
- `TotallyHotArcRouter.Mcp.McpServer`
- `TotallyHotArcRouter.Proxy.EnvironmentVariableProvider`
- `TotallyHotArcRouter.PriceCatalog.PriceSourceAdminGrpcService`
- `TotallyHotArcRouter.Telemetry.ITelemetryPublisher` (likely just an interface with a default member —
  verify before writing tests; may not need any)
- `TotallyHotArcRouter.Tools.RunVisibleTests`

**`TotallyHotArcRouter.Sandbox`:**
- `TotallyHotArcRouter.Sandbox.Firecracker.FirecrackerMicroVmLauncher`
- `TotallyHotArcRouter.Sandbox.Tier1.LinuxJailLauncher`

### What to do

1. For each class, determine **why** it's untested — some may be thin `IHostedService` wrappers
   where a real unit test needs little more than a lifecycle smoke test (start/stop, verify it calls
   through to its dependency); others (`LinuxJailLauncher`, `FirecrackerMicroVmLauncher`) exercise
   real OS-level process/VM launch paths that may need the same kind of environment-gated integration
   test `LinuxJailLauncherTests` already uses elsewhere in the suite (see the CI workflow's "Allow
   unprivileged user namespaces" step, which exists specifically to let a Tier-1 jail-launch test run
   on the Linux runner) — check whether a test file already exists for the launcher and is just
   skipped/gated rather than missing outright before assuming there's nothing there.
2. `ITelemetryPublisher` should be checked first — if it's a pure interface with no
   default-implemented members, it needs no test at all and can be dropped from this list; note the
   outcome either way so it isn't re-flagged in a future coverage sweep.
3. Add or extend tests to bring each class off 0%. Aim for meaningful coverage of the class's actual
   logic, not a token line-hit.
4. Re-run the exact verification done when this item was filed, to confirm progress and that neither
   assembly regresses:
   ```
   dotnet test src/TotallyHotArcRouter.Tests/TotallyHotArcRouter.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults/TotallyHotArcRouter.Tests
   dotnet test src/TotallyHotArcRouter.Sandbox.Tests/TotallyHotArcRouter.Sandbox.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults/TotallyHotArcRouter.Sandbox.Tests
   reportgenerator -reports:"TestResults/TotallyHotArcRouter.Tests/**/coverage.cobertura.xml;TestResults/TotallyHotArcRouter.Sandbox.Tests/**/coverage.cobertura.xml" -targetdir:TestResults/Report -reporttypes:JsonSummary;TextSummary
   ```
   Then check `TestResults/Report/Summary.json`'s per-assembly `coverage` values against the 80% bar,
   the same way `.github/workflows/dotnet-ci.yml`'s "Check coverage threshold" step does — **not**
   the root-level aggregate `line-rate` a single project's own cobertura report shows. That aggregate
   double-counts an assembly pulled in transitively but barely exercised by a given test project (e.g.
   `TotallyHotArcRouter.Tests` alone reports `TotallyHotArcRouter.Sandbox` at ~4% because it only loads that
   assembly as a side effect of a `ProjectReference`, not because it tests it) and understated the true
   combined number by roughly 13 points when this item was filed.

### Acceptance

Every listed class (except `ITelemetryPublisher` if it turns out to need none) has non-zero,
meaningful coverage. Both assemblies stay at or above 80% per the CI's own per-assembly check, with a
clearer safety margin than the current 80.1% on `TotallyHotArcRouter.Sandbox`.

### Not urgent

Neither assembly is failing today. This is a proactive margin-building task, not a fix for a current
gate failure — reasonable to pick up opportunistically alongside other work in these areas rather
than as a standalone push.

---

## #5 Get a human review of Phase 5's three design decisions

**Repo:** ArcRouter · **Status:** Open · **Filed:** 2026-07-31, from
[PR #91](https://github.com/davidpizon/ArcRouter/pull/91)

### Problem

Three decisions in [`tool-call-normalization.md`](tool-call-normalization.md) Phase 5 have been
reviewed by nobody. Ten rounds of automated review across PR #91 raised eleven findings and did not
touch any of them. Each is a case where being wrong is expensive and the wrongness is silent.

**1. The `.Tools` guard on emulation selection** — `ModelDialectResolver.RendersTools`.

A model is condemned to emulation when its Ollama chat template matches no registered dialect **and**
mentions neither `.Tools` nor `.ToolCalls`. The second half is the whole safety margin. A template can
support tools perfectly well in framing this build has not registered — DeepSeek is the live example,
and [#3](#3-research-deepseek-tool-call-delimiters-and-register-a-deepseek-dialect) is open precisely
because those delimiters are still unknown. If that check is wrong, or the substring match is too
loose or too strict, TotallyHotArcRouter **strips the native tool support a model actually has** and
replaces it with a prompt. The model still answers, so nothing looks broken.

Specific questions: is "the Go template names `.Tools` or `.ToolCalls`" a sound proxy for "this
template can render a tool schema" across the templates Ollama actually ships? What happens for a
template that renders tools through a helper or a nested `define` block? Is a substring match on a
template that also contains comments or unrelated text safe in both directions?

**2. `IsEmulating` suppression of dialect observations** — `ToolCallObservationRecorder.RecordMatch`.

When emulation teaches a model `<tool_call>` framing and the model complies, that reply is *not*
recorded as an observed dialect. Recording it would write `hermes` at `Observed` confidence, which
outranks the `emulated` row that produced the instructions — so the next request would arrive
un-emulated, the instructions would be gone, and the model would emit nothing. A classification that
erases the reason it was made.

A native `tool_calls` reply *is* still recorded, deliberately: it is the one thing an emulated request
cannot manufacture, and the signal that the model should never have been emulated.

Specific questions: is that the only escape hatch a wrongly-emulated model has? Since emulation strips
`tools`, a model with native support can no longer demonstrate it — is the classification effectively
self-locking until an operator intervenes, and is that acceptable given Phase 6 has not yet shipped
the operator override UI?

> **Answered by observation (Phase 8): yes, self-locking — and the worse case is the mirror image.**
> A classification can seal itself *without* emulation ever being involved. A model that emits a real
> `tool_calls` field on only some replies is recorded `openai-native` at `Observed` on the first one that
> succeeds; performance rule 2 then stops arming it, so no later reply is inspected, no contrary evidence
> is collected, and every subsequent free-text reply reaches the client as raw JSON. Confirmed live on
> `qwen2.5-coder-7b-instruct-ghidra-v2`, where it was the actual cause of a user-reported bug and the
> reason registering a new dialect for the observed framing had not fixed it.
>
> The operator override has now shipped (Phase 8), so the escape hatch exists and is no longer a hand-edit
> of SQLite. Making it *self*-correcting remains open: doing so needs either a demotion path for an
> `Observed` row contradicted by later evidence, or a cheap always-on observer for `openai-native` models —
> and the latter costs exactly what performance rule 2 was written to save. Worth deciding deliberately
> rather than by default.

**3. The measured emulation prompt** — `ToolCallDialectRegistry.Emulated`.

The wording is the Qwen/Hermes chat template's own instruction text, near-verbatim, chosen because
three hand-written alternatives scored 3/15 against a live `qwen2.5.1-coder-7b-instruct` while this
one scored 15/15. Two supporting decisions came out of the same measurements: the dialect registers
**both** Hermes delimiters (the model replies in `<tools>`, the tag it just saw framing JSON), and the
injected schemas keep their `{"type":"function",…}` wrapper.

Specific questions: the evidence is one model, and a Hermes-trained one — so it may comply more
readily than a genuinely tool-less model, which is emulation's actual target. Does the borrowed Qwen
wording generalize, or has it been tuned to the one family whose template it came from? Is borrowing
another vendor's prompt text the right long-term shape at all?

### Why this matters

The pattern across PR #91 is worth stating plainly, because it predicts what a future review will and
will not catch.

Automated review was **consistently accurate on local correctness**: it found three real silent-drop
defects (a missing `messages` fail-open guard, multi-part assistant content being overwritten,
non-message array entries being deleted along with a tool-result ordering bug), two genuine
doc-staleness catches, and one correct allocation point. That is a good hit rate on defects visible
from reading one function.

It was **consistently silent on the decisions above**, and that is not a criticism of the tool — it is
a structural limit. All three decisions are judgments about *what live models do*, and none is
checkable from the text of the code. The most valuable finding of the entire Phase 5 effort was of the
same kind and came from neither review nor re-reading: LM Studio emits `"tool_calls": []` on every
buffered response, and the `is not null` check read that as a native call, recording `openai-native` at
`Observed` confidence — permanently disabling tool-call normalization for that model, including for the
streaming clients that were working. It survived a green suite and two self-reviews, and took one live
request to expose.

The lesson generalizes past this phase: **where correctness depends on external behavior, tests and
static review confirm what the author already believed.** Only a live probe, or a reader who has
independently seen how these models behave, adds information.

### Proposed action

1. Human read of `tool-call-normalization.md` Phase 5 — the section carries the reasoning and the
   measurement table, so it is the cheapest entry point.
2. Consider `/code-review ultra` on the branch. It is user-triggered and billed, so it is an explicit
   choice, not something automation can start.
3. Record a **second model's transcript** in `RecordedModelTranscripts`, ideally a non-Qwen-family
   model with no tool training — the honest test of decision 3, and the one gap the current evidence
   openly has. The recording recipe is in that type's remarks. A transcript that *fails* is a finding
   worth keeping, not a broken test.

### Acceptance

Each of the three decisions has been either confirmed, corrected, or explicitly accepted-with-known-
risk by a human, with the outcome written back into
[`tool-call-normalization.md`](tool-call-normalization.md) Phase 5 so the next reader inherits the
judgment rather than re-deriving it.

