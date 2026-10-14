# 0026. Grade a response against its question, and learn only from complete grades

**Status:** accepted
**Date:** 2026-10-14
**Deciders:** David Pizon
**Accepted:** 2026-10-14 (David, chat sign-off on #293 plan)

## Context and Problem Statement

The router chooses agents from live grades ([#293](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/293)).
A grade is a mix of three signals: whether the code parses (`SyntaxValid`), a static analysis score, and a G-Eval
judge score. The judge has read the user's question since PR #117, and `GraderQuestionText.Require` makes every LLM
grader fail closed without it. The other two signals never see the question. When the judge is off, abstains, times
out, or its join is evicted, `QualityScorer` drops that axis and divides by the remaining weights, and
`RouterMemoryScoreObserver` and `EmbeddingMemoryScoreObserver` write the result into router memory exactly as if the
judge had answered. That score says "this is well-formed", not "this answers the question", so the router can learn
to prefer tidy answers that are wrong.

Four more facts make this a decision and not a patch:

- `JudgeRowPolicy` defaults to `DownWeight` at 0.5, so the only rows that carry question-aware evidence count for
  half and the rows that do not count in full.
- `DimensionCriteria` in `GEvalJudgeClient` is keyed on names the router does not use (two of nine
  `RouterDimension` values match), so the judge mostly runs a generic "overall quality" rubric.
- The scorer's effective weights vary per request, while the paper's Verifier (Zhou et al., arXiv:2606.22902, §3.3
  Eq. 8) fixes them per task type and requires they sum to 1.
- A grade is stored with a `correlation_id` and nothing that ties it to its question. Question text is held in
  memory and dropped, and [ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md) requires
  SQLite to stay a text-free index, so the link cannot simply be the text.

The change alters what the router learns from, which is why it needs an ADR. The detailed plan is
[`docs/plans/issue-293-graders-judge-against-the-question.md`](../plans/issue-293-graders-judge-against-the-question.md).
It implements phase 4 of [`router-evaluator-and-live-pricing.md`](../plans/router-evaluator-and-live-pricing.md).

## Decision Drivers

- **Learn the right target**: a live grade that trains the router must measure "answered the question", not
  "looks well-formed".
- **No silent renormalization**: an abstention must be visible and must not change the weights behind the scenes.
- **Text-free index**: grade rows may not hold question text ([ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md)),
  and a link to a question must not bypass the passkey gate ([ADR-0020](0020-require-passkey-verification-for-conversation-content.md)).
- **Bounded exposure**: how much of the user's conversation reaches a grader must be limited, and the grader must
  be no new destination.
- **Auditable and reproducible**: each grade must say which rules produced it.
- **A trustworthy start**: learning written under the old rule, and tallies that cannot be separated by row, must not
  sit beside grades that saw the question.

## Considered Options

- A. Fixed weights with explicit grade status, a learning gate that admits only `Complete` grades, and grade
  receipts that link to the question by keyed hash and fingerprint reference
- B. Keep renormalizing, and only down-weight rows that lack a judge
- C. Make the static graders question-aware (a question-versus-response similarity check) and keep learning from
  static-only grades
- D. Store the question text in the grade row so audits need no join

## Decision Outcome

Chosen option: "A. Fixed weights with explicit grade status, a learning gate that admits only `Complete` grades,
and grade receipts that link to the question by keyed hash and fingerprint reference", because only it satisfies
the **Learn the right target** and **No silent renormalization** drivers together while keeping the **Text-free
index** driver. A is the one option in which a grade that never measured the question can never move the router.

The rules, in short (full design in the plan):

1. A grade has a status: `Complete` (every nonzero-weight grader answered), `StructureOnly` (no judge is
   available), or `Incomplete` (a judge was available and did not answer). Only `Complete` grades enter router
   memory, the voters, or the trainers. The other two are stored for the dashboard and audit, labeled.
2. Weights are fixed per dimension and must sum to 1, checked at startup. Starting weights for code tasks are
   judge 0.60, syntax 0.25, analysis 0.15. The remaining-weight division and the syntax weight halving are removed.
   Heuristic syntax carries a confidence in `[0,1]`; below 0.70 the grade is `Incomplete` (weights stay fixed).
3. The judge scores one grade against three named criteria — relevance, correctness, completeness — with a
   per-dimension hint keyed on `RouterDimension`.
4. A grader sees the newest user message plus up to six earlier text messages, within 16,384 characters, after
   secret obscuring, and only through the provider the judge already uses. System prompts, tool output, file
   contents and images are never sent.
5. A `grade_receipts` table stores the status, an HMAC-SHA-256 of the question keyed by an install-local secret,
   size facts, the rule versions, and a nullable fingerprint reference reserved for
   [#294](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/294). It stores no question text.
6. `JudgeRowPolicy` defaults to `Include`. Existing live learning (rows and `live:` tallies) is deleted once on
   upgrade, finally, per ADR-0024. Benchmark data is kept. David chose this on 2026-10-14 over quarantining the
   rows.

### Consequences

- Good, because the router can no longer reward a tidy answer to the wrong question through a missing or failed
  judge.
- Good, because each grade can be traced to its question (by keyed hash, and to the text through the passkey-gated
  session file) and to the exact rules that produced it.
- Good, because the weights match the paper's Verifier and are visible in configuration.
- Bad, because with no judge available the router stops learning from live traffic. David accepted this on
  2026-10-14, so the dashboard must show "learning paused" instead of looking healthy.
- Bad, because the judge call is longer, so the free provider's quota and latency are used faster, and more of the
  user's conversation reaches that provider than the newest message alone.
- Bad, because the judge becomes the dominant signal (0.60), which makes the router's learning depend on one
  model's opinion and on the judge's known self-preference bias. The calibration analysis in
  `judge_shadow_scores` has not yet been run on the new rubric.
- Bad, because the one-time purge is irreversible: the router loses what it learned from live traffic and runs on
  benchmark priors until new `Complete` grades arrive, and the upgrade notes must say to copy the database first.
- Neutral, because `ScorerVersion` moves to 3.0 and the rescan re-grades history, which spends judge quota.
- Neutral, because `fingerprint_ref` stays NULL until #294, so similar-question comparison is exact-match only
  until then.
- Neutral, because the static graders stay blind to the question; Option C remains available as a later issue.

## Pros and Cons of the Options

### A. Fixed weights, explicit status, learning gate, keyed-hash receipts

- Good, because a grade that did not measure the question cannot teach the router.
- Good, because abstentions are recorded as such rather than hidden in a smaller denominator.
- Good, because the receipt links to the question without putting text in SQLite.
- Bad, because live learning pauses whenever no judge is available.
- Bad, because it needs a schema change, a startup validation, and a one-time purge migration.

### B. Keep renormalizing and down-weight rows lacking a judge

- Good, because it is the smallest change and learning never stops.
- Bad, because a down-weighted tidy-but-wrong grade still teaches the router the wrong thing, only more slowly.
- Bad, because it keeps the effective weights per request unfixed, against Eq. 8.
- Bad, because it is the policy already in place (`DownWeight`), and it is inverted: question-aware rows count less.

### C. Make the static graders question-aware and keep learning from static-only grades

- Good, because learning would continue with no judge, and a cheap relevance check could catch some wrong-question
  answers.
- Bad, because a similarity score between a question and code is a weak proxy for correctness and completeness.
- Bad, because it adds a new signal to calibrate before anything depends on it.
- Bad, because David scoped it out on 2026-10-14; it can follow as its own issue.

### D. Store the question text in the grade row

- Good, because an audit needs no join.
- Bad, because it puts prompt text in plaintext SQLite, against [ADR-0019](0019-store-conversation-text-in-encrypted-per-session-files.md).
- Bad, because it would let anyone who can read the index read the questions, around the passkey gate in
  [ADR-0020](0020-require-passkey-verification-for-conversation-content.md).

## More Information

- Plan: [`docs/plans/issue-293-graders-judge-against-the-question.md`](../plans/issue-293-graders-judge-against-the-question.md).
- Parent plan: [`docs/plans/router-evaluator-and-live-pricing.md`](../plans/router-evaluator-and-live-pricing.md) §14, §15.
- Code this changes: `QualityScorer`, `QualityScoreAggregator`, `GEvalJudgeClient`, `RouterMemoryScoreObserver`,
  `EmbeddingMemoryScoreObserver`, `JudgeRowPolicy`, `RouterMemoryDatabase`, `ScoreTableRetentionService`,
  `RequestTextExtractor`.
- Out of date by the time this lands: `docs/how-it-learns.md` ("Honest gaps", the graders-cannot-see-the-question
  bullet), corrected in the plan's phase 6.
- Related: [#288](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/288), [#291](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/291), [#292](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/292), [#294](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/294).
