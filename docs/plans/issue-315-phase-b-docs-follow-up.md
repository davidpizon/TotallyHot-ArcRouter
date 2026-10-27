# Plan: #315 docs follow-up after the grader epochs (Phase B proposal)

> **For agentic workers:** Do not start these edits until David signs off on this file, by a comment on [#315](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/315) or on the pull request that adds this file. The issue being open is not approval. Steps use checkbox (`- [ ]`) syntax. This file changes markdown only.

**Status:** Awaiting David's approval. This pull request adds the plan only. It does not edit the docs it lists.
**Issue:** [#315](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/315) — "Reconcile docs with code (26-item review)".
**Standing rule:** [Approved plan before coding](../router/standing-rules.md#approved-plan-before-coding). Each implementation pull request links this file.
**Tree this plan was read against:** `origin/main` at `37dfd1d0` (merge of [#351](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/351), 2026-10-27). Re-read the cited symbol before editing. A shifted line is not a reason to change the decision.
**Engines:** CodeGraph (`npx @colbymchenry/codegraph init -y`, 1,381 files, 25,813 nodes) and Serena (`find_symbol` / `get_symbols_overview`, session `8356b620`, C# language server). Both returned source. Serena was not skipped. The index was built on `eb909749` (after [#353](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/353), before #351). `QualityIngress`, `QualityRescanService`, `QualityScorer`, `LocalGradeTakeover`, and `GraderEpochOptions` did not change in #351, so those line numbers are the tree at `37dfd1d0`. The #351 chart files were read on this branch after `git fetch` of `37dfd1d0`. CodeGraph's two explore calls did not include those new files.

This pull request **closes nothing**. It does not cancel the writer in the approved plan.

## 0. Where Phase B came from

Phase A and the original Phase B are written down. They are not an inference.

| What | Where | What it is |
|---|---|---|
| Phase A | [#317](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/317) and the leftovers in [#318](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/318), both merged 2026-10-21 | Docs for original items #1–#3 and #5–#26. The plan is [`issue-315-docs-code-reconciliation.md`](issue-315-docs-code-reconciliation.md), merged in [#316](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/316). |
| Original Phase B | Same plan, §4, and the issue body | One **code** change: item #4, the bounded `Channel<ServedRequest>` writer (ADR-0018 and ADR-0019). Not a docs pass. Not started. |
| This file | Not in the issue, the comments (there are none), or any pull request | A **proposed** docs pass. The sentences Phase A wrote, and the living docs beside them, that later merges made false. |

Later merges this file accounts for:

| Merge | What landed that can make a doc false |
|---|---|
| [#334](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/334) through [#348](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/348) | #304 phases 0–8. Live ingress enqueues unfenced text. Takeover defaults on. |
| [#347](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/347) | Phase 8 plan and ADR-0027 Amendment 3. Docs only. |
| [#349](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/349) | Phase 9 plan. The file still says "Draft" and "implementation waits". |
| [#351](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/351) | Epoch marks, token-efficiency pill, incomplete-grade card. It updated `dashboard.md`, `how-it-learns.md`, and the evaluator plan, and left other sentences on seven pills. |
| [#353](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/353) | #350 test change. The plan file still says implementation has not started. |

**Open question 1** in §7 is the name. Until David answers, implementers treat this file as extra work beside §4 of the approved plan. They do not delete §4. They do not build the writer from this file.

## 1. Split

```mermaid
flowchart LR
  P["This plan, after sign-off"]
  A["PR B1: grading sentences"]
  B["PR B2: charts and the style list"]
  C["PR B3: plan status lines"]
  P --> A
  P --> B
  P --> C
```

Three pull requests. They do not depend on each other. Each is docs only. None of them contains the writer.

| PR | Items | Files |
|---|---|---|
| **B1. What gets graded** | §3.1–§3.7 | `README.md`, `docs/how-it-learns.md`, `docs/research/subagent-and-helper-routing-evidence.md`, `docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md`, `docs/adr/0027-grade-non-coding-responses-on-a-bootstrap-recipe-and-keep-them-out-of-code-tallies.md`, `docs/plans/issue-293-graders-judge-against-the-question.md`, `docs/plans/issue-163-subagent-aware-routing.md`, `docs/plans/issue-304-grade-non-coding-responses.md`, `docs/plans/issue-315-docs-code-reconciliation.md`, `docs/plans/issue-304-phase-8-grader-takeover.md` |
| **B2. The eighth pill** | §3.8–§3.10 | `docs/gui/dashboard.md`, `docs/gui/cost-analytics-visualization-spec.md`, `docs/gui/DESIGN.md`, `docs/plans/issue-304-phase-9-epoch-time-graph.md` |
| **B3. The test plan's status** | §3.11 | `docs/plans/issue-350-flaky-grader-epoch-phase7-tests.md` |

## 2. Rules for every edit

- Markdown only. No `.cs`, `.csproj`, `.razor`, `.js`, `.proto`, test, workflow, or `appsettings.json` edit. The three `style=` bugs in §6 stay bugs.
- No secrets, tokens, certificate passwords, or connection strings.
- Diagrams stay Mermaid.
- A closed plan or an accepted ADR is amended in place. Add a dated note that names #315. Do not delete the history of what the text used to require. Do change the sentence a reader would follow, so the current rule is the one on the page.
- The code is the source of truth. Do not "fix" a doc by describing behavior the code does not have. Do not describe a code bug as an allowed exception.
- Do not merge the plan pull request or any implementation pull request from the agent that opens it.
- There is no doc-test project in this repo (no project, workflow, or script that checks markdown links or quotes). Verification is the search in each item. Do not add a doc-test project in these pull requests.

**Verification, for every item.** Re-read the symbol named in that item. Then search the edited file for the retired sentence and confirm a reader cannot mistake it for the current rule. A dated "this used to say" note may keep the old words. Build nothing. These pull requests do not need `dotnet build`.

## 3. Items

### 3.1 README still says prose is not graded (§ original item #3)

**Why this is open again.** Phase A wrote the sentence the audit asked for, and it told #304 to update the README when prose grading shipped. Phases 0–9 have shipped. The README was not one of the files #351 edited.

**File.** `README.md`, the paragraph that starts "**The quality-scoring judge**" (lines 326–335).

**What the doc claims now.** Arc Router grades fenced code blocks. `QualityIngress` does not enqueue a turn whose `ISignalExtractor.Extract` returns null. `CodeBlockSignalExtractor` returns null when there is no fence. Grading every response, including prose, is open issue #304, not current behavior.

**What the code does.**

- `CodeBlockSignalExtractor.Extract` still returns null when `FencedCodeBlockParser.Parse` finds no block (`CodeBlockSignalExtractor.cs`, the `blocks.Count == 0` return).
- `QualityIngress.TryIngest` does **not** stop on that null. The call is `Extract(...) ?? CreateBootstrapRequest(...)`. `CreateBootstrapRequest` builds a `QualityRequest` with empty `Code`, `CodeLanguage.Unknown`, empty `Dimension`, and `RecipeId: GradingRecipes.Bootstrap`, then `TryEnqueue` runs.
- The ingress still returns without a grade when quality scoring is disabled, when sampling drops the turn, or when it stores a terminal incomplete instead of enqueueing: tool call and no text (`IncompleteReasons.DeferredToLoop`), empty text (`NoText` or `Empty`), text whose newest request turn is tool results (`LoopUnlinked`). A full queue logs and stores `QueueFull`. Those are receipts with `GradeStatus.Incomplete`. They are not a score that learns.
- `QualityRescanService.RescanOneAsync` is the other path. A null extract still calls `MarkQualityRescannedAsync` with a null score and returns. The sweep does not call `CreateBootstrapRequest`.
- `GraderEpochOptions.TakeoverEnabled` defaults to `true`. `LocalGradeTakeover.ApplyAsync` sets `AllowLocalGradeOfRecord` and skips the teacher when the artifact matches, takeover is on, the row is not low-confidence, the 5% draw missed, and `rollback.json` is absent. `QualityScorer.Score` uses `LocalScore` on the judge axis only while `AllowLocalGradeOfRecord` is still true.
- A low-confidence row sets `AllowLocalGradeOfRecord` false and calls the teacher. The emergency switch does the same for every row.
- A spot-check draw calls the teacher and leaves `AllowLocalGradeOfRecord` true on the way in. `QualityScoreAggregator.CollapseLocalGrade` then forces that flag off when `JudgeScore` is set, so a teacher score that arrived is the judge axis. `GraderEpochPhase8Tests.SpotCheck_TeacherMiss_KeepsLocalScore` is the miss: no `JudgeScore`, and `LocalScore` stays the grade of record. A low-confidence miss does not keep it, because the flag was already false.
- Not every response is graded. Sampling, the terminal reasons above, and a disabled judge are still real.

**Correction.** Replace that paragraph with:

> **The quality-scoring judge** is an implementation of G-Eval, by Liu et al. A fenced block is graded on the code recipe (`CodeBlockSignalExtractor.Extract` returns it, `GradingRecipes.Code`). Unfenced text is not dropped on the live path. `QualityIngress.TryIngest` builds a bootstrap request (`CreateBootstrapRequest`, empty dimension, `GradingRecipes.Bootstrap`) when `Extract` returns null, and enqueues that request. The ingress still skips a turn when quality scoring is disabled or when sampling drops it. It stores a terminal `Incomplete` receipt, and does not enqueue a score, for a tool call with no text (`deferred_to_loop`), empty text (`no_text` or `empty`), a text reply whose newest request turn is tool results (`loop_unlinked`), or a full queue (`queue_full`). The background sweep is a different path: `QualityRescanService.RescanOneAsync` still stamps a null score when `Extract` returns null and does not build a bootstrap request. The G-Eval judge (`GEvalJudgeClient`) joins only when the result has a correlation id, the judge is enabled, and a free backbone resolves (`JudgeAvailability`). Static syntax and analysis can score a block with no judge. When a kind has a matching accepted grader-epoch artifact, `GraderEpochOptions.TakeoverEnabled` (default true) lets a confident row that the 5% draw did not pick skip that judge. `JudgeScore` stays empty on that row, and `QualityScorer.Score` uses `LocalScore` on the judge axis. A 5% sample and a low-confidence row still call the teacher. If that call returns a score, `QualityScoreAggregator.CollapseLocalGrade` turns `AllowLocalGradeOfRecord` off and the scorer uses `JudgeScore`. A spot-check whose teacher call fails keeps `LocalScore`. A low-confidence miss does not. Issue #304 is this behavior. It is not a promise that every response receives a score.

Keep the G-Eval citation that follows the paragraph. Do not edit `QualityIngress.cs`. Its interface summary still says the ingress "extracts a runnable snippet, and enqueues it". That comment is §6. This pull request does not change it.

**Verify.** Re-read `QualityIngress.TryIngest`, `CreateBootstrapRequest`, `QualityRescanService.RescanOneAsync`, `LocalGradeTakeover.ApplyAsync` from the `TakeoverEnabled` check, and `QualityScorer.Score`. Search `README.md` for `open issue` next to #304 and for `does not enqueue a turn`. Both are gone. The new paragraph names `CreateBootstrapRequest` and the sweep's null stamp.

### 3.2 how-it-learns: takeover "is later", and a full queue "is never graded"

**File.** `docs/how-it-learns.md`.

**What the doc claims now.**

- Lines 128–131: a response dropped because the queue was full "is never graded *at all*", and the queue "drops work rather than deferring it".
- Lines 133–136: the sweep stamps a prose row that has no fence with a null score and does not send it to the teacher. This sentence is **true**. Do not delete it.
- Lines 150–156, "Teacher phase and takeover": passing the gate writes a shadow score that "is not the grade of record. Takeover is later."

**What the code does.** A full queue is `QualityIngress` `SubmitTerminal(..., IncompleteReasons.QueueFull)`. That writes an `Incomplete` receipt. It does not enqueue a score, and it does not learn. Takeover is in the tree (`#348`). `TakeoverEnabled` defaults to true. The kind names are `code` and `non-code` (`GraderEpochKinds`), not a prose keyword. `ShadowLocalScoreObserver` still stamps `LocalScore` beside a teacher score and does not change who the grade of record is. That path runs when the teacher still graded the row.

**Correction.**

Replace the queue-full clause so it says: a full queue stores a terminal `Incomplete` receipt with reason `queue_full` and does not enqueue a score. That receipt does not learn. The queue still does not defer the work. Keep the point that a rule change does not rewrite old traffic unless `Quality:ScorerVersion` is bumped, and that #304 does not bump it (`QualityOptions.ScorerVersion` defaults to `3.0`).

Leave the sweep paragraph (lines 133–140) as it is, including the null stamp and the "does not bump ScorerVersion" sentence. Those match `RescanOneAsync`.

Replace the "Teacher phase and takeover" section with:

> The pinned free teacher grades first. Two local graders, one for code recipes and one for non-code recipes (kind folders `code` and `non-code`), train on the teacher's judge score. `GraderEpochOptions.TakeoverEnabled` defaults to true. When that kind has a matching accepted artifact and no `rollback.json`, a confident answer that the 5% draw did not pick skips the teacher. `LocalScore` is then the grade of record, and `JudgeScore` stays empty. A shadow score written beside a teacher score is not the grade of record. That is what `ShadowLocalScoreObserver` does when there is no matching artifact yet, when the emergency switch is off, or when `rollback.json` is present. A 5% sample and a low-confidence answer still call the teacher. A teacher score that comes back wins: `CollapseLocalGrade` turns the local-grade flag off. A spot-check whose teacher call fails keeps `LocalScore`. A low-confidence miss does not. A spot-check window whose mean miss is greater than 0.10 gives that kind back to the teacher.

**Verify.** Search this file for `Takeover is later`. It is gone. Search for `never graded *at all*`. It is gone. The sweep sentence about a null stamp on an unfenced row is still present. Re-read `RescanOneAsync` and `GraderEpochOptions.TakeoverEnabled`.

### 3.3 The subagent evidence doc still says only fences are graded

**File.** `docs/research/subagent-and-helper-routing-evidence.md` §4, the two bullets that start "Only responses with a fenced code block are graded" (around lines 107–114). The section header says "From the code, not from docs."

**What the doc claims now.** `Extract` returns nothing with no fence, and `QualityIngress.TryIngest` then drops it. Helper output is effectively never graded, so a helper quality floor never fires. Subagent turns are mostly tool calls and are rarely graded.

**What the code does.** `TryIngest` does not drop a null extract (§3.1). A helper reply that is unfenced text is a bootstrap request and can be graded. A tool call with no text is `deferred_to_loop` and does not learn. A text reply that follows tool results is `loop_unlinked` and does not learn. A quality floor still protects only a turn that received a score.

**Correction.** Replace those two bullets. State the bootstrap enqueue, the two terminal tool reasons, and that a floor still does nothing for a turn with no score. Do not change §1–§3 (vendor behavior) or the utility-rule numbers in the same section. Those were not re-checked as false.

**Verify.** Search this file for `TryIngest` then drops. That sentence is gone. The new bullets name `CreateBootstrapRequest`, `deferred_to_loop`, and `loop_unlinked`.

### 3.4 ADR-0022's "what the router can measure" driver

**File.** `docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md`, Decision Drivers, the three bullets under "Respect what the router can measure" (lines 44–47). Status stays `accepted`. Do not change the decision outcome (route by kind). Do not change option 3's historical "Helpers are never graded" consequence except to point at the new note, so a reader does not apply it as today's measurement.

**What the doc claims now.** Only fenced responses are graded. Helpers are never graded, and subagent turns rarely are. A quality floor protects only where grades exist.

**What the code does.** Same as §3.3. The route-by-kind decision is still the accepted decision. Grading text helpers does not, by itself, change that decision. Changing the route would be a new ADR. This pull request does not open one.

**Correction.** Add a dated note (2026-10-27, #315) directly under those three bullets. Label the three bullets as the measurement on the day the ADR was accepted (2026-10-03). Then state the current measurement in three short sentences: unfenced text is a bootstrap grade; a tool-only turn and a `loop_unlinked` reply are `Incomplete` and do not learn; the floor still protects only a scored turn. The route-by-kind outcome is unchanged.

**Verify.** The status line still says `accepted`. The note names `CreateBootstrapRequest`. Search the decision-outcome section for a new routing rule. There is none.

### 3.5 ADR-0027 still says the drop is "today" and takeover is "a later phase"

**File.** `docs/adr/0027-grade-non-coding-responses-on-a-bootstrap-recipe-and-keep-them-out-of-code-tallies.md`.

**What the doc claims now.**

- Context, lines 13–14: "Today `CodeBlockSignalExtractor.Extract` returns null, `QualityIngress.TryIngest` enqueues nothing."
- Decision outcome point 3, lines 75–76: "This implementation ships the teacher path; takeover is a later phase of the same plan, not a second ADR."
- Amendment 3 records the procedure and does not say the code landed. Amendment 4 records the chart decision the same way. The procedures match the code. The missing fact is that they have landed.

**What the code does.** Phase 0 landed the bootstrap enqueue (`#334`). Phase 8 landed takeover (`#348`). Phase 9 landed the marks and the token-efficiency pill (`#351`). `RescanOneAsync` still stamps null. Amendment 2's explanation that phase 7 does not take over is still the right reading of phase 7. It is not a statement that phase 8 is unbuilt.

**Correction.**

- In the context paragraph, replace "Today" with "On 2026-10-23, before phase 0". Add one sentence: after [#334](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/334), a null extract calls `CreateBootstrapRequest` and is enqueued. `RescanOneAsync` still stamps a null score.
- Under decision point 3, add a dated note: phase 8 landed in #348 and phase 9 landed in #351. "Later phase" means later than phase 7. That later phase is in the tree. Amendment 3 is the procedure that shipped. It is not a second ADR, and it is not still waiting. Do not delete Amendment 2.
- Do not rewrite Amendment 3 or Amendment 4's procedure lists. They were checked against `LocalGradeTakeover`, `GraderEpochOptions`, and `GraderEpochEfficiency.TryKnown` and they match. A line-number citation inside Amendment 3 ("lines 98–102", "lines 100–117") is a plan-time citation. Do not treat a shifted line as a wrong decision. If a citation is re-checked and the method is gone, name the method and drop the dead line number. Do not hunt line numbers that still resolve.

**Verify.** Search the context for `enqueues nothing` used as the current tree. It is labeled as the pre-phase-0 tree. The new note names #348 and #351. The status line stays `accepted`.

### 3.6 Closed plans that still tell a reader the drop is live

Three files. Each gets a dated amendment. Do not rewrite the rest of the plan.

| File | Sentence a reader would follow | Correction |
|---|---|---|
| `docs/plans/issue-293-graders-judge-against-the-question.md` finding 5 (around line 204) | "Responses with no code block are never graded" and "`TryIngest` then enqueues nothing". | Add: **Amendment (2026-10-27, #315).** That was the finding on 2026-10-14. The live path now calls `CreateBootstrapRequest` when `Extract` returns null. `QualityRescanService.RescanOneAsync` still stamps a null score. #293 stays the code-reply contract. |
| `docs/plans/issue-163-subagent-aware-routing.md` "Why the design changed" point 2 (lines 31–33) | "Only responses with a fenced code block are graded" and "Helper output is never graded". | Add the same amendment. Point at §3.3 of this file for the tool-only exceptions. Do not change the approved route. |
| `docs/plans/issue-304-grade-non-coding-responses.md` | The status paragraph says the phase 9 file "is a draft" whose open questions are Q1 and Q2. The summary's first bullet says a reply with no fence "is dropped before any grader runs". §1 (line 25) says "The prose drop below is still the live path" at `88ef8bc2`. | Status: phases 0–8 landed as the paragraph already says for the earlier phases; phase 9 landed in #351; Q1, Q2, Q3, and the unit switch were answered before that merge; the phase 9 file is not a draft waiting on a decision. Summary bullet: replace the drop sentence with the live `CreateBootstrapRequest` fact, and say §1 is the pre-phase-0 record. §1 banner: replace "still the live path" with "this section is the tree at `88ef8bc2`, before phase 0. It is not the live path." Leave the old diagram in place under that banner. Do not redraw it. |

**Also in the phase 8 plan, one status sentence.** `docs/plans/issue-304-phase-8-grader-takeover.md` line 5 says "This pull request does not add that code" and "a type name or file path marked proposed is not in the tree yet." That described the plan pull request. Add one sentence at the top of the status: implemented in #348. The procedure in the file is what shipped. Do not rewrite P1–P9.

**Verify.** Search the three plan files and the phase 8 status for a current-tense "enqueues nothing", "is a draft", or "does not add that code" that is not inside a dated "this used to say" note. Those bare claims are gone. The #304 §1 diagram is still in the file.

### 3.7 The approved #315 plan's item #3 text

**File.** `docs/plans/issue-315-docs-code-reconciliation.md` §3.3 (the paragraph that begins "With: Arc Router grades fenced code blocks").

**What the doc claims now.** It is the instruction Phase A followed. Read as a current rule, it still says a null extract is not enqueued and that prose grading is not current behavior.

**Correction.** Do not rewrite §3.3 as if the 2026-10-20 audit had said something else. Add a dated note under it: the paragraph is the edit that was correct at `d3452c4a`. After #304 phases 0–9 it is not the current rule. The current README text is §3.1 of this file. §4 of the approved plan (the writer) is unchanged.

**Verify.** §4 still describes `Channel<ServedRequest>`. The new note names this file. The old paragraph is still visible and labeled as the audit-time instruction.

### 3.8 Cost Analytics docs still say seven charts, and one bullet sends the eighth pill through the wrong builder

**Files.** `docs/gui/dashboard.md`. #351 already added the eighth pill, the epoch strip, and the incomplete-grade card. Those new paragraphs match the code and stay. The sentences below do not.

**What the doc claims now.**

- Line 52: "the seven bespoke Cost Analytics charts".
- Lines 172 and 192: the bespoke charts are "one point per turn", and "the chart models are built by `CostChartBuilder.Build`".
- Lines 198–199: "Every other metric merges rollup-backed history with live conversation turns." "Every other" means every metric except Routing ROI, which now includes Token efficiency.
- Line 388: `MockData.BuildMetricHistory` populates "all seven metrics". This one is **true** for `MockData`. Token efficiency never uses it (`CostAnalytics.BuildEfficiency`, and `_isDemoCorpus` is set only inside `BuildCorpus`, which the token-efficiency branch does not call). Do not change "seven" to "eight" in that sentence.

**What the code does.**

- `CostMetric` has eight values. The eighth is `TokenEfficiency`. `CostAnalytics.BuildEfficiency` builds `CostChartKind.TokenEfficiencyLine` from `GraderEpochChartView.Points`. It does not call `CostChartBuilder.Build`.
- `GraderEpochEfficiency.Bucket` / `TryKnown`: a row counts only when both token counts and the cost are present, the cost is greater than 0, and the token sum is greater than 0. A null, a zero, and a negative are skipped in both views. The page does not divide.
- `LoadEpochAsync` does not read the session dropdown. The comment on that method says the chart is account-wide.
- `ShowLegend` is `_metric == CostMetric.TokenEfficiency || !_isDemoCorpus`. `ShowEpochChrome` also requires `_epoch is not null`. The legend can show on a non-demo pill when the epoch read failed. The strip, the keyboard list, and the detail region cannot. The doc's sentence that all four "stay off a pill that is showing demo data" is true and stays. Add the split so a later edit does not force them onto one flag.
- Epoch colors are `EpochMarkerPalette` (fixed hex, including "Feature list changed" for `feature_schema_changed`). They are not `ChartPalette`. `ChartPalette` still colors model series on the first seven pills.
- `UsageAdminService.GetGraderEpochChart` is a new RPC on a service `telemetry.md` already names. `telemetry.md`'s service list was re-read against every `service` line in `src/Protos/telemetry.proto` and `src/Protos/admin.proto` and every `MapGrpcService` call. No service was added. Do not add a service to that list. Do not give `GetGraderEpochChart` its own section in `telemetry.md` in this pull request. The chart's user-facing description belongs in `dashboard.md`.

**Correction.**

- Line 52: seven per-turn charts plus the token-efficiency line.
- The "one point per turn" parenthetical applies to the first seven pills. Token efficiency is buckets (`GraderEpochEfficiency`), not one point per turn.
- `CostChartBuilder.Build` builds the first seven. `CostAnalytics.BuildEfficiency` builds the eighth from the epoch chart, not from a turn corpus.
- Replace "Every other metric merges rollup-backed history…" so Routing ROI stays on its own corpus, the next six pills merge rollups and live turns, and Token efficiency does neither. It reads `GetGraderEpochChart`.
- After the epoch paragraph, one sentence: the legend uses `ShowLegend`. The strip, keyboard list, and detail use `ShowEpochChrome`, which also needs a successful epoch read.
- Line 388: keep "seven". Add that token efficiency is not in `MockData` and does not show `· demo data`.

**Verify.** Search `dashboard.md` for `seven bespoke`. It is gone. Search for `Every other metric merges`. It is gone. The MockData sentence still says seven and also says token efficiency is not in that corpus. Re-read `CostAnalytics.BuildEfficiency`, `ShowLegend`, `ShowEpochChrome`, and `GraderEpochEfficiency.TryKnown`.

### 3.9 The cost-analytics spec's status still says seven

**File.** `docs/gui/cost-analytics-visualization-spec.md`, the status banner (lines 3–7).

**What the doc claims now.** "The seven bespoke per-metric charts below are live" and "the per-metric chart models are built by" `CostChartBuilder`.

**What the code does.** The seven sections below are still the seven per-turn charts, and `CostChartBuilder` still builds those. The eighth pill is not specified in those sections.

**Correction.** Change the status sentence so the seven sections are the seven per-turn charts, and the eighth pill is Token efficiency, specified in `dashboard.md`, built by `GraderEpochEfficiency` and `CostAnalytics.BuildEfficiency`, not by `CostChartBuilder.Build`. Do not rewrite the seven chart sections. Do not add an eighth section that duplicates `dashboard.md`.

**Verify.** The banner no longer says the seven charts are the whole tab. The seven section headings are still in the file.

### 3.10 DESIGN.md's `style=` inventory is missing one legal color and hiding three bugs

**File.** `docs/gui/DESIGN.md` §7, the paragraph that starts "Every remaining `style=` attribute" (around lines 827–837).

**What the doc claims now.** Every remaining `style=` in `TotallyHotArcRouter.Gui.Components/Components` is one of the listed exceptions. The list names `CostAnalytics.razor` `background:@m.Color` and does not name `background:@entry.Color`.

**What the code does.** `git grep style=` on `src/TotallyHotArcRouter.Gui.Components/Components` at `37dfd1d0` also finds:

| Location | Attribute | Class |
|---|---|---|
| `CostAnalytics.razor` line 124 | `style="background:@entry.Color"` | Data-driven color. Same exception as `background:@m.Color`. `entry.Color` is `EpochMarkerPalette`. |
| `CostAnalytics.razor` line 147 | `style="max-height:180px;overflow:auto"` | Static layout. Not a sanctioned exception. |
| `CostAnalytics.razor` line 150 | `style="min-height:44px"` | Static layout. Not a sanctioned exception. |
| `LearningReportCard.razor` line 86 | `style="min-width:16rem;max-width:22rem"` | Static layout. Not a sanctioned exception. |

The design rule in the same section is that static layout is a CSS class, not a `style=` attribute. These three are code bugs. §6 says so. This pull request does not edit the `.razor` files, and it does not add the three attributes to the allowed list.

**Correction.** Add `background:@entry.Color` to the data-driven color list. Replace "Every remaining `style=` attribute … is one of the sanctioned exceptions" with: every `style=` except the three static attributes above is one of the sanctioned exceptions, and those three violate §7. Name the file and the attribute. Point at §6 of this plan. Do not delete them in this pull request.

**Verify.** Re-run `git grep -n 'style=' -- 'src/TotallyHotArcRouter.Gui.Components/Components/*.razor'`. Every hit is either on the sanctioned list or named as one of the three bugs. Search `DESIGN.md` for a sentence that calls `max-height:180px` an allowed exception. It is not there.

### 3.11 The #350 plan still says the tests have not changed

**File.** `docs/plans/issue-350-flaky-grader-epoch-phase7-tests.md`, the status paragraph (line 11).

**What the doc claims now.** "Waiting for David's approval. This pull request is docs only. … Implementation waits for David's explicit sign-off."

**What the code does.** [#353](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/353) merged the test change. At `37dfd1d0`, `SqliteConnection.ClearAllPools()` remains in exactly two places, which is decision 5 of that plan: `TestTempDirectorySweeper.Dispose` and `RouterMemoryDatabase.AdoptLegacyInstallDirectoryCopy`. `GraderEpochPhase7Tests.AssertTickDidNotThrow` fails the test when a log line contains `threw unexpectedly`. That is Task 2. The diagnosis in the plan body was not re-litigated. The status line is the sentence that is wrong.

**Correction.** Replace the status paragraph. Implemented in #353. The body below is the record of the decision that shipped. "Implementation waits" described the plan pull request, not the tree. Do not rewrite the hypothesis, the task list, or the "questions for David" inside that file. Do not edit a test.

**Verify.** The status names #353. Search the status paragraph for `Implementation waits`. It is gone, or it sits in a sentence that says it described the plan pull request. `rg "ClearAllPools" src` still shows the two call sites the plan said to leave, and no others.

## 4. Out of scope

- Item #4, the `Channel<ServedRequest>` writer. It stays §4 of [`issue-315-docs-code-reconciliation.md`](issue-315-docs-code-reconciliation.md).
- Editing `.cs`, `.razor`, `.js`, `.proto`, tests, or workflows. §6 records the code bugs and does not fix them.
- Marking G3 shipped. `docs/router/geval-shadow-scoring-plan.md`, `docs/README.md`, and `src/PLAN.md` say G3 is not in the tree because the old non-executable carve-out is not what shipped, and they already say the embedding-memory flag matches `JudgeScore.HasValue` after #319. #304 is a different recipe. Do not collapse those two sentences into "G3 shipped".
- Rewriting `telemetry.md`'s service list. It matches the `service` lines and the `MapGrpcService` calls. `GetGraderEpochChart` is an RPC on `UsageAdminService`, which is already named.
- `docs/plans/router-evaluator-and-live-pricing.md` §14. #351 added the three bullets the phase 9 plan required (teacher, separate epoch, `ScorerVersion` stays `3.0`). They match the code. Do not edit them.
- Port numbers, ACLs, Serilog `WriteTo`, `management-token.txt`, adaptive-routing default, metadata-only session lists, and the other Phase A items whose sentences were not touched by #304, #319, #326, #350, or #351. A full second pass over those is not this plan. If an edit in B1–B3 happens to sit next to one and the sentence is still true, leave it.
- Accepting ADR-0017, building `GetSessionTurnDetails`, or changing helper routing (question 2).

## 5. Checked and left alone

| Check | Result |
|---|---|
| `README.md` routing paragraph (original item #1) | Still says an explicit circuit-open model returns 503 `CircuitOpen` and is not substituted. Not part of #304. Left alone. |
| `src/README.md` unknown-model sentences (item #2) | Still say 400 is the empty-model and nothing-eligible cases. Left alone. |
| Proto `service` lines vs `MapGrpcService` vs `telemetry.md` | The same set of services. No new service in #351. |
| `DialogShell` components | Still the nine names in `DESIGN.md` §5.5. #351 did not add a dialog. |
| `docs/how-it-learns.md` sweep paragraph | Matches `RescanOneAsync`. Kept. |
| `docs/gui/dashboard.md` epoch strip, unit switch, incomplete-grade card | Match `CostAnalytics`, `EpochMarkerPalette`, `EpochMarkerText.NoCause`, and `LearningReportCard`. Kept. |
| G3 header | Still "not in the tree", with the #319 flag called out. Left alone (§4). |

## 6. Code bugs this plan will not paper over

These are real code defects against a written rule. The docs pull requests name them. They do not add an exception that makes the defect look intended. A later code pull request, with its own plan if the standing rule requires one, can move the styles into classes or fix the comment.

1. **Static `style=` attributes** from #351, listed in §3.10: `max-height:180px;overflow:auto`, `min-height:44px`, and `min-width:16rem;max-width:22rem`. `DESIGN.md` §7 forbids static layout in `style=`. Do not add them to the sanctioned list.
2. **`IQualityIngress` summary** (`src/TotallyHotArcRouter.Quality/Ingress/IQualityIngress.cs`). It says the ingress "extracts a runnable snippet, and enqueues it". The live path also enqueues a bootstrap request with empty code, and it stores terminal incomplete receipts. The summary describes the pre-#304 path. Fixing it is a source edit. Not in B1–B3.

## 7. Questions only David can answer

Each recommended answer is what this plan does if he says nothing is wrong with the recommendation. A different answer changes the named section before implementation.

1. **What should this stack of doc fixes be called?** The first plan already uses the name Phase B for a program: a background saver for request records (item #4). This file is only about words in the docs. Nobody wrote this docs list down before today.
   **Recommended answer.** Keep the writer where it is, in §4 of the approved plan. Do not build it from this file, and do not delete §4. Call this file the docs follow-up. The filename says `phase-b` because that is the name this task was given. If you want the writer to keep the name Phase B all by itself, say so and a later edit renames this file to Phase C. Until that comment, implementers use this file for the docs and the old §4 for the writer.

2. **Helpers can be graded now when they answer in words. Should we route them differently?** The old decision said helpers are never graded, which is why a helper quality floor never fires. A helper that returns text and no code fence can be graded now. A helper that only calls tools still gets no score.
   **Recommended answer.** No. Fix the sentences (§3.3 and §3.4). Leave the route-by-kind decision alone. Changing who serves a helper is a new decision, not a doc fix.

3. **Three pictures use a `style=` the design rules do not allow. Should the docs allow them?** They set a box's height, a button's height, and a card's width in the markup. The rule says that kind of layout lives in the stylesheet.
   **Recommended answer.** Do not allow them in the docs. Write them down as bugs (§6). A later code change moves them into CSS classes. This plan does not touch the Razor files.

## 8. Sign-off

David's comment on #315 or on this pull request is the sign-off. Until that comment exists, no edit in §3 is in scope for an implementation pull request. Sign-off of this file is not sign-off to build the writer, and sign-off of the writer is not sign-off of these doc edits.
