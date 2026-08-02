# Aspirational Design

This document defines the aspirational GUI design direction for TotallyHotArcRouter. It is prescriptive for future redesign work across all dashboard surfaces: **Live Stream**, **Cost Analytics**, **Governance**, and **Console**. It complements and will eventually supersede DESIGN.md and MOTION.md as outlined in the adoption plan (aspirational-design-adoption-plan.md).

## Design Principles

These five principles form the foundation for all design decisions:

1. **Data density without claustrophobia** — Pack information tight but let each metric breathe through consistent spacing and restrained borders.
2. **Hierarchy through weight, not decoration** — Use type scale and subtle background shifts to guide the eye, never gradients or decorative drop shadows for emphasis.
3. **Motion as meaning** — Animate only when state changes or values transition. Idle dashboards remain calm, never performative.
4. **Trust through restraint** — Limit color palette to 2-3 functional hues plus achromatic neutrals. Enterprise buyers read visual noise as immaturity.
5. **Progressive disclosure over feature walls** — Show headline metrics first; let users drill down into complexity. Complexity is earned, not dumped.

## Visual Direction: TotallyHot Arc Router Dashboard

A dark, immersive analytics interface where operational data, traffic visualizations, and cost metrics are the primary source of color. The UI recedes into shadow so content can glow.

### Visual Characteristics

