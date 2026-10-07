# Standing rules

Process rules for Arc Router board work. They bind every contributor — human, bot, or cloud
agent — until David changes them.

## Approved plan before coding

**Set by David on 2026-09-28.**

Before coding begins on any boarded item, a markdown plan must be written and approved by David.
No implementation starts until the plan is approved. A boarded item is a GitHub issue that has
been signed off for work. This applies to issue
[#165](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/165) and to every future boarded
item.

- **Plan path.** Plans live at `docs/plans/issue-<N>-<slug>.md`, where `<N>` is the issue number
  and `<slug>` is a short kebab-case name.
- **Approval.** Approval is David's explicit sign-off. A comment on the issue, or a comment on the
  pull request that adds the plan, is an explicit sign-off. The issue being boarded is not approval
  of the plan.
- **Implementation pull request.** The pull request that implements the boarded item must link the
  approved plan.

## Review locally, Copilot reviews once

**Set by David on 2026-10-05.**

Each Copilot code review uses one Copilot premium request. In the three weeks to 2026-10-05,
manual re-requests after each round of fixes drove up to 16 reviews on a single pull request
([#183](https://github.com/davidpizon/TotallyHot-ArcRouter/pull/183)). Iterate locally instead,
and let Copilot review each pull request once.

```mermaid
flowchart LR
    A[Open PR as draft] --> B[Local review: /code-review]
    B -->|findings| C[Fix and push]
    C --> B
    B -->|clean| D[Mark ready for review]
    D --> E[One automatic Copilot review]
    E --> F[Fix comments, verify with /code-review]
```

- **Open pull requests as drafts.** The "Preserve main" ruleset skips draft pull requests and does
  not re-review on push, so a draft costs no Copilot requests while you iterate.
- **Review locally before marking ready.** Run `/code-review` in Claude Code on the branch, or a
  reviewer on a different model (a subagent on another model, or another vendor's CLI), and fix
  what it finds. Local review uses no Copilot premium requests.
- **Mark ready once.** Marking the pull request ready triggers exactly one Copilot review. Do not
  request it by hand.
- **Do not re-request Copilot after fixing its comments.** Verify those fixes with a local
  `/code-review` on the new diff. Re-request a Copilot review only after a substantive redesign of
  the change, and say why in a pull request comment.
- **Dependabot pull requests** do not need a Copilot review.

## Nightly audits file issues only

**Set by David on 2026-10-07**, recorded in
[ADR-0008 Amendment 2](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-2-2026-10-07-scheduled-audits-that-do-not-churn).

Amendment 1 rule 2 banned every cadence audit. That blanket ban no longer holds. A scheduled
vulnerability scan and a scheduled code-smell scan may file GitHub issues, provided they do not
cause churn for its own sake. The smell scan files only findings that are new against the Qodana
baseline and that do not already have an issue. Neither scan opens a pull request, pushes a commit,
or starts coding.

An issue opened by the scan is not sign-off to work, and it is not approval of a plan. A fix is
boarded work under [Approved plan before coding](#approved-plan-before-coding): the plan at
`docs/plans/issue-<N>-<slug>.md` needs David's explicit sign-off before implementation. A smell fix
still needs an observed cost (Amendment 1 rule 1). A vulnerability fix's observed cost is the
vulnerability itself.
