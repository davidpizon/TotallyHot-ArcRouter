# TotallyHot Arc Router — Core

Model-routing framework for coding tasks (routes to backend models under a perf/cost tradeoff),
evaluated against the upstream CodeRouterBench dataset (not owned/republished by this repo).

## Canonical docs (read these, not just this memory)
- `AGENTS.md` (repo root) — single source of truth for agent instructions. `CLAUDE.md` and
  `.github/copilot-instructions.md` are symlinks to it; **edit AGENTS.md only**.
- `README.md`, `docs/HANDBOOK.md`, `src/PLAN.md` (migration roadmap/phases), `data/README.md`.
- `docs/gui/DESIGN.md` + `docs/gui/MOTION.md` — GUI design/motion system, binding.

## Source map
```
src/TotallyHotArcRouter*/     .NET solution (see mem:tech_stack) — router, GUI, sandbox, tests
docs/                         design docs, handbook, plans (docs/router/*, docs/gui/*)
%LOCALAPPDATA%\TotallyHot.ArcRouter\coderouterbench.db   synced-on-demand benchmark DB (not checked in)
```

## Project-wide invariants
- Every build must compile with **0 warnings/errors** — `Directory.Build.props` sets
  `TreatWarningsAsErrors`; several projects also set `GenerateDocumentationFile` so a missing XML
  doc comment on a public/protected member (CS1591) fails the build too.
- Every class/method needs accurate `///` XML docs (stale docs count as missing).
- Logging is Serilog-only, structured, static message templates (never string interpolation).
- New GUI windows/modals must copy `SettingsModal.razor`'s shell exactly (see `mem:gui`).

Further memories: `mem:tech_stack` (stack/build), `mem:suggested_commands` (build/test on
Windows), `mem:conventions` (code style specifics), `mem:task_completion` (definition of done),
`mem:gui` (GUI shell contract detail).
