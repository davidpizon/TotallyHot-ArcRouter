# Current Implementation Plan: Remaining Work Only

This plan excludes completed phases and tracks only unfinished work.

## Active Phases

### Phase A: Sandbox Documentation Accuracy
- Update XML docs in `src/TotallyHotArcRouter.Sandbox/Tier1/LinuxJailLauncher.cs`.
- Ensure cancellation cleanup behavior is documented for timeout and external cancellation.
- Exit: docs are accurate and build remains warning-free.

### Phase B: Windows MAUI CI Coverage
- Extend `.github/workflows/dotnet-ci.yml` with a `windows-latest` MAUI build job.
- Install MAUI workload and build `src/TotallyHotArcRouter.Gui/TotallyHotArcRouter.Gui.csproj`.
- Exit: Windows MAUI CI job passes and existing Linux jobs remain green.

### Phase C: Price Catalog Alias Overrides
- Implement explicit alias override persistence and precedence before auto-match.
- Add validation/conflict handling and deterministic tests.
- References: `docs/router/d3-alias-resolution.md`, `docs/router/model-price-catalog.md`.
- Exit: divergent provider/catalog model names resolve correctly for cost lookup.

### Phase D: Alias Override Management Surface
- Expose CRUD/reorder operations via existing admin API and GUI management UX.
- Add end-to-end tests from management action to runtime lookup behavior.
- Exit: overrides are manageable without manual file edits.

### Phase E: Advanced Price Tier Support
- Add batch/cached/multimodal tier support in schema, ingestion, and lookup estimation.
- Add regression tests for tier permutations and graceful fallback behavior.
- Exit: tier-aware cost estimates are correct for supported providers.

### Phase F: Deferred GUI Backlog Completion
- Add missing telemetry fields for currently mock-backed tabs/metrics.
- Implement GUI settings persistence and harden loopback gRPC stream behavior.
- Keep dialogs/modals aligned with `docs/gui/DESIGN.md`.
- Reference: `docs/gui/backlog.md`.
- Exit: remaining GUI views run on live data and settings survive restart.

## Final Validation Gate
1. `dotnet build` passes with zero warnings/errors.
2. Relevant tests pass for each changed phase.
3. Documentation is updated to match delivered behavior.
4. Any intentionally deferred items are explicitly documented with rationale.
