# Architecture Decision Records

This folder records significant architecture decisions for TotallyHot Arc Router: the choice made,
the alternatives considered, and *why* — so a decision doesn't have to be re-litigated or
reverse-engineered from a diff months later.

Not every change needs one. Write an ADR when a decision:

- is hard or expensive to reverse (a storage format, a wire protocol, a hashing scheme),
- was chosen over at least one real alternative, or
- will look arbitrary to someone reading the code later without the context.

Routine implementation choices, anything already explained in [`src/PLAN.md`](../../src/PLAN.md)'s
phase write-ups, or decisions fully captured in a plan doc under `docs/router/`/`docs/gui/` don't
need a separate ADR — link to those instead of duplicating them.

## Writing one

1. Copy [`adr-template.md`](adr-template.md), or run the scaffold script:

   ```powershell
   ./scripts/new-adr.ps1 -Title "Use git blob SHA-1 checksums"
   ```

   This creates `docs/adr/NNNN-use-git-blob-sha1-checksums.md` with the next number and today's
   date filled in.
2. Fill in every section — an ADR with an empty "Considered Options" is a decision log entry, not
   a decision record. If there was genuinely only one option, say so and explain why alternatives
   weren't viable rather than leaving the section blank.
3. Diagrams, if any, are Mermaid (repo-wide convention — see [`AGENTS.md`](../../AGENTS.md)).
4. Add a row to the index below and open a PR. Status starts as `proposed`; move it to `accepted`
   once the PR merges.
5. To use Claude Code to draft one conversationally, invoke the `adr-writer` skill
   (`.claude/skills/adr-writer/SKILL.md`) — it asks for context, drafts against the template, and
   checks the result against the required-sections list before handing it back for review.

## Changing a past decision

Never delete or silently rewrite an accepted ADR — the point is the historical record. If a
decision changes, write a new ADR and set the old one's status to `superseded by ADR-NNNN`
(linking forward); the new ADR should link back with `supersedes ADR-NNNN`.

## Index

| # | Title | Status |
|---|-------|--------|
| [0001](0001-git-blob-sha1-checksums-for-coderouterbench-sync.md) | Use git blob SHA-1, not MD5, to verify synced CodeRouterBench files | accepted |
| [0002](0002-store-probed-model-context-windows-in-their-own-table.md) | Store probed model context windows in their own table | proposed |
| [0003](0003-declare-tool-support-for-emulated-and-unclassified-models.md) | Declare tool support for emulated and unclassified models | proposed |
