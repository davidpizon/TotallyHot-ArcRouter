# Aspirational Design Adoption Plan

This plan describes how to adopt the new aspirational GUI design principles across TotallyHotArcRouter.Gui in controlled phases while preserving release stability.

## Objective

Adopt and operationalize the following principles across design docs, component contracts, and implementation behavior:

- Data density without claustrophobia
- Hierarchy through weight, not decoration
- Motion as meaning
- Trust through restraint
- Progressive disclosure over feature walls

## Success Criteria

- All GUI-facing design and implementation docs align with the five principles and no longer conflict.
- Live Stream, Cost Analytics, Governance, and Console reflect the principles in layout, typography hierarchy, and interaction flow.
- Motion behavior is restricted to state/value transitions and remains calm at idle.
- Color usage remains restrained and functional, with no decorative expansion.
- At least 80% unit test coverage is maintained and all builds/tests pass without warnings.

## Non-Goals

- Changing backend routing logic, telemetry schemas, or proxy behavior beyond what is needed for UI semantics.
- Rebranding the product or replacing the established dark visual identity.
- Introducing a mobile-first redesign that conflicts with the fixed-window desktop app model.

## Phase 0: Baseline Audit and Gap Map

### Goal
Create a complete baseline of where the current GUI and docs already match the principles versus where they diverge.

### Work
- Audit docs in docs/gui for principle conflicts and stale guidance.
- Capture baseline screenshots/video for each tab and major modal.
- Inventory current spacing, typography, color, border, and motion patterns in app.css and components.
- Mark every place where gradients, decorative shadows, or idle motion appear.
- Build a gap matrix mapping each principle to current-state pass/fail evidence.

### Deliverables
- Principle Gap Matrix (markdown table)
- Baseline visual audit pack (screenshots)
- Prioritized issue list grouped by severity and implementation complexity

### Exit Gate
- Every principle has explicit pass/fail evidence for all major surfaces.
- Top 10 high-impact divergences are identified and ranked.

## Phase 1: Documentation and Token Contract Alignment

### Goal
Align documentation and style contracts before visual refactors begin.

### Work
- Update gui planning docs to treat the five principles as the primary design intent.
- Add explicit guidance for spacing rhythm, border restraint, and hierarchy via typography weight.
- Add motion contract language that allows animation only for transitions and changing values.
- Add progressive disclosure standards for default views and drill-down behaviors.
- Define a restrained token contract for color roles, elevation, and semantic states.

### Deliverables
- Updated docs/gui guidance set with principle-first language
- Token contract section for spacing, type hierarchy, motion triggers, and semantic color usage
- Cross-reference map between DESIGN.md, MOTION.md, and tab-specific docs

### Exit Gate
- No conflicting guidance remains between docs/gui/DESIGN.md, docs/gui/MOTION.md, and tab plans.
- Principle checklist is embedded in relevant design docs.

## Phase 2: Foundation Refactor (Styles, Shared Components, Interaction Primitives)

### Goal
Establish shared building blocks that enforce the principles by default.

### Work
- Refactor shared CSS/component primitives to enforce consistent spacing and restrained borders.
- Normalize typography roles for headline, supporting, and diagnostic layers.
- Remove decorative hierarchy mechanisms (gradients, non-functional heavy shadows).
- Standardize motion utilities for state/value transitions only.
- Create reusable progressive-disclosure primitives (summary-first cards, expandable details, controlled drill-down sections).

### Deliverables
- Updated style primitives in app.css and shared component utilities
- Reusable disclosure and metric presentation components
- Motion utility set with approved transition patterns and durations

### Exit Gate
- Shared primitives support all five principles without one-off overrides.
- Visual regressions are reviewed and accepted for baseline components.

## Phase 3: Tab-by-Tab Adoption

### Goal
Apply the new foundation to each major dashboard surface in priority order.

### Workstream Order
1. Live Stream
2. Cost Analytics
3. Governance
4. Console
5. Settings and supporting dialogs

### Work per Workstream
- Recompose default views to show headline metrics first.
- Shift complexity into drill-down paths.
- Re-rank text hierarchy using weight and scale instead of decorative devices.
- Tighten dense metric layouts with consistent spacing and restrained separators.
- Ensure idle state calmness by removing non-semantic animation.

### Deliverables
- Updated component implementations per tab
- Before/after screenshots for each workstream
- UX notes on disclosure flow and scanability improvements

