# TotallyHotArcRouter.Gui Motion System

The authoritative motion reference for `TotallyHot.ArcRouter.Gui`, companion to [`DESIGN.md`](DESIGN.md).

Unlike `DESIGN.md` — which codifies the app's *existing* visual identity — this document is
**prescriptive**. It defines the target motion system. Some of it ships today in
[`wwwroot/css/app.css`](../../src/TotallyHotArcRouter.Gui/wwwroot/css/app.css); the rest is the spec that
new and refactored components should be built against. Every section marks which is which:

- **Shipping** — already in `app.css`, do not change the value
- **Proposed** — not yet implemented; implement as specified rather than inventing a variant

**Platform constraint:** this is a Blazor Hybrid app in a WebView2 (Chromium) host. There is no
Framer Motion, no GSAP, and no JS animation library — and no Tailwind build step, so `app.css` is
hand-maintained. **All motion is CSS.** Snippets below are CSS only; that is not an omission.

**Aspirational-design conformance (Phase 4 audit):** this motion system already satisfied
`aspirational-design.md` §5's "motion as meaning" principle before adoption began — every trigger
below maps to a state change, value change, user interaction, or tab transition, and the only ambient
loops (`pulse-dot`, `flash-*`) are load-bearing status signals, not decoration. Adoption changed two
things here: the accent color referenced by Value Tick (§6) and the addition of an unused
`--dur-entrance` token (§3, §9) for the one aspirational pattern (a full "theater" reveal) this app
doesn't currently have a use for. See §10 for the full conformance status per pattern.

---

## 1. Philosophy

Motion here has exactly one job: **tell the operator what changed.** This is a telemetry dashboard
where the numbers are always moving, so movement is a scarce signal that must not be spent on
decoration. If an animation cannot be traced to a specific state change, it does not belong.

Three rules follow from that, and they govern every token below:

1. **Every animation is attributable.** A card animates because its data changed, a panel animates
   because the operator switched to it. Nothing animates on a timer except the two liveness signals
   in §7 (`pulse-dot`, `flash-*`), which exist precisely to communicate "this is live" and "this
   crossed a threshold."
2. **No overshoot, anywhere.** Bounce and elastic curves read as playful. This tool enforces budgets
   and reports spend; its motion should read as *certain*. `DESIGN.md` §1 calls this "trust through
   restraint" — the motion equivalent is that things arrive and stop, they never overshoot and settle.
3. **Motion never moves data.** Numeric values must stay readable and positionally stable while
   updating. Highlight a changed value in place (§6 Value Tick); never slide, count up, or reflow it.
   An operator reading a cost figure mid-animation must get the true value, not a tween.

---

## 2. Spatial Model & Directionality

The app is a **fixed single window** — `html,body,#root{height:100%;overflow:hidden}`, no page
scroll, no breakpoints (`DESIGN.md` §5). So spatiality is expressed through layers, not through
scrolling a larger canvas.

### Layers

| Layer | Surface | Enters by | Exits by |
|---|---|---|---|
| **base** | Page background `#121212` | n/a — always present | n/a |
| **content** | Tab panels, cards, charts | Fade + micro-scale, in place | Fade, faster than enter |
| **overlay** | `SettingsModal`, `ProviderEditDialog` | Rise: scale up + fade, from above | Fade + shrink slightly |
| **floating** | `.ls-tooltip`, anchored popovers | Opacity only — **never transform** | Opacity only |

**Why floating never transforms:** tooltips are positioned by `js/tooltips.js` against a live anchor
rect. A transform on the tooltip fights that positioning and produces visible drift on first paint.
Opacity is the only safe channel. This is a hard constraint, not a stylistic preference.

### Directionality

- **The primary axis is vertical.** Every scrollable region — conversation list, turn list, console
  log, payload blocks — scrolls vertically. New content therefore enters on the Y axis, moving a
  short distance *toward* its resting position (`translateY(-4px) → 0` for prepended rows).
- **The tab bar is explicitly non-directional.** The five tabs (Live Stream / Cost Analytics / Model
  Distribution / Governance / Console) are peers, not a sequence — there is no "next" tab, and
  Governance is not spatially to the right of Console in any meaningful sense. Sliding panels
  horizontally would assert an ordering the information architecture does not have. **Tab panels
  crossfade in place** (§6 Panel Crossfade). This is a deliberate decision, not an unimplemented
  slide.
- **The one horizontal affordance is the split-pane divider**, which is direct manipulation and
  tracks the pointer 1:1 — see §5.

---

## 3. Duration Scale

