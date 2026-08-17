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
