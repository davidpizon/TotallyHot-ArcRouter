# Archived docs

Closed execution plans, superseded transport/update designs, and the non-runtime React design kit.
Living docs point here via stubs at the original paths. **Do not implement recipes in this tree** —
many name deleted MAUI/`TotallyHotArcRouter.Gui` projects.

| Archive path | Why it is here | Live contract |
|---|---|---|
| [`gui/web-gui-migration-plan.md`](gui/web-gui-migration-plan.md) | P1–P11 shipped; MAUI GUI deleted | ADRs 0011–0014, [`../gui/DESIGN.md`](../gui/DESIGN.md) |
| [`gui/phase5-conformance-checklist.md`](gui/phase5-conformance-checklist.md) | Cited deleted `windows-gui` CI / `Gui.Tests` | [`../gui/DESIGN.md`](../gui/DESIGN.md), [`../gui/MOTION.md`](../gui/MOTION.md) |
| [`gui/phase0-gap-matrix.md`](gui/phase0-gap-matrix.md) | MAUI-era snapshot | [`../gui/DESIGN.md`](../gui/DESIGN.md) |
| [`gui/console-tab-plan.md`](gui/console-tab-plan.md) | Console tab shipped | [`../gui/dashboard.md`](../gui/dashboard.md) |
| [`gui/livestream-redesign-plan.md`](gui/livestream-redesign-plan.md) | Sessions tab shipped; recipes name deleted `Gui/` paths | [`../gui/dashboard.md`](../gui/dashboard.md) |
| [`gui/aspirational-design-adoption-plan.md`](gui/aspirational-design-adoption-plan.md) | Adoption work landed | [`../gui/DESIGN.md`](../gui/DESIGN.md) |
| [`router/doc-code-reconciliation-plan.md`](router/doc-code-reconciliation-plan.md) | Audit complete | [`../README.md`](../README.md), [`../../src/PLAN.md`](../../src/PLAN.md) |
| [`router/grpc-migration.md`](router/grpc-migration.md) | SignalR → gRPC shipped | [`../router/telemetry.md`](../router/telemetry.md) |
| [`router/signalr-hub-security.md`](router/signalr-hub-security.md) | SignalR removed | [`../router/telemetry.md`](../router/telemetry.md) |
| [`router/auto-update-plan.md`](router/auto-update-plan.md) | `Updater.exe` deleted; apply is MSI via the tray | [`../router/packaging-and-distribution.md`](../router/packaging-and-distribution.md), [`../router/version-compatibility.md`](../router/version-compatibility.md) |
| [`router/token-tracking-implementation-plan.md`](router/token-tracking-implementation-plan.md) | All six phases shipped | [`../router/token-tracking-improvements.md`](../router/token-tracking-improvements.md) |
| [`router/openai-format-usage-accuracy-plan.md`](router/openai-format-usage-accuracy-plan.md) | Phases 1–3 shipped | [`../router/agent-cost-tracking.md`](../router/agent-cost-tracking.md) |
| [`router/anthropic-reported-usage-plan.md`](router/anthropic-reported-usage-plan.md) | Phases 1–3 shipped | [`../router/agent-cost-tracking.md`](../router/agent-cost-tracking.md) |
| [`router/secrets-at-rest-plan.md`](router/secrets-at-rest-plan.md) | All six phases shipped | [`../router/secrets-at-rest.md`](../router/secrets-at-rest.md) |
| [`router/mcp-endpoint-plan.md`](router/mcp-endpoint-plan.md) | Shipped | [`../router/mcp-endpoint.md`](../router/mcp-endpoint.md) |
| [`router/ollama-show-capabilities-plan.md`](router/ollama-show-capabilities-plan.md) | All eleven steps shipped | capability-scan code in `src/TotallyHotArcRouter` |
| [`router/phase-m2-plan.md`](router/phase-m2-plan.md) | M2 shipped | [`../router/orchestrator-live-path-plan.md`](../router/orchestrator-live-path-plan.md) |
| [`router/sessions-tab-training-data-plan.md`](router/sessions-tab-training-data-plan.md) | Shipped | [`../gui/dashboard.md`](../gui/dashboard.md) |
| [`router/routing-roi-regret-plan.md`](router/routing-roi-regret-plan.md) | Shipped | [`../router/self-organizing-classification-plan.md`](../router/self-organizing-classification-plan.md) |
| [`router/judge-join-deadlock-fix-plan.md`](router/judge-join-deadlock-fix-plan.md) | Shipped | [`../router/quality-verifier-architecture.md`](../router/quality-verifier-architecture.md) |
| [`design/`](design/) | Historical React kit, never runtime | [`../gui/DESIGN.md`](../gui/DESIGN.md), [`../gui/MOTION.md`](../gui/MOTION.md) |

Plans that still have unfinished work stay in `docs/router/` and `docs/gui/` (for example
`src/PLAN.md`, `live-feedback-learning-plan.md`, `counterfactual-token-estimation-plan.md`,
`regret-evaluation-harness-plan.md` Q5, `code-smell-refactoring-plan.md`).