Ordering constraint: in-page feedback is always faster than panel-level change, and everything
interactive lands inside 100–300ms. Ambient loops are a separate class and are deliberately slow so
they read as breathing rather than blinking.

| Token | Value | Status | Use for |
|---|---|---|---|
| `--dur-instant` | `100ms` | Shipping | Tooltip fade (`.ls-tooltip`) |
| `--dur-fast` | `150ms` | Shipping | Hover, focus, color/border change (`.card-hover`, `.transition-colors`, input focus, `.ls-divider`) |
| `--dur-default` | `200ms` | Shipping | Tab indicator, row enter, dropdowns, value tick |
| `--dur-slow` | `300ms` | Proposed | Overlay enter (modals), largest surfaces only |
| `--dur-exit` | `120ms` | Proposed | Exits — see rule below |
| `--dur-entrance` | `420ms` | Shipping (token only, unused) | Reserved for a full-screen/theater-style entrance (`aspirational-design.md` §5) — e.g. a first-load empty state or a whole-tab reveal. **Not** used for list rows or dense stat strips; at 420ms those would read as sluggish in a telemetry app the operator scans continuously. `--dur-default`/`--dur-slow` remain correct for everything currently shipping. |
| `--dur-pulse` | `2s` | Shipping | `.pulse-dot` liveness loop |
| `--dur-flash` | `1.2s` | Shipping | `.flash-amber` / `.flash-red`, 3 iterations |

**Exits are faster than enters.** An element leaving has already served its purpose and the operator
has moved on; a slow exit is dead time. Use `--dur-exit` (or roughly 60% of the enter duration) for
anything being dismissed.

**Nothing interactive exceeds 300ms.** If a transition seems to need more, the problem is the amount
of movement, not the duration.

---

## 4. Easing Curves

| Token | cubic-bezier | Status | Use for |
|---|---|---|---|
| `--ease-standard` | `(.4, 0, .2, 1)` | Shipping | **House default.** Hover, color, border, tab indicator — anything symmetric |
| `--ease-out-expo` | `(.16, 1, .3, 1)` | Proposed | Enters. Sharp deceleration = arrives decisively, settles instantly |
| `--ease-out-quart` | `(.25, 1, .5, 1)` | Proposed | Softer enter for large surfaces (modal panel) |
| `--ease-in-quart` | `(.5, 0, .75, 0)` | Proposed | Exits. Accelerates away — the mirror of an enter, not its reverse |
| `--ease-in-out-sine` | `(.37, 0, .63, 1)` | Shipping (as `ease-in-out`) | Ambient loops only — `pulse-dot`, `flash-*` |

**Explicitly excluded:** `ease-out-back`, `ease-in-out-back`, and every other overshoot curve
(control points outside `[0,1]` on the Y axis). See §1 rule 2. If a future component appears to need
bounce, that is a signal the interaction is wrong, not the curve.

**Do not use bare `ease`, `ease-in-out`, or `linear`** for interactive transitions. Two shipping
rules currently use bare `ease` (`.card-hover`, `.ls-divider`) — see §8 drift.

---

## 5. Springs

**This system intentionally defines no spring presets.**

Springs model momentum carried over from direct manipulation, and this app has exactly one
direct-manipulation surface: the split-pane divider (`js/split-pane.js`), which tracks the pointer
1:1 while dragging. Adding a spring there would introduce lag between cursor and divider — strictly
worse. Everywhere else, motion is triggered *indirectly* (a click opens a modal, data arrives over
gRPC), which is the easing-curve case by definition.

**If** a FLIP-style reorder animation is later added to the `PriceSourcesAdmin` drag-to-rank list —
today slot changes are instant, and the list is re-keyed rather than transformed — use a single
critically-damped curve rather than introducing a spring library:

```css
/* Snappy, no overshoot. Approximates stiffness 500 / damping 40 / mass 0.8. */
--ease-settle: cubic-bezier(0.22, 1, 0.36, 1);
```

WebView2's Chromium supports the CSS `linear()` easing function if a true spring curve is ever
required, but reach for it only with a concrete need — a cubic-bezier is cheaper to read and reason
about.

---

## 6. Transition Patterns

All snippets are CSS. Blazor conditionally renders these elements (`@if`), so entrance animations use
`animation` (fires on mount) rather than `transition` (needs a pre-existing state to change from).

### Panel Crossfade — *Coded and wired (§10) — not confirmed in UI*

Tab switching. Non-directional per §2. The scale is deliberately tiny (0.995) — enough to feel like
the panel settles rather than pops, small enough that dense text never appears to blur.

