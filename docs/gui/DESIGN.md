# TotallyHotArcRouter.Gui Design System

This is the authoritative design-system reference for `TotallyHot.ArcRouter.Gui`. It codifies the app's
**existing, shipping** visual identity — every value below is pulled from
[`wwwroot/css/app.css`](../../src/TotallyHotArcRouter.Gui/wwwroot/css/app.css) and the behavior described in
[`dashboard.md`](dashboard.md) — it is not a redesign proposal. Motion — durations, easing, entrance/exit patterns — lives in its
companion [`MOTION.md`](MOTION.md), which *is* prescriptive. For component-level specs, see
[`cost-analytics-visualization-spec.md`](cost-analytics-visualization-spec.md),
[`governance-model-cards.md`](governance-model-cards.md),
[`provider-management.md`](provider-management.md),
[`secret-field.md`](secret-field.md), and
[`livestream-redesign-plan.md`](livestream-redesign-plan.md).

> **Status: aspirational design adopted.** As of [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md)
> Phase 2/3, this document has been updated to reflect the near-black + Dark Green (`#1ed760`) visual
> identity from [`aspirational-design.md`](aspirational-design.md), which now supersedes the previous
> slate-900/sky-400 identity described in earlier revisions of this file. See §9 for the one deliberate
> deviation from the aspirational spec's literal font requirement (CircularSp licensing) and §4.2 for the
> one deliberate deviation on button geometry (dense per-row icon actions stay square, not pill/circular).

## 1. Design Principles

- **Data density without claustrophobia** — `ConversationSummary`'s pinned aggregate strip (Total
  Cost, Total Tokens, Avg ROI, Turns, Trend) and `ConversationCard`'s per-session summary pack several
  stats into a compact card, and every stat gets consistent `gap-*`/`px-*`/`py-*` spacing and a
  `.ls-stat-label` (10px, uppercase, muted `#64748b`) so the strip scans instead of blurring together.
  (`TurnCard`'s denser 8-stat strip — ROI, Cost, Tok P/C, Steps, Cache, TTFT, Ctx, Model — demonstrated
  this most aggressively, but the component is orphaned since the Sessions-tab rebuild: see the
  Progressive disclosure bullet below.)
- **Hierarchy through weight and color tier, not decoration** — text hierarchy comes from the
  `font-bold` / `font-semibold` / `font-medium` steps and the five-tier slate text-color ladder
  (§3), never from box shadows, gradients, or size alone. Agent identity comes from a deterministic
  per-agent color (tinted card border/background), not icons or badges.
- **Motion as meaning** — animation exists to mark a state change, not to decorate: `.pulse-dot`
  breathes on the `LIVE` indicator and the header status dot; `.flash-amber`/`.flash-red` briefly ring
  a card when it crosses a budget threshold; `.card-hover`/`.transition-colors` are 150ms
  `cubic-bezier(.4,0,.2,1)` — fast enough to register as feedback, not a performance. See
  [`MOTION.md`](MOTION.md) for the full duration/easing token set and entrance patterns.
- **Trust through restraint** — one accent (Dark Green `#1ed760`) plus three semantic colors (`emerald`/
  `amber`/`red`) on a near-black base is the entire palette. No additional brand hues; no gradients
  anywhere in `app.css`.
- **Progressive disclosure** — `ConversationSummary` shows one pinned stat strip; double-clicking a
  `ConversationCard` opens the Sessions tab's split view, replacing the full-width session list with
  session details (left) and `SessionConversationPane`'s chat-style reproduction of the conversation
  (right). Cost Analytics shows one chart at a time behind a ranked metric-picker pill row, not seven
  charts at once. (`TurnCard`'s in-place click-to-expand routing-decision log was the previous
  disclosure mechanism for turn detail; it is orphaned — unreferenced by any component — since this
  double-click split view replaced it, and does not render anywhere today.)

## 2. Visual Theme

Dark theme only — there is no light mode and no theme toggle (`dashboard.md` §Visual theme).

| Role | Value | CSS token | Tailwind class equivalent |
| --- | --- | --- | --- |
| Page background | `#121212` | `--surface-base` | `html,body,#root` (overridden) |
| Card / header / nav surface | `#181818` | `--surface-card` | `.bg-slate-700`\* / `bg-slate-800` markup, recolored |
| Interactive surface (inputs, buttons) | `#1f1f1f` | `--surface-interactive` | n/a — inline style |
| Elevated card (secondary) | `#252525` / `#272727` | `--surface-elevated-a` / `-b` | `.card-hover:hover` (recolored) |
| Border (default) | `#4d4d4d` | `--border-button` | `.border-slate-700` (recolored) |
| Border (hover / emphasis) | `#7c7c7c` | `--border-light` | `.hover\:border-slate-500` (recolored) |
| Text — primary | `#ffffff` | `--text-primary` | `.text-slate-200`/`.text-slate-100` (recolored) |
| Text — secondary | `#b3b3b3` | `--text-secondary` | `.text-slate-400`/`.text-slate-500` (recolored) |
| Text — bright secondary | `#cbcbcb` | `--text-bright-secondary` | `.text-slate-300` (recolored) |
| Text — muted | `#7c7c7c` | `--text-muted` | `.text-slate-600` (recolored) |
| Accent (action, active state, focus ring) | `#1ed760` | `--accent` | `.text-sky-400` (recolored) |
| Accent variant (hover/outline) | `#1db954` | `--accent-variant` | n/a — inline style |
| Positive / savings / healthy | `#10b981` (fill), `#34d399` text | `--color-success` | `.text-emerald-400` |
| Warning / degraded | `#f59e0b` (fill), `#fbbf24` text | `--color-warning` | `.text-amber-400` |
| Critical / failed | `#ef4444` (fill), `#f87171` text | `--color-critical` | `.text-red-400` |
| Inset surface — recessed | `#172033` | n/a | ticker row, active metric-picker pill |
| Inset surface — payload block | `#020617` | n/a | *Currently unused* — was `TurnCard`'s request/response `<pre>` blocks (`.ds-code-block`); orphaned since the Sessions-tab rebuild, no live component renders this surface |
| Inset surface — console | `#0b1120` | `--surface-inset` | `ConsoleTab` log surface |

\* `.bg-slate-700` is the one compiled-blob utility class actually applied to card-like surfaces in
markup; `bg-slate-800` never appears as a literal class (card surfaces are set via inline
`style="background:#181818"` instead — see the note in §7 about the compiled Tailwind blob).

