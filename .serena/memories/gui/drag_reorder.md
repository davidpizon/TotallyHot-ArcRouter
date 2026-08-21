# Drag-to-reorder cards

Implementation: `js/reorder-flip.js`, `Components/PriceSourcesAdmin.razor`, and the `.card-lifted` /
`.card-pinned` / `.card-dropping` / `.ds-card-slot` rules in `wwwroot/css/app.css`.
Prose: `docs/gui/DESIGN.md` §5.3–5.5, `docs/gui/MOTION.md` §6 (Reorder Settle, Lift Detach).

## Ownership

- JS owns the dragged element's position; Blazor owns list order. Never route `pointermove` through
  interop — at 60-120/sec the element visibly lags the cursor.
- JS calls back only on rank change: `DragStarted` / `MoveDraggedTo` / `EndDrag`, all `[JSInvokable]`.
- JS never inserts/removes/reorders DOM nodes. Reads rects, writes inline styles. Blazor owns the tree.

## Invariants (each cost an iteration to find)

- **Blazor rewrites the whole `class` attribute** on any render that changes it. A `classList`-only
  class dies on the next render. `.card-pinned` must be rendered by Razor *and* set by JS — JS for
  same-frame detach, Razor so it survives. Blazor does **not** touch `style`, so inline geometry is safe.
- **Losing the detached class is not cosmetic.** Inline `top`/`left` are viewport coordinates; reverting
  to `position: relative` displaces the element by that full amount. Symptom: "the card disappeared."
- **`overflow: clip` clips `position: fixed` descendants; `overflow: hidden` does not.** `hidden` only
  clips descendants whose containing block is inside it (a fixed element's is the viewport); `clip` is a
  paint-time clip over the whole subtree. Symptom: element vanishes on leaving that ancestor's box.
- **No ancestor of a detached element may have `transform` / `filter` / `perspective` /
  `backdrop-filter` / `contain` / `will-change`** — each makes it a containing block for fixed
  descendants. `.panel-enter` animates `transform` but only on tab switch, no fill-mode: safe, leave it.
- **A scroll container cannot be given breathing room.** `overflow-y: auto` forces used `overflow-x:
  auto`, always clips at the padding box, and `overflow-clip-margin` does not apply to `auto`.
  Enumerate every ancestor's overflow + padding before adjusting any of them.
- **Size growth in pixels against a measured budget** (`getBoundingClientRect` + `window.innerWidth`),
  never a percentage — percentage growth outruns any fixed margin at some window width. Do not instead
  shrink the resting state: that makes rendered width depend on window width, inconsistent across screens.
- **`EndDrag` fires from `transitionend`, not `pointerup`.** It clears state → re-renders without
  `.card-pinned` → strips the class mid-settle. General rule: state must outlive the transition that
  depends on it.
- **Gate the lift on travel (3px), not on `pointerdown`** — `pointerdown` on a child control bubbles to
  the card; lifting on the bare press makes every click flicker.

## Rank math

Cards are variable-height, so `round(y / rowHeight)` has no row height to use. Measure all slots once at
drag start; accumulate non-dragged heights + gap; pick the insertion point nearest the dragged element's
top edge. Decided from the dragged element's position, not from what is under the cursor.

## bUnit

No layout engine — geometry and pointer math are untestable there. Drive the component through the same
surface JS uses (`cut.Instance.DragStarted()` etc. via `cut.InvokeAsync`), not synthetic pointer events;
see the `DragAsync` helper in `PriceSourcesAdminTests.cs`. Requires
`ctx.JSInterop.Mode = JSRuntimeMode.Loose`; `ctx.JSInterop.Invocations` still asserts interop calls.