```css
@keyframes panel-enter {
  from { opacity: 0; transform: scale(0.995); }
  to   { opacity: 1; transform: scale(1); }
}
.panel-enter { animation: panel-enter var(--dur-default) var(--ease-out-expo); }
```

### Overlay Rise — *Coded and wired (§10) — not confirmed in UI*

`SettingsModal`, `ProviderEditDialog`. The backdrop fades while the panel rises — two durations, one
gesture. Exit shrinks slightly and fades fast; it does **not** retrace the entrance path downward.

New windows get this for free by reusing the System Settings shell — the `.overlay-backdrop` /
`.overlay-panel` class pair *is* the animation hook, so a window that hand-rolls its own backdrop
markup silently opts out of it. See [`DESIGN.md`](DESIGN.md) §4.1.

```css
@keyframes overlay-backdrop-enter { from { opacity: 0; } to { opacity: 1; } }
@keyframes overlay-panel-enter {
  from { opacity: 0; transform: translateY(8px) scale(0.96); }
  to   { opacity: 1; transform: translateY(0)   scale(1); }
}
.overlay-backdrop { animation: overlay-backdrop-enter var(--dur-default) var(--ease-standard); }
.overlay-panel    { animation: overlay-panel-enter    var(--dur-slow)    var(--ease-out-quart); }
```

### Row Enter — *Coded and wired on `ConversationCard` (§10) — not confirmed in UI; still Proposed on `TurnCard`*

A new conversation or turn arriving from the live gRPC stream. Rows prepend (newest first), so the
row moves *down* into place from `-4px`.

**Live arrivals are never staggered.** Stagger implies a batch the operator is scanning for the first
time; telemetry arrives one row at a time, already in motion. Staggering it would make a busy stream
look like a slot machine. Stagger applies only on initial mount — see §7.

```css
@keyframes row-enter {
  from { opacity: 0; transform: translateY(-4px); }
  to   { opacity: 1; transform: translateY(0); }
}
.row-enter { animation: row-enter var(--dur-default) var(--ease-out-expo); }
```

### Tooltip Fade — *Shipping*

Opacity only, per the §2 floating-layer constraint. Already correct in `app.css`; hidden via
`opacity` rather than `display:none` so `aria-describedby` survives.

```css
.ls-tooltip { opacity: 0; transition: opacity var(--dur-instant) var(--ease-standard); }
.ls-tooltip.visible { opacity: 1; }
```

### Value Tick — *Proposed*

A numeric stat whose value just changed. Colour only — **no transform, no layout change, no
count-up** (§1 rule 3). JetBrains Mono is tabular, so the digits will not reflow; the operator can
read the true value throughout.

```css
@keyframes value-tick {
  0%   { color: var(--accent); }   /* accent, #1ed760 as of aspirational-design adoption */
  100% { color: inherit; }
}
.value-tick { animation: value-tick 400ms var(--ease-out-quart); }
```

### Disclosure Expand — *Coded and wired (§10) — not confirmed in UI*

`TurnCard` expanding to show the routing-decision log. Animate `opacity` and `transform` on the
revealed content; do **not** animate `height` — the payload blocks are variable-height and
height animation forces layout on every frame in a WebView.

```css
@keyframes disclosure-enter {
  from { opacity: 0; transform: translateY(-2px); }
  to   { opacity: 1; transform: translateY(0); }
}
.disclosure-enter { animation: disclosure-enter var(--dur-default) var(--ease-out-expo); }
```

---

## 7. Ambient & Stagger

### Ambient loops — *Shipping, do not change*

The only motion in the app not caused by a user action. Both are load-bearing signals.

| Class | Animation | Means |
|---|---|---|
| `.pulse-dot` | `pulse-dot 2s ease-in-out infinite` (opacity 1 → .4) | Stream is live |
| `.flash-amber` | `flash-border 1.2s ease-in-out 3` | Crossed 80% of budget |
| `.flash-red` | `flash-border-red 1.2s ease-in-out 3` | Crossed 100% — routing now skips this provider |

The 3-iteration count matters: it terminates. An indefinite alarm becomes wallpaper and stops being
read.

### Stagger — *Proposed*

Stagger applies **only on initial mount of a list** (tab entry, first data load), never to live
appends. Order by importance where the list has a meaningful rank; otherwise order top-down to match
reading direction.

| Surface | Motion | Delay | Order carries |
|---|---|---|---|
| Conversation list (initial) | Row Enter | `min(i, 8) * 30ms` | Recency — newest first, top-down |
| Provider / model card grid | Row Enter | `min(i, 8) * 30ms` | Scan order, not rank |
| Routing-decision steps | Disclosure Expand | `i * 25ms` | **Causal sequence** — step order is the meaning |

