# TotallyHotArcRouter.Gui Design System

This is the authoritative design-system reference for `TotallyHotArcRouter.Gui`. It codifies the app's
**existing, shipping** visual identity — every value below is pulled from
[`wwwroot/css/app.css`](../../src/TotallyHotArcRouter.Gui/wwwroot/css/app.css) and the behavior described in
[`dashboard.md`](dashboard.md) — it is not a redesign proposal. Motion — durations, easing, entrance/exit patterns — lives in its
companion [`MOTION.md`](MOTION.md), which *is* prescriptive. For component-level specs, see
[`cost-analytics-visualization-spec.md`](cost-analytics-visualization-spec.md),
[`governance-model-cards.md`](governance-model-cards.md),
[`provider-management.md`](provider-management.md), and
[`livestream-redesign-plan.md`](livestream-redesign-plan.md).

> **Status: aspirational design adopted.** As of [`aspirational-design-adoption-plan.md`](aspirational-design-adoption-plan.md)
> Phase 2/3, this document has been updated to reflect the near-black + Dark Green (`#1ed760`) visual
> identity from [`aspirational-design.md`](aspirational-design.md), which now supersedes the previous
> slate-900/sky-400 identity described in earlier revisions of this file. See §9 for the one deliberate
> deviation from the aspirational spec's literal font requirement (CircularSp licensing) and §4.2 for the
> one deliberate deviation on button geometry (dense per-row icon actions stay square, not pill/circular).

## 1. Design Principles

- **Data density without claustrophobia** — `TurnCard` and `ConversationCard` pack a full stat strip
  (ROI, Cost, Tok P/C, Steps, Cache, TTFT, Ctx, Model) into a two-line card, but every stat gets
  consistent `gap-*`/`px-*`/`py-*` spacing and a `.ls-stat-label` (10px, uppercase, muted `#64748b`)
  so the strip scans instead of blurring together.
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
- **Progressive disclosure** — `ConversationSummary` shows one pinned stat strip; clicking a `TurnCard`
  header expands the step-by-step routing-decision log and request/response payload. Cost Analytics
  shows one chart at a time behind a ranked metric-picker pill row, not seven charts at once.

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
| Inset surface — payload block | `#020617` | n/a | `TurnCard` request/response `<pre>` blocks |
| Inset surface — console | `#0b1120` | `--surface-inset` | `ConsoleTab` log surface |

\* `.bg-slate-700` is the one compiled-blob utility class actually applied to card-like surfaces in
markup; `bg-slate-800` never appears as a literal class (card surfaces are set via inline
`style="background:#181818"` instead — see the note in §7 about the compiled Tailwind blob).

**The accent swapped from sky-400 (`#38bdf8`) to Dark Green (`#1ed760`)** as part of aspirational-design
adoption — every reference to the old accent used as UI chrome (active tab text, focus rings, the Stop
icon-action color, tooltip focus outline) now points at `--accent`. Semantic colors (success/warning/
critical) are unchanged — they already matched the aspirational spec before adoption. **Sky is NOT
fully retired**, though: it also plays a data-encoding role below (the Cost/Model stat-strip hue and
the routing-step "Info" tone) and stays sky in those two roles specifically — see the note in the next
section.

Inset surfaces sit *below* the page background rather than above it — they mark a region as a
well (raw data, logs) rather than a card. They are the inverse of elevation: no border-lightening,
no shadow, just a darker fill.

### Data-encoding palettes

The three-color semantic palette above governs *chrome*. Two additional palettes encode *data*, and
they are deliberately broader — collapsing them into the chrome palette would destroy the encoding:

**Stat-strip categorical hues** — each stat in the `TurnCard` strip carries its own hue so a
specific metric can be found by color in a dense two-line strip, without reading labels:

| Stat | Color | | Stat | Color |
| --- | --- | --- | --- | --- |
| ROI, Cache | `#10b981` emerald (zero → `#64748b`) | | Steps | `#f59e0b` amber |
| Cost, Model | `#38bdf8` sky (fallback model → amber) | | TTFT | `#fb7185` rose |
| Tok P/C | `#a78bfa` violet | | Ctx | `#cbd5e1` slate-300 |

Rose and violet appear **only** here. They are categorical labels, not brand colors — do not use
them for chrome, borders, or status. **Sky (`#38bdf8`) is likewise preserved here as the Cost/Model
categorical hue** even though the same hex was retired as the chrome accent (§2) — recoloring it to
match the new green accent would make Cost visually indistinguishable from the emerald ROI/Cache stat
in the same strip, defeating the whole point of a categorical palette.

