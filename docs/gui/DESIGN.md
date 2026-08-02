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

## 1. Design Principles

- **Data density without claustrophobia** — `TurnCard` and `ConversationCard` pack a full stat strip
  (ROI, Cost, Tok P/C, Steps, Cache, TTFT, Ctx, Model) into a two-line card, but every stat gets
  consistent `gap-*`/`px-*`/`py-*` spacing and a `.ls-stat-label` (10px, uppercase, muted `#7c7c7c`,
  was `#64748b`)
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
- **Trust through restraint** — one accent (Dark Green `#1ed760`, was `sky-400`) plus three semantic
  colors (`emerald`/`amber`/`red`) on a near-black neutral base is the entire palette. No additional
  brand hues; no gradients anywhere in `app.css`.
- **Progressive disclosure** — `ConversationSummary` shows one pinned stat strip; clicking a `TurnCard`
  header expands the step-by-step routing-decision log and request/response payload. Cost Analytics
  shows one chart at a time behind a ranked metric-picker pill row, not seven charts at once.

## 2. Visual Theme

Dark theme only — there is no light mode and no theme toggle (`dashboard.md` §Visual theme).

As of this re-skin (see `docs/gui/aspirational-design.md` §1), the app moved off the
slate-900/sky-400 palette onto a near-black ramp with a Dark Green (`#1ed760`) accent. Class names
in `app.css` are unchanged (e.g. `.text-slate-400` still exists as a selector) — only the hex value
each class/token resolves to changed, so Razor markup did not need to reference new class names.

| Role | Value | Tailwind class equivalent |
| --- | --- | --- |
| Page background | `#121212` | `bg-slate-900` (root `html,body,#root`) — was `#0f172a` |
| Card / header / nav surface | `#181818` | `bg-slate-800` — was `#1e293b` |
| Hover surface | `#1f1f1f` | `.card-hover:hover` — was `#263548` |
| Border (default) | `#4d4d4d` | `border-slate-700` — was `#334155` |
| Border (hover / emphasis) | `#7c7c7c` | `border-slate-600`, `hover:border-slate-500` — was `#475569` |
| Text — primary | `#ffffff` | `text-slate-200` — was `#e2e8f0` |
| Text — bright | `#fdfdfd` | `text-slate-100` — was `#f1f5f9` |
| Text — bright secondary | `#cbcbcb` | `text-slate-300` — was `#cbd5e1` |
| Text — secondary | `#b3b3b3` | `text-slate-400` — was `#94a3b8` |
| Text — muted | `#7c7c7c` | `text-slate-500` — was `#64748b` |
| Text — faint | `#4d4d4d` | `text-slate-600` — was `#475569` |
| Accent (info / active / focus ring) | `#1ed760` (Dark Green) | `text-sky-400` — was `#38bdf8` sky |
| Accent variant (border/outline) | `#1db954` | secondary/outlined button border — new, no prior equivalent |
| Positive / savings | `#10b981` (fill), `#34d399` text | `text-emerald-400` — **unchanged** |
| Warning | `#f59e0b` (fill), `#fbbf24` text | `text-amber-400` — **unchanged** |
| Critical | `#ef4444` (fill), `#f87171` text | `text-red-400` — **unchanged** |
| Inset surface — recessed | `#1f1f1f` | ticker row, active metric-picker pill — was `#172033` |
| Inset surface — payload block | `#121212` | `TurnCard` request/response `<pre>` blocks — was `#020617` |
| Inset surface — console | `#121212` | `ConsoleTab` log surface — was `#0b1120` |

Inset surfaces sit *below* the page background rather than above it — they mark a region as a
well (raw data, logs) rather than a card. They are the inverse of elevation: no border-lightening,
no shadow, just a darker fill. With the near-black ramp so compressed (`#121212`→`#272727`), inset
wells now share the base/interactive surfaces rather than getting their own darker-than-base tint —
there is no headroom below `#121212` (the palette's explicit floor; pure black `#000000` is
disallowed per `aspirational-design.md`).

Semantic colors (amber/red/emerald) and the 12-color agent-hash palette (`Utils/ColorUtils.cs`,
deterministic FNV-1a hash of agent name) are **data encodings, not chrome**, and are unchanged by
this re-skin even where a hash happens to land on the old accent hex — see "Data-encoding palettes"
below.