**Cap the index at 8.** A 200-row conversation list must not leave the last row arriving six seconds
late. After the eighth item everything lands together.

**The stat strip never staggers.** `TurnCard` / `ConversationCard` pack 8 stats into a two-line strip
designed to be scanned as one unit (`DESIGN.md` §1). Staggering them would defeat the density the
layout exists to provide.

Blazor sets the per-item delay via an inline custom property on the loop index:

```razor
<div class="row-enter" style="--i:@Math.Min(i, 8)">
```
```css
.row-enter { animation-delay: calc(var(--i, 0) * 30ms); animation-fill-mode: backwards; }
```

`animation-fill-mode: backwards` is required — without it, delayed items render at full opacity
before their animation starts, producing a visible flash.

---

## 8. Reduced Motion

Non-negotiable, and cheap because the system is small. Operators run this on a second monitor for
hours; vestibular safety and "stop distracting me" are the same requirement here.

Opacity transitions survive (they carry the *what changed* signal). Transforms, stagger, and the
ambient loops do not.

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
    scroll-behavior: auto !important;
  }
  /* Keep the meaning, drop the motion: threshold alerts become a static ring. */
  .flash-amber { box-shadow: 0 0 0 2px #f59e0b; }
  .flash-red   { box-shadow: 0 0 0 2px #ef4444; }
  /* Liveness stays legible as a steady dot rather than a breathing one. */
  .pulse-dot   { opacity: 1; }
}
```

Note the `.flash-*` and `.pulse-dot` overrides: blanking the animation alone would silently delete a
*status signal*, not just an effect. Reduced motion must degrade to a static equivalent, never to
nothing.

---

## 9. Token Definitions

Add to `app.css` below the compiled Tailwind blob:

```css
:root {
  --dur-instant:  100ms;
  --dur-fast:     150ms;
  --dur-default:  200ms;
  --dur-slow:     300ms;
  --dur-exit:     120ms;
  --dur-entrance: 420ms;

  --ease-standard:     cubic-bezier(.4, 0, .2, 1);
  --ease-out-expo:     cubic-bezier(.16, 1, .3, 1);
  --ease-out-quart:    cubic-bezier(.25, 1, .5, 1);
  --ease-in-quart:     cubic-bezier(.5, 0, .75, 0);
  --ease-in-out-sine:  cubic-bezier(.37, 0, .63, 1);
}
```

`--dur-entrance` is now defined in `app.css` alongside the rest (§3 explains why it's unused today).

---

## 10. Implementation Status

### Landed

| Item | Where |
|---|---|
| Duration + easing tokens | `app.css` `:root` |
| Bare `ease` → `var(--ease-standard)` | `.card-hover`, `.ls-divider`, input focus |
| `.tab-indicator` actually applied (was dead CSS) | `Dashboard.razor` nav buttons |
| `prefers-reduced-motion` block | `app.css` |

### Coded and wired — not confirmed in UI

Source review confirms these are correctly wired (right CSS, right Blazor markup, no conflicting
rules found), but nobody has visually confirmed them running in the actual app. Possible reasons a
build wouldn't show them: a stale build/install predating the CSS, or Windows' "Show animations"
accessibility setting forcing `prefers-reduced-motion: reduce` (collapses all durations to 0.01ms).

| Item | Where |
|---|---|
| Panel Crossfade on tab switch | `Dashboard.razor` — `@key`-ed wrapper in `<main>` |
| Overlay Rise | `SettingsModal`, `ProviderEditDialog` |
| Row Enter + first-mount stagger | `ConversationCard` via `LiveStream._listHasRendered` |
| Disclosure Expand + causal-order stagger | `TurnCard` routing-decision log |

### Not yet implemented

| Item | Why it's deferred |
|---|---|
| **Value Tick** (§6) | Needs per-stat previous-value tracking to fire on. The CSS is intentionally *not* in `app.css` — unused rules rot, as `.tab-indicator` did. Add rule and wiring together. |
| **Row Enter on `TurnCard`** | Turn lists re-render on selection change; entrance animation there needs the same first-mount gate `LiveStream` uses for conversations, or every conversation switch replays it. |
| **Grid stagger on provider/model cards** (§7) | Straightforward; not yet applied. |

### Notes for future work

The compiled Tailwind blob at the top of `app.css` still contains literal durations
(`.15s`, `.2s`). Those rules are re-pointed at tokens by the override block at the bottom of the
file rather than edited in place, because the blob is generated output. If a Tailwind build step is
ever added, move the overrides into the source config instead.