- **Theme:** Dark, immersive, operational, cost-focused
- **Primary Accent:** Dark Green (#1ed760) — functional only, never decorative
- **Surfaces:** Near-black ramp (#121212, #181818, #1f1f1f, #252525, #272727)
- **Shadows:** Heavy and dramatic (0.3–0.5 opacity) for strong elevation on dark backgrounds
- **Geometry:** Pill buttons (500px–9999px), circular controls (50%)
- **Typography:** CircularSp (Circular by Lineto) with global fallback stack (Arabic, Hebrew, Cyrillic, Greek, Devanagari, CJK)
- **Type System:** Compact (10px–24px range), bold/regular binary (700/400), semibold sparingly (600)
- **Color Source:** Visualizations, charts, and data provide all UI color — interface chrome remains achromatic
- **Design Metaphor:** Theater-like environment where the stage (UI) recedes as lights dim, and content (data) becomes the star. Entrance animations slide scenery in as lights raise; transitions dim/raise to shift focus.

### Applicable Across All Tabs

This design applies **uniformly** across:
- **Live Stream** — Real-time routing status, active connections, live metrics
- **Cost Analytics** — Historical and real-time cost/token accrual, spending trends, budget tracking
- **Governance** — Configuration forms, policy rules, administrative controls
- **Console** — Logs, diagnostics, command interface

## 1. Color Palette and Roles

### Core Palette

**Neutral Surfaces (Achromatic UI Chrome):**
- **Deepest background:** #121212 — Base layer, page/sidebar background
- **Core surface:** #181818 — Cards, containers, panels
- **Interactive surface:** #1f1f1f — Button backgrounds, inputs
- **Elevated card (A):** #252525 — Secondary card surface
- **Elevated card (B):** #272727 — Alternate card surface

**Text and Metadata:**
- **Primary text:** #ffffff — Headlines, interactive labels
- **Secondary text:** #b3b3b3 — Supporting copy, metadata, inactive states
- **Bright secondary:** #cbcbcb — Slightly elevated secondary text
- **Highest emphasis:** #fdfdfd — Maximum-contrast highlights

**Functional Accent:**
- **Primary accent (Action):** #1ed760 (Dark Green) — Play/execute buttons, active states, CTAs, system health indicators
- **Accent variant:** #1db954 — Border/outline variant of accent

**Semantic Colors (Health Thresholds):**
- **Healthy/Success:** #10b981 (Emerald) — Route is healthy, cost within budget, service up
- **Degraded/Warning:** #f59e0b (Amber) — Route has issues, cost approaching threshold, service degraded
- **Failed/Error:** #ef4444 (Red) — Route failed, cost limit exceeded, service down

**Boundaries & Separators:**
- **Button border:** #4d4d4d — Subtle borders on dark
- **Light border:** #7c7c7c — Outlined buttons, dividers
- **Separator line:** #b3b3b3 — Visual dividers between sections

### Color Rules (All Contexts)

- **Accent is functional, never decorative.** Dark Green (#1ed760) appears only on buttons, active states, and semantic success/health.
- **Semantic colors signal state meaning only.** Red = error, Amber = warning, Green = healthy. Applied consistently across all four tabs.
- **Most surfaces remain neutral.** Dark grays + white text maintain enterprise trust and reduce cognitive load.
- **Saturation capped at 80%** to prevent eye strain on long-duration dashboard use.
- **No pure black (#000000).** Use #121212 (off-black) for deepest backgrounds.
- **Glassmorphism limited to modals only.** Modal windows use `backdrop-filter: blur(8px)` + rgba(255,255,255,0.1) for frosted glass effect. Card surfaces remain flat with solid backgrounds.

## 2. Typography

### Font Family

**Primary Font:** CircularSp (Circular by Lineto — proprietary)

**Fallback Stack:**
1. CircularSp-Arab (Arabic)
2. CircularSp-Hebr (Hebrew)
3. CircularSp-Cyrl (Cyrillic)
4. CircularSp-Grek (Greek)
5. CircularSp-Deva (Devanagari)
6. Helvetica Neue
7. Arial
8. Noto Sans (CJK variants)
9. Hiragino Sans
10. MS Gothic

### Type System

**Weights:**
- **700 (Bold):** Section anchors, navigation, button labels, highest-emphasis text
- **600 (Semibold):** Subheadings, secondary section labels, used sparingly
- **400 (Regular):** Body text, supporting copy, metadata

**Hierarchy (By Role)**

| Role | Size | Weight | Line Height | Notes |
|------|------|--------|-------------|-------|
| Display/Hero | 2.5rem–4rem (clamp) | 700 | 1.2 | Page-level headlines, dashboard headers |
| H1 | 2.25rem | 700 | normal | Major section titles |
| H2 | 1.5rem | 600 | 1.3 | Secondary section headings |
| H3 | 1.125rem | 600 | 1.3 | Card titles, subsection heads |
| Body | 1rem | 400 | 1.6 | Standard reading text, metric descriptions |
| Body Small | 0.9375rem | 400 | 1.6 | Supporting descriptions |
| Label/Caption | 0.875rem | 500 | normal | Form labels, field descriptions (use 600 weight for emphasis) |
| Small | 0.75rem | 400 | normal | Metadata, timestamps, fine print |
| Micro | 0.625rem | 400 | normal | Smallest text: badges, extreme metadata |

**Button Labels:**
- All uppercase
- Letter-spacing: 1.4px–2px
- Weight: 600–700
- Creates systematic "label" voice distinct from body text

### Typography Principles

- **Bold/regular binary is the hierarchy engine.** Most text is either 700 or 400; use 600 sparingly for secondary emphasis.
- **Compact scale (10px–24px) supports dense dashboards.** Designed for fast scanning of routes, costs, and statuses.
- **Uppercase buttons establish affordance.** The systematic uppercase + tracking signals interactivity without relying on shadow or color alone.
- **No decorative emphasis.** Type hierarchy carries meaning; decorative devices are prohibited.

## 3. Layout and Spacing

### Spacing System

**Base Unit:** 8px (0.5rem)

**Scale:**
- **Micro:** 1px, 2px, 3px — Borders, hairline separators
- **Small:** 4px, 5px, 6px — Internal component padding
- **Standard:** 8px, 10px, 12px — Component padding, tight spacing
- **Medium:** 14px, 15px, 16px — Section spacing, gaps
- **Large:** 20px, 24px, 32px — Card margins, contained gaps
- **XL:** 40px, 48px, 56px, 64px — Section gaps, major spatial breaks

### Dashboard Layout

**Desktop-Only (Fixed Window):**
- No mobile-responsive breakpoints
- Sidebar: 240–280px fixed on left
- Main content: fills remaining horizontal space
- Top bar: branding (placeholder logo) + system status
- Bottom bar: status/token usage display (persistent across all tabs)

**Grid System:**
- **Primary:** CSS Grid for dashboard layouts
- **Secondary:** Flexbox for component-level layouts
- **Max-content width:** 1280px centered with 1.5rem (24px) side padding
- **Card grids:** Adaptive columns (3–5 columns depending on data density)
- **No horizontal overflow** — content fills viewport width or scrolls vertically

### Density and Whitespace

- **Dense by design:** Pack metrics and controls to maximize information density
- **Breathable through spacing rhythm:** Consistent micro-spacing prevents claustrophobia
- **Dark compression philosophy:** Dark backgrounds provide visual rest without requiring large gaps
- **Every pixel serves comprehension or action** — no decorative whitespace

### Navigation Structure

**Primary Navigation (Sidebar):**
- Four main tabs as top-level navigation items:
  1. Live Stream
  2. Cost Analytics
  3. Governance
  4. Console
- Logo/branding placeholder in top left
- Active tab: text in white (#ffffff) + weight 700
- Inactive tab: text in secondary (#b3b3b3) + weight 400
- Hover: subtle background tint
- Padding: 12px 16px per item

**Top Bar:**
- Branding placeholder logo
- System status indicators
- Breadcrumb navigation if needed (Separator: "/" or "›")

**Bottom Bar (Token Usage Display):**
- Fixed across all tabs
- Shows: "Current Month: [X tokens used] / [Y limit]"
- Color-coded status (green/amber/red based on usage threshold)
- Compact layout: Icon + text + status badge

## 4. Components and Patterns

### Shared Component Language

All four tabs (Live Stream, Cost Analytics, Governance, Console) use this unified component set:

**Metric/Status Cards:**
- Background: #181818 or #1f1f1f
- Radius: 6px–8px
- Headline value: H2 weight (600–700)
- Secondary metric or status: 14px weight 400, #b3b3b3
- Health indicator: Color-coded badge (green/amber/red) or text
- Padding: 16px–20px
- Shadow on hover: rgba(0,0,0,0.3) 0px 8px 8px
- No border by default; optional 1px border for emphasis

**Charts and Visualizations:**
- Restrained chrome (minimal labels/legend)
- Colorful data (charts provide visual variety)
- Smooth animations on data update (250–300ms ease-out)
- Responsive: reflow/stack as needed
- Subtitle: 0.875rem, #b3b3b3, below title

**Configuration Forms (Governance Tab):**
- Label above field, 0.875rem weight 500
- Input background: #1f1f1f
- Text: #ffffff
- Border: 1px #7c7c7c
- Focus ring: 2px #1ed760 offset 2px
- Validation text below: #ef4444 (error) or #10b981 (success), 0.75rem
- Call-to-action buttons: primary style (see below)

**Buttons (Unified Across All Tabs):**

*Primary Button:*
- Background: #0F172A or #1f1f1f
- Text: #1ed760 (Dark Green) or #ffffff
- Radius: 12px
- Padding: 12px 16px (min-height: 44px)
- Weight: 700, uppercase, letter-spacing 1.4px
- Hover: 8% darken background + subtle lift shadow (0 4px 12px rgba(0,0,0,0.3))
- Active: scale 98% (tactile press feedback)
- Use: Execute route, save configuration, primary actions

*Secondary Button (Outlined):*
- Background: transparent
- Border: 1.5px solid #1ed760 (Dark Green)
- Text: #1ed760
- Radius: 12px
- Padding: 12px 16px
- Hover: 10% background fill (#1ed760 at 10% opacity)
- Use: Secondary actions, cancel, reset

*Ghost Button:*
- Background: transparent
- Border: 1px solid #7c7c7c
- Text: #ffffff
- Hover: subtle background tint
- Use: Tertiary actions, help links, info

*Circular Action Button:*
- Radius: 50%
- Size: 48px (desktop)
- Background: #1ed760 (Dark Green) or #1f1f1f with icon
- Icon: white or black (#121212)
- Hover: scale 102% + shadow increase
- Active: scale 98% (press feedback)
- Use: Play/execute, quick actions

**Pills and Search:**
- Radius: 500px–9999px (full pill)
- Background: #1f1f1f
- Text: #ffffff
- Border: 1px inset #7c7c7c (tactile quality)
- Padding: 12px 48px (icon-aware)
- Focus: border #000000 + outline 1px
- Use: Search inputs, navigation pills, filters

**Empty/No Results States:**
- Large icon (64–96px, icon system)
- Headline: weight 600, #ffffff
- Description: 14px weight 400, #b3b3b3
- Action button: primary style
- Example: "Create Route", "Add Configuration", "No data yet"

**Skeleton States:**
- Shimmer animation matching final component dimensions
- No circular spinners (use rectangular placeholders)
- Pulse animation: opacity fade 1.2s ease-in-out infinite

**Modals and Overlays:**
- Background: #0F172A or #121212
- Blur effect: `backdrop-filter: blur(8px)`
- Semi-transparent background: rgba(255,255,255,0.1) or rgba(0,0,0,0.5)
- Subtle top highlight: 1px rgba(255,255,255,0.2) for depth cue
- Shadow: rgba(0,0,0,0.5) 0px 8px 24px
- Z-index: 300
- Close affordance: X button (primary style or icon-only)

## 5. Motion and Animation

### Allowed Triggers

Animation only occurs on:
1. **State transitions** (e.g., route status: healthy → degraded → failed)
2. **Value changes** (e.g., counter increments, cost accrual, token usage)
3. **User interactions** (e.g., hover, click, open/close, expand/collapse)
4. **Tab/page transitions** (slide scenery, dim/raise lights)

**Disallowed:**
- Always-on ambient animation in idle dashboards
- Decorative motion unrelated to state change
- Parallax or unnecessary scroll-triggered animations

### Motion Patterns

**Entry Animations (Theater Metaphor):**
- Fade + translate-Y (16px down → 0) over 420ms ease-out
- Staggered cascades for lists: 80ms between items
- Modal/overlay entrance: fade + scale (95% → 100%) over 300ms ease-out
- Lights "raise" as new content enters; stage becomes focus

**Hover States:**
- Subtle color shift (text or background) over 200ms
- Card lift: 2–4px with shadow increase
- Button: 8% background darken + subtle lift

**Active/Press States:**
- Scale: 98–99% over 100ms
- Tactile feedback: -1px translate down
- Visual confirmation: immediate (not delayed)

**Value Transitions:**
- Counter ticks: 400–600ms with ease-out easing
- Status color transitions: 300ms ease-out
- Progress bar/gauge: smooth linear or ease-out over 200–500ms

**Page/Tab Transitions:**
- Fade only: 200–300ms ease-out
- Lights dim as old content fades; raise as new content enters
- No slide/swipe animations (desktop-only, no mobile metaphors)

### Motion Principles

- **Duration:** 200–300ms for most interactions; 420ms for full-screen transitions
- **Easing:** Ease-out curves (cubic-bezier(0.4, 0, 0.2, 1)) for natural deceleration
- **Performance:** Only `transform` and `opacity` animated (no layout-triggering properties)
- **Accessibility:** Respect `prefers-reduced-motion` by disabling non-essential animations
- **Idle state:** Completely calm, no ambient motion

## 6. Elevation and Depth

### Elevation Levels

| Level | Treatment | Use |
|-------|-----------|-----|
| Base (0) | #121212 background | Deepest layer, page background |
| Surface (1) | #181818 or #1f1f1f | Cards, containers, primary content |
| Elevated (2) | Shadow: rgba(0,0,0,0.3) 0px 8px 8px | Hover cards, dropdowns, lifted elements |
| Dialog (3) | Shadow: rgba(0,0,0,0.5) 0px 8px 24px | Modals, overlays, critical dialogs |
| Inset | Border: rgb(18,18,18) 0px 1px 0px inset + rgb(124,124,124) 0px 0px 0px 1px inset | Input borders, recessed quality |

### Shadow Philosophy

- **Heavy shadows on dark.** Dark backgrounds require 0.3–0.5 opacity shadows to show depth; light shadows disappear.
- **Medium shadow (0.3 opacity):** Subtle card lift, dropdown hover, hover states
- **Heavy shadow (0.5 opacity):** Dramatic "floating in darkness" effect for modals and critical overlays
- **Inset border-shadow combo:** Creates recessed, tactile quality for inputs
- **No decorative shadows.** Shadows signal elevation and interactivity only, never applied for visual decoration.

### Z-Index Contract

- **Base:** 0
- **Sticky/fixed navigation:** 100
- **Dropdown/popover:** 200
- **Modal/dialog:** 300
- **Toast/notification/alert:** 500

## 7. Dashboard Tabs: Specific Guidance

### All Tabs (Shared)

- Progressive disclosure: headline metric first, drill-down second, diagnostics on demand
- Consistent spacing rhythm and typography hierarchy
- Unified component language (buttons, cards, inputs, modals)
- No decorative hierarchy — use weight and position only
- Red/amber/green health coding where applicable

### Live Stream

- Real-time status and metrics
- Metric cards showing current values, deltas, health status
- Active connections/routes highlighted
- Minimize latency indicators where relevant

### Cost Analytics

- Historical and real-time token/cost accrual
- Trend charts (time-series) showing spending over time
- Budget thresholds with color-coded indicators (green/amber/red)
- Drill-down to per-route or per-provider cost breakdown
- Projection/forecast if available

### Governance

- Configuration forms for providers, price sources, routing rules
- Action-heavy: save, delete, duplicate, enable/disable
- Form validation with green/red status
- Optional help text and inline documentation
- No visualization — structure and form

### Console

- Logs, diagnostics, command output
- Monospace font (JetBrains Mono) for technical output
- Supports progressive disclosure (summary log → detailed view → raw output)
- Status codes and error states color-coded as semantic colors

## 8. Acceptance Criteria

This design is successful when:

**Visual Design:**
- ✓ Color palette limited to near-black ramp (#121212–#272727) + Dark Green accent (#1ed760) only
- ✓ Semantic colors (green/amber/red) used consistently for health thresholds
- ✓ No pure black (#000000), gradients, or decorative shadows
- ✓ Pill and circular geometry on all buttons (no square buttons)
- ✓ Glassmorphism applied to modals only (not card surfaces)
- ✓ All surfaces have 6–8px radius (cards) or 12px (buttons)

**Typography:**
- ✓ CircularSp font family throughout with proper fallback stack
- ✓ Weight binary: 700 (bold) or 400 (regular), with 600 sparingly
- ✓ Uppercase buttons with 1.4px–2px letter-spacing
- ✓ Compact scale: 10px–24px range
- ✓ No decorative emphasis; hierarchy via weight and size

**Layout:**
- ✓ 8px base unit applied consistently
- ✓ Sidebar: 240–280px fixed on left
- ✓ Bottom status bar fixed and visible across all tabs
- ✓ Max-content width: 1280px with 24px side padding
- ✓ No horizontal overflow; content reflows vertically

**Components:**
- ✓ Unified component language across all four tabs
- ✓ Metric cards with headline + secondary + status indicator
- ✓ Charts with restrained chrome and colorful data
- ✓ Forms in Governance tab with validation states
- ✓ Buttons in primary/secondary/ghost styles, all pill or circular
- ✓ Empty states with icon + text + action button

**Motion:**
- ✓ Entrance animations: fade + translate-Y over 420ms
- ✓ Hover states: color shift + shadow adjustment over 200ms
- ✓ Value transitions: smooth ticks/fades over 200–500ms
- ✓ Tab transitions: fade only, 200–300ms
- ✓ No idle animation; all motion has semantic trigger
- ✓ Respects `prefers-reduced-motion`

**Accessibility:**
- ✓ Contrast ≥ 4.5:1 for text on backgrounds
- ✓ Dark Green (#1ed760) text on white/light backgrounds ≥ 4.5:1
- ✓ Color not sole signal (icons, text, position combinations)
- ✓ Focus rings visible on all interactive elements
- ✓ Touch targets ≥ 44x44px
- ✓ Semantic HTML and ARIA labels for icon-only buttons

**Documentation:**
- ✓ All five design principles are reflected in layout and interaction
- ✓ No conflicting guidance in DESIGN.md, MOTION.md, or component docs
- ✓ Component behavior documented with semantic trigger mappings
- ✓ Progressive disclosure patterns explicit for each tab

## Next Steps

This aspirational design becomes the actual design through the phases defined in aspirational-design-adoption-plan.md:

1. **Phase 0:** Baseline audit and gap map
2. **Phase 1:** Documentation and token contract alignment
3. **Phase 2:** Foundation refactor (shared CSS, components)
4. **Phase 3:** Tab-by-tab adoption (Live Stream → Cost Analytics → Governance → Console)
5. **Phase 4:** Motion hardening
6. **Phase 5:** Verification and rollout
7. **Phase 6:** Post-release calibration

As each phase completes, DESIGN.md and MOTION.md will be updated to reflect the new aspirational-design principles, and they will become the shipping standards for the GUI.
