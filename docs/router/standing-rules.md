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
