---
name: TotallyHotArcRouter-design
description: Use this skill to generate well-branded interfaces and assets for TotallyHotArcRouter (the TotallyHotArcRouter coding-task model router and its Windows tray dashboard), either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the README.md file within this skill, and explore the other available files.

If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, you can copy assets and read the rules here to become an expert in designing with this brand.

If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

Key things to know before you start:

- This is a **dark-theme-only, data-dense ops console** (routing/cost/governance telemetry for an LLM router), not a marketing product — keep copy terse and numeric, never adjective-heavy.
- Two font roles: `--font-ds` (`"Century Gothic", "Avenir Next", "Poppins", Inter, system-ui, sans-serif` — Spotify's own font can't be legally bundled, so this is a substitute stack) for UI text, JetBrains Mono for every number/ID/timestamp. Never mix these up.
- No logo exists in the source — use plain type for the brand name; the only real vector asset is a small app-tray icon (`assets/appicon.svg`), not a wordmark.
- Colors: near-black neutrals + Dark Green `#1ed760` (accent) / emerald (positive) / amber (warning) / red (critical), plus a fixed 12-color deterministic per-agent palette. See `tokens/colors.css`.
- Motion is functional only, never decorative — but it is not absent: modals rise in, tab panels crossfade, list rows enter with a first-mount stagger, and toasts slide in. Chrome never overshoots (no bounce/elastic); charts deliberately do. The only looping animation is a pulsing "live" dot, plus a one-shot attention flash. See `docs/gui/MOTION.md` for the full contract.
- Buttons: CTAs are pill-shaped (`.btn-*`), uppercase with 1.4px tracking, 44px min-height, and shrink to `scale(0.98)` on press. Dense per-row icon actions stay square and have no press effect.
- Components live in `components/`; the full interactive dashboard recreation lives in `ui_kits/dashboard/`.

