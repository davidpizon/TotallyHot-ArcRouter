# Suggested Commands (Windows)

## Build
```
dotnet build src/TotallyHotArcRouter.slnx -clp:ErrorsOnly
```
Must report 0 warnings/errors (enforced by `TreatWarningsAsErrors` in `src/Directory.Build.props`).

## Test — xUnit v3 on Microsoft.Testing.Platform
`global.json` pins `test.runner` to `Microsoft.Testing.Platform` and every test project sets
`TestingPlatformDotnetTestSupport=true`, so `dotnet test` works directly (CI uses it too):
```
dotnet test src/<Project>.Tests/<Project>.Tests.csproj --configuration Release
```
Filter with `--filter-class`/`--filter-method` (Microsoft.Testing.Platform's flags, not VSTest's
`--filter`). You can still run the built exe directly with `-class`/`-method` filters if you want
to bypass `dotnet test`'s MSBuild overhead:
```
./src/<Project>.Tests/bin/Debug/net10.0/<Project>.Tests.exe -class "Full.Namespace.ClassName"
./src/<Project>.Tests/bin/Debug/net10.0/<Project>.Tests.exe -method "Full.Namespace.ClassName.MethodName"
```
Build first if the exe is stale. Skipped tests (e.g. requiring a locally-cached ONNX model) are
expected/benign — check the skip reason before treating as a failure.

## Shell notes
- PowerShell is primary; Bash tool (Git Bash/POSIX) is also available — each needs its own syntax
  (see `mem:core`'s referring context / project CLAUDE.md for the split).
- Symlinks (`CLAUDE.md`, `.github/copilot-instructions.md` → `AGENTS.md`) require Windows
  Developer Mode + `git config core.symlinks true`; Git Bash's `ln -s` silently makes a *copy* on
  Windows instead of a real symlink — always recreate via PowerShell `New-Item -ItemType
  SymbolicLink` if drift is suspected.

## GitHub PR workflow
`gh` CLI is used for PR review/comment work against `github.com/davidpizon/TotallyHot-ArcRouter`.
Copilot PR review re-requests: `gh pr edit <n> --add-reviewer copilot-pull-request-reviewer`.