`.ls-card-action-stop` (Governance › Providers card action row) previously reused the accent
(`#38bdf8` sky) specifically because it read as a neutral, non-semantic hue next to the emerald
Play and red Remove actions. Now that the accent itself is green (`#1ed760`), reusing it there would
visually collide with Play's emerald. Stop is now a neutral gray (`#7c7c7c`, hover `#ffffff`) instead
of accent-colored — the one place in the app where "neutral action" and "the accent color" have
deliberately diverged.

### Data-encoding palettes

The three-color semantic palette above governs *chrome*. Two additional palettes encode *data*, and
they are deliberately broader — collapsing them into the chrome palette would destroy the encoding:

**Stat-strip categorical hues** — each stat in the `TurnCard` strip carries its own hue so a
specific metric can be found by color in a dense two-line strip, without reading labels:

| Stat | Color | | Stat | Color |
| --- | --- | --- | --- | --- |
| ROI, Cache | `#10b981` emerald (zero → `#7c7c7c`) | | Steps | `#f59e0b` amber |
| Cost, Model | `#1ed760` accent green (fallback model → amber, was `#38bdf8` sky) | | TTFT | `#fb7185` rose |
| Tok P/C | `#a78bfa` violet | | Ctx | `#cbcbcb` (was `#cbd5e1` slate-300) |

Rose and violet appear **only** here. They are categorical labels, not brand colors — do not use
them for chrome, borders, or status.

**Text on tinted semantic surfaces** — wherever a semantic color tints a background, the text steps
one shade lighter (`-300`) for contrast against that tint. The pattern is always
`background:<hue>11` + `border:<hue>44` + `-300` text:

| Context | Background | Border | Text |
| --- | --- | --- | --- |
| Error banners, failed pulls | `#ef444411` | `#ef444444` | `#fca5a5` red-300 |
| Routing step — Warn | `rgba(245,158,11,0.12)` | `#f59e0b` (left, 2px) | `#fcd34d` amber-300 |
| Routing step — Info | `rgba(30,215,96,0.1)` | `#1ed760` (left, 2px) | `#86efac` (was sky-300 `#7dd3fc`) |
| Routing step — OK | `rgba(16,185,129,0.08)` | `#10b981` (left, 2px) | `#6ee7b7` emerald-300 |

Fonts: **Inter** for all UI text, **JetBrains Mono** for every numeric/monospace value (token counts,
costs, timestamps, session/trace IDs) — set globally via `.font-mono`.

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

- **Cards** (`ConversationCard`, `TurnCard`, ticker stat cards) — `bg-slate-800` surface,
  `border-slate-700` (1px), `rounded-lg` (8px, `.5rem`) corners, `.card-hover` on interactive cards
  (`background-color` transition to `#1f1f1f` over 150ms, was `#263548`). Agent identity and fallback
  state are communicated by tinting the card's left border and background with the agent/alert color
  rather than adding an icon.
- **Badges / pills** — `rounded-full` (`.rounded-full`, pill shape) is used for status dots and small
  count/label chips (e.g. the agent-color dot, fallback `⚠` badge); larger containers use `rounded`
  (4px) or `rounded-lg` (8px), never a pill. The ranked metric-picker pill row in Cost Analytics
  follows the same `rounded-full` pattern.
- **Status bars** — `.progress-bar-track` (`background:#1f1f1f`, was `#1e3a5f`, `rounded` 2px, 6px
  tall) hosts the OK/WARNING/CRITICAL utilization fill for provider budgets and price-source caps.
- **Card action buttons** (the glyph row in a Governance › Providers card header) — a 26×26 square
  (`rounded p-1.5` around a 14px `Icon`), `rounded` 4px, never a pill, 150ms `transition-colors`.
  Edit stays on the slate ramp via Tailwind utilities (`slate-400`→`slate-200`); the semantic ones
  live in `.ls-card-action-*` because their hexes are off the utility palette: Stop `#7c7c7c` (neutral
  gray — was the accent `#38bdf8` before accent turned green; kept distinct from Play's emerald, see
  §2), Play `#10b981`, Remove `#dc2626`. Remove is the card's only destructive glyph — a second,
  separate delete control is not the pattern — and it opens a type-to-confirm dialog
  (`RemoveProviderDialog`, built on the §4.1 shell) rather than acting on the click.
- **Inputs** (Live Stream conversation search) — `background:#121212`, `border:1px solid #4d4d4d`,
  `color:#ffffff`; on focus the border becomes the accent `#1ed760` (150ms `border-color` transition,
  was `#38bdf8`). No pill inputs — search/text inputs are square-cornered via the shared input rule,
  not `.ls-*`.
