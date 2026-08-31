---
name: adr-writer
description: Draft an Architecture Decision Record (ADR) for this repo's docs/adr/ system. Use when the user asks to write, draft, or document an ADR, or wants a past/pending architectural decision (storage format, protocol, hashing scheme, library choice, etc.) captured with its alternatives and rationale.
user-invocable: true
---

Draft an ADR against `docs/adr/adr-template.md`, following the process in `docs/adr/README.md`.
This mirrors the writer -> validator -> formatter pipeline from
["Building an Architecture Decision Record Writer Agent"](https://piethein.medium.com/building-an-architecture-decision-record-writer-agent-a74f8f739271),
adapted to run as one conversational pass instead of separate agents, since the checks are cheap
enough not to need separate handoffs.

## 1. Gather context (writer role)

Before drafting, make sure you actually have a decision to record, not just a topic:

- What is the problem or question? What forces make it non-obvious (perf, compliance, an existing
  convention it might break)?
- What options were, or are being, considered? Get at least two — if the user only gives one, ask
  what the alternative would have been, even a naive one, so the "why not" has content.
- What was chosen (if already decided), or help the user think through the trade-offs to reach one.
- Is there a plan doc, PR, or issue this decision belongs to? Prefer linking over duplicating —
  check `docs/router/`, `docs/gui/`, and `src/PLAN.md` for existing write-ups covering the same
  ground.

If the user points at code or a past decision instead of describing it, read the relevant
files/commits yourself (CodeGraph first, if available, for source context) rather than asking them
to summarize it.

## 2. Draft

Copy `docs/adr/adr-template.md` structure exactly. Non-negotiable:

- Number: one past the highest existing `docs/adr/NNNN-*.md`, or run
  `./scripts/new-adr.ps1 -Title "..."` to scaffold it.
- Every section filled in — no `<!-- placeholder -->` comments left in the output.
- At least two entries under "Considered Options" and matching "Pros and Cons" subsections. If
  there really was only one viable option, say so explicitly in that section instead of leaving it
  sparse.
- "Decision Outcome" names the decisive driver(s), not a restatement of what the option does.
- Diagrams, if any, are Mermaid (repo-wide rule, see `AGENTS.md`).

## 3. Validate (validator role)

Before handing the draft back, check it against this list and fix anything missing rather than
flagging it to the user:

- [ ] Status, date, and deciders are filled in (not placeholder text)
- [ ] Context section explains *why this is a decision*, not just what the system does
- [ ] Every considered option has a pros/cons entry
- [ ] Decision Outcome references a Decision Driver by name
- [ ] Consequences includes at least one "Bad, because" — a decision with zero trade-offs is
      usually under-examined
- [ ] Any file/symbol/API named in the ADR actually exists (grep or CodeGraph it — don't take the
      user's description on faith if you're citing specific code)

## 4. Hand off (formatter role)

Write the file to `docs/adr/NNNN-kebab-case-title.md`. Then:

- Add a row to the index table in `docs/adr/README.md` (`| NNNN | Title | proposed |`).
- Tell the user the status starts as `proposed` and should move to `accepted` once the PR merges.
- If this ADR supersedes an earlier one, update the old ADR's status line to
  `superseded by ADR-NNNN` and add `supersedes ADR-NNNN` to the new one's context — do this in the
  same pass, don't leave it as a follow-up.

Do not create the PR or mark the status `accepted` yourself — that's the user's call once it's
reviewed.
