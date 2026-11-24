# Plan: open Qodana smells #308–#311, #322–#324, #331, #374–#376

**Status:** Approved by David on 2026-11-24 (Cursor chat: address all open issues).

**Observed cost:** David directed these nightly findings cleared on 2026-11-24.

## What this change edits

`AppendToCollectionExpression` is still in the tree at the three sites filed on 2026-11-24:

- `DiagnosticSeverityAnalyzer.Analyze` builds the note list in one collection expression (#376).
- `SessionFileTests.Open_VersionOneFile_IsRefused` builds the version-1 header in one expression (#375).
- `StreamingSecretObscurerTests.Stream_InvalidUtf8AndSplitCharacters_PassThroughUnchanged` builds the body in one expression (#374).

## What is already true on main

These issues were not refreshed by the 2026-11-24 audit, so the findings are gone:

- #322, #323, #324 stay public. Data-annotation validation only sees public properties. Each already carries `ReSharper disable once MemberCanBePrivate.Global` and a remark that says why (cleared in `be82f49a`).
- #308 and #310 keep the per-result parameter. Today's host policy does not read it. Each already carries `ParameterOnlyUsedForPreconditionCheck.Global` and a remark (cleared in `f419bec4`).
- #309 `transcriptDatabase` is stored and used by startup maintenance. The cited `transcriptStore` parameter is not in the constructor.
- #311 `serilogLogger` is passed to `SerilogLoggerProvider` when it is not null. It is not a null-check-only parameter.
- #331 `Issue319UntrustedJudgeProvenancePurgeKey` is already `private`.
