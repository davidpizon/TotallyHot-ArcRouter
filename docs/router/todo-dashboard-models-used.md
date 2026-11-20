# A "models used" line in the dashboard

> **Status: proposal — not implemented.** There is no single line under each answer that lists the
> models used for that answer. The fields such a line would need are already on the response and on
> the Sessions turn. Nothing here is an approved plan or permission to code.
> [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
> requires an observed cost before the work is scheduled.

The idea is taken from GitHub's page
[About GitHub Copilot Fusion](https://docs.github.com/en/copilot/concepts/models/copilot-fusion),
read on 2026-11-20. Fusion's page says: "When the response is provided, you can hover over the
effort level name below the response to see a list of models used." It also says intermediate
drafts may be discarded and only the final response is shown, and that you can follow each step
and the model being used while Fusion is still working.

Arc Router has no effort-level name to hover, and a single request is served by one candidate.
The proposal is to show the model information the code already emits, in a place that is easier
to see. It does not add a draft/judge/escalation list. Those calls do not happen
([`todo-cascade-mode.md`](todo-cascade-mode.md)).

## What each answer already sends

`UpstreamResponseWriter` stamps three headers before the first body byte, on the success path and
on the router-authored error paths that go through `RoutingResponseHeaders.WriteTo`:

| Header | Source |
|---|---|
| `X-ArcRouter-Requested-Model` | The client's literal `model` string. |
| `X-ArcRouter-Routed-Model` | `route.ModelName` for the hop that was committed. |
| `X-ArcRouter-Substitution-Reason` | `None`, `AutoSelect`, `UnresolvedName`, `ModelStopped`, `CircuitOpen`, or `Failover`. |

`X-ArcRouter-Stripped-Features` is added only when the winning hop had features removed. The
orchestrator live-path plan (M2.2) puts this pair in headers because the body stays
provider-shaped. The JSON `model` field is not an Arc Router "models used" field.

The same facts are on the telemetry event (`RoutingTelemetryEvent`): `RequestedModel`,
`RoutedModel`, `ResolvedModel` (the provider's model id), `Provider`, `IsFallback`,
`SubstitutionReason`, and `EstimatedCostUsd` when a price was known. `IsFallback` is true when a
later candidate served the request. The event does not list the candidates that were attempted
and did not answer.

`LiveConversationMapper` copies those fields onto `ConversationTurn`. It does not invent any.

## What the dashboard already shows

The Sessions tab already renders this data. It is spread across the row and the details pane
described in [`docs/gui/DESIGN.md`](../gui/DESIGN.md):

- `SessionMessageRow` labels the response with `RoutedModel`, or `Model` when the routed name is
  absent. A live fallback adds a "fallback" chip. A substitution reason other than `None` or
  `AutoSelect` adds a "substituted" chip.
- `MessageDetailsTab`, response side, shows routed model, resolved model (`Turn.Model`), provider,
  cost (`TotalCost`, or "not priced"), and substitution. `None` and `AutoSelect` are displayed as
  a dash. Those two reasons are the client's own choice (`LiveConversationMapper` calls them
  silent and does not emit a warning step for them).
- `RoutingDetailsTab` shows `Requested → routed`, the same silent-reason rule, and a step
  `Route Confirmed:` plus the resolved model name. Router overhead cost is a separate figure
  (`RouterCostUsd`), live turns only.
- `SessionOverviewTab` has a **Models used** section. It groups loaded turns by
  `RoutedModel ?? Model` and shows turn count, share of known cost, and last-used time. That is a
  session aggregate. It is not a line under one answer, and it is not on the message row.

A typical coding client shows the assistant text and does not show `X-ArcRouter-*` headers. On the
dashboard, the per-answer model is the sender label and the details tabs. The session-level
"Models used" block is on the Session tab of that same pane. `AutoSelect`, the usual `"model":
"auto"` path, is omitted from the substitution line, so the visible reason for the common case is
the routed-model label alone.

## What a clearer line could show

A line on the response, in the Sessions messages pane, built only from fields `ConversationTurn`
already has:

- the routed model (`RoutedModel`, falling back to `Model` as the row already does)
- the requested model, when it differs
- the substitution reason, including `AutoSelect` if the line's job is to explain `auto`
- `IsFallback` when the turn is live and the flag is set
- `TotalCost` when it is non-null, using the same "not priced" treatment as the Message tab

`Provider` and `ResolvedModel` are already on the Message tab. Repeating them on the line is
optional. They are not new fields.

The line must not grow a list of models that "participated" in one answer. One hop commits the
response. Failed attempts are not on the turn. Adding them would be a new telemetry field, which
this proposal does not include.

Fusion's hover sits under the response next to an effort level. Arc Router has no effort level
([`todo-effort-levels.md`](todo-effort-levels.md)). The line should not invent one to hang the
model name on.

## Measured cost before anyone builds it

The data is already stored and already drawn in the details pane. A new line is justified only if
that placement has an observed cost: people missing which model served a turn, with examples from
real sessions rather than from Fusion's hover. [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
treats "the information exists but is easy to miss" as a hypothesis until someone records the
miss. This file does not record one.

CodeGraph had no index for this tree when the note was written, and Serena was not asked. This is
not a smell finding and does not belong in
[`code-smell-refactoring-plan.md`](code-smell-refactoring-plan.md). Moving a label is still a GUI
change, and it still waits on that cost.
