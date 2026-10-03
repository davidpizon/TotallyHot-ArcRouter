# 0021. Carry the subagent routing signal on the telemetry wire as an optional field

**Status:** proposed — accepted by David Pizon on 2026-10-02; moves to `accepted` when its PR merges (see the README)
**Date:** 2026-10-02
**Deciders:** David Pizon

## Context and Problem Statement

Issue [#163](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/163) makes the router recognise
vendor-documented markers that a request comes from a subagent or side task, and route it by kind
([ADR-0022](0022-route-harness-subagent-and-helper-traffic-by-kind.md);
[`docs/plans/issue-163-subagent-aware-routing.md`](../plans/issue-163-subagent-aware-routing.md)). Some kinds get a
cheaper pick, and the rest route as they would without a signal.
David decided on 2026-09-29 that the detected signal must be visible in the router's log line **and** on the
dashboard, so an operator can see which requests were biased and why.

The log line needs no decision. The dashboard does: the Live Stream view is fed by
`RoutingTelemetryEvent`, which crosses the router/GUI boundary as the `RoutingTelemetryEvent` message in
[`src/Protos/telemetry.proto`](../../src/Protos/telemetry.proto) (ADR-0011: the GUI is a router-served Blazor
WebAssembly app speaking gRPC-Web). Showing the signal there means changing a transport contract, and
AGENTS.md is categorical that transport changes get an ADR first, even when the change is purely additive.
The change would touch the C# event record, `TelemetryBroadcaster.ToWire`, the proto, and the GUI's own
models (`RoutingTelemetryEventDto`, `ConversationAggregator`, `LiveDataStore`, `LiveConversationMapper`,
`ConversationTurn`). The forces are compatibility (an older router must not break a newer GUI, and the
reverse), that the signal's value derives from a request header a client controls, and the existing
convention that telemetry enums travel as strings.

## Decision Drivers

- **Additive and skew-safe:** a router without the field and a GUI that knows it (and the reverse) must both
  keep working, with the badge simply absent.
- **No new client-controlled text on the wire:** the value comes from a request header, so it must not be
  free text a client can choose.
- **Match the existing wire conventions:** `cost_confidence` and `substitution_reason` already carry enums as
  strings with `optional` for presence; a reader should find nothing new to learn.
- **Small blast radius:** `RoutingTelemetryEvent` has 32 construction sites (mostly tests); no existing
  caller may need to change.
- **Honour David's decision** that the dashboard shows the signal alongside the routing decision.

## Considered Options

- Option 1: One new `optional string subagent_signal = 25` field carrying a `harness/kind` label.
- Option 2: A structured `SubagentSignal` sub-message (`harness`, `kind`, `source`).
- Option 3: Log line only; no wire or GUI change.
- Option 4: Fold the signal into an existing field (for example `substitution_reason` or `request_summary`).

## Decision Outcome

Chosen option: "Option 1: one new optional string field", because it is the smallest change that satisfies
David's dashboard requirement while meeting the skew-safety and convention drivers: it is additive, follows
the same string-label pattern `cost_confidence` and `substitution_reason` already use, and its value is drawn
from a fixed vocabulary inside `SubagentSignalDetector` rather than copied from a header.

The C# side adds one nullable trailing member to `RoutingTelemetryEvent` (so the 32 positional callers
compile unchanged) and maps it in `TelemetryBroadcaster.ToWire` only when present, as that method already
does for every other optional field. The GUI chain carries it as an optional member, and `LiveStream` renders
a badge when it is non-empty and nothing when it is absent.

### Consequences

- Good, because the field is `optional`, so presence is tracked: an older router sends nothing and the GUI
  renders no badge; an older GUI ignores the unknown field 25. Router and GUI ship together under ADR-0011,
  so skew is bounded to a mid-upgrade window anyway.
- Good, because the label is `harness/kind` from a closed set (`claude-code/subagent`,
  `codex/thread_spawn`, `copilot/utility-alias`, and so on), so nothing a client types reaches the wire, the
  logs or the UI.
- Good, because no existing `RoutingTelemetryEvent` caller changes.
- Bad, because a string label drops the `source` (which header produced it). An operator who needs that has
  to read the router log line, which can carry more detail.
- Bad, because it adds one more positional-adjacent member to an already wide record, which works against the
  open smell C2 (the 25-parameter publish path). It is contained to a single trailing member and a single
  new argument on the private `RequestTelemetryPublisher` method that builds the event.
- Neutral, because the signal is not persisted: the transcript store keeps `IsUtility` but not the reason, so
  the Live Stream badge shows on live turns only, not on turns reloaded from history.
  - **Follow-up, with a trigger:** add one nullable `subagent_signal TEXT` column to `request_transcripts`, holding
    the same label and never folded into `score`.
  - **Trigger:** when the Sessions tab (#176) needs the badge on saved turns, or a learning or analytics consumer
    needs to filter cost-driven picks.
  - **Constraint:** coordinate it with ADR-0019, ADR-0020 and #184, which are reshaping that store.
- Neutral, because field number 25 is now taken, and the label vocabulary is a de facto contract: a new
  harness adds a value, not a field.

## Pros and Cons of the Options

### Option 1: One new `optional string subagent_signal` field

- Good, because it is purely additive and follows the existing `cost_confidence` / `substitution_reason`
  convention.
- Good, because the dashboard needs a label to show, not a record.
- Bad, because it flattens `harness`, `kind` and `source` into one string; a consumer that wants them
  separately must split on `/`.

### Option 2: A structured `SubagentSignal` sub-message

- Good, because each part is a typed field and a future part (a confidence, a version) costs nothing.
- Bad, because it is a heavier contract for a badge, adds a nested type to the GUI models, and nothing needs
  the parts separately today.
- Bad, because it is a new shape in a file whose existing optional metadata is all scalar strings.

### Option 3: Log line only

- Good, because there is no transport change at all and no ADR is needed.
- Bad, because it does not meet David's 2026-09-29 decision that the dashboard shows the signal; an operator
  would have to correlate router logs with the Live Stream by hand.

### Option 4: Fold the signal into an existing field

- Good, because no proto change.
- Bad, because it overloads the meaning of a field other code branches on: `LiveConversationMapper` treats
  `substitution_reason` values as warnings, and `request_summary` is user-visible text. A signal is neither.
- Bad, because the packed value would have to be parsed back out by every reader, which is the same contract
  change with worse typing.

## More Information

- Plan: [`docs/plans/issue-163-subagent-aware-routing.md`](../plans/issue-163-subagent-aware-routing.md),
  Phase 3; signal evidence in [`docs/router/utility-model-routing.md`](../router/utility-model-routing.md).
- Wire: `message RoutingTelemetryEvent` in [`src/Protos/telemetry.proto`](../../src/Protos/telemetry.proto),
  highest field currently 24 (`substitution_reason`); the new field is 25.
- Mapping: `TelemetryBroadcaster.ToWire` in `src/TotallyHotArcRouter/Telemetry/TelemetryBroadcaster.cs`.
- Read by: `RoutingTelemetryEventDto`, `ConversationAggregator`, `LiveDataStore`, `LiveConversationMapper`,
  `ConversationTurn`, and `LiveStream.razor`.
- Related: [ADR-0011](0011-router-served-blazor-webassembly-gui-over-grpc-web.md) (the GUI and its
  transport), [ADR-0018](0018-persist-request-telemetry-off-the-request-path-via-a-bounded-channel.md)
  (the publisher path this event is built on).
- Implementation order: the router log line does not depend on this ADR and lands first. The proto,
  `RoutingTelemetryEvent`, publisher and GUI changes wait for this ADR to be accepted.
