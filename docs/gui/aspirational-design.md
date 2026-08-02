# Aspirational Design

This document defines the aspirational GUI direction for TotallyHotArcRouter. It is prescriptive for future redesign work and complements the shipping-state references in DESIGN.md and MOTION.md.

Two design directions are documented here:
1. **Dark Operational Theme** (current baseline) — immersive near-black surfaces with functional accents
2. **Corporate Blue SaaS** (aspirational) — modern analytics aesthetic with corporate trust signals

## Design Principles

- Data density without claustrophobia — pack information tight but let each metric breathe through consistent spacing and restrained borders.
- Hierarchy through weight, not decoration — use type scale and subtle background shifts to guide the eye, never gradients or drop shadows for emphasis.
- Motion as meaning — animate only when a number changes or a state transitions; idle dashboards should feel calm, not performative.
- Trust through restraint — limit palette usage to a small functional set plus neutrals; enterprise buyers read visual noise as immaturity.
- Progressive disclosure over feature walls — show the headline metric first, then let users drill down; complexity is earned, not dumped.
- Clean B2B aesthetics — glassmorphism with subtle depth cues, generous whitespace, and accessible contrast throughout.

## Aspirational SaaS Direction

The aspirational design borrows from successful analytics SaaS products (Amplitude, Mixpanel, Looker) that established a shared visual language around 2016+. These products realized that a gorgeous dashboard screenshot above the fold converts prospects more effectively than a demo request.

### Visual Characteristics

- **Density:** 8/10 — Dense but breathable
- **Variance:** 2/10 — Highly structured and predictable
- **Motion:** 4/10 — Subtle, meaningful, performant
- **Style:** Corporate, Clean, Data-Driven
- **Keywords:** SaaS landing, analytics dashboard, metrics cards, corporate blue, B2B, business intelligence, charts, integrations, trust, clean UI, glassmorphism, data visualization
- **Era:** 2020s SaaS
- **Light/Dark Support:** ✓ Full support for both light and dark modes

### Design Intent

Analytics dashboards must feel powerful enough for data engineers yet approachable enough for executives. This tension shaped every decision:
- Metric cards with clear hierarchy and optional drill-down
- Charts that feel alive without being overwhelming
- Sidebar navigation with active state clarity
- Accent colors used sparingly and functionally
- Generous whitespace that suggests confidence and stability

## 1. Visual Theme and Atmosphere

### Dark Operational Theme (Baseline)

The interface should remain dark, immersive, and content-first. Near-black surfaces create a quiet stage where routing status, cost signals, and telemetry details carry focus.

Primary near-black ramp:

- Deepest background: #121212
- Core surface: #181818
- Interactive surface: #1f1f1f
- Elevated card alternates: #252525 and #272727

Design intent:

- The UI recedes so operational content stands out.
- Dense information is expected, but spacing rhythm prevents crowding.
- Premium feel comes from consistency and precision, not dramatic visual effects.

Key characteristics to preserve:

- Near-black immersive theme where chrome stays quiet
- Compact, scan-first typography
- Pill and circular control geometry
- Functional accent usage only
- Restrained separators and borders

### Corporate Blue SaaS Theme (Aspirational)

A modern analytics-forward aesthetic inspired by successful SaaS products. This theme emphasizes trust, clarity, and data storytelling through refined color usage and generous whitespace.

**Core Blue Palette:**
- **Dark Navy (Primary Background):** #0F172A — Deep, immersive primary surface
- **Royal Blue (Secondary Accent):** #1E40AF — Bold action and interactive highlights
- **Bright Blue (Tertiary Accent):** #3B82F6 — Data visualization and charts
- **Light Blue (Neutral Text):** #60A5FA — Secondary text and metadata
- **White (Surface):** #FFFFFF — Clean working surfaces and cards
- **Light Grey (Accent Background):** #F8FAFC — Subtle surface variation and contrast
- **Dark Grey (Additional Surface):** #334155 — Deep contrast for elevated elements

**Design Intent:**
- Corporate trust through restrained, professional color usage
- Blue as a confidence signal (stability, intelligence, reliability)
- Neutral surfaces with functional accent highlights
- Light and dark mode support with equivalent contrast and hierarchy
- Enterprise-grade visual polish without visual noise

## 2. Color Palette and Roles

### Two Palette Directions

