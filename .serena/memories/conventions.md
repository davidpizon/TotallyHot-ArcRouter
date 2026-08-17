# Code Conventions

- Nullable reference types on; async/await where appropriate; DI + options binding for config.
- Logging: Serilog exclusively. Always a **static string literal** message template —
  `logger.LogInformation("Model selected: {Model} with confidence {Confidence}", model, confidence)`,
  never string interpolation/formatting into the template. Log routing decisions, proxy
  interceptions, memory updates, and errors (audit-trail requirement).
- XML docs (`///`) required on every public/protected type and member, enforced as a build error
  (CS1591) on: `TotallyHotArcRouter`, `TotallyHotArcRouter.Gui`, `TotallyHotArcRouter.Sandbox`,
  `.Gui.Admin`/`.Charts`/`.Console`/`.Telemetry`. Multi-sentence `<summary>` explaining *why* where
  non-trivial; `<inheritdoc/>` for interface implementations; one-liners OK for plain DTOs.
  Stale docs (wrong params, removed behavior, placeholder text) are treated as a build-quality
  defect even though the compiler won't catch staleness — review docs whenever changing the code
  they describe.
- No fallback/defensive code for scenarios that can't happen; keep changes minimal and scoped.
- Diagrams in any markdown doc: Mermaid only.
- Unusually heavy unit tests: 5 second max.
- Coverage floor: 80% at each phase boundary (see `src/PLAN.md` for phase structure).

See `mem:gui` for the GUI-specific window/modal shell contract.
