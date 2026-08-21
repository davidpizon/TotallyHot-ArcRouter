# GUI Window/Modal Shell Contract

Every new window, modal, or dialog in `TotallyHot.ArcRouter.Gui` must copy the shell of
`src/TotallyHotArcRouter.Gui/Components/SettingsModal.razor`:
- Same `.overlay-backdrop` / `.overlay-panel` classes (carry the entrance animation) and blurred
  backdrop.
- Same `max-w-md` slate panel; same header bar with uppercase `text-sm` title + `x` close glyph.
- Closing is exposed as an `EventCallback` parameter — the window never closes itself.
- `Components/ProviderEditDialog.razor` is a worked example to copy from.
- Deviate only where content genuinely requires it (e.g. a wider panel for a table); never deviate
  on the header, dismissal behavior, or close API.
- Full contract: `docs/gui/DESIGN.md` §4.1 (colors/typography/components) and `docs/gui/MOTION.md`
  (durations/easing/entrance-exit patterns) — read these for anything beyond the shell.

## Detached elements (rendering outside a parent container)

Tooltips, modals, and the dragged price-source card all escape their container via `position: fixed`
plus JS-measured geometry. Before adding another, or before touching `overflow` / `transform` /
`filter` / `contain` / `will-change` on anything that could be an ancestor of one, read
`docs/gui/DESIGN.md` §5.5 — several of those properties silently re-trap a fixed element, and the
failure looks like a rendering bug rather than a CSS mistake.

## Drag-to-reorder

`mem:gui/drag_reorder` — the JS/Blazor ownership split for pointer-driven reordering, the CSS and
lifecycle invariants that make a detached dragged element survive re-renders and clipping ancestors,
variable-height rank math, and how to test any of it under bUnit. Read it before changing
`js/reorder-flip.js`, `PriceSourcesAdmin`'s drag, or the `.card-lifted` / `.card-pinned` /
`.ds-card-slot` rules.
