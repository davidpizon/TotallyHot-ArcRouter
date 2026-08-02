# 📺 Streaming Log Console Specification

> **Status: Implemented.** The fifth "Console" tab described below is real: `Components/ConsoleTab.razor`
> in `TotallyHotArcRouter.Gui` renders a live, color-coded log stream backed by every Serilog log event the
> proxy emits. `src/TotallyHotArcRouter/Telemetry/TelemetryLogEventSink.cs` (renamed from
> `SignalRLogEventSink.cs` when the telemetry transport migrated to gRPC - see
> [`../router/grpc-migration.md`](../router/grpc-migration.md)) is a custom `ILogEventSink` wired
> into `Program.cs`'s Serilog pipeline (additive - it doesn't replace the existing `Console` sink);
> each event is normalized to the DEBUG/INFO/WARN/ERROR/FATAL levels below and pushed as a
> `LogLineEvent` over the same telemetry gRPC stream the routing telemetry uses, as the `log_line`
> oneof case. `TotallyHotArcRouter.Gui.Services.LiveDataStore` receives it, buffers it (see
> `TotallyHotArcRouter.Gui.Console.LogBuffer`, bounded to 1,000 lines), and `ConsoleTab.razor` renders it
> with the color mapping and Auto-Scroll/Smart-Disengage behavior specified below.

A lightweight, real-time log monitoring component with color-coded severity levels and toggleable
auto-scroll behavior.

## 🎛️ Toolbar Interface

The toolbar sits directly above the log viewport and contains the following control elements:

| Element | UI Type | Action / Behavior |
|---|---|---|
| Auto-Scroll | Toggle Switch / Button | ON: Viewport snaps to the newest log entry. OFF: Viewport stays frozen on the current view for manual reading. |
| Copy | Button (clipboard SVG icon) | Copies every buffered line to the system clipboard, formatted exactly as shown in the viewport, oldest first. Briefly shows "Copied!" for confirmation. No-op if the buffer is empty. |
| Clear Buffer | Button (trashcan SVG icon) | Flushes the current text buffer and empties the console screen. |

---

## 🖼️ Console Viewport

The main display area features a dark-mode theme designed for maximum readability during
continuous log streaming.

### Visual Styling

- Font Family: Monospace (Fira Code, Courier New, or SF Mono). Implemented using the app's existing
  `.font-mono` utility class (JetBrains Mono, falling back to Fira Code) rather than a new font stack,
  for visual consistency with the rest of the dashboard.
- Font Size: 13px / Line Height: 1.5

### 🎨 Color-Coded Log Levels

Text coloring maps directly to log severity levels to allow for rapid visual scanning:

```
[2026-07-08 21:10:01] [DEBUG]  Connecting to internal database cluster...
[2026-07-08 21:10:02] [INFO]   Successfully connected to database: 'prod_db'.
[2026-07-08 21:10:15] [WARN]   API latency spike detected: 450ms (threshold: 200ms).
[2026-07-08 21:11:00] [ERROR]  Failed to write payload to session token cache.
[2026-07-08 21:11:01] [FATAL]  Out of memory error. Service shutting down.
```

- ⚪ Gray (`#A0A0A0`): `DEBUG` — Low-level diagnostic data.
- 🟢 Green (`#4CAF50`): `INFO` — Standard system operational events.
- 🟡 Yellow (`#FFC107`): `WARN` — Non-blocking anomalies or performance alerts.
- 🔴 Red (`#F44336`): `ERROR` — Operational failures requiring intervention.
- 🟣 Magenta (`#E91E63`): `FATAL` / `CRITICAL` — Total application crash.

---

## ⚙️ Core Behavior Rules

### 1. Auto-Scroll Logic

- **Enabled (Default)**: When a new log line arrives, the component automatically calculates the
  container's maximum scroll height and instantly scrolls down to display the new line.
- **Disabled**: New lines append to the bottom of the document out of view, but the scrollbar
  position does not change.

### 2. Smart-Disengage (UX Safeguard)

- If Auto-Scroll is ON and the user manually scrolls upward using their mouse wheel or trackpad,
  Auto-Scroll automatically switches to OFF.
- This prevents the text from violently jumping away from the user while they are actively trying
  to highlight or read an earlier log entry.