### Exit Gate
- Each tab passes the principle checklist and design QA review.
- No feature wall defaults remain in primary dashboards.

## Phase 4: Motion and State Semantics Hardening

### Goal
Guarantee that every animation and transition has semantic purpose.

### Work
- Audit all transitions and classify as allowed or disallowed.
- Remove idle or decorative motion patterns.
- Bind remaining motion to explicit triggers: value change, threshold crossing, state transition, open/close interaction.
- Validate reduced-motion compatibility and fallback behavior.

### Deliverables
- Motion conformance report
- Updated motion rules in docs/gui/MOTION.md
- Component-level trigger mapping for animated elements

### Exit Gate
- 100% of surviving animations map to documented semantic triggers.
- No always-on ambient animation in idle dashboard states.

## Phase 5: Verification, Testing, and Rollout

### Goal
Ship with confidence through measurable quality gates and staged rollout.

### Work
- Run full build and test suite with warning-free output.
- Add/update unit tests for behavior that changed due to disclosure or state logic.
- Run visual QA checklist against all major views and dialogs.
- Execute accessibility and keyboard-flow validation for disclosures and tooltips.
- Conduct stakeholder review with explicit sign-off against the five principles.

### Deliverables
- Test and build evidence
- Final design conformance checklist
- Rollout notes and rollback plan

### Exit Gate
- All builds/tests pass with zero warnings/errors.
- Principle conformance signed off for all target surfaces.
- Coverage floor maintained at 80% or higher.

## Phase 6: Post-Release Calibration

### Goal
Measure real-world impact and refine without violating the principles.

### Work
- Capture user feedback on scanability, trust perception, and drill-down discoverability.
- Track interaction metrics for expansion depth and time-to-first-insight.
- Triage follow-ups into minor tuning versus structural rework.
- Update docs to reflect validated patterns and lessons learned.

### Deliverables
- 30-day adoption review
- Follow-up backlog with severity/impact scoring
- Updated long-term design roadmap

### Exit Gate
- At least one feedback cycle is closed with documented actions.
- No regressions reintroducing decorative hierarchy or feature-wall defaults.

## Cross-Phase Governance

- Principle checklist is required in every design review and PR touching GUI surfaces.
- Any exception to the principles must include written rationale and expiration date.
- Documentation updates are mandatory for behavior changes that affect hierarchy, motion, or disclosure.

## Documentation Update Strategy

As the aspirational design is adopted through the phases, the design documentation in `docs/gui/` must be updated to reflect the new standards:

- **DESIGN.md** — Update color, typography, components, and layout guidance to match the consolidated aspirational design
- **MOTION.md** — Update motion triggers, timing, and easing to match the new semantic animation contract
- **Any tab-specific design docs** — Align Live Stream, Cost Analytics, Governance, and Console documentation with unified component language and principles
- **Update timeline:** 
  - Phase 1: Begin documenting the new guidance
  - Phase 2: Update DESIGN.md and MOTION.md with foundation changes
  - Phase 3: Update tab-specific guidance as each workstream completes
  - Phase 5: Finalize all docs for release

The aspirational-design.md file serves as the **source of truth** during adoption. As each phase completes and designs ship, content from aspirational-design.md progressively replaces the previous guidance in DESIGN.md, MOTION.md, and related docs until the aspirational design **becomes the actual design standard**.

## Risks and Mitigations

- Risk: Dense telemetry surfaces become harder to scan after compression tweaks.
- Mitigation: Require side-by-side scan tests and preserve spacing rhythm constraints.

- Risk: Removal of decorative emphasis reduces perceived affordance.
- Mitigation: Increase clarity through typography contrast, labeling, and interaction states.

- Risk: Disclosure-first design may hide important diagnostics.
- Mitigation: Define mandatory always-visible headline diagnostics per tab.

- Risk: Motion reductions could hide important state changes.
- Mitigation: Keep semantic motion for threshold/status/value transitions with explicit trigger tests.

## Ownership Model

- Design lead: principle interpretation, QA rubric, final conformance sign-off.
- GUI engineering lead: component architecture, sequencing, implementation quality.
- QA lead: test matrix, accessibility checks, regression validation.
- Product/maintainer: prioritization, rollout approval, post-release calibration.

## Tracking Template

Use this status line for each phase in weekly updates:

- Phase X | Status: Not Started/In Progress/Blocked/Done | Owner | ETA | Gate Risks | Next 3 Actions
