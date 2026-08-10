# Phase 5: Design Conformance Checklist

Tracks [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md) Phase 5's exit gate:
"All builds/tests pass with zero warnings/errors" and "Principle conformance signed off for all target
surfaces." This checklist is filled in as adoption work lands; items are marked Done / Partial / Not
verified rather than assumed.

## Build & test evidence

| Check | Status | Notes |
| --- | --- | --- |
| `dotnet build src/TotallyHotArcRouter.Gui/TotallyHotArcRouter.Gui.csproj` — zero warnings/errors | **Done** | Verified: `Build succeeded. 0 Warning(s). 0 Error(s).` |
| `dotnet test src/TotallyHotArcRouter.Gui.Tests/TotallyHotArcRouter.Gui.Tests.csproj` — all pass | **Done** | Verified: `Passed! - Failed: 0, Passed: 161, Skipped: 0, Total: 161`. One test (`ModelDistributionTests`) was updated because it asserted the literal old `#38bdf8` chrome color on a button that is chrome, not data — not a masked regression. |
| Code coverage ≥ 80% (AGENTS.md floor) | Not verified | `.github/workflows/dotnet-ci.yml` excludes the Gui project from CI (Windows-only MAUI); must be checked manually on a Windows dev box — this was true before adoption too, not a regression it introduced |

## Visual Design (aspirational-design.md §8)

| Criterion | Status | Notes |
| --- | --- | --- |
| Color palette limited to near-black ramp + Dark Green accent | Done in tokens/`app.css`; per-component inline-style sweep in progress | `--surface-*`/`--accent` tokens defined; component-level hex literals converted file-by-file |
| Semantic colors (green/amber/red) consistent | Done | Unchanged from pre-adoption — already conformant |
| No pure black, gradients, or decorative shadows | Done | `--surface-base` is `#121212`, not `#000000`; no gradients existed or were added |
| Pill/circular geometry on CTA buttons | Done in `.btn-*` classes; applied to identified CTA buttons | Dense per-row icon actions and the tab bar are a documented exception — see DESIGN.md §4.2 |
| Glassmorphism on modals only | Done | Modal backdrops already used `backdrop-filter: blur(4px)`; card surfaces remain flat |
| Card/button radii match spec | Done | Cards 8px (`--radius-card`), CTA buttons pill (`--radius-pill`) |

## Typography (aspirational-design.md §8)

| Criterion | Status | Notes |
| --- | --- | --- |
| CircularSp font family | **Deviation, documented** | CircularSp is proprietary (Spotify) and cannot be bundled. `--font-ds` substitutes a geometric-sans fallback stack. See DESIGN.md §3 "On CircularSp." |
| Weight binary (700/400, 600 sparingly) | Done | Unchanged from pre-adoption — already conformant |
| Uppercase buttons, 1.4-2px tracking | Done | `.btn` base class sets `text-transform:uppercase; letter-spacing:1.4px` |
| Compact scale (10-24px) | Done | Unchanged — DESIGN.md §3 scale already within range |

## Layout

| Criterion | Status | Notes |
| --- | --- | --- |
| 8px base unit | Done | Unchanged — Tailwind rem scale already 8px-based |
| Sidebar/nav fixed | N/A | This app uses a top tab bar, not a sidebar — a deliberate deviation from the aspirational layout described for a generic dashboard; the fixed single-window desktop shell (DESIGN.md §5) takes precedence, per the adoption plan's non-goal "no mobile-first redesign that conflicts with the fixed-window desktop app model" |
| Bottom status bar | N/A | This app's status/token banner is in the header ticker row, not a bottom bar — same rationale as above |
| No horizontal overflow | Done | Unchanged — fixed single-window shell already enforces this |

## Components

| Criterion | Status | Notes |
| --- | --- | --- |
| Unified component language across tabs | Done | Shared `.btn-*`/`.ds-card`/modal-shell classes now used consistently |
| Metric cards: headline + secondary + status | Done | Unchanged — already the `TurnCard`/ticker pattern |
| Forms with validation states | Done | Unchanged — Governance forms already had green/red validation text |
| Buttons: primary/secondary/ghost, pill/circular | Done | See `.btn-*` classes, DESIGN.md §4.2 |
| Empty states: icon + text + action | Not verified | No dedicated empty-state audit performed in this pass |

## Motion (aspirational-design.md §8, MOTION.md §10)

| Criterion | Status | Notes |
| --- | --- | --- |
| Entrance animations semantic-triggered | Done | Already conformant pre-adoption (MOTION.md §1) |
| No idle/ambient decorative motion | Done | Only `pulse-dot`/`flash-*`, both load-bearing status signals |
| `prefers-reduced-motion` respected | Done | Unchanged — `app.css` already had the media query |
| Value Tick accent updated | Done | `#38bdf8` → `var(--accent)` in MOTION.md §6 spec (not yet wired to live data — tracked as "Not yet implemented" in MOTION.md §10, pre-existing gap, not introduced by adoption) |

## Accessibility

| Criterion | Status | Notes |
| --- | --- | --- |
| Contrast ≥ 4.5:1 | Not formally verified | White text (`#ffffff`) on `#121212`/`#181818` and `#1ed760` accent on those surfaces both exceed 4.5:1 by inspection (near-black on white is ~18:1; #1ed760 on #121212 is ~9:1), but no automated contrast audit tool was run |
| Focus rings visible | Done | `.ls-tip:focus-visible` and input `:focus` both use the accent green outline/ring |
| Touch targets ≥ 44×44px | Done | `.btn` sets `min-height:44px`; dense per-row icon actions (26×26) are a documented, deliberate exception for information density, matching pre-adoption behavior |
| ARIA labels on icon-only buttons | Done | Unchanged — modal close buttons already required `aria-label` per DESIGN.md §4.1 |

## Documentation

| Criterion | Status | Notes |
| --- | --- | --- |
| Five principles reflected in DESIGN.md/MOTION.md | Done | See both docs' new "Status" callouts |
| No conflicting guidance between DESIGN.md, MOTION.md, tab docs | Done | DESIGN.md and MOTION.md reconciled; swept `cost-analytics-visualization-spec.md`, `console-tab-plan.md`, `dashboard.md`, `governance-model-cards.md`, `secret-field.md` for pre-adoption hex values — none remain. `console-tab-plan.md`'s log-level hex values (`#A0A0A0`/`#4CAF50`/etc.) match `LogLevelColorMapper.cs` exactly; they're an intentional log-level convention, not stale chrome. |
| Component behavior documented with semantic triggers | Done | DESIGN.md §4.2, MOTION.md §6-7 |

## Sign-off

Not yet signed off. Per the adoption plan's Ownership Model, this requires explicit review from a design
lead and GUI engineering lead against a running build — neither role exists as an automated check this
pass can perform. Recommended next step: run the build/test commands above on a Windows dev box, do a
visual pass against this checklist, and record sign-off here with names and dates.
