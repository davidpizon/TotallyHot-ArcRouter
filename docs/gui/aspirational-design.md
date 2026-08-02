# Aspirational Design

This document defines the aspirational GUI direction for TotallyHotArcRouter. It is prescriptive for future redesign work and complements the shipping-state references in DESIGN.md and MOTION.md.

Three design directions are documented here:
1. **Dark Operational Theme** (current baseline) — immersive near-black surfaces with functional accents
2. **Arc Router Admin Dashboard** (aspirational) — modern routing administration aesthetic with professional trust signals
3. **TotallyHot Arc Router** (content-first dark) — routing visualization-focused immersive experience with heavy elevation

## Design Principles

- Data density without claustrophobia — pack information tight but let each metric breathe through consistent spacing and restrained borders.
- Hierarchy through weight, not decoration — use type scale and subtle background shifts to guide the eye, never gradients or drop shadows for emphasis.
- Motion as meaning — animate only when a number changes or a state transitions; idle dashboards should feel calm, not performative.
- Trust through restraint — limit palette usage to a small functional set plus neutrals; enterprise buyers read visual noise as immaturity.
- Progressive disclosure over feature walls — show the headline metric first, then let users drill down; complexity is earned, not dumped.
- Clean B2B aesthetics — glassmorphism with subtle depth cues, generous whitespace, and accessible contrast throughout.

## TotallyHot Arc Router Direction (Content-First Dark Theme)

The TotallyHot Arc Router aesthetic wraps operators in a near-black cocoon where routing visualizations and traffic data become the primary source of color. This is "content-first darkness" — the UI recedes into shadow so routes, configurations, and real-time metrics can glow.

### Visual Characteristics

