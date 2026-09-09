# TotallyHotArcRouter.Gui

A Windows system tray application providing UI/UX for the TotallyHotArcRouter proxy, built as a
**.NET MAUI Blazor Hybrid** app. This project is independent of the `TotallyHotArcRouter` proxy service - it
does not start, stop, or otherwise manage it.

## Behavior

- On launch, only a tray icon appears (no window, no console).
- Right-click the tray icon and select **Show Dashboard** to open the dashboard window (or double-click
  the icon). The dashboard is a Razor single-page app hosted in a `BlazorWebView` - see
  [`docs/gui/dashboard.md`](../../docs/gui/dashboard.md) for a full
  description of the UI.
- Clicking the dashboard window's minimize button, or its close (X) button, hides it back into the tray
  icon rather than minimizing to the taskbar or exiting the app.
- Select **Exit** from the tray context menu to actually quit.

## Current status

The **Sessions** and **Console** tabs are live-wired: `Services/LiveDataStore.cs` streams routing
telemetry and log lines from the running TotallyHotArcRouter proxy over gRPC (default
`https://localhost:5002`, a dedicated TLS port separate from the plain-HTTP proxy port 5001;
configurable via `GuiSettingsStore` and `SettingsModal.razor`). Both show nothing until the proxy is
reachable and has forwarded a request / emitted a log event, rather than falling back to mock data.
Sessions additionally merges persisted history from the router's `request_transcripts` table
(`Services/PersistedSessionStore.cs`), so a session survives a GUI restart.

The rest of the dashboard is live with narrower gaps, not mock:

- **Cost Analytics** plots live turns merged with rollup-backed history (`UsageStore`), falling back
  to the deterministic `MockData` corpus only when there is neither. Routing ROI and Cache Hit Rate
  are real; **Tool Steps** and **Context Buffer** still have no live source and are demonstrated by
  the mock corpus only.
- **Model Distribution** fetches real buckets through `UsageStore.LoadRollupAsync` on every filter
  change, using `MockData` only as the offline/no-proxy fallback.
- **Governance** — Providers, Price Sources, and Models are fully live against the proxy's
  management API.
- **Header ticker** — System Tokens is real (`UsageStore.LoadSummaryAsync`); Total Saved and Avg.
  Cost Reduction are still mock and labelled "(demo)".

See [`docs/router/telemetry.md`](../../docs/router/telemetry.md) for the full pipeline,
[`docs/gui/dashboard.md`](../../docs/gui/dashboard.md) for the per-tab breakdown, and
[`docs/gui/backlog.md`](../../docs/gui/backlog.md) for what is left.

## Project layout

| Path | Purpose |
| --- | --- |
| `App.cs`, `MainPage.cs`, `MauiProgram.cs` | MAUI shell: one window hosting a full-window `BlazorWebView`. |
| `Components/` | The dashboard's Razor components (tabs, cards, settings modal, icons). |
| `Components/SettingsModal.razor` | The **System Settings** window - also the reference shell every new window/modal copies (see below). |
| `Models/DashboardData.cs` | Dashboard data model + the mock data. |
| `Services/LiveDataStore.cs` | gRPC client connecting to the proxy's `TelemetryService.StreamEvents` RPC; accumulates and re-aggregates live routing events into `Conversation`/`ConversationTurn` records. |
| `Services/LiveConversationMapper.cs` | Maps `TotallyHot.ArcRouter.Gui.Telemetry`'s live-aggregation output onto the dashboard's `Conversation`/`ConversationTurn` view-model shape, with honest defaults for fields telemetry doesn't cover. |
| `Platforms/Windows/TrayWindowManager.cs` | Win32 tray icon + WndProc subclass implementing the tray-resident window behavior (MAUI has no built-in tray support). |
| `wwwroot/` | Blazor host page and the dashboard stylesheet (`css/app.css`). Static source - no build step. |

## Adding a new window

New windows, modals, and dialogs build on `Components/DialogShell.razor` - the shared implementation
of the **System Settings** shell - rather than hand-rolling chrome. The full contract (its
parameters, what it renders, and the one sanctioned way to deviate) is in
[`docs/gui/DESIGN.md`](../../docs/gui/DESIGN.md) §4.1.

Charts are rendered with [Apache ECharts](https://echarts.apache.org/) (Apache-2.0), vendored as
`wwwroot/lib/echarts/echarts.min.js` and driven by `wwwroot/js/echarts-interop.js` through the reusable
`Components/EChart.razor` host, so the charts work offline inside the WebView with no NuGet chart
dependency. The per-chart models are built in the pure `TotallyHot.ArcRouter.Gui.Charts` library
(`CostChartBuilder` for the Cost Analytics tab) and serialized to the renderer with `ChartJson`.

## Prerequisites

- Windows 10 1809+ (the app targets `net10.0-windows10.0.19041.0` and uses Win32 tray APIs).
- The **.NET MAUI workload**: either check ".NET Multi-platform App UI development" in the Visual Studio
  installer, or run `dotnet workload install maui-windows`.
- The Microsoft Edge **WebView2 runtime** (preinstalled on Windows 11 and most updated Windows 10
  machines).

## Running

```powershell
cd src/TotallyHotArcRouter.Gui
dotnet run
```

Or open the solution in Visual Studio and press F5 (the "Windows Machine" profile runs the app
unpackaged - no MSIX registration or signing needed).

Note: the app starts minimized to the system tray by design. If nothing seems to happen after launch,
look for the TotallyHotArcRouter icon in the tray, right-click it, and choose **Show Dashboard**.