**Text on tinted semantic surfaces** — wherever a semantic color tints a background, the text steps
one shade lighter (`-300`) for contrast against that tint. The pattern is always
`background:<hue>11` + `border:<hue>44` + `-300` text:

| Context | Background | Border | Text |
| --- | --- | --- | --- |
| Error banners, failed pulls | `#ef444411` | `#ef444444` | `#fca5a5` red-300 |
| Routing step — Warn | `rgba(245,158,11,0.12)` | `#f59e0b` (left, 2px) | `#fcd34d` amber-300 |
| Routing step — Info | `rgba(56,189,248,0.1)` | `#38bdf8` (left, 2px) | `#7dd3fc` sky-300 |
| Routing step — OK | `rgba(16,185,129,0.08)` | `#10b981` (left, 2px) | `#6ee7b7` emerald-300 |

"Routing step — Info" also keeps sky rather than moving to the new accent green — it is a semantic
step-outcome tone (info, distinct from OK/Warn/Error) in `TurnCard`'s routing-decision log, not chrome.

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

- **Cards** (`ConversationCard`, `TurnCard`, ticker stat cards) — `--surface-card` (`#181818`)
  background, `border-color: var(--border-button)` (1px), `rounded-lg` (8px, `.5rem`, matching
  `--radius-card`) corners, `.card-hover`/`.ds-card:hover` for interactive cards (background shift to
  `--surface-elevated-a` and/or `--shadow-elevated` over 150ms). Agent identity and fallback state are
  communicated by tinting the card's left border and background with the agent/alert color rather
  than adding an icon.
- **Badges / pills** — `rounded-full` (`.rounded-full`, pill shape) is used for status dots and small
  count/label chips (e.g. the agent-color dot, fallback `⚠` badge); larger containers use `rounded`
  (4px) or `rounded-lg` (8px). The ranked metric-picker pill row in Cost Analytics follows the same
  `rounded-full` pattern.
- **Status bars** — `.progress-bar-track` (`background:#1e3a5f`, `rounded` 2px, 6px tall) hosts the
  OK/WARNING/CRITICAL utilization fill for provider budgets and price-source caps.
- **Card action buttons** (the glyph row in a Governance › Providers card header) — a 26×26 square
  (`rounded p-1.5` around a 14px `Icon`), `rounded` 4px, deliberately **not** pill/circular, 150ms
  `transition-colors`. Edit stays on the slate ramp via Tailwind utilities (recolored to the new
  neutral tokens); the semantic ones live in `.ls-card-action-*` because their hexes are off the
  utility palette: Stop `var(--accent)` (`#1ed760`), Play `#10b981`, Remove `#dc2626`. Remove is the
  card's only destructive glyph — a second, separate delete control is not the pattern — and it opens
  a type-to-confirm dialog (`RemoveProviderDialog`, built on the §4.1 shell) rather than acting on the
  click. **These dense per-row action buttons are the one deliberate exception to §4.2's pill/circular
  button geometry** — see §4.2 for why.
- **Inputs** (Live Stream conversation search, form fields) — `background: var(--surface-interactive)`
  (`#1f1f1f`), `border: 1px solid var(--border-light)`, `color: var(--text-primary)`; on focus the
  border becomes the accent `var(--accent)` with a green focus ring (150ms `border-color` transition).
  No pill inputs — search/text inputs are square-cornered via the shared input rule, not `.ls-*`.
- **Navigation** (5-tab bar: Live Stream / Cost Analytics / Model Distribution / Governance /
  Console) — `.tab-indicator` transitions all properties over 200ms `cubic-bezier(.4,0,.2,1)`; active
  vs. inactive is a text-color step (white/`var(--accent)` vs. `var(--text-secondary)`), not a
  background fill. The tab bar itself is navigation, not a CTA, so it is exempt from the pill/circular
  button geometry in §4.2.
- **Tooltips** (`.ls-tooltip`, shared floating tooltip driven by `data-tip` + `tooltips.js`) —
  `var(--surface-card)` background, `border: 1px solid var(--border-light)`, `rounded` (4px),
  `box-shadow: var(--shadow-lift)` — one of two heavy shadow values in the whole stylesheet, reserved
  for floating/elevated UI (§6). Focus-visible state: `outline: 2px solid var(--accent);
  outline-offset: 2px`.
- **Modals / windows** — panel on `#121212` (the deepest surface, not the card surface — see §4.1),
  `border-color: var(--border-button)`, backdrop blur, elevated with `var(--shadow-dialog)`
  (`0 8px 24px rgba(0,0,0,0.5)`). **`SettingsModal.razor` ("System Settings") is the reference
  implementation every new window matches** — see §4.1.

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