**The accent swapped from sky-400 (`#38bdf8`) to Dark Green (`#1ed760`)** as part of aspirational-design
adoption — every reference to the old accent used as UI chrome (active tab text, focus rings, the Stop
icon-action color, tooltip focus outline) now points at `--accent`. Semantic colors (success/warning/
critical) are unchanged — they already matched the aspirational spec before adoption. Sky's two
data-encoding roles — the `TurnCard` stat strip's Cost/Model hue and its routing-step "Info" tone — are
both dormant now that `TurnCard` is orphaned (see §1's Progressive disclosure bullet); see the note in
the next section.

Inset surfaces sit *below* the page background rather than above it — they mark a region as a
well (raw data, logs) rather than a card. They are the inverse of elevation: no border-lightening,
no shadow, just a darker fill.

### Data-encoding palettes

The three-color semantic palette above governs *chrome*. One additional palette encodes *data*
(below); it is deliberately broader — collapsing it into the chrome palette would destroy the
encoding.

**Stat-strip categorical hues** — *currently unused.* Each stat in `TurnCard`'s 8-stat strip carried
its own hue so a specific metric could be found by color in a dense two-line strip, without reading
labels. `TurnCard` is orphaned since the Sessions-tab rebuild (§1), so this palette renders nowhere
today; the CSS (`.stat-color-tokens`, `.stat-color-ttft`, and the inline sky/emerald/amber spans in
`TurnCard.razor`) is left in place rather than deleted in case a future dense turn view revives it:

| Stat | Color | | Stat | Color |
| --- | --- | --- | --- | --- |
| ROI, Cache | `#10b981` emerald (zero → `#64748b`) | | Steps | `#f59e0b` amber |
| Cost, Model | `#38bdf8` sky (fallback model → amber) | | TTFT | `#fb7185` rose |
| Tok P/C | `#a78bfa` violet | | Ctx | `#cbd5e1` slate-300 |

Rose and violet appeared **only** here, and appear nowhere in the app today. They were categorical
labels, not brand colors — do not repurpose them for chrome, borders, or status if this palette is
ever revived. Sky (`#38bdf8`) was likewise preserved there as the Cost/Model categorical hue distinct
from the retired chrome accent (§2); that reasoning still applies if the strip returns.

**Text on tinted semantic surfaces** — wherever a semantic color tints a background, the text steps
one shade lighter (`-300`) for contrast against that tint. The pattern is always
`background:<hue>11` + `border:<hue>44` + `-300` text:

| Context | Background | Border | Text |
| --- | --- | --- | --- |
| Error banners, failed pulls (`.ds-step-critical`, live: `ProviderEditDialog`, `ProvidersAdmin`) | `#ef444411` | `#ef444444` | `#fca5a5` red-300 |
| Routing step — Warn (`.ds-step-warning`) *currently unused* | `rgba(245,158,11,0.12)` | `#f59e0b` (left, 2px) | `#fcd34d` amber-300 |
| Routing step — Info (`.ds-step-info`) *currently unused* | `rgba(56,189,248,0.1)` | `#38bdf8` (left, 2px) | `#7dd3fc` sky-300 |
| Routing step — OK (`.ds-step-success`) *currently unused* | `rgba(16,185,129,0.08)` | `#10b981` (left, 2px) | `#6ee7b7` emerald-300 |

The three "Routing step" rows were `TurnCard`'s routing-decision log tones. That log doesn't render
anywhere today — `TurnCard` is orphaned since the Sessions-tab rebuild (§1) — but the `.ds-step-*`
classes remain in `app.css` (§7 keeps `.ds-step-critical` alive for error banners) so the palette is
ready if a routing-decision view returns.

Fonts: `var(--font-ds)` (`"Century Gothic", "Avenir Next", "Poppins", Inter, system-ui, sans-serif`) for
all UI text, **JetBrains Mono** for every numeric/monospace value (token counts, costs, timestamps,
session/trace IDs) — set globally via `.font-mono`.

**On CircularSp:** `aspirational-design.md` specifies CircularSp (Circular by Lineto), which is
Spotify's proprietary font and cannot legally be bundled with this app. `--font-ds` substitutes a stack
of freely-available geometric sans faces with similar proportions, falling back through Inter (the
previous UI font) to `system-ui` so text never breaks on a machine with none of the geometric faces
installed. If a properly-licensed geometric webfont is embedded later, swap the `--font-ds` value in
`app.css` — no markup changes needed, every UI text element already reads through that token.

**Button labels are uppercase** with `1.4px` letter-spacing and `700` weight (§4) — the systematic
"label" voice `aspirational-design.md` calls for, layered on top of the existing bold/regular binary
rather than replacing it.

### Status semantics (OK / WARNING / CRITICAL)

The same three-state model drives both Governance sub-views:

- **OK** (< 80% of budget/cap) — emerald
- **WARNING** (≥ 80%, < 100%) — amber, `.flash-amber` on crossing
- **CRITICAL** (≥ 100%) — red, `.flash-red` on crossing, breached providers are skipped in routing

This is the same threshold logic used by the header status banner (`dashboard.md` §Header) and the
per-provider budget utilization bars.

## 3. Typography Scale

Compact, functional range (10px–18px) — this is a telemetry app, not a document reader. Hierarchy is
built from a bold/regular binary with `font-semibold` used sparingly, matching the weight steps that
actually appear in `app.css`.

| Role | Size | Weight | Tracking | Example |
| --- | --- | --- | --- | --- |
| Headline figure | `text-lg` (18px), mono | `font-semibold` | normal | Cost Analytics headline |
| Window / modal title | `text-sm` (14px) | `font-semibold` | `tracking-wide`, uppercase | "System Settings" (§4.1) |
| Stat value (numeric) | `text-sm`/`text-lg`, mono | `font-bold` | normal | Ticker stats, turn cost |
| Body / labels | `text-sm` (14px) | `font-medium` | normal | Card titles, nav labels |
| Stat-strip label | 10px (`.ls-stat-label`) | 400 | `0.05em`, uppercase | "ROI", "COST", "TTFT" |
| Small / caption | `text-xs` (12px) | `font-medium`/400 | `tracking-wide` | Badge text, timestamps |
| Tooltip body | 11px (`.ls-tooltip`) | 400 | normal, `line-height:1.4` | Metric explanations |

`tracking-widest` (0.1em) is reserved for the rare all-caps section label; most uppercase text uses
`tracking-wide` (0.025em), matching `.ls-stat-label`'s 0.05em.

## 4. Components

- **Cards** (`ConversationCard`, ticker stat cards) — `--surface-card` (`#181818`)
  background, `border-color: var(--border-button)` (1px), `rounded-lg` (8px, `.5rem`, matching
  `--radius-card`) corners, `.card-hover`/`.ds-card:hover` for interactive cards (background shift to
  `--surface-elevated-a` and/or `--shadow-elevated` over 150ms). Agent identity and fallback state are
  communicated by tinting the card's left border and background with the agent/alert color rather
  than adding an icon.
- **Badges / pills** — `rounded-full` (`.rounded-full`, pill shape) is used for status dots and small
  count/label chips (e.g. the agent-color dot, fallback `⚠` badge); larger containers use `rounded`
  (4px) or `rounded-lg` (8px). The ranked metric-picker pill row in Cost Analytics follows the same
  `rounded-full` pattern.
- **Status bars** — `.progress-bar-track` (`background:#1f1f1f`, `rounded` 2px, 6px tall) hosts the
  OK/WARNING/CRITICAL utilization fill for provider budgets and price-source caps. The same track class
  also hosts a plain `.progress-bar-fill` (`background:var(--accent)`, width set inline as a percentage)
  for a determinate transfer progress bar — the established pattern for any multi-file transfer.
  Governance › Benchmark Data's Task Matrix and Local Voter Model cards each stack two of these while
  their own sync runs: a cumulative bar over the whole update, and beneath it a per-file bar labelled
  with the file currently downloading. Both bars are rendered only for the sync's duration.
- **Card action buttons** (the glyph row in a Governance › Providers card header) — a 32×32 square
  (`rounded p-1.5` around a 20px `Icon`), `rounded` 4px, deliberately **not** pill/circular, 150ms
  `transition-colors`. Edit stays on the slate ramp via Tailwind utilities (recolored to the new
  neutral tokens); the semantic ones live in `.ls-card-action-*` because their hexes are off the
  utility palette: Stop `var(--accent)` (`#1ed760`), Play `#10b981`, Remove `#dc2626`. Remove is the
  card's only destructive glyph — a second, separate delete control is not the pattern — and it opens
  a type-to-confirm dialog (`RemoveProviderDialog`, built on the §4.1 shell) rather than acting on the
  click. **These dense per-row action buttons are the one deliberate exception to §4.2's pill/circular
  button geometry** — see §4.2 for why. See §4.3 for the icon standard behind every `Icon` referenced
  in this document.
- **Inputs** (Live Stream conversation search, form fields) — `background: var(--surface-interactive)`
  (`#1f1f1f`), `border: 1px solid var(--border-light)`, `color: var(--text-primary)`; on focus the
  border becomes the accent `var(--accent)` with a green focus ring (150ms `border-color` transition).
  No pill inputs — search/text inputs are square-cornered via the shared input rule, not `.ls-*`.
- **Secret fields** (`SecretField.razor`, the Custom Headers value boxes) — a shared input with a
  padlock toggle inside its right edge (`.ds-secret-field` / `.ds-secret-toggle*`; hand-authored
  because the compiled blob has no `right-*`/`pr-7` utilities, §5.1). Unlocked it is an ordinary text
  box with a muted open padlock; locked it renders as a password box with a `--color-warning-text`
  closed padlock; armed for unlock it turns `--color-critical-text`, matching every other destructive
  control. Full contract, including why unlocking clears the value: [`secret-field.md`](secret-field.md).
- **Navigation** (5-tab bar: Live Stream / Cost Analytics / Model Distribution / Governance /
  Console) — the selected tab reads as a folder tab continuous with its panel: `var(--accent)`
  text, a `var(--surface-base)` fill, a `var(--border-button)` border on three sides, `6px 6px 0 0`
  radius, and a bottom edge painted `var(--surface-base)` that hides `.ds-toolbar`'s
  `border-bottom` underneath it. Inactive tabs are `var(--text-muted)` on a transparent fill.
  **That border and the `-1px` bottom margin are declared on the base `.tab-button` rule, in
  `transparent`, so both states occupy identical space** — sizing them per-state made the selected
  tab 2px larger than its peers and reflowed the tab row (and the `flex-1` `<main>` below it) on
  every click. `.tab-button`/`.tab-indicator` therefore transition only `color`,
  `background-color`, and `border-color`, over `--dur-default` `--ease-standard`; never `all`,
  which would re-admit layout properties. The tab bar itself is navigation, not a CTA, so it is
  exempt from the pill/circular button geometry in §4.2.
- **Tooltips** (`.ls-tooltip`, shared floating tooltip driven by `data-tip` + `tooltips.js`) —
  `var(--surface-card)` background, `border: 1px solid var(--border-light)`, `rounded` (4px),
  `box-shadow: var(--shadow-lift)` — one of two heavy shadow values in the whole stylesheet, reserved
  for floating/elevated UI (§6). Focus-visible state: `outline: 2px solid var(--accent);
  outline-offset: 2px`.
- **Modals / windows** — panel on `var(--surface-card)` (`#181818`), `border-color:
  var(--border-button)`, backdrop blur, elevated with `var(--shadow-dialog)`
  (`0 8px 24px rgba(0,0,0,0.5)`). **`DialogShell.razor` is the shared shell implementation every new
  window builds on** — see §4.1.

### 4.2 Buttons: Primary / Secondary / Ghost / Circular Action

Per `aspirational-design.md` §4, explicit call-to-action buttons (Save, Cancel, Confirm, Add Provider)
use pill or circular geometry — `border-radius: var(--radius-pill)` (`9999px`) for rectangular CTAs,
`50%` for standalone circular quick-actions — via the shared `.btn`/`.btn-primary`/`.btn-secondary`/
`.btn-ghost`/`.btn-critical`/`.btn-circular` classes in `app.css`:

| Class | Look | Use |
| --- | --- | --- |
| `.btn-primary` | `--surface-interactive` bg, `--accent` text, uppercase 700 weight | Save, primary confirm |
| `.btn-secondary` | Transparent, 1.5px `--accent` border/text | Secondary actions, "Add header" |
| `.btn-ghost` | Transparent, `--border-light` border, white text | Cancel, tertiary actions |
| `.btn-critical` | Solid `--color-critical` bg | Destructive confirm (Purge, Remove) |
| `.btn-circular` | 48px, 50% radius, `--accent` bg | Standalone circular quick-action |

All are `min-height: 44px` (touch target), uppercase with `1.4px` letter-spacing, `700` weight, and
`transform: scale(0.98)` on `:active` for tactile press feedback, matching the aspirational spec's
button motion (§4 of `aspirational-design.md`).

**Deliberate exception: dense per-row icon actions stay square, not pill/circular.** The 32×32
Edit/Stop/Play/Remove glyphs in a Governance › Providers card row (`.ls-card-action*`, §4 above) are
NOT converted to `.btn-circular` even though the aspirational spec's circular-button use case ("quick
actions") could describe them. Reason: "Data density without claustrophobia" is one of the five
adopted principles, and a 48px circular button in a compact multi-row card list would blow out the
row height and contradict that principle for the sake of literal geometry conformance. Pill/circular
geometry is reserved for explicit, standalone CTA buttons — the ones §4.2's table covers — not for
dense repeated row actions.

### 4.1 New windows follow the System Settings pattern

Any new modal, dialog, or window builds on
[`DialogShell.razor`](../../src/TotallyHotArcRouter.Gui/Components/DialogShell.razor) rather than
inventing its own chrome. `DialogShell` is the extracted, shared implementation of the shell every
dialog in this app already followed by convention (`SettingsModal.razor` "System Settings",
`ProviderEditDialog.razor`, `RemoveProviderDialog.razor`, `UnlockSecretFieldDialog.razor`) - that
consistency is the point, and `DialogShell` is what makes it structural instead of copy-pasted.

Usage:

```razor
<DialogShell Title="Window Title"
             CloseAriaLabel="Close window title"
             OnClose="OnClose">
    @* body *@
</DialogShell>
```

`DialogShell`'s parameters:

| Parameter | Contract |
| --- | --- |
| `Title` (required) | The uppercase header title. |
| `CloseAriaLabel` (required) | The close glyph's accessible name - the button's only content is an SVG, so without one a screen reader announces an unnamed button. |
| `OnClose` (required) | `EventCallback` invoked by the backdrop click, the close glyph, and (when `EnableEscapeToClose` is set) Escape. The shell never closes itself - the caller decides what closing means. A caller whose own public parameter is named `OnCancel` (matching `docs/gui/DESIGN.md`'s historical either/or) wires it straight through: `OnClose="OnCancel"`. |
| `ChildContent` (required) | The body, rendered inside the scrollable `.overlay-content` wrapper. |
| `ContentClass` (optional, default `overlay-content p-5 space-y-4`) | The body wrapper's classes - override the `space-y-*` gap for a denser layout (`ProviderEditDialog` uses `overlay-content p-5 space-y-3`) while keeping the `overlay-content p-5` base. |
| `EnableEscapeToClose` (optional, default `false`) | Whether Escape closes the dialog, bound on the panel (not the body) so it fires regardless of which descendant has focus. Off by default - only opt in for a dialog that genuinely wants it (`RemoveProviderDialog`, `UnlockSecretFieldDialog` do; `SettingsModal`, `ProviderEditDialog` don't). |

What `DialogShell` renders, and the contract behind it:

| Element | Contract |
| --- | --- |
| Backdrop | `.overlay-backdrop` (`rgba(0,0,0,0.7)` + `backdrop-filter:blur(4px)`, both from the CSS class - no inline `style`), `z-50`, centers its panel. Backdrop click invokes `OnClose` |
| Dismissal | Backdrop click closes; panel carries `@onclick:stopPropagation="true"` so body clicks don't |
| Panel | `.overlay-panel w-full max-w-md rounded-lg border border-slate-700`. `.overlay-panel`'s own CSS supplies `background: var(--surface-card)` (`#181818`) and the **dynamic sizing**: max-width `min(90vw, 700px)`, min-width `min(100% - 2rem, 400px)`, max-height `calc(100vh - 120px)`, `display: flex; flex-direction: column` |
| Header | `px-5 py-4`, `border-b border-slate-700`, title left / close `x` right. **Always stays fixed** during content scroll |
| Title | `text-sm font-semibold text-slate-200 tracking-wide uppercase` — **not** a large heading |
| Close glyph | `<Icon Name="x" Size="20" />`, `slate-400`→`slate-200` on hover, 150ms `transition-colors`, labeled by `CloseAriaLabel` |
| Content area | The `ChildContent` you pass is wrapped in `.overlay-content` (classes from `ContentClass`) automatically, enabling vertical scrolling when content exceeds `max-height` while keeping the header fixed |
| Body | Primary/secondary/destructive action buttons in the body use `.btn-*` (§4.2), not ad hoc `rounded` + inline-color styling. **No horizontal overflow**: if fields risk wrapping, stack them vertically rather than shrinking |

`.overlay-backdrop`/`.overlay-panel` also supply the entrance animation for free (see
[`MOTION.md`](MOTION.md) §Overlay Rise); a window that hand-rolls its backdrop loses it.

**Modal Content Sizing Requirements:**
- Every modal **must dynamically adjust to fit its content** with no horizontal overflow - `DialogShell`'s `.overlay-content` wrapper handles this for free.
- Modals grow to fit content up to `max-width: min(90vw, 700px)` and `max-height: calc(100vh - 120px)` — staying within the viewport with breathing room.
- Only the content area scrolls vertically; the header bar (title + close button) and action buttons remain fixed and visible.
- **Never use horizontal scroll** in modals. If fields risk wrapping, stack them vertically (e.g., custom headers in ProviderEditDialog).
- Exception: If content genuinely requires more space (e.g., a data table), widen the panel via CSS override in `app.css`, not inline `max-w-*` — but scrolling behavior remains unchanged.

Deviate only where the content genuinely demands it — a wider panel for a table, say — and keep
the header, dismissal behavior, close API, and dynamic scrolling identical regardless.

### 4.3 Icons

**Standard: [Heroicons](https://heroicons.com/) Solid, 24×24, MIT-licensed
(`github.com/tailwindlabs/heroicons`, `optimized/24/solid/`).** Every glyph in `Icon.razor` is
embedded verbatim as Heroicons' own `<path fill-rule="evenodd" d="..." clip-rule="evenodd"/>` data —
`fill="currentColor" stroke="none"` on the shared `<svg>` wrapper, no hand-drawn stroke primitives.
This replaced an earlier ad hoc set of 27 hand-drawn stroke icons that had drifted into real bugs: two
call sites referenced icon names (`trash-2`, `bot`) that didn't exist in the switch and silently
rendered nothing, and `trash`/`delete` had become two different glyphs for the same "remove this"
meaning. Heroicons closes both gaps and gives future icon additions a canonical name to look up
instead of inventing a new hand-drawn primitive.

**Heroicons also ships a "Mini" variant** (20×20, meant for icons under 20px) alongside Solid. **This
app is Solid-only for now** — Mini is a deliberate future pass, not adopted here. Don't "fix" the
16px dense-inline tier below into Mini without revisiting this section first.

**Size convention — three tiers:**

| Tier | Size | Where | Why |
|---|---|---|---|
| Default | **20px** | Tab bar, card header action rows, alerts/badges, modal close glyphs, search, settings — the large majority of call sites | Heroicons' own Solid-vs-Mini threshold |
| Dense-inline exception | **16px** | `ProvidersAdmin.razor`'s nested per-model row Stop/Play/Remove icons (~20-24px row height) (`TurnCard.razor`'s inline step icons used this tier too, but the component is orphaned — see §1 — and no longer renders) | Jumping straight to 20px would visually dominate rows built around "data density without claustrophobia" (§1) |
| Unchanged | **12–14px** | `grip-vertical` (`PriceSourcesAdmin.razor`'s drag handle, §5.3) | Hand-drawn, not a Heroicons glyph — not subject to the Solid-threshold rationale |

**The one hand-drawn exception: `grip-vertical`.** Heroicons ships no drag-handle glyph — the nearest
candidates, `ellipsis-vertical` and `bars-3`, both read as a "more options" menu trigger, which would
mislead users about the *drag* affordance §5.3 documents. Two columns of filled dots stays hand-drawn
as the one glyph in the app not sourced from Heroicons.

**Old-name → Heroicons-name reference** (the `Icon.razor` `case` label is the stable name every call
site uses; only the glyph inside changed):

| `Icon` name | Heroicons solid file | Notes |
|---|---|---|
| `activity` | `signal` | Live Stream tab |
| `bar-chart` | `chart-bar` | Cost Analytics tab |
| `trending-up` | `arrow-trending-up` | Model Distribution tab |
| `shield-check` | `shield-check` | Governance tab |
| `search` | `magnifying-glass` | |
| `alert-triangle` | `exclamation-triangle` | most-used glyph in the app |
| `check-circle` | `check-circle` | |
| `clock` | `clock` | |
| `copy` | `square-2-stack` | Heroicons' canonical duplicate/copy glyph |
| `x` | `x-mark` | |
| `trash` | `trash` | absorbs the former `delete` case — one canonical "remove" glyph |
| `terminal` | `command-line` | |
| `plus` | `plus` | |
| `lock` | `lock-closed` | |
| `unlock` | `lock-open` | |
| `server` | `server-stack` | two-rack visual, matching the previous hand-drawn glyph |
| `database` | `circle-stack` | |
| `grip-vertical` | *(hand-drawn — no Heroicons equivalent)* | see above and §5.3 |
| `chevron-up` / `chevron-down` | `chevron-up` / `chevron-down` | |
| `play` | `play` | |
| `stop` | `stop` | |
| `settings` | `cog-6-tooth` | Heroicons v2's canonical settings gear |
| `refresh` | `arrow-path` | |
| `sliders` | `adjustments-horizontal` | "Config" glyph on a provider card |
| `cpu-chip` | `cpu-chip` | routing/voter model row icon (fixed the former undefined `bot` reference) |

### 4.4 Toasts

App-wide error notifications (`ToastHost.razor`, driven by `ToastService`), used when a failure needs to
be visible even though its source component has already re-rendered past the moment it happened - e.g.
`ProvidersAdmin`'s "Refresh from endpoint" succeeding at the HTTP level while the router's own discovery
failed underneath it (an expired API key). One `ToastHost` instance lives in `Dashboard.razor`'s shell, so
a toast is visible regardless of which Governance sub-tab is active.

- **Placement:** fixed, top-center, `top: 88px` (clear of the header/ticker), `z-index: 500` - the toast
  layer reserved above `.overlay-backdrop` (`z-50`), so a toast stays visible even while a modal is open.
- **Surface:** `.ds-surface-card-critical`, matching the "Proxy management API unreachable" banner's
  critical-state styling elsewhere in Governance.
- **Content:** `alert-triangle` icon (amber, `text-amber-400`, 20px), a bold title, a muted message line,
  and an `x` close glyph (16px) - errors only, no success toasts.
- **Stacking:** multiple toasts stack vertically, newest last, each independently dismissible.
- **Dismissal:** auto-dismisses after 6 seconds (`ToastService.AutoDismissAfter`) or on the close glyph,
  whichever comes first. See `MOTION.md` for the enter/exit timing.

## 5. Layout & Spacing

- **Fixed, non-scrolling shell**: `html,body,#root{height:100%;overflow:hidden}` — the whole app is
  `h-screen overflow-hidden`; individual panels scroll internally (`overflow-y-auto`) where their
  content can overflow. There is no page scroll and no responsive breakpoint system — this is a
  single-window Windows desktop app, not a responsive web layout.
- **Spacing scale**: Tailwind's default rem scale as used in `app.css` — `0`, `0.125rem` (0.5),
  `0.25rem` (1), `0.375rem` (1.5), `0.5rem` (2), `0.625rem` (2.5), `0.75rem` (3), `1rem` (4),
  `1.25rem` (5), `1.5rem` (6).
- **Live Stream split pane** — draggable divider between the conversation list and turn detail
  panels (`wwwroot/js/split-pane.js`); left panel defaults to 35% width (`.ls-left{width:35%}`),
  clamped 20–65% while dragging. The divider itself (`.ls-divider`) is 8px wide with a 4px `rounded`
  hit target and a 2px `rounded` grip mark.
- **Scrollbars**: thin (4px) custom scrollbar, `var(--border-button)` (`#4d4d4d`) thumb on
  `var(--surface-base)` (`#121212`) track, `var(--border-light)` (`#7c7c7c`) on hover.
- **`<main>`'s padding (`Dashboard.razor`) is the app's single content inset, shared by every tab** —
  Live Stream, Cost Analytics, Model Distribution, Governance, Console — at the pre-existing
  `p-3` (`0.75rem` all sides). **Define it in exactly one place** (`<main>` itself) and let every tab
  inherit it; do not give an individual pane its own separate outer margin. This was inflated to a
  percentage-based value at one point specifically to give `PriceSourcesAdmin`'s lifted card room to
  grow — an app-wide layout change made to solve a problem local to one card in one tab — and reverted
  once that card was detached from the layout instead (§5.4, §5.5). Note that this 12px *is* what
  `reorderFlip._place` ends up measuring as the lifted card's grow budget, so changing it changes how
  much that card grows; it measures rather than assumes, so nothing breaks, but the two are linked.

### 5.1 Available Tailwind Spacing Utilities

**Important:** The compiled `app.css` only includes Tailwind utilities that were actually used in the codebase. Many common Tailwind classes are **NOT available**. Always check this list before using a spacing utility; if it's not listed here, create a CSS class instead (see §7).

| Category | Available | NOT Available |
| --- | --- | --- |
| **Margin (all sides)** | `ml-auto` only | `m-0`, `m-1`, `m-2`, `m-3`, etc. |
| **Margin (horizontal)** | None | All `mx-*` variants |
| **Margin (vertical)** | None | All `my-*` variants |
| **Margin (top)** | `mt-0.5`, `mt-1.5`, `mt-2` | `mt-1`, `mt-3`, `mt-4`, `mt-6`, etc. |
| **Margin (bottom)** | `mb-0.5`, `mb-1`, `mb-2`, `mb-3` | `mb-1.5`, `mb-4`, etc. |
| **Margin (left/right)** | `ml-auto`, `pr-1`, `pr-3` | `ml-*`, `mr-*` (except `ml-auto`) |
| **Padding (all sides)** | `p-2`, `p-3`, `p-4`, `p-5` | `p-0`, `p-1`, `p-6`, etc. |
| **Padding (horizontal)** | `px-1.5`, `px-2`, `px-2.5`, `px-3`, `px-4`, `px-5` | `px-1`, `px-6`, etc. |
| **Padding (vertical)** | `py-0.5`, `py-1.5`, `py-2`, `py-2.5`, `py-3`, `py-4` | `py-1`, `py-5`, etc. |
| **Padding (top)** | `pt-1` | Most `pt-*` |
| **Padding (bottom)** | `pb-3` | Most `pb-*` |
| **Gap (flex/grid)** | `gap-0`, `gap-1`, `gap-1.5`, `gap-2`, `gap-2.5`, `gap-3`, `gap-4`, `gap-6` | `gap-5`, etc. |
| **Space-y (flex column)** | `space-y-1`, `space-y-1.5`, `space-y-2`, `space-y-3`, `space-y-4` | `space-y-0`, `space-y-5`, etc. |

**Pattern:** When you need a spacing utility not in this list (e.g., `m-1`, `p-1`, `pt-2`), create a CSS class in `app.css` instead — do NOT use the Tailwind utility hoping it will work. Example:

```css
.my-custom-spacing { margin: 0.25rem; }
```

Then use `class="my-custom-spacing"` in markup. See `.btn-small-action` (added for compact action buttons with margin) as a reference pattern.

### 5.2 Card stack spacing

**The gap between sibling cards in a stacked list is `0.75rem` (12px), everywhere in the app.** It is
defined once, as the `--space-card-gap` token in `app.css`, and every pane derives from it. Do not
write the number into a pane.

| Class | Use when | Example |
| --- | --- | --- |
| `.ds-card-stack` | A container whose children are all cards. **Preferred.** | `PriceSourcesAdmin` — the draggable source list; `ProvidersAdmin` — the provider list |
| `.ds-card-gap` | A card follows another but the two have no shared stack parent to hang the rule on | `BenchmarkData` — the Local Voter Model section |

`BenchmarkData` needs the second form because its two cards live in different `@if` branches that
flatten into the same DOM level, alongside error banners that carry their own `mb-3`; a stack
container would double those banners' spacing rather than only separating the cards.

**Do not express this gap with `space-y-3`/`gap-3`**, even though they compute to the same value.
The token says *why* the number is what it is, and `space-y-3` is a general vertical-rhythm utility
used for unrelated things (modal body blocks in `ProviderEditDialog`, `SettingsModal`, and the
destructive-confirm boxes) — overloading it hides which stacks are card lists.

**Why a token and not a utility class.** This gap was previously `mt-6` on the Benchmark Data pane
and rendered *nothing at all*: per §5.1 the compiled blob ships only the utilities the original mock
used, and neither `mt-6` nor `mt-3` is among them, so the class was inert and the two cards sat flush.
A missing utility fails silently; a missing token does not.

### 5.3 Reorderable cards

**A card that can be dragged into a new position must show a `grip-vertical` grab handle — never a
click-to-move control (chevrons/arrows, "move up"/"move down" buttons).** `PriceSourcesAdmin`'s
Price Data Sources list is the canonical example: each card in `.ds-card-stack` gets a
`grip-vertical` icon (12–14px, `text-slate-600`) at its leading edge as the sole reorder affordance,
titled `"Drag to reorder..."`. There is no separate arrow-button path — the handle isn't a label next
to a different control, it *is* the control, made draggable by `@onpointerdown` on the card itself
(see the remarks below). `grip-vertical` is the one glyph in the app not sourced from Heroicons — see
§4.3 for why.

- **Pointer events, not HTML5 drag-and-drop.** WinUI's WebView2 — the host `BlazorWebView` uses on
  Windows — never delivers in-page `dragstart`/`dragover` events (`microsoft-ui-xaml#10576`), so a
  `draggable="true"` card is inert in this app. Reorder gestures are built on `pointerdown` /
  `pointermove` / `pointerup` instead, the same primitive `js/split-pane.js` already drags the
  split-pane divider with. The move and up listeners live on `document`, not on the card or the list,
  so a drag survives the pointer leaving the list and still ends wherever it is released.
- **Each card sits in a slot.** `.ds-card-slot` is the in-flow layout unit — it carries the card-stack
  gap, it carries `data-flip-key` for the settle, and it holds its row open at a measured height while
  the card inside is detached. The card is what gets picked up; the slot is what stays put.
- **The dragged card follows the cursor, and is detached to do it** — `.card-lifted`: `box-shadow:
  0 12px 28px -12px rgba(0,0,0,.55)` + the `.ds-source-border-lifted` accent border-color, reserved for
  this state only (§6). `js/reorder-flip.js` adds `.card-pinned`, which switches it to `position: fixed`
  tracking the pointer on Y and grows it ~10px per side. It has to leave the flow to do that: the pane
  around the list is a scroll container, and those always clip at their padding box, so an in-flow card
  could never follow a cursor past it (§5.4). The grow is sized in **pixels from the measured rect**,
  never as a percentage — see §5.5. **Y axis only**: these cards are full-width, so horizontal movement
  would carry no meaning.
- **A press becomes a drag only after it travels.** Below a 3px threshold a press is a click. This
  matters because `pointerdown` on the enable/disable toggle bubbles up to the card — lifting on the
  bare press would make every toggle click flicker.
- **The rank is decided from the dragged card's own position, not from what's under the cursor.** JS
  measures every slot once at drag start, then picks the rank whose accumulated offset is nearest the
  card's top edge. This is the variable-height generalisation of the usual `round(y / rowHeight)` — our
  cards differ in height (a failed-pull banner makes one taller), so there is no single row height to
  divide by, and the real heights are accumulated instead.
- **Both the shuffle and the drop animate.** Cards displaced by the drag glide into their new slots via
  the FLIP pass rather than jumping, and on release the card settles into its slot instead of snapping
  there. See `MOTION.md` §6 "Reorder Settle" and "Lift Detach". This is the one place in the app where
  JS reads layout to drive an animation, and it is not a general animation library — it stays scoped to
  this one interaction.
- **JS owns the card's position; Blazor owns the order.** Pointer tracking cannot round-trip through
  interop — at 60–120 events per second the card visibly lags the cursor — so `js/reorder-flip.js`
  moves the card frame to frame and calls back into the component (`DragStarted`, `MoveDraggedTo`,
  `EndDrag`) only when the rank actually changes, a handful of times per drag. The JS never inserts,
  removes, or reorders DOM nodes; it only reads rects and writes inline styles, so Blazor stays the
  single owner of the tree.
- **The detached state is rendered by Blazor *and* set by JS.** `.card-pinned` appears in the Razor
  class expression and is added by `classList` in the same frame the drag starts. Both are required
  and neither is redundant — see §5.5, which explains why a JS-only class is destroyed by the very
  next render and what that costs.
- **`EndDrag` fires after the release animation, not on `pointerup`.** It clears the drag state, and
  that render drops `.card-pinned` — announcing it while the card is still settling puts the card back
  in the flow mid-animation with viewport coordinates still on it, which is the §5.5 failure again.
  The commit is therefore delayed by the settle (~200ms), which nothing perceives; the working order
  stays on screen throughout regardless.
- **Do not also offer a click-based reorder control alongside the handle.** A prior version of this
  list paired the handle with per-row up/down chevrons; they were removed rather than kept as a
  second affordance, because two controls for the same action on the same card invites them to drift
  out of sync (e.g. one path animating, the other not). If a non-pointer way to reorder is needed
  later (keyboard, accessibility), it should replace the handle's own interaction model — keyboard
  focus + arrow keys on the handle itself — not reintroduce a second visible control.

### 5.4 Elements never extend past the application window

**No visual element — a lifted/scaled card, a shadow, a tooltip, a dropdown, an animation's transient
frame — may render outside the app's own window.** This is a fixed, non-resizable-below-content,
single-window desktop app (§5): there is no outer chrome to imply "the rest continues off-screen,"
so anything that visually escapes the window reads as a rendering bug, not a design choice, even if
it is purely decorative and momentary.

**Concrete case this rule exists for:** `PriceSourcesAdmin`'s lifted drag card. `.card-lifted {
transform: scale(1.02) }` grows the card's own box past whatever it was laid out at — that's
layout-neutral, so nothing about the box stopped it from growing past its pane, which sets
`overflow-y-auto` only (Tailwind's utility never touches `overflow-x`, §5.1). Five fixes were tried
and reverted before landing on the one that stuck:

1. **Plain `overflow: hidden` on the list container.** Stops the escape, but clips flush against the
   box edge with zero room for the scale (or the box-shadow's own blur) to actually paint — the
   card's own left/right border ends up partly *inside* the clipped region and disappears, which is a
   different, equally visible bug.
2. **Remove the scale, keep only `box-shadow` + border-color for the lift cue.** No box growth, so
   nothing to clip — but with the container still clipping flush (fix 1's `overflow: hidden`), the
   shadow's own blur (which also extends past the box) had nowhere to paint into either, so the lift
   read as invisible.
3. **Restore the scale, switch the container to `overflow: clip` + `overflow-clip-margin`.** The
   margin paints a fixed halo around the container's box *before* clipping starts, so the scale and
   the shadow both render. This fixed the invisible-shadow regression from fix 2, but the card still
   grows to 1.02× its box — on a wide-enough pane, 1% of the card's width exceeds even a generous
   fixed-pixel margin, and the card visibly extended past the window again.
4. **Rest every card 2% *smaller* (`scale(0.98)`), let the lifted card return to `scale(1)`.** No
   value ever exceeds the card's own laid-out box, so nothing escapes at any window width — this
   genuinely fixed the overflow. But it made every card's rendered width a function of the window's
   width (2% of a wide window is a bigger pixel shrink than 2% of a narrow one), which reads as
   *inconsistent card sizing across screens* rather than a deliberate design — a different, subtler
   problem than the one it fixed.
5. **Keep cards a uniform, unscaled width; reserve `--main-inset-x` (`calc(24px + 3%)`, then a plain
   `2%`) on `<main>` instead.** The idea — move the growth budget off the element and onto the one
   shared container — was sound, and the math for it checks out: at any card/window width, the scale's
   1%-per-side growth plus the shadow's ~22px reach both fit comfortably inside that inset. In
   practice the card was still reported cut off at the (larger) inset boundary after this change
   shipped. The exact mechanism was never conclusively identified — possibilities include an
   `overflow-clip-margin`/WebView2 rendering interaction with the transformed, `z-index`-raised
   element that the arithmetic above doesn't capture — but *what actually escaped kept escaping* is
   what mattered, twice, at two different inset sizes. An app-wide layout change (every tab's content
   inset) also isn't a proportionate fix for a bug local to one card in one tab.

6. **Remove the growth entirely — `box-shadow` + border-color only, no transform at all.** Nothing
   grows, so nothing can escape. Correct, and it shipped for a while, but it spends the whole visual
   cue to buy containment.

**Why five of those six were fighting the wrong element.** The clip chain from the card outward is:

| # | Element | overflow | h-padding |
|---|---|---|---|
| 1 | `.ds-card-stack` | `clip` + 24px clip-margin | 0 |
| 2 | **the pane — `PriceSourcesAdmin.razor:12`** | **`auto` — a scroll container** | **0 left**, 4px right |
| 3 | `<main>` | `hidden` | 12px |
| 4–7 | app shell / `#root` / `body` / `html` | `hidden` | 0 |

Every attempt above adjusted layer 1 or layer 3. **Nothing ever touched layer 2, and layer 2 is a
scroll container** — `overflow-y: auto` forces the used value of `overflow-x` to `auto` too, and a
scroll container always clips at its padding box. `overflow-clip-margin` does not apply to `auto`, and
its left padding is `0`. So the card's left edge was being sliced there every single time, no matter
what happened above or below it. **An in-flow descendant of a scroller can never paint outside it.**
That is a hard CSS constraint, not a tuning problem — which is why no margin anywhere ever fixed it,
including the one in attempt 5 whose arithmetic checked out.

**The actual fix takes the card out of the chain rather than widening anything in it.** `.card-pinned`
switches the card to `position: fixed` — clipped by the viewport and nothing else — and only then
grows it. `<main>` keeps its original `p-3`, and `.ds-card-stack` was eventually stripped of its
`overflow` entirely (see the warning below). The full contract for detaching an element this way is
**§5.5**, which is the sanctioned exception to this section's rule.

> **`overflow: clip` does not behave like `overflow: hidden` here, and the difference bites.** `hidden`
> only clips descendants whose containing block is inside it — and a fixed-position element's
> containing block is the viewport, so it escapes. `clip` applies a paint-time clip to the whole
> subtree regardless of containing block, so it **does** clip fixed descendants. `.ds-card-stack`
> carried `overflow: clip` through several of the attempts above; it went unnoticed for as long as the
> card was pinned over its own slot, always inside that box, and made the card vanish outright the
> moment it started following the cursor out of it. If a container that hosts a detached element needs
> containing, contain the effect, not the container.

**What this means in practice:**
- **Enumerate the whole clip chain before adjusting anything in it.** Five fixes in a row adjusted a
  container that was not the one clipping. The cheapest possible check — walk every ancestor and write
  down its `overflow` and padding — would have found layer 2 immediately. Note especially that
  Tailwind's `overflow-y-auto` *looks* like it only touches one axis but forces both to `auto`.
- **A scroll container cannot be given breathing room.** If the thing that must not be clipped lives
  inside a scroller, the only options are to not overflow, or to leave the flow (§5.5). There is no
  third answer, and no amount of padding, `overflow-clip-margin`, or ancestor inset is one.
- **Growth sized as a percentage of the element can always outrun a fixed containment budget.** Attempts
  3 and 5 both died on this: `scale(1.02)` grows `0.01 × width` per side, so on a wide window it
  exceeds any fixed pixel margin. If an effect must grow, size it in pixels **against a measured
  budget** (§5.5), not as a percentage.
- **Don't solve "the growth can escape" by making the growth depend on the container (attempt 4).**
  Shrinking the resting state so growth never crosses `scale(1)` does stop the escape, but every
  element carrying that resting shrink then renders at a size that varies with its container's width,
  which reads as inconsistent sizing across screens.
- **An app-wide layout change (attempt 5's shared `<main>` inset) is a proportionate fix only for an
  app-wide problem.** Inflating every tab's content margin to solve one card in one tab is a bigger
  change than the bug warrants, and it has to be reverted in lockstep with the effect it was added for.
- **Reserve `overflow: clip` + `overflow-clip-margin` for effects with a reach that's fixed regardless
  of window size** — a shadow's blur, a glow. It is not a way to contain something that grows, and it
  must never sit on an ancestor of a detached element (§5.5) since it clips fixed descendants too.

### 5.5 Detached elements

**The sanctioned exception to §5.4.** An element may render outside its parent container — and must
then not be clipped by it — when it is *simulating existence outside the layout* rather than
participating in it. Three qualify today: `.ls-tooltip`, the four modal `.overlay-backdrop`/
`.overlay-panel` dialogs, and `PriceSourcesAdmin`'s lifted drag card. An ordinary hover or emphasis
state does **not** qualify; it is still part of the layout and §5.4 applies to it in full.

**To qualify, a detached element must:**

- **Use `position: fixed`, never `absolute`.** A positioned ancestor re-traps an absolute element in
  the clip chain. Fixed elements are clipped only by the viewport. Note this needs no portal or
  reparenting — all three cases stay exactly where they are in the DOM, so Blazor's event wiring,
  bubbling, and `@key` identity are untouched. (`.ls-tooltip` is body-level for its own reasons — it's
  a singleton shared by every trigger — not because detaching required it.)
- **Compute its geometry from `getBoundingClientRect()` and clamp against `window.innerWidth` /
  `innerHeight`.** `tooltips.js` clamps its position this way; `reorderFlip._beginLift` clamps its
  *grow* the same way. Never assume a budget — measure it, because the thing you'd be assuming is some
  ancestor's padding, and that's exactly what §5.4's attempt 5 got wrong.
- **Size any growth in pixels against that measured budget, never as a percentage.** Percentage growth
  scales with the element and will outrun any fixed budget at some window size.
- **Claim a documented z-index tier.** Current inventory, and nothing may be inserted without updating
  this table:

  | z-index | What | Note |
  |---|---|---|
  | `10` | `.card-lifted` | Was a local claim inside the stack; once pinned it is **app-wide**. Must stay under 50. |
  | `50` | `.z-50` — all four modals | A drag can't start while a modal is open, but a modal opened another way must cover the card. |
  | `100` | `.ls-tooltip`, `#blazor-error-ui` | Nothing sits above these. |

- **Hold its old slot open, if it came out of a list.** A detached element leaves the flow, so whatever
  it vacated collapses. `.ds-card-slot` exists for this: it stays in flow and takes an explicit height
  while its card is pinned, so the list below doesn't jump on pickup.
- **Release on every path that can end its detached state.** For the drag card there are seven (drop,
  drop-that-was-a-click, `pointerleave`, OS `pointercancel`, a release the window never saw, a rejected
  commit, disposal). Rather than calling `unpin` from each, `PriceSourcesAdmin` reconciles from the
  rendered state in `OnAfterRenderAsync` — every one of those paths re-renders, so all of them
  converge and none can be forgotten.

**The invariant that keeps all of this working — and the two ways it will break.**

1. **No ancestor may acquire `transform`, `filter`, `perspective`, `backdrop-filter`, `contain`, or
   `will-change`.** Any one of those establishes a containing block for `position: fixed` descendants,
   which silently pulls the element back into the clip chain. The failure mode is a subtly mis-placed
   element, not an error.
2. **No ancestor may acquire `overflow: clip`.** Unlike `hidden`, it clips fixed-position descendants
   too (§5.4). The failure mode here is the element vanishing outright once it moves outside that
   ancestor's box — which is exactly how it presented.

**A detached element's class must be rendered by Blazor, not only added by JS.** Blazor rewrites an
element's entire `class` attribute on any render that changes it, so a class only ever added via
`classList` survives until the next render and no further. That is not a cosmetic loss: the inline
`top`/`left` that make the detachment meaningful are *viewport* coordinates, so an element that falls
back to `position: relative` is displaced by that full amount and thrown out of the layout. JS may add
the class as well — `.card-pinned` is added from both sides, so the card detaches on the current frame
rather than an interop round-trip later — but Blazor rendering it is what makes it stick.

`.panel-enter` (`Dashboard.razor:102`) is the live example to be careful around for invariant 1: its
keyframe animates `transform: scale(0.995) → scale(1)`, so while running it **is** a containing block.
It fires only on tab switch, has no `animation-fill-mode`, and is finished ~200ms later, long before
any drag can start — which is why it is safe today and why it should be left alone.

`.panel-enter` (`Dashboard.razor:102`) is the live example to be careful around: its keyframe animates
`transform: scale(0.995) → scale(1)`, so while running it **is** a containing block. It fires only on
tab switch, has no `animation-fill-mode`, and is finished ~200ms later, long before any drag can
start — which is why it's safe today and why it should be left alone.

## 6. Elevation & Depth

Elevation is expressed sparingly, but the aspirational spec calls for heavier shadows than the
previous theme (dark backgrounds need more shadow opacity to read as depth than light ones do —
`aspirational-design.md` §6). `app.css` now defines two shadow tokens plus the pre-existing drag-lift
value, which remains the only surface that rises above floating UI:

| Level | Treatment | Use |
| --- | --- | --- |
| Base | `var(--surface-base)` (`#121212`), no shadow | Page background |
| Surface | `var(--surface-card)` (`#181818`), 1px `var(--border-button)`, no shadow | Cards, header, nav, panels |
| Hover | Surface + `background-color: var(--surface-elevated-a)` and/or `--shadow-elevated` (`0 8px 8px rgba(0,0,0,0.3)`) | `.card-hover`/`.ds-card:hover` interactive cards |
| Floating / elevated | Surface + `box-shadow: var(--shadow-lift)` (`0 4px 12px rgba(0,0,0,0.3)`) | Tooltips (`.ls-tooltip`), hover lift on `.btn-primary` |
| Dialog | `#121212` + `box-shadow: var(--shadow-dialog)` (`0 8px 24px rgba(0,0,0,0.5)`) | Settings modal, Provider dialogs |
| Dragging | Surface + `box-shadow: 0 12px 28px -12px rgba(0,0,0,.55)` + `.ds-source-border-lifted` accent border-color, plus a ~10px-per-side grow once `.card-pinned` detaches it and it starts following the cursor (§5.5). Not contained — it's out of the clip chain entirely | The lifted card in `PriceSourcesAdmin`'s drag-to-rank list |

Floating/hover UI uses the lighter 0.3-opacity shadow; modals/dialogs use the heavier 0.5-opacity
shadow — this two-tier split (rather than one shared value) is the one elevation change adoption
introduced; new floating UI should pick whichever tier matches its role rather than inventing a third
opacity. The drag-lift value is reserved for the card physically under the pointer and should not be
reused for static elevation.

The drag-lift shadow's `-12px` spread is load-bearing, not a taste call: sideways reach is
`blur/2 + spread`, so that spread sets how much of the measured budget the shadow claims before the
card's own grow gets what's left (`SHADOW_REACH_PX` in `js/reorder-flip.js`). Changing it changes how
much the card can grow — recompute both together.

## 7. Inline Styles Policy & Refactoring Status

### Policy

**Core Principle:** ALL styling belongs in CSS, not inline. Even conditional and animation styling must be in `.css` files via class binding or CSS variables—never `style="..."` attributes in markup.

**Inline styles are NEVER permitted**, except for:
1. **Data-driven values from the backend** — when a value comes from a database/API and changes per-item (e.g., `style="background:@(agent.Color)"`). Even then, consider CSS variables (`--agent-color: @(agent.Color)`) as the preferred path if the value is used in multiple rules.

**Conditional and animation styling — use CSS classes, not inline conditionals:**
- ✅ **DO:** `class="@(isActive ? "button-active" : "button-inactive")"` → Define `.button-active` and `.button-inactive` in `app.css`
- ❌ **DON'T:** `style="background:@(isActive ? "#fff" : "#999)"`
- ✅ **DO:** `class="overlay-backdrop"` with `style="background-color:rgba(0,0,0,0.7);backdrop-filter:blur(4px)"` in CSS
- ❌ **DON'T:** Inline modal backdrop styling

**Modal backdrop is a special case — it MUST be in CSS:**
```css
.overlay-backdrop {
  background-color: rgba(0,0,0,0.7);
  backdrop-filter: blur(4px);
}
```

**Positioning and padding/margin are NEVER inline.** All layout, padding, margin, and positioning must use:
- **Tailwind utility classes** — but ONLY those listed in §5.1. Do not assume a utility exists; check the table first. If not listed, create a CSS class instead.
- **CSS classes** (`.ds-*`, `.btn-*`, `.ls-*`, `.btn-small-action`, etc.) for spacing not available in Tailwind
- Never hardcoded `style="padding:..."`, `style="margin:..."`, or `style="position:..."` attributes

**Why:** The compiled `app.css` uses tree-shaking and only includes Tailwind utilities that were actually used. Utilities like `m-1`, `p-1`, most `mt-*`, and most `mb-*` variants are not in the compiled output, so they won't work even if you use them. Always check §5.1 first; if the utility isn't listed, create a dedicated CSS class instead (reference: `.btn-small-action` for action buttons with margins).

**Antipattern — hardcoded static colors:** Every color like `#1f1f1f`, `#181818`, `#4d4d4d`, `#10b981`, `#f59e0b`, `#ef4444` should be a CSS variable or utility class, never hardcoded inline. 

**Refactor path:** When adding a new component or touching existing markup:
- Extract hardcoded color+style tuples to a new `.ds-*` class (or extend an existing one)
- Use `var(--token-name)` in the class, not the hex, so theming changes propagate
- Reserve inline `style=` only for the three cases above

**Common patterns (prefer these classes over building inline styles):**
- `.overlay-backdrop` — modal backdrop with `rgba(0,0,0,0.7)` + `blur(4px)` ✓ (already exists)
- Semantic form inputs — use `.ds-input` (TBD) instead of hand-rolling `background: var(--surface-interactive); border: 1px solid var(--border-light)`
- Tinted semantic surfaces (error/warning/success steps) — use `.ds-step-*` classes instead of inline color lists

See §4.2's `.btn-*` classes and §4's `.overlay-backdrop` as the reference pattern — static styling belongs in CSS, dynamic behavior stays inline.

### Refactoring Status
**Complete.** All static and conditional inline styles have been extracted to CSS classes. The
following classes have been added to support this, on top of the earlier round below:
- `.ls-turn-card`, `.ls-turn-card-toggle`, `.ls-flex-auto`, `.ls-stat-strip-gap` — `TurnCard`/
  `ConversationSummary` static chrome (the AgentColor-tinted background/border-left stays inline —
  that part is genuinely data-driven, exception 1 above). `.ls-turn-card`/`.ls-turn-card-toggle` are
  now only referenced by the orphaned `TurnCard.razor` (§1); `.ls-flex-auto`/`.ls-stat-strip-gap` stay
  live via `ConversationSummary`.
- `.ds-dashboard-ticker` — Dashboard ticker row border/background
- `.ls-console-line` — `ConsoleTab` line wrapping (the per-level text color stays inline, exception 1
  above)
- `.ls-drag-placeholder` — `PriceSourcesAdmin` reorder-arrow placeholder sizing (removed along with
  the arrow buttons themselves once the grab handle became the sole reorder affordance, §5.3)
- `.drag-enabled`/`.drag-disabled` cursor + `.drag-enabled.card-lifted` — replaced
  `PriceSourcesAdmin`'s conditional `style="cursor:@cursor"` with class binding
- `.ls-provider-budget-chart`, `.ls-provider-trend-chart` — `ProvidersAdmin` chart container sizing
- `.ls-price-overrides-grid`, `.ls-governance-grid` — grid-template-columns for `PriceOverridesAdmin`/
  `GovernanceModelCards`
- `.ls-livestream-right-panel`, `.ls-model-distribution-panel` — panel sizing for `LiveStream`/
  `ModelDistribution`

Earlier round of classes:
- `.ds-surface-base`, `.ds-surface-card-bordered`, `.ds-toolbar` — card and container styling
- `.ds-divider`, `.ds-divider-subtle` — separator lines
- `.ds-code-block` — code/payload block styling
- `.tab-button` with `.active`/`.inactive` states — tab bar button styling
- `.ds-card-stack` / `.ds-card-gap` — the standard `--space-card-gap` (0.75rem) gap between stacked
  cards; see §5.2 for which form to use
- `.ls-disclosure` with `.open` — expand/collapse pane wrapper for cards whose contents unfold
  (Benchmark Data's file lists, the provider card's add-model pane). Wraps exactly one child, which
  is the clipping window; spacing utilities go on an element inside that child, and the wrapper takes
  `inert` while collapsed. Motion contract: [`MOTION.md`](MOTION.md) §6 Disclosure Collapse
- `.btn-state-active`, `.btn-state-inactive`, `.btn-metric-active`, `.btn-metric-inactive` — conditional button states

Every remaining `style=` attribute in `TotallyHotArcRouter.Gui/Components` is one of the sanctioned
exceptions: a per-agent/per-model color computed from backend data (`ColorUtils`, `m.Color`,
`share.Color`, `AgentColor`), a log-level color (`LogLevelColorMapper`), a `--i` stagger-index custom
property feeding the `.row-enter`/`.disclosure-enter` animation delay (§6/MOTION.md), or `Icon.razor`'s
`Style` passthrough parameter (a generic per-instance API, unused by any current caller).

## 8. Do's and Don'ts

- **Do** build every new window/modal on the System Settings shell (§4.1) — same backdrop, panel,
  header, close glyph, and `OnClose` callback. New chrome for a new window is the thing to avoid.
- **Do** stay dark-only — no light-mode variant, no theme toggle.
- **Do** keep *chrome* to one accent (Dark Green `#1ed760`, was `sky-400`) plus three semantic colors
  on near-black neutrals — no additional brand hues. The stat-strip categorical hues (including the
  retained sky-400 in its data-encoding role) and the `-300` on-tint text tier (§2) are data encodings
  and are the only sanctioned exceptions.
- **Do** reserve pill/circular button geometry (§4.2) for explicit CTA buttons — Save, Cancel, Add,
  Confirm/destructive-confirm. Dense per-row icon actions and the tab bar stay square/non-pill.
- **Do** use `rounded-full`/`.btn-*` for status dots, small chips, and explicit CTA buttons (§4.2);
  use `rounded`/`rounded-lg` for cards, containers, and dense per-row actions — never a pill-shaped
  card, and never a pill on a dense repeated row action.
- **Do** prefer CSS classes and variables over inline styles — extract static colors to `.ds-*` classes
  (§7). Reserve `style=` for dynamic/conditional values, data-driven sizing, and semantic data-encoding
  exceptions.
- **Don't** use gradients — none exist anywhere in `app.css`; solid fills and tinted borders carry
  all color meaning.
- **Don't** hardcode colors inline — use `var(--token-name)` in CSS classes instead, so theme changes
  propagate without touching markup.
- **Don't** introduce a third shadow opacity for elevated UI — reuse `--shadow-lift` (0.3 opacity) for
  floating/hover UI or `--shadow-dialog` (0.5 opacity) for modals (§6). The drag-lift shadow is the
  sole further exception and is reserved for drag state.
- **Don't** add responsive breakpoints or a mobile layout — the app is a fixed single window.
- **Don't** hand-edit the compiled Tailwind utility blob at the top of `app.css` — it is generated
  output. Color/typography changes to the *chrome* it renders belong in the override section below it
  (same pattern the motion tokens and this adoption's `--surface-*`/`--text-*`/`--accent` overrides
  use); new component-level styling belongs in a `.btn-*`/`.ds-card`/`.ls-*`-style hand-written class.

