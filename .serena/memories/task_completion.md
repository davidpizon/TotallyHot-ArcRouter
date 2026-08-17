# Task Completion Checklist

A change is done only when:
1. `dotnet build src/TotallyHotArcRouter.slnx -clp:ErrorsOnly` → 0 warnings, 0 errors.
2. Relevant xUnit v3 tests pass — run the built `.Tests.exe` directly (see
   `mem:suggested_commands`; `dotnet test` does not work here). Add/update tests for any behavior
   change.
3. XML docs on touched public/protected members are accurate (not just present — see
   `mem:conventions`).
4. For GUI/frontend changes: actually run the app/feature (dev server or MAUI app) and exercise
   the golden path + edge cases before claiming success — type/test checks alone don't verify UI
   behavior.
5. 80% unit-test coverage maintained at phase boundaries (`src/PLAN.md`).
6. Any deliberate deviation from repo conventions is documented inline (comment) or in the PR/doc,
   not silent.