- **Theme:** Dark, immersive, routing-focused, operational
- **Primary Accent:** TotallyHot Green (#1ed760) — functional only, never decorative
- **Surfaces:** Near-black ramp (#121212, #181818, #1f1f1f)
- **Shadows:** Heavy and dramatic (0.3–0.5 opacity) for strong elevation on dark backgrounds
- **Geometry:** Pill buttons (500px–9999px), circular controls (50%)
- **Typography:** System UI stack with Arabic, Hebrew, Cyrillic, Greek, Devanagari, CJK support — compact (10px–24px), bold/regular binary, uppercase labels with wide tracking
- **Color Source:** Route visualizations and traffic dashboards provide all UI color — the interface itself is achromatic
- **Design Philosophy:** Theater-like environment where operational data is the star, UI is the stage

### Key Principles

- **Content-first darkness:** UI disappears behind routes, configurations, and real-time traffic visualizations
- **Pill and circle geometry:** All buttons are rounded (pill or circle), creating a premium, touch-optimized feel
- **Heavy shadows for elevation:** Shadows on dark backgrounds must be heavy (0.3–0.5 opacity) to be visible
- **Global script support:** Typography stack includes Arabic, Hebrew, Cyrillic, Greek, Devanagari, and CJK
- **Achromatic UI + single accent:** Green is functional only; the complete palette is green + grays
- **Compact typography:** 10px–24px range, designed for scanning routes and configurations quickly

## Aspirational Arc Router Admin Dashboard Direction

The aspirational design borrows from successful routing and network administration platforms that established a shared visual language around 2016+. These products realized that a gorgeous dashboard screenshot above the fold demonstrates capability more effectively than a feature list.

### Visual Characteristics

- **Density:** 8/10 — Dense but breathable
- **Variance:** 2/10 — Highly structured and predictable
- **Motion:** 4/10 — Subtle, meaningful, performant
- **Style:** Professional, Clean, Operations-Focused
- **Keywords:** Arc Router admin dashboard, routing metrics, route cards, professional blue, enterprise, network operations, route visualization, integrations, trust, clean UI, glassmorphism, data visualization
- **Era:** 2020s Enterprise Admin Tools
- **Light/Dark Support:** ✓ Full support for both light and dark modes

### Design Intent

Routing administration dashboards must feel powerful enough for network engineers yet approachable enough for operations managers. This tension shaped every decision:
- Metric cards with clear hierarchy and optional drill-down
- Visualizations that feel responsive without being overwhelming
- Sidebar navigation with active state clarity
- Accent colors used sparingly and functionally
- Generous whitespace that suggests confidence and professionalism

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

### Arc Router Admin Dashboard Theme (Aspirational)

A modern operations-forward aesthetic inspired by successful routing and network administration platforms. This theme emphasizes trust, clarity, and operational transparency through refined color usage and generous whitespace.

**Core Blue Palette:**
- **Dark Navy (Primary Background):** #0F172A — Deep, immersive primary surface
- **Royal Blue (Secondary Accent):** #1E40AF — Bold action and interactive highlights
- **Bright Blue (Tertiary Accent):** #3B82F6 — Route visualization and status indicators
- **Light Blue (Neutral Text):** #60A5FA — Secondary text and metadata
- **White (Surface):** #FFFFFF — Clean working surfaces and cards
- **Light Grey (Accent Background):** #F8FAFC — Subtle surface variation and contrast
- **Dark Grey (Additional Surface):** #334155 — Deep contrast for elevated elements

**Design Intent:**
- Professional trust through restrained, operational color usage
- Blue as a confidence signal (stability, reliability, security)
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

#### Arc Router Admin Dashboard (Aspirational)

**Primary Brand Colors:**
- Dark Navy (#0F172A) — Primary surface, hero backgrounds, deep UI elements
- Royal Blue (#1E40AF) — Secondary accents, active states, links
- Bright Blue (#3B82F6) — Tertiary accents, route highlights, CTAs
- Light Blue (#60A5FA) — Neutral accent, metadata text, borders

**Neutral Surfaces:**
- White (#FFFFFF) — Clean card backgrounds, panels, elevated surfaces
- Light Grey (#F8FAFC) — Subtle surface variation, page backgrounds
- Dark Grey (#334155) — Deep contrast for layered elevation

**Text Roles (Light Mode):**
- Primary text: #0F172A (Dark Navy)
- Secondary text: #334155 (Dark Grey)
- Tertiary text: #60A5FA (Light Blue)
- Positive/Success: #10B981 (Emerald) — Healthy routes
- Warning: #F59E0B (Amber) — Degraded routes
- Error/Negative: #EF4444 (Red) — Failed routes
- Informational: #3B82F6 (Bright Blue) — Status updates

#### TotallyHot Arc Router (Content-First Dark)

**Primary Brand:**
- TotallyHot Green (#1ed760) — Primary brand accent, action buttons, active states, CTAs (functional only, never decorative)

**Near Black Ramp:**
- Deepest background (#121212) — Base surface, darkest layer
- Dark surface (#181818) — Cards, containers, elevated surfaces
- Mid dark (#1f1f1f) — Button backgrounds, interactive surfaces

**Text:**
- White (#ffffff) — Primary text
- Silver (#b3b3b3) — Secondary text, muted labels, inactive nav
- Near white (#cbcbcb) — Slightly brighter secondary text
- Light (#fdfdfd) — Maximum emphasis text

**Semantic Colors:**
- Negative red (#f3727f) — Error states, route failures
- Warning orange (#ffa42b) — Warning states, degraded routes
- Announcement blue (#539df5) — Informational states, status updates

**Surface & Border:**
- Dark card (#252525) — Elevated card surface
- Mid card (#272727) — Alternate card surface
- Border gray (#4d4d4d) — Button borders on dark
- Light border (#7c7c7c) — Outlined button borders, muted links
- Separator (#b3b3b3) — Divider lines
- Light surface (#eeeeee) — Light-mode buttons (rare, configuration-only)
- TotallyHot green border (#1db954) — Green accent border variant

**Shadows (TotallyHot-specific):**
- Heavy: rgba(0,0,0,0.5) 0px 8px 24px — Dialogs, menus, elevated panels
- Medium: rgba(0,0,0,0.3) 0px 8px 8px — Cards, dropdowns
- Inset border: rgb(18,18,18) 0px 1px 0px, rgb(124,124,124) 0px 0px 0px 1px inset — Input tactile quality

**Color Rules (All Themes):**
- Accent is functional, never purely decorative
- Semantic colors signal state meaning only
- Most surfaces remain neutral to maintain enterprise trust
- Saturation capped at 80% to avoid eye strain
- No pure black (#000000) — use off-blacks or navy variants
- No oversaturated accent colors
- Glassmorphism surfaces: frosted glass effect with subtle blur and transparency
- TotallyHot: Route visualizations are the primary color source; UI remains achromatic

## 3. Typography Rules

### Font Families (Current/Baseline)

- Title: UiDisplay
- UI and body: UiText
- Fallback stack: Noto Sans Arabic, Noto Sans Hebrew, Noto Sans, Helvetica Neue, helvetica, arial, Hiragino Sans, Hiragino Kaku Gothic ProN, Meiryo, MS Gothic

### Font Families (Arc Router Admin Dashboard)

- **Primary Font:** Inter (Variable or static weights 400, 500, 600, 700)
- **Monospace:** JetBrains Mono — Used for code, metadata, and technical values
- **Fallback stack:** System UI fonts (-apple-system, BlinkMacSystemFont), segoe ui, helvetica, arial, sans-serif

### Font Families (TotallyHot Arc Router)

- **Primary Font:** System UI stack (Segoe UI, -apple-system, BlinkMacSystemFont, Arial)
- **Fallback Stack:** Noto Sans Arabic, Noto Sans Hebrew, Noto Sans, Helvetica Neue, helvetica, arial, Hiragino Sans, Hiragino Kaku Gothic ProN, Meiryo, MS Gothic
  - **Note:** Extensive global script support for enterprise reach (Arabic, Hebrew, Cyrillic, Greek, Devanagari, CJK)

**TotallyHot Typography Principles:**
- **Bold/regular binary:** Most text is either weight 700 (bold) or 400 (regular), with 600 used sparingly
- **Uppercase buttons:** All button labels use uppercase + wide letter-spacing (1.4px–2px) for systematic "label" voice
- **Compact sizing:** 10px–24px range — narrower than most systems, designed for scanning routes and configurations quickly
- **Semibold as accent:** Weight 600 used for secondary emphasis and section headings

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

### TotallyHot Arc Router Typography Hierarchy

| Role | Font | Size | Weight | Line Height | Letter Spacing | Notes |
|------|------|------|--------|-------------|----------------|-------|
| Section Title | System UI | 24px (1.50rem) | 700 | normal | normal | Bold section anchor |
| Feature Heading | System UI | 18px (1.13rem) | 600 | 1.30 | normal | Semibold section heads |
| Body Bold | System UI | 16px (1.00rem) | 700 | normal | normal | Emphasized text |
| Body | System UI | 16px (1.00rem) | 400 | normal | normal | Standard body |
| Button Uppercase | System UI | 14px (0.88rem) | 600–700 | 1.00 | 1.4px–2px | `text-transform: uppercase` |
| Button | System UI | 14px (0.88rem) | 700 | normal | 0.14px | Standard button |
| Nav Link Bold | System UI | 14px (0.88rem) | 700 | normal | normal | Navigation (active) |
| Nav Link | System UI | 14px (0.88rem) | 400 | normal | normal | Navigation (inactive) |
| Caption Bold | System UI | 14px (0.88rem) | 700 | 1.50–1.54 | normal | Bold metadata |
| Caption | System UI | 14px (0.88rem) | 400 | normal | normal | Metadata |
| Small Bold | System UI | 12px (0.75rem) | 700 | 1.50 | normal | Tags, counts |
| Small | System UI | 12px (0.75rem) | 400 | normal | normal | Fine print |
| Badge | System UI | 10.5px (0.66rem) | 600 | 1.33 | normal | `text-transform: capitalize` |
| Micro | System UI | 10px (0.63rem) | 400 | normal | normal | Smallest text |

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

### Corner Radius Tokens

#### TotallyHot Arc Router Scale

- **Minimal:** 2px — Badges, explicit tags
- **Subtle:** 4px — Inputs, small elements
- **Standard:** 6px–8px — Route visualization containers, cards
- **Comfortable:** 8px–10px — Sections, dialogs
- **Medium:** 10px–20px — Panels, overlay elements
- **Large Pill:** 100px–500px — Large pill buttons
- **Full Pill:** 9999px — Search input, navigation pills
- **Circle:** 50% — Action buttons, avatars, circular controls

**Philosophy:** Pill and circle geometry is central to TotallyHot's premium, touch-optimized feel. All buttons use rounded corners; square buttons break identity.

#### Arc Router Admin Dashboard Scale

- **Small (sm):** 12px — Primary use for buttons, small cards, input fields
- **Medium (md):** 24px — Configuration cards, modals, dialog boxes
- **Large (lg):** 36px — Hero sections, large content areas
- **Pill/Full:** 9999px — Buttons with pill shape, search inputs, badges

**Baseline (Dark Operational):** 6px-8px for standard cards, 9999px for pills and circular controls

### Button Patterns

#### Arc Router Admin Dashboard Buttons

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
- Use: Primary actions (execute route, save configuration)

**Secondary Button (Outlined):**
- Background: transparent
- Border: 1.5px solid #60A5FA (Light Blue)
- Text: #0F172A (Dark Navy)
- Rounded: 12px
- Padding: 12px 16px
- Hover: subtle background fill (10% Light Blue)
- Use: Secondary actions (cancel, clear, reset)

**Ghost Button:**
- Background: transparent
- Border: 1px solid #D1D5DB (Light Grey)
- Text: #0F172A
- Hover: slight background tint
- Use: Tertiary actions (help, info links)

**Button Sizing:**
- Small: 8px 12px, 12px text, weight 600
- Default: 12px 16px, 14px text, weight 600
- Large: 14px 20px, 16px text, weight 700

#### TotallyHot Arc Router Buttons

**Dark Pill (Secondary Actions)**
- Background: #1f1f1f
- Text: #ffffff or #b3b3b3
- Padding: 8px 16px
- Radius: 9999px (full pill)
- Font: System UI 14px, weight 600–700, uppercase, letter-spacing 1.4px–2px
- Use: Navigation pills, secondary actions

**Dark Large Pill (Primary Navigation)**
- Background: #181818
- Text: #ffffff
- Padding: 0px 43px
- Radius: 500px
- Font: System UI 14px, weight 700, uppercase
- Use: Primary app navigation buttons

**Light Pill (Configuration/Administration)**
- Background: #eeeeee
- Text: #181818
- Radius: 500px
- Use: Light-mode CTAs (rare, configuration contexts only)

**Outlined Pill (Secondary Action)**
- Background: transparent
- Text: #ffffff
- Border: 1px solid #7c7c7c
- Padding: 4px 16px 4px 36px (asymmetric for leading icon)
- Radius: 9999px
- Font: System UI 14px, weight 600
- Use: Secondary actions, toggle options

**Circular Action Button (Route-Focused)**
- Background: #1f1f1f (or route visualization overlay)
- Icon: #ffffff or #1ed760 (TotallyHot Green)
- Padding: 12px
- Radius: 50% (perfect circle)
- Use: Route execution, primary routing interaction
- Hover: slight background shift, subtle lift shadow

**Button Uppercase Convention:**
- All button labels: uppercase + wide letter-spacing (1.4px–2px)
- Creates systematic "label" voice distinct from body text
- Signals interactive affordance at a glance

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

#### Arc Router Admin Dashboard Cards

- Background: #FFFFFF or #F8FAFC (light mode) / #1E40AF with opacity (glassmorphism dark mode)
- Radius: 12px (sm token) for standard cards, 24px (md) for configuration cards
- Border: 1px solid #E5E7EB (light mode) or rgba(255,255,255,0.1) (dark mode)
- Shadow (light mode): 0 2px 12px rgba(0,0,0,0.06)
- Shadow (dark mode): 0 4px 20px rgba(0,0,0,0.25)
- Padding: 16px-24px
- Hover: subtle shadow increase, 2-3px lift
- Glassmorphism variant: backdrop-filter: blur(8px), semi-transparent background

**Status/Metric Cards:**
- Headline value in H2 weight (600-700)
- Secondary metric or status below in smaller weight
- Optional indicator/status badge
- Support color-coded status (error, warning, success, healthy)

#### TotallyHot Arc Router Cards

- Background: #181818 or #1f1f1f
- Radius: 6px–8px (standard cards), 10px–20px (panels, overlay elements)
- Border: None (or very subtle)
- Shadow (hover): rgba(0,0,0,0.3) 0px 8px 8px
- Shadow (elevated): rgba(0,0,0,0.5) 0px 8px 24px
- Hover: slight background lightening, shadow increases
- Padding: Varies by content (route configuration cards, route grids, status rows)
- Typography: Title (14px weight 700, System UI), subtitle (14px weight 400, #b3b3b3)

**Route Visualization Container:**
- Radius: 6px (subtle, visualization should shine)
- Shadow: Medium (0.3 opacity) for lift
- Aspect ratio: Flexible (grid or list layout)
- Hover: shadow increase, slight scale (102%)

#### Dark Operational Cards (Baseline)

- Background: #181818 or #1f1f1f
- Radius: 6px-8px for standard cards
- Borders: restrained, low contrast, consistent thickness
- Hover: subtle background shift only

### Inputs

#### Arc Router Admin Dashboard Inputs

- **Light Mode:**
  - Background: #F3F4F6 (Light Grey variant)
  - Text: #0F172A (Dark Navy)
  - Border: 1px #D1D5DB
  - Label above input, weight 500, 0.875rem
  - Radius: 12px
  - Padding: 10px 12px
  - Focus ring: 2px #3B82F6 offset 2px
  - Validation text: #EF4444 (error) or #10B981 (success) below, weight 400, 0.75rem
  - Placeholder: #9CA3AF (Neutral Grey)

- **Search Field:**
  - Radius: 9999px (pill shape)
  - Padding: 12px 48px
  - Icon leading (search icon 16px, 12px left)
  - Background: #F3F4F6
  - Hover: slight background darken

#### TotallyHot Arc Router Inputs

**Search/Filter Input:**
- Background: #1f1f1f
- Text: #ffffff
- Radius: 500px (full pill for search-led experience)
- Padding: 12px 96px 12px 48px (icon-aware on both sides)
- Font: System UI 14px, weight 400
- Border: Inset shadow: rgb(18,18,18) 0px 1px 0px, rgb(124,124,124) 0px 0px 0px 1px inset
- Focus: Border becomes #000000, outline 1px solid
- Placeholder: #b3b3b3 (silver, muted)

**General Input/Configuration Field:**
- Background: #1f1f1f
- Border: Inset shadow (tactile, recessed quality)
- Focus ring: 1px solid border
- Radius: 4px–8px (smaller than buttons)

#### Dark Operational Inputs (Baseline)

- Search field background: #1f1f1f
- Text: #ffffff
- Radius: 500px for search-led experiences
- Padding: 12px 96px 12px 48px
- Focus: visible border/outline with accessible contrast

### Navigation

#### Arc Router Admin Dashboard Navigation

- **Primary Navigation (Top or Sidebar):**
  - Background: #0F172A (Dark Navy) or white
  - Active item: #3B82F6 (Bright Blue) accent indicator + weight 600
  - Inactive item: #60A5FA (Light Blue) at weight 500
  - Hover: subtle background tint
  - Padding: 12px 16px per item
  - Sections: Dashboard, Routes, Configuration, Monitoring, Settings

- **Breadcrumbs:**
  - Separator: "/" or "›"
  - Active: #0F172A weight 600
  - Inactive: #60A5FA weight 400
  - Size: 0.875rem
  - Format: Dashboard → Routes → [specific route] → [detail]

#### TotallyHot Arc Router Navigation

**Sidebar Navigation:**
- Background: #121212 (darkest background)
- Width: Fixed sidebar (240–280px typical)
- Logo: Top-left, TotallyHot green
- Active item: 14px weight 700, #ffffff text
- Inactive item: 14px weight 400, #b3b3b3 text
- Hover (inactive): text brightens to #cbcbcb or #ffffff
- Padding per item: 12px 16px
- Circular icon buttons (50% radius) for utility actions (new configuration, etc.)
- Responsive: Sidebar collapses to collapsed → hidden as viewport shrinks

**Bottom Navigation (Mobile):**
- Visible at all breakpoints but especially mobile
- Icons + text below
- Fixed to bottom of viewport
- Active item: #1ed760 (TotallyHot Green) icon/text
- Inactive item: #b3b3b3 icon/text

**Active Route/Status Bar:**
- Maintained at all responsive breakpoints
- Background: #181818 or slightly elevated
- Fixed to bottom (above mobile nav if present)
- Route info: name (14px weight 700), status (14px weight 400, #b3b3b3)
- Control buttons: circular action buttons

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

**Arc Router Admin Dashboard Rhythm:**
- Component internal padding: 12px-16px
- Card margins: 16px-20px
- Section vertical gaps: clamp(4rem, 8vw, 8rem)
- Sidebar width: 240-280px (collapsible on mobile)
- Max-content width: 1280px centered with 1.5rem (24px) side padding
- Content area fills remaining space after sidebar

**TotallyHot Arc Router Layout:**
- Base unit: 8px
- Scale: 1px, 2px, 3px, 4px, 5px, 6px, 8px, 10px, 12px, 14px, 15px, 16px, 20px
- Sidebar (fixed): 240–280px width
- Main content area: fills remaining space
- Status bar (fixed): bottom of screen
- Content density: High — routes and configurations are tightly spaced
- Whitespace philosophy: Dark compression — the dark background provides visual rest without needing large gaps

### Grid System (Arc Router)

- **Primary Layout:** CSS Grid preferred for complex layouts
- **Max-width Containment:** 1280px centered with 1.5rem side padding
- **Dashboard Layout:** Sidebar + main content area (split layout on desktop, stacked on mobile)
- **Configuration Sections:** Structured layouts with grouped settings (no 3-equal-columns)
- **Card Grid:** 1 column (mobile) → 2 columns (tablet) → 3+ columns (desktop)
- **No horizontal overflow** — responsive collapsing below 768px
- **Route Grid:** Adaptive layout for route cards and metrics

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

### TotallyHot Arc Router Elevation & Shadows

**Elevation Levels:**

| Level | Treatment | Use |
| --- | --- | --- |
| Base (Level 0) | #121212 background | Deepest layer, page/sidebar background |
| Surface (Level 1) | #181818 or #1f1f1f | Cards, containers, main content |
| Elevated (Level 2) | rgba(0,0,0,0.3) 0px 8px 8px | Dropdown menus, hover cards, visualization lift |
| Dialog (Level 3) | rgba(0,0,0,0.5) 0px 8px 24px | Modals, overlays, contextual menus |
| Inset (Border) | rgb(18,18,18) 0px 1px 0px, rgb(124,124,124) 0px 0px 0px 1px inset | Input borders, tactile recessed quality |

**Shadow Philosophy (TotallyHot-Specific):**
- Shadows are notably heavy for a dark-themed operational interface
- Medium shadow (0.3 opacity) at 8px blur provides subtle card lift
- Heavy shadow (0.5 opacity) at 24px blur creates dramatic "floating in darkness" effect for dialogs/menus
- Inset border-shadow combo on inputs creates recessed, tactile quality
- Light shadows are invisible on dark backgrounds — must use heavy opacity

**Why Heavy Shadows Work:**
- Dark backgrounds need strong shadows to show depth
- Premium operational device metaphor: tactile, physical appearance
- Theater-like environment: shadows create spotlight effect on operational data

### Dark Operational (Baseline)

| Level | Treatment | Use |
| --- | --- | --- |
| Base (0) | #121212 | Page/root background |
| Surface (1) | #181818 / #1f1f1f | Cards, panels, navigation |
| Elevated (2) | Subtle shadow and/or border contrast | Hover cards, dropdowns |
| Dialog (3) | Stronger but restrained elevation | Modal/dialog surfaces |
| Inset | Recessed border treatment | Inputs and embedded panes |

### Arc Router Admin Dashboard Elevation & Glassmorphism

**Light Mode Depth:**
- Base: #FFFFFF
- Surface: #F8FAFC
- Elevated: Subtle shadow (0 2px 12px rgba(0,0,0,0.06))
- Dialog/Configuration Modal: Stronger shadow (0 10px 30px rgba(0,0,0,0.12))
- Hover state: +4px lift, shadow increase

**Dark Mode Glassmorphism:**
- Base: #0F172A (Dark Navy)
- Surface: Semi-transparent cards with backdrop-filter: blur(8px)
- Frosted Glass Effect: rgba(255,255,255,0.1) background with blur
- Elevated: rgba(59,130,246,0.1) (subtle blue tint with blur, for active routes)
- Glow (subtle): Thin 1px highlight at top of cards for depth cue

**Z-index Contract:**
- Base: 0
- Sticky navigation/sidebar: 100
- Dropdown/popover: 200
- Modal/dialog: 300
- Toast/notification/alerts: 500

**Elevation Principles:**
- Do not use heavy shadows as primary hierarchy signals
- Use depth sparingly and consistently
- Prefer type and layout hierarchy over visual theatrics
- Glassmorphism adds sophistication without clutter
- Shadows should enhance, not dominate
- Route status should be clear through color and positioning, not shadow alone

## 7. Motion and Animation

### Allowed Triggers (Both Themes)

- Number/value changes (KPI deltas, counters)
- State transitions (ok to warning to critical)
- Expand/collapse and open/close interactions
- Page transitions (fade only)

### Arc Router Admin Dashboard Motion Details

**Entry Animations:**
- Fade + translate-Y (16px → 0) over 420ms ease-out
- Staggered cascades for route lists: 80ms between items
- Metric updates: JS-driven number ticks with easing

**Hover States:**
- Subtle color shift + shadow adjustment over 200ms
- Card lift: 2-4px with shadow increase
- Button shade: 8% darken + subtle lift
- Route row: highlight with background tint

**Active/Press States:**
- Scale-down: 98-99% at 100ms
- Tactile feedback: -1px translate down
- Route activation: visual feedback with status change

**Scroll Animations:**
- Fade-in + slide-up on scroll trigger
- No parallax (performance consideration)
- Smooth reveal with ease-out curve
- Lazy-load route cards on scroll

**Physics & Easing:**
- Ease-out curves primary (cubic-bezier(0.4, 0, 0.2, 1))
- Duration: 200-300ms for most interactions
- 420ms for full dashboard/page transitions

### TotallyHot Arc Router Motion

**Allowed Triggers:**
- Route state changes (active, paused, error)
- Route selection and configuration updates
- Configuration changes and status transitions
- Real-time metric updates (visual feedback)
- Expand/collapse route details
- Hover on interactive elements (slight lift, shadow shift)

**Animation Details:**
- Primary interaction: 200-300ms duration with ease-out easing
- Card hover: subtle shadow increase, optional scale (102%)
- Route transitions: fade between routes (subtle crossfade)
- Action button: quick visual feedback (scale press effect)
- Configuration reveal: smooth slide/fade

**Visualization Animation:**
- Subtle rotation or scale on route status change
- Crossfade when switching route configurations
- Hover: slight shadow increase, optional glow effect

### Disallowed Patterns (All Themes)

- Always-on ambient motion in idle dashboards
- Decorative motion unrelated to state change
- Animation that competes with data readability
- Unnecessary bouncing or spring animations
- Continuous spinning loaders (use skeleton states instead)

### Performance Requirements

- Only transform and opacity animated
- No layout-triggering properties (width, height, position)
- GPU acceleration via will-change (sparingly)
- 60fps target on all interactions
- Smooth at typical playback frame rates

## 8. Responsive Behavior

### TotallyHot Arc Router Breakpoints

| Name | Width | Key Changes |
| --- | --- | --- |
| Mobile Small | <425px | Compact mobile layout, single column |
| Mobile | 425–576px | Standard mobile, hamburger nav, bottom status bar |
| Tablet | 576–768px | 2-column grid, sidebar may show/hide |
| Tablet Large | 768–896px | Expanded layout, sidebar persistent |
| Desktop Small | 896–1024px | Sidebar visible, route grid 3 columns |
| Desktop | 1024–1280px | Full desktop layout, route grid 5 columns |
| Large Desktop | >1280px | Expanded grid, full sidebar, expanded panels |

**Collapsing Strategy (TotallyHot):**
- Sidebar: full (240px) → collapsed (icon-only) → hidden (replaced by hamburger)
- Route grid: 5 columns → 3 → 2 → 1 (mobile)
- Status bar: maintained at all sizes (critical to routing experience)
- Search: pill input maintained, width adjusts
- Navigation: sidebar → bottom bar on mobile (tabbed interface)
- Route details: full width on mobile, side-by-side on tablet+

### Dashboard/Operational Breakpoints

Primary target remains desktop dashboard usage, with resilient fallback behavior for smaller widths.

| Name | Width | Key Change |
| --- | --- | --- |
| Mobile Small | <425px | Tight single-column compression |
| Mobile | 425-576px | Single-column with stacked controls |
| Tablet | 576-768px | Two-column summary/detail splits |
| Tablet Large | 768-896px | Expanded control rows |
| Desktop Small | 896-1024px | Sidebar optional/compact |
| Desktop | 1024-1280px | Full operational layout |
| Large Desktop | >1280px | Expanded data surfaces |

**Collapsing strategy:**
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

### TotallyHot Arc Router Theme

**Do:**
- ✓ Use near-black backgrounds (#121212–#1f1f1f) — depth through shade variation
- ✓ Apply TotallyHot Green (#1ed760) only for action controls, active states, and primary CTAs
- ✓ Use pill shape (500px–9999px) for all buttons — circular (50%) for action controls
- ✓ Apply uppercase + wide letter-spacing (1.4px–2px) on button labels
- ✓ Keep typography compact (10px–24px range) — this is an operational tool, not a document
- ✓ Use heavy shadows (0.3–0.5 opacity) for elevated elements on dark backgrounds
- ✓ Let route visualizations and metrics provide color — the UI itself is achromatic
- ✓ Maintain fixed status bar at all breakpoints
- ✓ Use inset border-shadow combo on inputs for tactile quality
- ✓ Preserve fixed sidebar on desktop, collapse to hamburger on mobile

**Do Not:**
- ✗ Don't use TotallyHot Green decoratively or on backgrounds — it's functional only
- ✗ Don't use light backgrounds for primary surfaces — the dark immersion is core
- ✗ Don't skip the pill/circle geometry on buttons — square buttons break identity
- ✗ Don't use thin/subtle shadows — on dark backgrounds, shadows need to be heavy to be visible
- ✗ Don't add additional brand colors — green + achromatic grays is the complete palette
- ✗ Don't use relaxed line-heights — TotallyHot's typography is compact and dense
- ✗ Don't expose raw gray borders — use shadow-based or inset borders instead
- ✗ Don't hide the status bar on any breakpoint — it's essential to routing operations
- ✗ Don't use pure black (#000000) — use off-black (#121212) instead
- ✗ Don't use decorative motion — only animate on state change

### Arc Router Admin Dashboard Theme

**Do:**
- ✓ Navbar + Hero with mockup of routing dashboard
- ✓ Features + Integrations sections (API, vendor support, etc.)
- ✓ Performance metrics + case studies/examples
- ✓ Security/compliance section (TLS, authentication, audit logs)
- ✓ Subtle scroll animations with fade-in/slide-up
- ✓ Meta tags for SEO and social sharing
- ✓ Footer with documentation and support links
- ✓ Adequate color contrast (WCAG AA minimum)
- ✓ Use glossy/frosted effects sparingly with blur
- ✓ Support full light and dark mode variants
- ✓ Use responsive grid with no horizontal overflow
- ✓ Lazy-load route visualizations and metrics

**Do Not:**
- ✗ No emojis in UI — use icon system only (Lucide, Heroicons)
- ✗ No pure black (#000000) — use off-black or charcoal variants
- ✗ No oversaturated accent colors (saturation cap: 80%)
- ✗ No 3-column equal-width configuration layouts — use structured forms
- ✗ No `h-screen` CSS — use `min-h-[100dvh]` instead
- ✗ No clichéd marketing copywriting ("Elevate", "Seamless", "Unleash")
- ✗ No broken external image links — use inline SVG or icons
- ✗ No generic lorem ipsum in demos — use realistic routing examples

## 10. Component Patterns to Reuse

### TotallyHot Arc Router Patterns

**Route/Configuration Cards:**
- Visualization: 1:1 square with 6px radius (or flexible for list layout)
- Title: 14px weight 700, System UI, white
- Status/Details: 14px weight 400, #b3b3b3
- Padding: 12px
- Hover: shadow increase (0.3 opacity), optional scale (102%)
- Click affordance: action button appears on hover (circular, centered)

**Route List Rows:**
- Compact row layout: route icon/visualization (thumbnail) + route info + status + menu button
- Route title: 14px weight 600, white
- Route endpoint/target: 14px weight 400, #b3b3b3
- Status indicator: 12px weight 400, #b3b3b3, right-aligned
- Hover: slight background shift (#1f1f1f), action button appears
- Active (current route): title in TotallyHot Green (#1ed760)

**Action Button (Primary Interaction):**
- Circular (50% radius)
- Background: #1ed760 (TotallyHot Green) or semi-transparent overlay on visualization
- Icon: white or black (#121212)
- Size: 48px (desktop), 36px (mobile)
- Hover: slight shadow increase, scale 102%
- Active: scale-down 98% (tactile press)

**Navigation Sidebar (Desktop):**
- Width: 240–280px
- Background: #121212
- Logo: top-left, TotallyHot Green
- Menu items: 14px weight 700 (active), 400 (inactive)
- Active: white text, slight background highlight
- Inactive: #b3b3b3 text
- Icons: 24px, circular buttons (50%) for utilities
- Responsive: collapses to icon-only on tablet, hidden on mobile

**Status/Route Bar (Fixed Bottom):**
- Height: 64px (desktop), 56px (mobile)
- Background: #181818
- Content: route icon (48px square), route info, control buttons, status indicator
- Route info: name (14px weight 700), status (12px weight 400, #b3b3b3)
- Controls: previous route, execute/pause (large circular), next route
- Status: indicator or badge
- Responsive: stays visible at all breakpoints (critical)

**Search/Filter Input:**
- Radius: 500px (full pill)
- Background: #1f1f1f
- Text: #ffffff, 14px weight 400
- Placeholder: #b3b3b3
- Icon: 24px search icon, left-aligned
- Padding: 12px 48px
- Border: inset shadow (tactile)
- Focus: border becomes #000000

**Overlay/Menu (Context):**
- Background: #282828 (slightly lighter than base)
- Border: none
- Shadow: rgba(0,0,0,0.5) 0px 8px 24px
- Items: 14px weight 400, #ffffff
- Hover: background shift to #333333
- Padding: 8px 0
- Radius: 6px–8px

### Arc Router Admin Dashboard Patterns

**Status/Metric Cards:**
- Clear headline value in bold (H2 weight: 600-700)
- Status indicator or metric change (up/down arrow + percentage)
- Secondary metric or time period below
- Color-coded status (healthy: emerald, warning: amber, error: red, degraded: orange)
- Optional drill-down link (small, secondary color)
- Padding: 20px-24px, shadow on hover

**Route Visualization Containers:**
- Restrained chrome (minimal borders, clean labels)
- Strong labeling and legend
- Smooth animations on status update
- Responsive: stack/reflow below tablet breakpoints
- Subtitle: light grey text, 0.875rem, below title
- Interactive elements: hover to reveal controls

**Configuration Cards:**
- Icon above title (48-64px, accent color)
- H3 heading (weight 600, 1.125rem)
- Configuration details (2-3 lines max, secondary grey)
- Action buttons (edit, delete, duplicate)
- Hover: subtle shadow increase + highlight

**Dashboard Hero:**
- Headline: clamp(2.5rem, 5vw, 4rem), weight 700
- Subheading: 1.25rem, weight 400, secondary color
- Call-to-action button (primary or secondary style)
- Optional visualization/screenshot on right (split-screen layout)

**Buttons:**
- Primary: Dark Navy background, Light Blue text, 12px radius
- Secondary: Outlined, Light Blue border, Navy text
- Icon+text: Icon leading (16px), 8px gap
- Full-width on mobile, auto on desktop

**Configuration Form Elements:**
- Label above, weight 500, 0.875rem
- 1px border, Light Blue focus ring
- Validation state: green text (success) or red text (error) below, 0.75rem
- Placeholder: neutral grey
- Disabled: reduced opacity (60%)
- Help text: 0.75rem, light blue, below field

**Skeleton States:**
- Shimmer animation matching component dimensions
- No circular spinners — use rectangular placeholders
- Pulse animation: opacity fade 1.2s ease-in-out infinite

**Empty/No Results States:**
- Large icon (64-96px, icon system)
- Descriptive headline (weight 600)
- Brief explanation text (secondary color)
- One clear action button (primary style) — e.g., "Create Route", "Add Configuration"
- No decorative illustrations — use functional icons only

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

### TotallyHot Arc Router Prompts

**Quick Color Reference:**
- Background: Near Black (#121212)
- Surface: Dark Card (#181818)
- Text: White (#ffffff)
- Secondary text: Silver (#b3b3b3)
- Accent: TotallyHot Green (#1ed760)
- Border/shadow: rgba(0,0,0,0.5) 0px 8px 24px

**Example Component Prompts:**
- "Create a dark card: #181818 background, 6px radius. Title at 16px System UI weight 700, white text. Subtitle at 14px weight 400, #b3b3b3. Shadow rgba(0,0,0,0.3) 0px 8px 8px on hover."
- "Design a pill button: #1f1f1f background, white text, 9999px radius, 8px 16px padding. 14px System UI weight 700, uppercase, letter-spacing 1.4px."
- "Build a circular action button: TotallyHot Green (#1ed760) background, #000000 icon, 50% radius, 12px padding. Hover: scale 102%, shadow rgba(0,0,0,0.3) 0px 8px 8px."
- "Create search/filter input: #1f1f1f background, white text, 500px radius, 12px 48px padding. Inset border: rgb(124,124,124) 0px 0px 0px 1px inset. Focus: border #000000."
- "Design navigation sidebar: #121212 background. Active items: 14px weight 700, white. Inactive: 14px weight 400, #b3b3b3. Icons: 50% radius circular buttons."
- "Build route visualization card: flexible layout, 6px radius, white text overlay. On hover: action button (circular, TotallyHot Green, 48px) appears centered with shadow rgba(0,0,0,0.5) 0px 8px 24px."
- "Create status bar: #181818 background, 64px height. Route icon 48px square, route name 14px weight 700, status 12px weight 400 #b3b3b3. Control buttons circular."

**Iteration Guide (TotallyHot):**
1. Start with #121212 — everything lives in near-black darkness
2. TotallyHot Green for functional highlights only (action, active, CTA)
3. Pill everything — 9999px for buttons, 500px for search, 50% for circular
4. Uppercase + wide tracking on buttons — the systematic label voice
5. Heavy shadows (0.3–0.5 opacity) for elevation — light shadows are invisible on dark
6. Route visualizations provide all the color — the UI stays achromatic
7. Fixed status bar at bottom — it's core to the routing experience

### Arc Router Admin Dashboard Prompts

- Build a status metric card with clear headline value, route status indicator, and shadow-on-hover using #0F172A backgrounds and #60A5FA accent text.
- Design an Arc Router admin dashboard with hero section (split-screen text+routing visualization mockup), configuration in structured layout, integrations, and performance metrics.
- Create a route visualization chart with clean labels, smooth animations on route change, and responsive reflow below 768px.
- Implement a control button row with primary (Navy bg, Light Blue text, 12px radius, 8% hover darken) and secondary (outlined, blue border) variants for route operations.
- Build a full-page navigation with active section indicator in accent blue, responsive collapse to hamburger below 768px, and sticky positioning (sidebar or top nav).
- Design a glassmorphic card overlay (backdrop-filter blur, 10% white transparency, top highlight glow) for route detail modals and configuration dialogs.

## 12. Acceptance Checklist

### TotallyHot Arc Router Theme

**Visual Design:**
- ✓ Color palette: #121212–#1f1f1f (near-black ramp) + TotallyHot Green (#1ed760) only
- ✓ No pure black (#000000) — use #121212 instead
- ✓ TotallyHot Green used only for action controls, active states, CTAs — never decorative
- ✓ Pill geometry: 9999px (full pill), 500px (large pill), 50% (circular) — no square buttons
- ✓ Shadows heavy and visible: 0.3–0.5 opacity with 8–24px blur

**Typography:**
- ✓ System UI stack throughout (Segoe UI, -apple-system, etc.)
- ✓ Weight binary: 700 (bold) or 400 (regular), 600 sparingly
- ✓ Uppercase buttons with 1.4px–2px letter-spacing
- ✓ Compact scale: 10px–24px range
- ✓ Global script support in fallback stack (Arabic, Hebrew, Cyrillic, Greek, Devanagari, CJK)

**Spacing & Layout:**
- ✓ Base unit 8px applied consistently
- ✓ Sidebar: 240–280px fixed on desktop, collapses to icon-only → hidden on mobile
- ✓ Status bar: fixed at bottom, visible at ALL breakpoints (critical)
- ✓ Route grid: 5 columns (desktop) → 3 → 2 → 1 (mobile)
- ✓ Dark compression: content densely spaced, dark bg provides visual rest

**Components:**
- ✓ Action button: Circular (50%), TotallyHot Green, 48px (desktop), press-down on active
- ✓ Pill buttons: 9999px radius, uppercase labels, 1.4px letter-spacing
- ✓ Card hover: shadow increases to 0.3 opacity, optional scale 102%
- ✓ Route visualization: flexible layout, 6px radius, action button appears on hover
- ✓ Route rows: icon + title (700) + endpoint (400, #b3b3b3) + status + menu

**Interactions:**
- ✓ Execute/pause: instant visual feedback, scale press effect
- ✓ Route change: crossfade or fade transition
- ✓ Hover states: shadow increase, text color shift (if applicable)
- ✓ No decorative motion — only on state change

**Responsive:**
- ✓ Hamburger navigation on mobile (width < 768px)
- ✓ Status bar maintained at all sizes
- ✓ Touch targets ≥ 44x44px
- ✓ Search input remains pill-shaped and accessible
- ✓ Bottom navigation tabs on mobile (alternative to sidebar)

**Accessibility:**
- ✓ Sufficient contrast: white (#ffffff) on dark (#181818+) ≥ 7:1
- ✓ TotallyHot Green (#1ed760) only with white or light text ≥ 4.5:1
- ✓ Focus rings visible on all interactive elements
- ✓ Execute button has clear visual affordance
- ✓ Keyboard navigation: tab through all controls
- ✓ ARIA labels for icon-only buttons (execute, pause, next, previous)

### Dark Operational Theme

- Dense screens remain readable with consistent spacing rhythm
- Visual hierarchy is driven by type and structure, not decorative effects
- Idle screens remain still; motion appears only on meaningful change
- Palette usage stays restrained and function-first
- Default views show headline metrics before deeper detail
- Progressive disclosure is consistent across tabs and dialogs
- Focus and keyboard states remain visible and predictable

### Arc Router Admin Dashboard Theme

**Visual Design:**
- ✓ Color palette limited to core branding (Navy, Blues, Greys) + semantic colors
- ✓ No pure black (#000000) used anywhere
- ✓ Saturation capped at 80% on all accent colors
- ✓ Corner radius consistent: 12px (buttons, cards), 24px (modals), 9999px (pills)
- ✓ Glassmorphism effects: blur(8px) + 10% transparency on dark cards
- ✓ Shadows follow light physics: soft (0 2px 12px for standard), stronger (0 10px 30px for modals)
- ✓ Route status clearly indicated by color and positioning (not shadow alone)

**Typography:**
- ✓ Inter font family used throughout (fallback to system fonts)
- ✓ Font sizes follow established scale (no arbitrary sizes)
- ✓ Font weights: 400 (body), 500 (labels), 600 (subheadings), 700 (headlines)
- ✓ Line heights match scale: 1.2 (display), 1.3 (headings), 1.6 (body)
- ✓ Contrast ratio ≥ 4.5:1 for body text, ≥ 3:1 for UI elements (WCAG AA)
- ✓ Route names and statuses clearly readable at all sizes

**Spacing & Layout:**
- ✓ 8px base unit applied consistently
- ✓ No horizontal overflow at any breakpoint
- ✓ Responsive collapse: 1 col (mobile) → 2 col (tablet) → 3+ col (desktop)
- ✓ Max-content width: 1280px with 24px side padding
- ✓ Sidebar or top navigation maintains clear hierarchy
- ✓ Grid-based layout (CSS Grid or structured flexbox)

**Components:**
- ✓ Buttons: proper sizing (44px min-height), clear active/focus states, no shadow-only affordances
- ✓ Cards: 12-24px radius, subtle shadows, 1px border, consistent padding
- ✓ Inputs: label above, focus ring visible (2px offset), validation state clear
- ✓ Status cards: headline value prominent, status indicator clear, optional drill-down
- ✓ Route controls: accessible and intuitive (execute, pause, edit, delete)
- ✓ Empty states: icon + text + action (e.g., "Create Route", "Add Configuration")

**Motion:**
- ✓ Entrance animations: fade + translate-Y over 420ms ease-out
- ✓ Interactions: 200-300ms duration, ease-out easing
- ✓ Hover states: color shift + shadow adjustment, no layout jumps
- ✓ Route status changes: smooth visual transitions
- ✓ No ambient idle animation
- ✓ Performance: transform/opacity only, 60fps target

**Responsiveness:**
- ✓ Navigation adapts: full → compact → hamburger as viewport shrinks
- ✓ Route visualizations reflow smoothly (no clipping, stacking when needed)
- ✓ Touch targets ≥ 44x44px
- ✓ Dashboard remains usable on mobile (single column, scrollable)
- ✓ Text readable at all sizes (no font-size clipping)

**Accessibility:**
- ✓ Color not the only signal (icons, text, shape variations)
- ✓ Route status indicated by multiple cues (color + icon + text)
- ✓ Focus ring visible on all interactive elements
- ✓ Sufficient contrast for all text + backgrounds
- ✓ Form labels properly associated with inputs
- ✓ Route names and statuses conveyed in plain text, not just color
- ✓ ARIA labels for icon-only buttons (execute, pause, next, previous)
