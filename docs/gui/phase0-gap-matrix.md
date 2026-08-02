# Phase 0: Principle Gap Matrix

Baseline audit for [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md) Phase 0,
taken against the GUI as it stood before adoption began (the state described by the pre-adoption revision
of [`DESIGN.md`](DESIGN.md) and [`MOTION.md`](MOTION.md)). Evidence is drawn from `app.css`,
`src/TotallyHotArcRouter.Gui/Components/*.razor`, and the docs listed in each row.

> **Scope note on this document:** the visual/screenshot evidence normally captured in a Phase 0 audit
> (baseline screenshots/video per surface, per the plan's "Deliverables") is not something this pass could
> produce — there is no build/screenshot pipeline available for the Windows-only MAUI Blazor Hybrid app in
> this environment. This matrix is evidence-based on source inspection (CSS values, component markup)
> rather than rendered screenshots. Capturing actual before/after screenshots on a Windows dev box remains
> an open follow-up — see the Phase 5 checklist.

## Matrix

| Principle | Surface | Pre-adoption status | Evidence |
| --- | --- | --- | --- |
| Data density without claustrophobia | Live Stream (`TurnCard`, `ConversationCard`) | **Pass** | 8-stat strip already used consistent `gap-*`/`px-*`/`py-*` spacing (DESIGN.md §1, pre-adoption) |
| Data density without claustrophobia | Cost Analytics | **Pass** | One chart behind a metric-picker pill row, not several charts at once (`CostAnalytics.razor`) |
| Data density without claustrophobia | Governance (Providers) | **Pass** | Compact provider cards with dense stat rows; 26×26 icon actions kept small (not blown out by generic component reuse) |
| Data density without claustrophobia | Console | **Pass** | Monospace log surface, no padding bloat (`console-tab-plan.md`) |
| Hierarchy through weight, not decoration | All tabs | **Pass** | Bold/regular binary + five-tier slate text ladder was already the mechanism (DESIGN.md §1); no gradients/decorative shadows existed anywhere in `app.css` |
| Motion as meaning | All tabs | **Pass** | MOTION.md §1 already enforced "every animation is attributable"; the only ambient loops (`pulse-dot`, `flash-*`) were already load-bearing status signals, not decoration |
| Trust through restraint | All tabs — **chrome hue** | **Fail (by literal spec)** | Chrome accent was `sky-400` (`#38bdf8`), not the Dark Green `#1ed760` `aspirational-design.md` specifies. Restraint itself (2-3 hues + neutrals) was already satisfied; only the specific hue diverged. |
| Trust through restraint | Buttons — **geometry** | **Fail (by literal spec)** | All buttons were square-cornered (`rounded`/`rounded-lg`, 4-8px); `aspirational-design.md` specifies pill (`9999px`)/circular geometry for CTA buttons. |
| Trust through restraint | Typography — **font family** | **Fail (by literal spec)** | UI font was Inter; spec calls for CircularSp (proprietary, not bundleable — see DESIGN.md §3 "On CircularSp"). |
| Trust through restraint | Surface ramp | **Partial** | Surfaces were `#0f172a`/`#1e293b` (slate-900/800), not the `#121212`-`#272727` near-black ramp; both are within "near-black, achromatic" intent but the literal hex values diverged. |
| Progressive disclosure over feature walls | Live Stream | **Pass** | `ConversationSummary` pinned strip + `TurnCard` click-to-expand routing log (DESIGN.md §1) |
| Progressive disclosure over feature walls | Cost Analytics | **Pass** | Ranked metric-picker pill row gates which single chart renders |
| Progressive disclosure over feature walls | Governance | **Pass** | Card-first list with edit/remove dialogs for detail, not an all-fields-visible table |
| Progressive disclosure over feature walls | Console | **Pass** | `console-tab-plan.md`'s summary→detail→raw model |

## Top divergences, ranked by impact/complexity

1. **Chrome accent hue** (sky → green) — high visual impact, low complexity (single token + a handful of hardcoded-hex call sites). Addressed in Phase 2.
2. **Button geometry** (square → pill/circular for CTAs) — high visual impact, medium complexity (touches every explicit CTA button across ~6 component files). Addressed in Phase 2/3.
3. **Font family** (Inter → CircularSp-alike) — medium visual impact, low complexity, but constrained by licensing (CircularSp itself is unavailable). Addressed in Phase 2 with a documented substitution.
4. **Surface ramp hex drift** (`#0f172a`/`#1e293b` → `#121212`-`#272727`) — medium visual impact, medium complexity (many inline-style call sites, no single override point). Addressed in Phase 2/3.
5. **Elevation shadow tiering** (one 0.5-opacity value → two-tier 0.3/0.5 split) — low visual impact, low complexity. Addressed in Phase 2.

Everything else in the matrix already passed before adoption began — the five principles were largely
already the operating model for this app's information architecture and motion; what adoption changed
was bringing the literal token values (color, radius, font) into alignment with `aspirational-design.md`.
