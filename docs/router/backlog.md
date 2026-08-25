# Router: Known Defects and Not-Yet-Implemented Work

Router-side (`src/TotallyHotArcRouter/`) counterpart to [`../gui/backlog.md`](../gui/backlog.md), which
covers `src/TotallyHotArcRouter.Gui/` only. Same structure: **Open** items are live gaps, **Recently
completed** records what has landed so the narrative reads top-to-bottom.

## Open

### ✅ 1. `ManagementFacade` silently drops provider fields on write — Resolved

Two separate write paths in
[`src/TotallyHotArcRouter/Proxy/Management/ManagementFacade.cs`](../../src/TotallyHotArcRouter/Proxy/Management/ManagementFacade.cs)
rebuild a `ProviderOptions` by listing its properties explicitly. Both lists are now incomplete, so
every property they forgot silently resets to its default (`false` for the bools, `null` for the
env-var names) on the next write. `ProviderOptions`' properties are `init`-only, so this is a
construct-fresh-object hazard, not a mutation bug: nothing warns when a new property is added to the
type and not added to these two methods.

The loss is **durable, not just in-memory** — `IProviderConfigStore` persists the whole config to
`model-routing.json` on every mutation (see
[`../gui/provider-management.md`](../gui/provider-management.md)'s Architecture section), so a
dropped field is written out as its default and is gone until someone hand-edits the file.

> **Resolved.** `ProviderOptions` (`src/TotallyHotArcRouter/Models/ModelRoutingOptions.cs`) is now a
> `record`. `MergeProvider` builds `var baseline = existing ?? new ProviderOptions(); return baseline
> with { ...only the fields this request can actually change... }`, and `WithEnabled` is now
> `source with { Enabled = enabled }` — both per the suggested fix below, so a newly added property
> carries across by construction instead of by remembering to update two hand-maintained lists.
> Covered by `src/TotallyHotArcRouter.Tests/Proxy/Management/ProviderOptionsPreservationTests.cs`,
> including the reflection-based "no property silently dropped" guard
> (`UpsertProvider_CarriesEveryPropertyItWasNotAskedToChange`,
> `SetEnabled_CarriesEveryPropertyExceptEnabled`) plus targeted `EnableToolCallGuard` and `Aws*`
> round-trip tests.

#### 1a. `MergeProvider` drops `EnableToolCallGuard` and all four `Aws*` fields

[`MergeProvider`](../../src/TotallyHotArcRouter/Proxy/Management/ManagementFacade.cs) (~L227–251) sets
`BaseUrl`, `AuthHeaderName`, `Headers`, `IsFree`, and `Enabled`. It omits:

- `EnableToolCallGuard`
- `AwsRegion`, `AwsAccessKeyIdEnvVar`, `AwsSecretAccessKeyEnvVar`, `AwsSessionTokenEnvVar`

It backs `UpsertProviderAsync` → `PUT /admin/providers/{key}`, which is what the Governance →
Providers edit dialog calls on save. So:

- **Any provider edit through the GUI clears that provider's `EnableToolCallGuard`** without telling
  the operator. This was severe when the flag was the *only* thing arming tool-call rewriting;
  [`tool-call-normalization.md`](tool-call-normalization.md) Phase 4 has since made arming automatic
  per (provider, model), so losing the flag now only loses the forced-on override it was demoted to —
  and Phase 6 removes it outright.
- **Editing a Bedrock provider through the GUI drops its region and credential env-var names**,
  leaving the Bedrock Runtime SDK client to fall back to the default AWS credential chain — or fail
  outright if no ambient credentials exist. `WithEnabled`'s own doc comment (~L253–259) already
  names this exact `Aws*` hazard as its reason for existing, so the defect is known at the point of
  the workaround but was never fixed at the source.

#### 1b. `WithEnabled` drops `EnableToolCallGuard`

[`WithEnabled`](../../src/TotallyHotArcRouter/Proxy/Management/ManagementFacade.cs) (~L260–274) exists
precisely to avoid 1a's field loss and does copy all four `Aws*` fields — but it is itself missing
`EnableToolCallGuard`, which was added to `ProviderOptions` after it was written. Its doc comment
closes with *"if a new one is added to `ProviderOptions`, add it here too"*; that instruction was
not followed when the guard flag landed.

It backs `SetEnabledAsync` → the Governance → Providers **Stop/Play** control. So toggling an
LM Studio/Ollama provider off and back on silently disables its tool-call guard permanently.

#### Suggested fix

Do not add two more hand-maintained property lists. Give `ProviderOptions` a single copy helper (a
`with`-style clone, or convert the type to a `record` so `with` expressions work natively) and route
both `MergeProvider` and `WithEnabled` through it, so each method states only the fields it
intends to *change*. That makes a newly added property carry across by construction rather than by
remembering to update two call sites.

Whatever shape it takes, cover it with a test that fails when a new `ProviderOptions` property is
added and not carried — e.g. reflect over the type's properties, set every one to a non-default
value, round-trip through both methods, and assert nothing but the intended field changed. A
per-field assertion list would just be a third hand-maintained list with the same failure mode.

#### Tests to add

`src/TotallyHotArcRouter.Tests/Proxy/Management/ManagementFacadeTests.cs`:

- Upsert over a provider with `EnableToolCallGuard = true` → still `true` afterwards.
- Upsert over a Bedrock provider (`AwsRegion` + the three env-var names set) → all four survive.
- `SetEnabledAsync` false-then-true over a provider with `EnableToolCallGuard = true` → still `true`.
- The reflection-based "no property is silently dropped" guard described above.

#### Related — now settled

Whether `EnableToolCallGuard` belongs on the provider at all was an open question when this entry was
written. It is now answered by [`tool-call-normalization.md`](tool-call-normalization.md): the flag's
granularity is wrong, and so is the flag itself. The guard's tag set (`<tools>`/`<tool_call>`) and
required `name` + `arguments` payload are specific to the Qwen/Hermes chat-template family, and a chat
template is a property of the loaded model, not of the server hosting it — so a per-model **dialect**
(detected and cached, not hand-toggled) replaces the per-provider boolean entirely. That plan's
Phase 6 removes `EnableToolCallGuard` from configuration, which also retires §2 below.

This entry stays open regardless, and is a **prerequisite** for that plan's Phase 2: both phases add
provider-level state through the very write paths that currently drop fields, so building on them
unfixed reproduces the same class of bug in new fields.

### 2. `EnableToolCallGuard` has no GUI surface

`Components/ProviderEditDialog.razor` carries `IsFree` but not `EnableToolCallGuard`, so the flag is
editable only by hand in `model-routing.json`/`appsettings.json`. Combined with 1a, this used to be
worse than merely inconvenient: the only way to set the flag was the one path a subsequent GUI edit
wipes.

**Do not fix this by adding the toggle.** [`tool-call-normalization.md`](tool-call-normalization.md)
Phase 4 demoted the flag to a one-release forced-on override — normalization now arms itself from the
capability store — and Phase 6 deletes it in favor of a per-model dialect display with an operator
override. Building the GUI surface now would be building it to delete it.

> **Resolved as designed (Phase 8).** The replacement shipped: a per-model tool-dialect dropdown in
> Governance → Providers, backed by
> `PUT /admin/providers/{key}/models/{modelName}/tool-dialect`, writing at
> `DetectionConfidence.Operator` so no automatic scan can overwrite it, and clearing the pin to resume
> detection.
>
> **Fully resolved (2026-08-25).** `EnableToolCallGuard` itself has now been deleted from
> `ProviderOptions`/`ResolvedModelRoute` and `ToolCallNormalizerFactory` no longer has a forced-on
> override path. Arming is entirely per-(provider, model) from the capability store, as
> [`tool-call-normalization.md`](tool-call-normalization.md) always intended.

### ✅ 3. `ToolCallHistoryRenderer` reads OpenAI-shaped history with dialect-specific key names — Resolved

[`ToolCallHistoryRenderer.RenderAssistantToolCalls`](../../src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallHistoryRenderer.cs)
(~L254, L268) reads a prior assistant turn's tool calls using the *dialect's* key names:

```csharp
if (function[dialect.NameKey] is not JsonValue nameValue || …)
var argumentsText = (function[dialect.ArgumentsKey] as JsonValue)?.TryGetValue<string>(out var raw) == true ? raw : null;
```

The incoming message history is always **OpenAI-shaped** — the client speaks OpenAI regardless of which
dialect the model answers in — so those fields are literally `name` and `arguments`. The dialect keys
belong on the *write* side (rendering the payload back out), not the read side. Read and write are
conflated here, and they only agree by coincidence.

For [`ToolCallDialectRegistry.Llama3Json`](../../src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallDialectRegistry.cs),
`ArgumentsKey` is `"parameters"`, so `function["parameters"]` is always null on real history. The code
then falls through to `function[dialect.ArgumentsKey]?.DeepClone() ?? new JsonObject()` and emits an
**empty arguments object** — silently losing the arguments of every prior tool call in a multi-turn
conversation. The model would read its own transcript as having called a tool with no arguments.

**Latent, not live.** Only dialects carrying an `EmulationPrompt` reach this path, and the two that do
(`Emulated`, `Constrained`) both use `arguments` for `NameKey`/`ArgumentsKey`, so read and write happen
to match. It becomes live the moment a dialect with a differing `ArgumentsKey` gains an emulation
prompt, or the renderer is reused for one — e.g. if the open DeepSeek work (tracked TODO #3) registers a
family with different payload keys.

The quirk predates the file: it was carried over verbatim when this code was extracted from
`ToolCallEmulationRewriter` as a deliberately behavior-preserving refactor
([`tool-call-normalization.md`](tool-call-normalization.md) Phase 8), rather than fixed in place, so the
extraction could be validated against the existing tests unchanged.

> **Resolved**, per the suggested fix below: `RenderAssistantToolCalls` in
> `src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallHistoryRenderer.cs` now reads
> `function["name"]`/`function["arguments"]` (the fixed OpenAI names, including the fallback on the
> arguments-parse-failure path) and only uses `dialect.NameKey`/`dialect.ArgumentsKey` when writing the
> payload back out. Covered by
> `src/TotallyHotArcRouter.Tests/Proxy/Translation/ToolCalling/ToolCallHistoryRendererTests.cs`: a
> `Llama3Json`-dialect round trip (`ArgumentsKey = "parameters"`) now preserves a prior turn's
> arguments instead of emitting an empty object, and `Emulated`/`Constrained` still round-trip
> unchanged.

#### Suggested fix

Read with the fixed OpenAI names, keep writing with the dialect's:

```csharp
if (function["name"] is not JsonValue nameValue || …)
var argumentsText = (function["arguments"] as JsonValue)?.TryGetValue<string>(out var raw) == true ? raw : null;
…
payloads.Add(new JsonObject
{
    [dialect.NameKey]      = name,          // write side keeps the dialect keys
    [dialect.ArgumentsKey] = argumentsNode ?? new JsonObject(),
});
```

Note the fallback on the last line also needs updating — `function[dialect.ArgumentsKey]?.DeepClone()`
has the same read-side bug.

#### Tests to add

- A Llama3-style dialect (`ArgumentsKey = "parameters"`) re-rendering a prior OpenAI assistant turn
  **preserves its arguments** — the regression that would have caught this.
- `Emulated`/`Constrained` round-trip unchanged, proving the fix is behavior-preserving for the
  dialects in use today.

Tests live in `src/TotallyHotArcRouter.Tests/Proxy/Translation/ToolCalling/`; `ConstrainedToolCallTests`
already covers the envelope-history case and is the closest existing example.