#### Dark Operational (Baseline)

**Core Neutral Surfaces:**
- Near Black: #121212
- Dark Surface: #181818
- Mid Dark: #1f1f1f
- Dark Card: #252525
- Mid Card: #272727

**Text:**
- Primary text: #ffffff
- Secondary text: #b3b3b3
- Bright secondary: #cbcbcb
- Highest-emphasis light text: #fdfdfd

**Functional Accent and Semantic Colors:**
- Functional accent (action/active): #1ed760
- Error/negative: #f3727f
- Warning: #ffa42b
- Informational/announcement: #539df5

**Border and Separator Roles:**
- Button border on dark: #4d4d4d
- Light outline border: #7c7c7c
- Separator line: #b3b3b3
- Accent border variant: #1db954

#### Corporate Blue SaaS (Aspirational)

**Primary Brand Colors:**
- Dark Navy (#0F172A) — Primary surface, hero backgrounds, deep UI elements
- Royal Blue (#1E40AF) — Secondary accents, active states, links
- Bright Blue (#3B82F6) — Tertiary accents, chart highlights, CTAs
- Light Blue (#60A5FA) — Neutral accent, metadata text, borders

**Neutral Surfaces:**
- White (#FFFFFF) — Clean card backgrounds, panels, elevated surfaces
- Light Grey (#F8FAFC) — Subtle surface variation, page backgrounds
- Dark Grey (#334155) — Deep contrast for layered elevation

**Text Roles (Light Mode):**
- Primary text: #0F172A (Dark Navy)
- Secondary text: #334155 (Dark Grey)
- Tertiary text: #60A5FA (Light Blue)
- Positive/Success: #10B981 (Emerald)
- Warning: #F59E0B (Amber)
- Error/Negative: #EF4444 (Red)
- Informational: #3B82F6 (Bright Blue)

**Color Rules (Both Themes):**
- Accent is functional, never purely decorative
- Semantic colors signal state meaning only
- Most surfaces remain neutral to maintain enterprise trust
- Saturation capped at 80% to avoid eye strain
- No pure black (#000000) — use off-blacks or navy variants
- No oversaturated accent colors
- Glassmorphism surfaces: frosted glass effect with subtle blur and transparency

## 3. Typography Rules

### Font Families (Current/Baseline)

- Title: UiDisplay
- UI and body: UiText
- Fallback stack: Noto Sans Arabic, Noto Sans Hebrew, Noto Sans, Helvetica Neue, helvetica, arial, Hiragino Sans, Hiragino Kaku Gothic ProN, Meiryo, MS Gothic

### Font Families (Aspirational SaaS)

- **Primary Font:** Inter (Variable or static weights 400, 500, 600, 700)
- **Monospace:** JetBrains Mono — Used for code, metadata, and technical values
- **Fallback stack:** System UI fonts (-apple-system, BlinkMacSystemFont), segoe ui, helvetica, arial, sans-serif

**Inter Usage:**
- Versatile sans-serif optimized for screen readability
- Variable weight support for refined hierarchy without loading multiple font files
- Excellent at small sizes (UI labels, captions) and display sizes (hero headings)
- Tight letter-spacing option (-.02em) for impactful headlines

### Scale (Aspirational)

- **Hero/Display:** clamp(2.5rem, 5vw, 4rem) — Variable sizing for responsive impact
- **H1:** 2.25rem / 2.5rem (weight 700)
- **H2:** 1.5rem (weight 600)
- **H3/Subheading:** 1.125rem (weight 600)
- **Body:** 1rem / 1.6 line-height (weight 400)
- **Body Small:** 0.9375rem (weight 400)
- **Label/Caption:** 0.875rem (weight 500, slight letter-spacing)
- **Small:** 0.75rem (weight 400)
- **Micro:** 0.625rem (weight 400)

**Line Height & Spacing:**
- Display: 1.2 (tight for impact)
- Heading: 1.3
- Body: 1.6 (generous for reading comfort)
- UI: 1.4

### Hierarchy Table

| Role | Font | Size | Weight | Line Height | Letter Spacing | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Section Title | UiDisplay | 24px (1.50rem) | 700 | normal | normal | Bold section anchor |
| Feature Heading | UiText | 18px (1.13rem) | 600 | 1.30 | normal | Secondary heading layer |
| Body Bold | UiText | 16px (1.00rem) | 700 | normal | normal | Local emphasis |
| Body | UiText | 16px (1.00rem) | 400 | normal | normal | Standard reading text |
| Button Uppercase | UiText | 14px (0.88rem) | 600-700 | 1.00 | 1.4px-2px | text-transform uppercase |
| Button | UiText | 14px (0.88rem) | 700 | normal | 0.14px | Non-uppercase variant |
| Nav Link Bold | UiText | 14px (0.88rem) | 700 | normal | normal | Active nav |
| Nav Link | UiText | 14px (0.88rem) | 400 | normal | normal | Inactive nav |
| Caption Bold | UiText | 14px (0.88rem) | 700 | 1.50-1.54 | normal | Metadata emphasis |
| Caption | UiText | 14px (0.88rem) | 400 | normal | normal | Metadata default |
| Small Bold | UiText | 12px (0.75rem) | 700 | 1.50 | normal | Tags, counts |
| Small | UiText | 12px (0.75rem) | 400 | normal | normal | Fine print |
| Badge | UiText | 10.5px (0.66rem) | 600 | 1.33 | normal | text-transform capitalize |
| Micro | UiText | 10px (0.63rem) | 400 | normal | normal | Smallest text |

Typography principles:

- Bold/regular binary remains the main hierarchy engine.
- 600 is used sparingly for intermediate emphasis.
- Uppercase button labels establish a systematic control voice.
- Compact 10px-24px scale supports dense dashboards and fast scanning.

## 4. Shape Language and Component Styling

### Corner Radius Tokens (Aspirational)

- **Small (sm):** 12px — Primary use for buttons, small cards, input fields
- **Medium (md):** 24px — Feature cards, modals, dialog boxes
- **Large (lg):** 36px — Hero sections, large promotional areas
- **Pill/Full:** 9999px — Buttons with pill shape, search inputs, badges

**Baseline (Dark Operational):** 6px-8px for standard cards, 9999px for pills and circular controls

### Button Patterns

#### Aspirational SaaS Buttons

**Primary Button (Dark Navy Base):**
- Background: #0F172A (Dark Navy)
- Text: #60A5FA (Light Blue)
- Rounded: 12px
- Padding: 12px 16px (minimum touch target: 44px height)
- Font weight: 600
- Border: None
- Hover: 8% darken + subtle lift shadow
- Active: -1px translate (tactile press effect)
- Focus ring: 2px accent offset 2px

**Secondary Button (Outlined):**
- Background: transparent
- Border: 1.5px solid #60A5FA (Light Blue)
- Text: #0F172A (Dark Navy)
- Rounded: 12px
- Padding: 12px 16px
- Hover: subtle background fill (10% Light Blue)

**Ghost Button:**
- Background: transparent
- Border: 1px solid #D1D5DB (Light Grey)
- Text: #0F172A
- Hover: slight background tint

**Button Sizing:**
- Small: 8px 12px, 12px text, weight 600
- Default: 12px 16px, 14px text, weight 600
- Large: 14px 20px, 16px text, weight 700

#### Dark Operational Buttons (Baseline)

Dark Pill:

- Background: #1f1f1f
- Text: #ffffff or #b3b3b3
- Padding: 8px 16px
- Radius: 9999px
- Use: navigation pills, secondary actions

Dark Large Pill:

- Background: #181818
- Text: #ffffff
- Padding: 0px 43px
- Radius: 500px
- Use: primary navigation actions

Light Pill (rare):

- Background: #eeeeee
- Text: #181818
- Radius: 500px
- Use: exceptional contexts only

Outlined Pill:

- Background: transparent
- Text: #ffffff
- Border: 1px solid #7c7c7c
- Padding: 4px 16px 4px 36px
- Radius: 9999px
- Use: secondary actions with icon-leading layout

Circular Control:

- Background: #1f1f1f
- Text/icon: #ffffff
- Padding: 12px
- Radius: 50%
- Use: immediate action controls

### Cards and Containers

#### Aspirational SaaS Cards

- Background: #FFFFFF or #F8FAFC (light mode) / #1E40AF with opacity (glassmorphism dark mode)
- Radius: 12px (sm token) for standard cards, 24px (md) for feature cards
- Border: 1px solid #E5E7EB (light mode) or rgba(255,255,255,0.1) (dark mode)
- Shadow (light mode): 0 2px 12px rgba(0,0,0,0.06)
- Shadow (dark mode): 0 4px 20px rgba(0,0,0,0.25)
- Padding: 16px-24px
- Hover: subtle shadow increase, 2-3px lift
- Glassmorphism variant: backdrop-filter: blur(8px), semi-transparent background

**Metric Cards:**
- Headline value in H2 weight (600-700)
- Secondary metric below in smaller weight
- Optional delta/trend arrow
- Support color-coded status (error, warning, success)

#### Dark Operational Cards (Baseline)

- Background: #181818 or #1f1f1f
- Radius: 6px-8px for standard cards
- Borders: restrained, low contrast, consistent thickness
- Hover: subtle background shift only

### Inputs

#### Aspirational SaaS Inputs

- **Light Mode:**
  - Background: #F3F4F6 (Light Grey variant)
  - Text: #0F172A (Dark Navy)
  - Border: 1px #D1D5DB
  - Label above input, weight 500, 0.875rem
  - Radius: 12px
  - Padding: 10px 12px
  - Focus ring: 2px #3B82F6 offset 2px
  - Error text: #EF4444 below, weight 400, 0.75rem
  - Placeholder: #9CA3AF (Neutral Grey)

- **Search Field:**
  - Radius: 9999px (pill shape)
  - Padding: 12px 48px
  - Icon leading (search icon 16px, 12px left)
  - Background: #F3F4F6
  - Hover: slight background darken

#### Dark Operational Inputs (Baseline)

- Search field background: #1f1f1f
- Text: #ffffff
- Radius: 500px for search-led experiences
- Padding: 12px 96px 12px 48px
- Focus: visible border/outline with accessible contrast

### Navigation

#### Aspirational SaaS Navigation

- **Primary Navigation (Top or Sidebar):**
  - Background: #0F172A (Dark Navy) or white
  - Active item: #3B82F6 (Bright Blue) accent indicator + weight 600
  - Inactive item: #60A5FA (Light Blue) at weight 500
  - Hover: subtle background tint
  - Padding: 12px 16px per item

- **Breadcrumbs:**
  - Separator: "/" or "›"
  - Active: #0F172A weight 600
  - Inactive: #60A5FA weight 400
  - Size: 0.875rem

#### Dark Operational Navigation (Baseline)

- Dark sidebar style remains valid where appropriate
- Active item: higher weight and brighter text
- Inactive item: muted text
- Circular icon affordances are allowed for utility actions

## 5. Layout and Disclosure Model

### Spacing System

**Base Unit:** 8px (0.5rem)

**Scale:**
- Micro: 1px, 2px, 3px (borders, separators)
- Small: 4px, 5px, 6px (internal padding)
- Standard: 8px, 10px, 12px (component padding)
- Medium: 14px, 15px, 16px (section spacing)
- Large: 20px, 24px, 32px (card margins)
- XL: 40px, 48px, 56px, 64px (section gaps)

**Aspirational SaaS Rhythm:**
- Component internal padding: 12px-16px
- Card margins: 16px-20px
- Section vertical gaps: clamp(4rem, 8vw, 8rem)
- Sidebar width: 240-280px
- Max-content width: 1280px centered with 1.5rem (24px) side padding

### Grid System (Aspirational)

- **Primary Layout:** CSS Grid preferred for complex layouts
- **Max-width Containment:** 1280px centered with 1.5rem side padding
- **Hero Layout:** Split-screen (text left, visual/dashboard right)
- **Feature Sections:** Zig-zag alternating text+image rows (no 3-equal-columns)
- **Card Grid:** 1 column (mobile) → 2 columns (tablet) → 3+ columns (desktop)
- **No horizontal overflow** — responsive collapsing below 768px

### Density and Whitespace

- Dense by design, but not claustrophobic
- Micro-spacing consistency is mandatory
- Use separators and grouping to keep metrics legible
- Every pixel should support comprehension or action

### Progressive Disclosure Contract

1. Headline metric and status first
2. Supporting metrics second
3. Diagnostic detail on demand

This pattern applies across Live Stream, Cost Analytics, Governance, and Console.

### Grid and Layering Guidance

- Prefer structured, predictable layout variance (low variance by default)
- Keep major shells simple: navigation + primary content + focused utility zones
- Use explicit z-index contract for overlays and modals: base 0, sticky 100, overlay 200, modal 300, toast 500

## 6. Depth and Elevation

### Dark Operational (Baseline)

| Level | Treatment | Use |
| --- | --- | --- |
| Base (0) | #121212 | Page/root background |
| Surface (1) | #181818 / #1f1f1f | Cards, panels, navigation |
| Elevated (2) | Subtle shadow and/or border contrast | Hover cards, dropdowns |
| Dialog (3) | Stronger but restrained elevation | Modal/dialog surfaces |
| Inset | Recessed border treatment | Inputs and embedded panes |

### Aspirational SaaS Elevation & Glassmorphism

**Light Mode Depth:**
- Base: #FFFFFF
- Surface: #F8FAFC
- Elevated: Subtle shadow (0 2px 12px rgba(0,0,0,0.06))
- Dialog: Stronger shadow (0 10px 30px rgba(0,0,0,0.12))
- Hover state: +4px lift, shadow increase

**Dark Mode Glassmorphism:**
- Base: #0F172A (Dark Navy)
- Surface: Semi-transparent cards with backdrop-filter: blur(8px)
- Frosted Glass Effect: rgba(255,255,255,0.1) background with blur
- Elevated: rgba(59,130,246,0.1) (subtle blue tint with blur)
- Glow (subtle): Thin 1px highlight at top of cards for depth cue

**Z-index Contract:**
- Base: 0
- Sticky navigation: 100
- Overlay/menu: 200
- Modal: 300
- Toast/notification: 500

**Elevation Principles:**
- Do not use heavy shadows as primary hierarchy signals
- Use depth sparingly and consistently
- Prefer type and layout hierarchy over visual theatrics
- Glassmorphism adds sophistication without clutter
- Shadows should enhance, not dominate

## 7. Motion and Animation

### Allowed Triggers (Both Themes)

- Number/value changes (KPI deltas, counters)
- State transitions (ok to warning to critical)
- Expand/collapse and open/close interactions
- Page transitions (fade only)

### Aspirational SaaS Motion Details

**Entry Animations:**
- Fade + translate-Y (16px → 0) over 420ms ease-out
- Staggered cascades for lists: 80ms between items
- Counter animations: JS-driven number ticks with easing

**Hover States:**
- Subtle color shift + shadow adjustment over 200ms
- Card lift: 2-4px with shadow increase
- Button shade: 8% darken + subtle lift

**Active/Press States:**
- Scale-down: 98-99% at 100ms
- Tactile feedback: -1px translate down

**Scroll Animations:**
- Fade-in + slide-up on scroll trigger
- No parallax (performance consideration)
- Smooth reveal with ease-out curve

**Physics & Easing:**
- Ease-out curves primary (cubic-bezier(0.4, 0, 0.2, 1))
- Duration: 200-300ms for most interactions
- 420ms for full-page transitions

### Disallowed Patterns (Both Themes)

- Always-on ambient motion in idle dashboards
- Decorative motion unrelated to state change
- Animation that competes with data readability
- Unnecessary bouncing or spring animations

### Performance Requirements

- Only transform and opacity animated
- No layout-triggering properties (width, height, position)
- GPU acceleration via will-change (sparingly)
- 60fps target on all interactions

## 8. Responsive Behavior

Primary target remains desktop dashboard usage, with resilient fallback behavior for smaller widths.

Suggested breakpoints:

| Name | Width | Key Change |
| --- | --- | --- |
| Mobile Small | <425px | Tight single-column compression |
| Mobile | 425-576px | Single-column with stacked controls |
| Tablet | 576-768px | Two-column summary/detail splits |
| Tablet Large | 768-896px | Expanded control rows |
| Desktop Small | 896-1024px | Sidebar optional/compact |
| Desktop | 1024-1280px | Full operational layout |
| Large Desktop | >1280px | Expanded data surfaces |

Collapsing strategy:

- Navigation: full -> compact -> hidden pattern as needed
- Dense grids: reduce columns progressively
- Headline metrics remain pinned near top of view
- Disclosure hierarchy remains identical at every breakpoint

## 9. Do and Do Not

### Dark Operational Theme (Baseline)

**Do:**
- Use near-black depth through shade variation
- Keep accent usage functional and intentional
- Preserve pill/circle geometry where it supports recognition
- Keep typography compact and hierarchy weight-driven
- Keep dashboards calm at idle
- Use restrained separators and borders

**Do Not:**
- Do not use gradients for emphasis
- Do not use decorative drop shadows to force hierarchy
- Do not expand color usage beyond functional roles
- Do not front-load feature walls
- Do not add ambient idle animation

### Aspirational SaaS Theme

**Do:**
- ✓ Navbar + Hero with mockup of dashboard
- ✓ Features + Integrations sections
- ✓ Testimonials + Pricing tiers
- ✓ Security/LGPD/compliance section
- ✓ Subtle scroll animations with fade-in/slide-up
- ✓ Meta tags for SEO and social sharing
- ✓ Footer with documentation and support links
- ✓ Adequate color contrast (WCAG AA minimum)
- ✓ Use glossy/frosted effects sparingly with blur
- ✓ Support full light and dark mode variants
- ✓ Use responsive grid with no horizontal overflow
- ✓ Lazy-load images and optimize for Core Web Vitals

**Do Not:**
- ✗ No emojis in UI — use icon system only (Lucide, Heroicons)
- ✗ No pure black (#000000) — use off-black or charcoal variants
- ✗ No oversaturated accent colors (saturation cap: 80%)
- ✗ No 3-column equal-width feature layouts — use zig-zag or asymmetric grid
- ✗ No `h-screen` CSS — use `min-h-[100dvh]` instead
- ✗ No clichéd AI copywriting ("Elevate", "Seamless", "Unleash", "Next-Gen")
- ✗ No broken external image links — use picsum.photos or inline SVG
- ✗ No generic lorem ipsum in demos — use realistic data

## 10. Component Patterns to Reuse

### Aspirational SaaS Patterns

**Metric Cards:**
- Clear headline value in bold (H2 weight: 600-700)
- Delta/trend indicator (up/down arrow + percentage)
- Secondary metric or time period below
- Color-coded status (success: emerald, warning: amber, error: red)
- Optional drill-down link (small, secondary color)
- Padding: 20px-24px, shadow on hover

**Chart Containers:**
- Restrained chrome (minimal borders, clean labels)
- Strong axis labeling and legend
- Smooth animations on data update
- Responsive: stack/reflow below tablet breakpoints
- Subtitle: light grey text, 0.875rem, below title

**Feature Cards (Marketing):**
- Icon above title (48-64px, accent color)
- H3 heading (weight 600, 1.125rem)
- Body text (2-3 sentences max, secondary grey)
- Optional CTA link or button
- Hover: subtle shadow increase + color shift

**Section Hero:**
- Headline: clamp(2.5rem, 5vw, 4rem), weight 700
- Subheading: 1.25rem, weight 400, secondary color
- Call-to-action button (primary or secondary style)
- Optional illustration/screenshot on right (split-screen layout)

**Buttons:**
- Primary: Dark Navy background, Light Blue text, 12px radius
- Secondary: Outlined, Light Blue border, Navy text
- Icon+text: Icon leading (16px), 8px gap
- Full-width on mobile, auto on desktop

**Inputs & Form Elements:**
- Label above, weight 500, 0.875rem
- 1px border, Light Blue focus ring
- Error state: red text below, 0.75rem
- Placeholder: neutral grey
- Disabled: reduced opacity (60%)

**Skeleton States:**
- Shimmer animation matching component dimensions
- No circular spinners — use rectangular placeholders
- Pulse animation: opacity fade 1.2s ease-in-out infinite

**Empty States:**
- Large icon (64-96px, icon system)
- Descriptive headline (weight 600)
- Brief explanation text (secondary color)
- One clear action button (primary style)
- No decorative illustrations required

### Dark Operational Baseline Patterns

- Metric cards with clear headline value, delta/status, and optional drill-down
- Chart containers with restrained chrome and strong labeling
- Secondary and ghost buttons with clear active/focus states
- Inputs with explicit labels and accessible focus ring
- Skeleton states that mirror final layout proportions
- Empty states with icon + explanation + one clear action

## 11. Practical Prompting Guide for Design/Build Tasks

### Dark Operational Prompts

- Build a dense dark metric card with near-black surfaces, 8px spacing rhythm, restrained border, strong value hierarchy, and no decorative gradients.
- Design a pill control row where active state is shown by weight and subtle surface shift, not shadow glow.
- Create an expandable diagnostics panel with summary-first default and calm open/close transition.
- Implement chart containers where motion appears only when values update or thresholds are crossed.
- Build dashboard navigation with compact typography, clear active/inactive contrast, and predictable spacing.

### Aspirational SaaS Prompts

- Build a metric card with clear headline value, delta indicator, and shadow-on-hover using #0F172A backgrounds and #60A5FA accent text.
- Design a corporate SaaS landing page with hero section (split-screen text+dashboard mockup), features in zig-zag layout, testimonials, and pricing.
- Create a data visualization chart with clean labels, smooth animations on update, and responsive reflow below 768px.
- Implement a button row with primary (Navy bg, Light Blue text, 12px radius, 8% hover darken) and secondary (outlined, blue border) variants.
- Build a full-page navigation with active indicator in accent blue, responsive collapse to hamburger below 768px, and sticky positioning.
- Design a glassmorphic card overlay (backdrop-filter blur, 10% white transparency, top highlight glow) that layers above content.

## 12. Acceptance Checklist

### Dark Operational Theme

- Dense screens remain readable with consistent spacing rhythm
- Visual hierarchy is driven by type and structure, not decorative effects
- Idle screens remain still; motion appears only on meaningful change
- Palette usage stays restrained and function-first
- Default views show headline metrics before deeper detail
- Progressive disclosure is consistent across tabs and dialogs
- Focus and keyboard states remain visible and predictable

### Aspirational SaaS Theme

**Visual Design:**
- ✓ Color palette limited to core branding (Navy, Blues, Greys) + semantic colors
- ✓ No pure black (#000000) used anywhere
- ✓ Saturation capped at 80% on all accent colors
- ✓ Corner radius consistent: 12px (buttons, cards), 24px (modals), 9999px (pills)
- ✓ Glassmorphism effects: blur(8px) + 10% transparency on dark cards
- ✓ Shadows follow light physics: soft (0 2px 12px for standard), stronger (0 10px 30px for modals)

**Typography:**
- ✓ Inter font family used throughout (fallback to system fonts)
- ✓ Font sizes follow established scale (no arbitrary sizes)
- ✓ Font weights: 400 (body), 500 (labels), 600 (subheadings), 700 (headlines)
- ✓ Line heights match scale: 1.2 (display), 1.3 (headings), 1.6 (body)
- ✓ Contrast ratio ≥ 4.5:1 for body text, ≥ 3:1 for UI elements (WCAG AA)

**Spacing & Layout:**
- ✓ 8px base unit applied consistently
- ✓ No horizontal overflow at any breakpoint
- ✓ Responsive collapse: 1 col (mobile) → 2 col (tablet) → 3+ col (desktop)
- ✓ Max-content width: 1280px with 24px side padding
- ✓ Grid-based layout (CSS Grid or structured flexbox)

**Components:**
- ✓ Buttons: proper sizing (44px min-height), clear active/focus states, no shadow-only affordances
- ✓ Cards: 12-24px radius, subtle shadows, 1px border, consistent padding
- ✓ Inputs: label above, focus ring visible (2px offset), error state clear
- ✓ Metric cards: headline value prominent, delta indicator clear, optional drill-down
- ✓ Empty states: icon + text + action (no generic placeholders)

**Motion:**
- ✓ Entrance animations: fade + translate-Y over 420ms ease-out
- ✓ Interactions: 200-300ms duration, ease-out easing
- ✓ Hover states: color shift + shadow adjustment, no layout jumps
- ✓ No ambient idle animation
- ✓ Performance: transform/opacity only, 60fps target

**Responsiveness:**
- ✓ Navigation adapts: full → compact → hamburger as viewport shrinks
- ✓ Charts reflow smoothly (no clipping, stacking when needed)
- ✓ Touch targets ≥ 44x44px
- ✓ Text readable at all sizes (no font-size clipping)

**Accessibility:**
- ✓ Color not the only signal (icons, text, shape variations)
- ✓ Focus ring visible on all interactive elements
- ✓ Sufficient contrast for all text + backgrounds
- ✓ Form labels properly associated with inputs
- ✓ Skip links if applicable
- ✓ ARIA labels for icon-only buttons
