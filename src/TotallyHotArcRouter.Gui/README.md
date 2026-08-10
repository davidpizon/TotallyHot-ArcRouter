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

## Current limitations

The Live Stream tab and the Cost Analytics tab's Token Compounding chart connect to the running
TotallyHotArcRouter proxy's telemetry hub (`Services/LiveDataStore.cs`, default
`http://localhost:5001/telemetry/hub`, configurable via `GuiSettingsStore` and `SettingsModal.razor`)
and show nothing until the proxy is
reachable and has forwarded at least one request. The rest of the dashboard (Model Distribution,
Governance, the header ticker, and Cost Analytics' other two charts) still reads from hard-coded
mock data (`Models/DashboardData.cs`) - no telemetry source exists for that data yet. See
[`docs/router/telemetry.md`](../../docs/router/telemetry.md) for the full pipeline and
[`docs/gui/backlog.md`](../../docs/gui/backlog.md) for what's left.

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

New windows, modals, and dialogs match the **System Settings** window
(`Components/SettingsModal.razor`): copy its backdrop/panel/header shell rather than styling new
chrome, keep the `.overlay-backdrop`/`.overlay-panel` classes (they carry the entrance animation),
and expose closing as an `EventCallback` parameter instead of self-closing.
`Components/ProviderEditDialog.razor` is an existing example. The full contract - every class, size,
and color - is in [`docs/gui/DESIGN.md`](../../docs/gui/DESIGN.md) §4.1.

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

