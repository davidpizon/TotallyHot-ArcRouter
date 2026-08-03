# Phase 6: Post-Release Calibration

Template for [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md) Phase 6.

> **This phase cannot be executed yet.** Phase 6 requires a shipped release and real usage — "capture
> user feedback," "track interaction metrics," "close a feedback cycle" — none of which exist for a
> desktop app that has not been released with this design to actual operators. What follows is the
> structure to fill in once that happens, not a completed calibration. Treating a template as a finished
> deliverable would misrepresent the state of the work; this file exists so the phase has a concrete
> home when it becomes executable, and to make the gap explicit rather than silently skipping the phase.

## 30-day adoption review (template)

| Question | Answer |
| --- | --- |
| Release date of the aspirational design | _fill in_ |
| Review date (30 days later) | _fill in_ |
| Feedback channels checked | _e.g. internal operator survey, support tickets, direct interviews_ |
| Scanability feedback | _fill in_ |
| Trust-perception feedback | _fill in_ |
| Drill-down discoverability feedback | _fill in_ |

## Interaction metrics (template)

Requires instrumentation this app does not currently have (no telemetry on UI interaction depth or
time-to-first-insight exists in the codebase as of this writing). Before this section can be filled in,
someone needs to decide whether to add that instrumentation and where the data would be reviewed.

| Metric | Baseline | 30-day value | Delta |
| --- | --- | --- | --- |
| Expansion depth (avg. `TurnCard` drill-downs per session) | _fill in_ | _fill in_ | _fill in_ |
| Time-to-first-insight (time to first meaningful metric view) | _fill in_ | _fill in_ | _fill in_ |

## Follow-up backlog (template)

| Item | Severity | Impact | Category |
| --- | --- | --- | --- |
| _fill in as feedback arrives_ | Low / Med / High | Low / Med / High | Minor tuning / Structural rework |

## Documentation updates

Once real patterns are validated (or invalidated) by the review above, update:

- [`DESIGN.md`](DESIGN.md) and [`MOTION.md`](MOTION.md) — replace "as specified" language with "as
  validated" where a pattern has real usage evidence behind it.
- This file — replace the template rows with actual findings and closed action items.
- [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md)'s tracking table — mark
  Phase 6 Done only once at least one feedback cycle is closed with documented actions, per its exit gate.

## Known open items carried from Phase 0/5

These are not Phase 6 findings — they're gaps identified earlier in adoption that Phase 6 should
prioritize once real feedback exists to weigh them against:

1. No actual screenshot/visual regression evidence was captured (Phase 0 gap matrix note) — a Windows
   dev box is needed to produce and compare real renders.
2. CircularSp substitution (`--font-ds` fallback stack) has not been visually validated against the
   aspirational spec's intent — worth a design-lead review once the app is actually running.
3. Accessibility contrast ratios were checked by inspection, not an automated audit tool (Phase 5
   checklist) — worth running a proper contrast checker once a build is available to test against.
