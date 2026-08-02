# AGENTS.md

This repository contains the TotallyHot Arc Router project.

> **This file is the single source of truth for agent instructions.** `CLAUDE.md` and
> `.github/copilot-instructions.md` are symlinks to it — edit this file, never those. See
> [Agent instruction files](#agent-instruction-files) below.

## Start Here
- Read `README.md` first for the current project overview, quick start, demos, and repository layout.
- Read `docs/HANDBOOK.md` for maintainer guidance and extended notes.
- Use `PLAN.md` for the current C# migration roadmap.

## Working Rules
- Keep changes minimal and scoped to the user request.
- Prefer the existing repository conventions and document any deliberate deviation.
- When editing C# code, follow .NET 10 best practices: nullable reference types, async/await where appropriate, dependency injection, options binding, and structured logging.
- Add or update tests when behavior changes.
- Validate changes before finishing.
- **All builds must compile without warnings or errors.** This applies to every build, not just phase boundaries — never leave a build in a state with compiler errors or warnings (including analyzer warnings), and never suppress a warning without documenting why. Enforced at the compiler level: `src/Directory.Build.props` sets `TreatWarningsAsErrors` for every project, so a warning fails the build.
- **Every class and function must have accurate XML documentation.** All types (classes, interfaces, records, enums, structs) and all methods/properties need a `///` doc comment that reflects current behavior — stale docs (describing removed parameters, old behavior, or placeholder text like "this class will contain...") are treated the same as missing docs. Follow the codebase's established convention: multi-sentence `<summary>` explaining *why*, not just what; `<inheritdoc/>` for interface implementations; type-level `<param>` tags for primary-constructor records; one-line summaries are acceptable for plain DTOs. Enforced at the compiler level for `TotallyHotArcRouter`, `TotallyHotArcRouter.Sandbox`, `TotallyHotArcRouter.Gui`, and `TotallyHotArcRouter.Gui.Admin/.Charts/.Console/.Telemetry`: each sets `GenerateDocumentationFile`, so a missing doc on a public/protected member raises `CS1591`, which `TreatWarningsAsErrors` turns into a build failure. Only accuracy on update is not machine-checked — a stale-but-present doc still compiles, so review docs for correctness whenever you change the code they describe.
- **New GUI windows must match the "System Settings" window.** Every new window, modal, or dialog in `TotallyHotArcRouter.Gui` copies the shell of `src/TotallyHotArcRouter.Gui/Components/SettingsModal.razor` — same `.overlay-backdrop`/`.overlay-panel` classes (they carry the entrance animation), same blurred backdrop, same `max-w-md` slate panel, same header bar with an uppercase `text-sm` title and an `x` close glyph, and closing exposed as an `EventCallback` parameter rather than the window closing itself. `Components/ProviderEditDialog.razor` is an existing example to copy. Deviate only where content genuinely requires it (e.g. a wider panel for a table) and never on the header, dismissal behavior, or close API. Full contract: [`docs/gui/DESIGN.md`](docs/gui/DESIGN.md) §4.1.
- Set an upper bound for unusually heavy unit tests at 5 seconds maximum.
- **Markdown Diagrams:** All diagrams in markdown documentation MUST be represented using Mermaid syntax (not ASCII art, text boxes, or other formats). This ensures consistency, readability, and platform compatibility across documentation.
- **Phase Completion Criteria:**
  - The application must always compile with no errors or warnings at the end of each phase.
  - The application must always pass all unit tests at the end of each phase.
  - The application must maintain at least 80% code coverage in unit tests.
- **Logging:** Use Serilog exclusively for all logging. Configure Serilog via `appsettings.json` to support output destinations based on configuration:
  - **File logging:** Enable via `Serilog.WriteTo.File` configuration with customizable path, retention, and rolling file policies.
  - **Windows Event Viewer logging:** Enable via `Serilog.Sinks.EventLog` configuration for events that should be captured by the Windows Event Viewer (errors, critical events, audit trails).
  - Example configuration in appsettings.json:
    ```json
    {
      "Serilog": {
        "Using": ["Serilog.Sinks.File", "Serilog.Sinks.EventLog"],
        "MinimumLevel": "Information",
        "WriteTo": [
          {
            "Name": "File",
            "Args": {
              "path": "./logs/arcrouter-.log",
              "rollingInterval": "Day",
              "retainedFileCountLimit": 30
            }
          },
          {
            "Name": "EventLog",
            "Args": {
              "source": "TotallyHotArcRouter",
              "logName": "Application",
              "restrictedToMinimumLevel": "Error"
            }
          }
        ],
        "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"]
      }
    }
    ```
  - Use structured logging, and **always keep the message template a static string literal** — never string interpolation and never string formatting. Write `logger.LogInformation("Model selected: {Model} with confidence {Confidence}", model, confidence)`, not `logger.LogInformation($"Model selected: {model} with confidence {confidence}")`.
  - Log all routing decisions, proxy interceptions, memory updates, and errors to enable audit trails and diagnostics.

## Agent instruction files

This repo keeps **one** canonical agent-instruction file. `CLAUDE.md` and
`.github/copilot-instructions.md` are symlinks to `AGENTS.md`, so Claude Code, GitHub Copilot, and
any other agent all read the same rules and cannot drift apart:

```
AGENTS.md                          ← canonical source (edit this)
CLAUDE.md                          → symlink to AGENTS.md
.github/copilot-instructions.md    → symlink to ../AGENTS.md
```

Edit `AGENTS.md` only. If a symlink is ever replaced by a real file (a checkout without
`core.symlinks=true`, or a tool that rewrites rather than follows links), restore it from PowerShell —
Git Bash's `ln -s` silently creates a *copy* on Windows, which is what caused the drift this layout fixes:

```powershell
Remove-Item CLAUDE.md, .github\copilot-instructions.md -Force
New-Item -ItemType SymbolicLink -Path "CLAUDE.md" -Target "AGENTS.md"
New-Item -ItemType SymbolicLink -Path ".github\copilot-instructions.md" -Target "..\AGENTS.md"
```

Requires Windows Developer Mode and `git config core.symlinks true`.

## Key References
- `README.md`
- `docs/HANDBOOK.md`
- `PLAN.md`
- `data/README.md`
- `docs/gui/DESIGN.md` — GUI design system (colors, typography, components, the window/modal shell)
- `docs/gui/MOTION.md` — GUI motion system (durations, easing, entrance/exit patterns)