- **Navigation** (5-tab bar: Live Stream / Cost Analytics / Model Distribution / Governance /
  Console) — `.tab-indicator` transitions all properties over 200ms `cubic-bezier(.4,0,.2,1)`; active
  vs. inactive is a text-color step (`slate-200`/`slate-100` vs. `slate-400`/`slate-500`), not a
  background fill.
- **Tooltips** (`.ls-tooltip`, shared floating tooltip driven by `data-tip` + `tooltips.js`) —
  `bg-slate-800`, `border:1px solid #7c7c7c` (was `#475569`), `rounded` (4px),
  `box-shadow: 0 4px 12px rgba(0,0,0,0.5)` — the one heavy shadow value in the whole stylesheet,
  reserved for floating/elevated UI (§6). Focus-visible state:
  `outline: 2px solid #1ed760; outline-offset: 2px` (was `#38bdf8`).
- **Modals / windows** — same `bg-slate-800`/`border-slate-700` card treatment, elevated with the
  same `0 4px 12px rgba(0,0,0,0.5)` shadow as tooltips. **`SettingsModal.razor` ("System Settings")
  is the reference implementation every new window matches** — see §4.1.

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
    <div class="overlay-panel w-full max-w-md rounded-lg border border-slate-700" style="background:#181818"
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
| Panel | `.overlay-panel`, `max-w-md`, `rounded-lg`, `border-slate-700`, `background:#181818` (was `#1e293b`) |
| Header | `px-5 py-4`, `border-b border-slate-700`, title left / close `x` right |
| Title | `text-sm font-semibold text-slate-200 tracking-wide uppercase` — **not** a large heading |
| Close glyph | `<Icon Name="x" Size="16" />`, `slate-400`→`slate-200` on hover, 150ms `transition-colors`. **Requires an `aria-label`** — the button's only content is an SVG, so without one a screen reader announces an unnamed button |
| Body | `p-5` with `space-y-3`/`space-y-4` between blocks |
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
- **Scrollbars**: thin (4px) custom scrollbar, `#4d4d4d` thumb on `#121212` track, `#7c7c7c` on hover
  (was `#334155`/`#0f172a`/`#475569`).

## 6. Elevation & Depth

Elevation is expressed sparingly. `app.css` contains exactly one shadow value; a second exists
inline in `PriceSourcesAdmin.razor` for the drag-lift state, which is the only surface that rises
above floating UI:

| Level | Treatment | Use |
| --- | --- | --- |
| Base | `bg-slate-900` (`#121212`, was `#0f172a`), no shadow | Page background |
| Surface | `bg-slate-800` (`#181818`, was `#1e293b`), 1px `border-slate-700`, no shadow | Cards, header, nav, panels |
| Hover | Surface + `background-color:#1f1f1f` (150ms transition, was `#263548`) | `.card-hover` interactive cards |
| Floating / elevated | Surface + `box-shadow: 0 4px 12px rgba(0,0,0,0.5)` | Tooltips (`.ls-tooltip`), Settings modal |
| Dragging | Surface + `box-shadow: 0 12px 28px -6px rgba(0,0,0,.55)` + `transform: scale(1.02)` | The lifted card in `PriceSourcesAdmin`'s drag-to-rank list |

There is no intermediate "dropdown" shadow tier today — new floating UI should reuse the same
`0 4px 12px rgba(0,0,0,0.5)` value rather than introduce a new opacity/blur combination. The
drag-lift value is reserved for the card physically under the pointer and should not be reused for
static elevation.

## 7. Do's and Don'ts

- **Do** build every new window/modal on the System Settings shell (§4.1) — same backdrop, panel,
  header, close glyph, and `OnClose` callback. New chrome for a new window is the thing to avoid.
- **Do** stay dark-only — no light-mode variant, no theme toggle.
- **Do** keep *chrome* to one accent (Dark Green `#1ed760`, was `sky-400`) plus three semantic colors
  on near-black neutrals — no additional brand hues. The stat-strip categorical hues and the `-300`
  on-tint text tier (§2) are data encodings and are the only sanctioned exceptions.
- **Do** use `rounded-full` for status dots and small chips, `rounded`/`rounded-lg` for cards and
  containers — never a pill-shaped card or button.
- **Don't** use gradients — none exist anywhere in `app.css`; solid fills and tinted borders carry
  all color meaning.
- **Don't** introduce a new shadow value for elevated UI — reuse `0 4px 12px rgba(0,0,0,0.5)` (§6).
  The drag-lift shadow is the sole exception and is reserved for drag state.
- **Don't** add responsive breakpoints or a mobile layout — the app is a fixed single window.

