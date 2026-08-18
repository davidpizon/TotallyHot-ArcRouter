# Tech Stack

- .NET 10 (`net10.0`), C# with `LangVersion` preview, nullable reference types enabled,
  implicit usings, .NET analyzers at `AnalysisLevel latest` with code-style enforced in build.
- Solution file: `src/TotallyHotArcRouter.slnx` (slnx format, not .sln).
- Projects (all under `src/`):
  - `TotallyHotArcRouter` — main router app (`Sdk="Microsoft.NET.Sdk.Web"`, `net10.0`), gRPC
    (Grpc.AspNetCore), MCP server (ModelContextProtocol[.AspNetCore]), Serilog, SQLite
    (Microsoft.Data.Sqlite + SQLitePCLRaw pinned for a CVE), ONNX Runtime + OnnxRuntimeGenAI +
    FastBertTokenizer for local embedding/generation inference, Microsoft.SemanticKernel.
  - `TotallyHotArcRouter.Gui` — Windows-only .NET MAUI Blazor Hybrid app
    (`net10.0-windows10.0.19041.0`, `UseMaui`, unpackaged/no MSIX). BlazorWebView-hosted dashboard,
    lives in the system tray.
  - `TotallyHotArcRouter.Gui.Admin` / `.Charts` / `.Console` / `.Telemetry` — GUI-adjacent
    libraries, each with matching `.Tests` project.
  - `TotallyHotArcRouter.Sandbox` — Linux-only sandboxed executor pieces (see
    `AddSandbox_DoesNotRegisterLinuxOnlyServicesOffLinux` test); referenced by main router project.
  - `*.Tests` — xUnit v3 test projects per module (see `mem:suggested_commands` for the
    non-standard run procedure on this machine).
- Protobuf: shared `.proto` under `src/Protos/telemetry.proto`, compiled server-side into the
  router project and client-side into the GUI project from the same file (kept structurally in
  sync deliberately).
- Diagrams in markdown docs: Mermaid syntax only (no ASCII art).
