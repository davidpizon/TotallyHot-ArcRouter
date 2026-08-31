# 0003. Declare tool support for emulated and unclassified models

**Status:** proposed
**Date:** 2026-08-29
**Deciders:** David Pizon

## Context and Problem Statement

Ollama's `POST /api/show` returns a `capabilities` array, and capability-filtering clients gate on it:
Visual Studio's Copilot chat silently drops any model that does not declare `"tools"` from its picker. The
router must now populate that array (see
[ollama-show-capabilities-plan.md](../router/ollama-show-capabilities-plan.md)), which forces a question it
has never had to answer publicly: what should it claim for a model whose native tool support is absent or
unknown?

Two states make this non-obvious. A model recorded as dialect `emulated` **cannot** call tools natively —
that row is written precisely because its chat template renders none — yet the router transparently
emulates them on its behalf. And a model with **no** capability row at all is the dominant case: a fresh
install has run no scan, and every hosted provider (OpenAI, Anthropic, Bedrock) is unprobeable by
construction, since the detection tiers are gated on `OllamaNative` / `LmStudioNative`.

This is a wire contract with external clients, so it is expensive to change once tools depend on it.

## Decision Drivers

- The declaration must describe what *this endpoint* can do with *this model name* — the client is talking
  to the router, not to the weights.
- It must not make the router's own tool-call emulation invisible to the clients that filter on this field.
- It must not filter out cloud models that unambiguously support tools merely because they are unprobeable.
- It must not over-claim in a way that produces a hard failure the client cannot diagnose.

## Considered Options

- Declare `tools` only when a scan positively confirmed native tool support
- Declare `tools` for every model the router will accept a tools request for
- Declare `tools` only for models whose dialect is known and not `emulated`, omitting it when unclassified

## Decision Outcome

Chosen option: "Declare `tools` for every model the router will accept a tools request for", because of the
first driver, with the second and third making the alternatives untenable in practice.

`/api/show` is answered by the router about an endpoint the router owns. For every dialect state, a request
carrying `tools` is accepted and returns real `tool_calls`: forwarded byte-for-byte for `openai-native`;
rewritten by `ToolCallNormalizingTranslator` for `hermes`, `mistral`, `llama3-json`, and `function-call`;
handled by `ConstrainedToolCallTranslator` for `constrained`; and taught and read back by
`ToolCallEmulatingTranslator` for `emulated`. On the wire these are indistinguishable. Declaring `false` for
`emulated` would describe an object the client cannot address, and would make the router's entire emulation
feature invisible to the one client that filters on this field — a strict regression against the bug the
change exists to fix.

The unclassified (`null`) case decides whether the fix works at all. Omitting `tools` there would filter out
exactly the hosted models that most obviously support tools, since they can never be probed. It would also
misdescribe the router, which reads `null` as "forward natively and arm the union scanner" — it forwards
`tools` upstream and can read back either shape.

Stated plainly, so it is not mistaken for an oversight: **every dialect state yields
`["completion","tools"]` today.** The mapping is still implemented as an explicit function
(`OllamaModelCapabilities.ForDialect`) with a `Theory` over `ToolCallDialectRegistry.All` and an
exhaustiveness assertion, so that adding a dialect fails the build until someone consciously decides its
capability. It is the seam where a future dialect meaning "cannot express a tool call at all" would land.

The same "describe the endpoint" principle sets the synthetic `totallyhot-arcrouter` alias to the **union**
of capabilities across available-and-enabled models, alongside the **maximum** of their context lengths.

### Consequences

- Good, because the Copilot picker works on a fresh install with no scan run — `capabilities` alone is what
  it gates on, and it is available with zero probing.
- Good, because emulated models remain selectable, which is the feature working as designed rather than an
  over-claim.
- Bad, because non-chat models get declared tool-capable. Model reconciliation auto-adds every id a provider
  lists, including embedding and reranker models, which will report `["completion","tools"]`. Passing
  through Ollama's own `capabilities` array — already present in the document the probe parses — is the real
  fix, recorded as follow-up in the plan.
- Bad, because emulation is best-effort, so `tools` is a statement of surface area rather than a quality
  guarantee. This matches real Ollama, which declares `tools` for plenty of models whose tool calling is
  unreliable, and the router holds no per-model "emulation is known to fail" signal to gate on anyway.
- Neutral, because the union rule for the alias means it can advertise a capability that the specific model
  auto-select happens to pick does not natively have — which the emulation layer then covers. The parallel
  maximum rule for context length has no such backstop and is recorded as a known risk in the plan.

## Pros and Cons of the Options

### Declare `tools` only on positively confirmed native support

- Good, because it never claims anything the underlying model cannot do unaided, and is the most literal
  reading of the field.
- Bad, because it filters out every hosted provider, which cannot be probed at all — the models most
  certain to support tools would be the ones excluded.
- Bad, because it reports `false` for emulated models, hiding a working router feature from the only
  clients that read the field.
- Bad, because it makes the fix depend on the operator having run "Refresh from endpoint", so an upgrade
  with no action would appear to change nothing.

### Declare `tools` for every model the router accepts a tools request for

- Good, because it describes the endpoint the client is actually addressing, which is what the field means.
- Good, because it works with no scan, on every provider type.
- Bad, because it currently declares `tools` for embedding and reranker models that reconciliation added
  automatically.
- Bad, because it makes the mapping function branchless today, which risks reading as unnecessary
  indirection without the exhaustiveness test explaining it.

### Declare `tools` only for known non-`emulated` dialects, omitting when unclassified

- Good, because it looks like a cautious middle path and avoids claiming anything for an unscanned model.
- Bad, because it combines the worst of both: it still hides emulated models, and "unclassified" is the
  dominant state, so most models would carry no declaration at all.
- Bad, because omission and negation are indistinguishable to the client — both drop the model from the
  picker — so the caution buys nothing observable.

## More Information

The wire shape, the omit-when-unknown rule for `model_info`, and the aggregation rules for the synthetic
alias are specified in
[ollama-show-capabilities-plan.md](../router/ollama-show-capabilities-plan.md). The storage decision behind
the context length reported alongside these capabilities is
[ADR-0002](0002-store-probed-model-context-windows-in-their-own-table.md).

Two verification steps in the plan can affect this ADR: confirming Ollama's actual `capabilities`
vocabulary (it is an implementation detail, not a specification), and confirming that Visual Studio filters
on `/api/show` rather than `/api/tags`. The latter would change *where* the array is emitted, not what it
declares.