**Deliberate exception: dense per-row icon actions stay square, not pill/circular.** The 26×26
Edit/Stop/Play/Remove glyphs in a Governance › Providers card row (`.ls-card-action*`, §4 above) are
NOT converted to `.btn-circular` even though the aspirational spec's circular-button use case ("quick
actions") could describe them. Reason: "Data density without claustrophobia" is one of the five
adopted principles, and a 48px circular button in a compact multi-row card list would blow out the
row height and contradict that principle for the sake of literal geometry conformance. Pill/circular
geometry is reserved for explicit, standalone CTA buttons — the ones §4.2's table covers — not for
dense repeated row actions.

### 4.1 New windows follow the System Settings pattern

Any new modal, dialog, or window copies the shell of
[`SettingsModal.razor`](../../src/TotallyHotArcRouter.Gui/Components/SettingsModal.razor) rather than
inventing its own chrome. `ProviderEditDialog.razor` already does this, so the two are identical
above the body — that consistency is the point, and it is what new windows are expected to preserve.

The shell, verbatim:

```razor
<div class="overlay-backdrop fixed inset-0 z-50 flex items-center justify-center"
     style="background-color:rgba(0,0,0,0.7);backdrop-filter:blur(4px)"
     @onclick="OnClose">
    <div class="overlay-panel w-full max-w-md rounded-lg border border-slate-700" style="background:#121212"
         @onclick:stopPropagation="true">
        <div class="flex items-center justify-between px-5 py-4 border-b border-slate-700">
            <span class="text-sm font-semibold text-slate-200 tracking-wide uppercase">Window Title</span>
            <button @onclick="OnClose"
                    aria-label="Close window title"
                    class="text-slate-400 hover:text-slate-200 transition-colors">
                <Icon Name="x" Size="16" />
            </button>
        </div>
        <div class="p-5 space-y-4">
            @* body *@
        </div>
    </div>
</div>
```

The load-bearing details:

| Element | Contract |
| --- | --- |
| Backdrop | `.overlay-backdrop`, `rgba(0,0,0,0.7)` + `backdrop-filter:blur(4px)`, `z-50`, centers its panel |
| Dismissal | Backdrop `@onclick` closes; panel carries `@onclick:stopPropagation="true"` so body clicks don't |
| Panel | `.overlay-panel`, `max-w-md`, `rounded-lg`, `border-slate-700` (→ `var(--border-button)`), `background:#121212` — the deepest surface (`--surface-base`), not the card surface, per `aspirational-design.md`'s modal treatment |
| Header | `px-5 py-4`, `border-b border-slate-700`, title left / close `x` right |
| Title | `text-sm font-semibold text-slate-200 tracking-wide uppercase` — **not** a large heading |
| Close glyph | `<Icon Name="x" Size="16" />`, `slate-400`→`slate-200` on hover, 150ms `transition-colors`. **Requires an `aria-label`** — the button's only content is an SVG, so without one a screen reader announces an unnamed button |
| Body | `p-5` with `space-y-3`/`space-y-4` between blocks. Primary/secondary/destructive action buttons in the body use `.btn-*` (§4.2), not ad hoc `rounded` + inline-color styling |
| Close API | A `[Parameter] public EventCallback OnClose` (or `OnCancel`) — the window never closes itself |

`.overlay-backdrop`/`.overlay-panel` also supply the entrance animation for free (see
[`MOTION.md`](MOTION.md) §Overlay Rise); a window that hand-rolls its backdrop loses it.

Deviate only where the content genuinely demands it — a wider `max-w-*` for a table, say — and keep
the header, dismissal behavior, and close API identical regardless.

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
| Dragging | Surface + `box-shadow: 0 12px 28px -6px rgba(0,0,0,.55)` + `transform: scale(1.02)` | The lifted card in `PriceSourcesAdmin`'s drag-to-rank list |

Floating/hover UI uses the lighter 0.3-opacity shadow; modals/dialogs use the heavier 0.5-opacity
shadow — this two-tier split (rather than one shared value) is the one elevation change adoption
introduced; new floating UI should pick whichever tier matches its role rather than inventing a third
opacity. The drag-lift value is reserved for the card physically under the pointer and should not be
reused for static elevation.

## 7. Inline Styles Policy

**Principle:** Prefer CSS variables and classes over inline styles. Inline styles are permitted only when:
1. **Dynamic/conditional values** — color or size changes based on runtime state (e.g., `style="color:@(isActive ? #fff : #999)"`)
2. **Data-driven sizing** — dimensions from calculation or loop iteration (e.g., `style="width:@(item.Width)px"`)
3. **Semantic data-encoding glyphs** — when a glyph must stay a specific color (e.g., sky-blue in the Info routing step) despite global class overrides for the same color in chrome (§2.2)

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

