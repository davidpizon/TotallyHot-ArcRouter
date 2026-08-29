# Serilog Logging Configuration Guide

> **Status: Partially implemented.** The config-driven approach described here is real —
> `Program.cs` calls `UseSerilog((context, services, cfg) => cfg.ReadFrom.Configuration(...))`,
> and `appsettings.json`'s `Serilog` section is genuinely read at startup. `Console` and `File`
> sinks are both configured (`"WriteTo": [{ "Name": "Console" }, { "Name": "File", ... }]`), and
> `Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Settings.Configuration`, `Serilog.Sinks.Console`,
> and `Serilog.Sinks.File` are all referenced in `TotallyHotArcRouter.csproj`. The `File` sink
> writes to `C:\Logs\ArcRouter\arcrouter-.log`, rolling daily with a 30-day retention window. What's **not**
> implemented: the `EventLog` sink and package below — still the **proposed** target configuration,
> not what's installed today. There's also a separate bootstrap logger in `Program.cs`
> (`Log.Logger = new LoggerConfiguration()...WriteTo.Console()`, used only before the host is
> built) that is hardcoded, not config-driven.
>
> One additional sink beyond the config-driven `Console` sink *is* wired up in code (not via
> `appsettings.json`): `TelemetryLogEventSink` (renamed from `SignalRLogEventSink` when the telemetry
> transport migrated to gRPC - see [`grpc-migration.md`](grpc-migration.md)), added with
> `.WriteTo.Sink(...)` directly in `Program.cs`, forwards every log event to the GUI's Console tab
> over the telemetry gRPC stream. See [`../gui/console-tab-plan.md`](../gui/console-tab-plan.md) and
> [`telemetry.md`](telemetry.md)'s "Transport: gRPC" section.

## Two processes, two logs

The Router and the GUI are separate processes running as **different identities** — the Router as
`LocalSystem` under the service control manager, the GUI as the interactive user — so they cannot share
one log file without one of them being denied. Each has its own `appsettings.json` and its own `File`
sink:

| Process | Configuration | Log file |
| --- | --- | --- |
| Router (`TotallyHotArcRouter`) | `src/TotallyHotArcRouter/appsettings.json` | `C:\Logs\ArcRouter\arcrouter-.log` |
| GUI (`TotallyHotArcRouter.Gui`) | `src/TotallyHotArcRouter.Gui/appsettings.json` | `%LOCALAPPDATA%\TotallyHotArcRouter\logs\arcrouter-gui-.log` |

Both roll daily with a 30-day retention limit and share the same output template, so the two files line
up when they are read side by side during an incident.

### GUI logging

The GUI had **no logging at all** until it was added; a WebView2 environment failure in the installed
build opened a blank dashboard window with nothing written anywhere, because the only log that existed
belonged to a different process. `Services/GuiLogging.cs` is what closes that, and it is deliberately
defensive:

- The log lives under `%LOCALAPPDATA%` — the same per-user root `GuiSettingsStore` and
  `WebViewUserData` already use — because the interactive user cannot write to the Router's
  `C:\Logs\ArcRouter`, and the installed GUI cannot write beside its own executable under
  `%ProgramFiles%`.
- `%LOCALAPPDATA%` in the configured path is expanded **in code** before Serilog reads it. An
  unexpanded variable would otherwise become a literal `%LOCALAPPDATA%` directory relative to the
  working directory, which for the GUI's registry Run-key auto-start is not even the install folder.
- A missing or unreadable `appsettings.json` falls back to a built-in rolling file sink at that same
  per-user path and records *why* it fell back. A bootstrap that quietly produces a logger with no
  sinks would reproduce the exact undiagnosable symptom this exists to prevent.

What the GUI logs at startup:

| Event | Where |
| --- | --- |
| Version, PID, user, base directory, working directory, OS | `MauiProgram.CreateMauiApp` |
| The resolved WebView2 user-data folder (or the failure to create it) | `MauiProgram.CreateMauiApp` → `WebViewUserData.Apply` |
| BlazorWebView initializing / initialized, and the WebView2 runtime version | `MainPage` |
| WebView2 browser-process failures | `MainPage`, via the platform control's `CoreProcessFailed` |
| `Failed to create WebView2 environment` | .NET MAUI's own logger — reaches the file because `MauiProgram` routes `Microsoft.Extensions.Logging` into Serilog |
| Tray window attached; exit chosen from the tray menu | `Platforms/Windows/TrayWindowManager` |
| Unhandled exceptions (app domain, WinUI thread, unobserved tasks) | `MauiProgram` and `Platforms/Windows/App.xaml.cs` |

**Diagnosing a blank dashboard:** look for `BlazorWebView initializing` in the GUI log. If no
`BlazorWebView initialized` line follows it, WebView2 never came up — the reason is either MAUI's
`Failed to create WebView2 environment` entry (runtime missing, or the user-data folder unwritable) or a
`CoreProcessFailed` entry immediately after.

---

## Overview

This document describes the target Serilog logging configuration for the TotallyHotArcRouter .NET 10 application. Serilog provides structured logging with flexible output destinations (file and Windows Event Viewer) configured via `appsettings.json`.

---

## Why Serilog?

- ✅ **Structured Logging:** Captures context via named properties, not just strings
- ✅ **Multiple Sinks:** Output to file, Event Viewer, console, cloud services simultaneously
- ✅ **Configuration-Driven:** All settings in `appsettings.json` (no code changes needed)
- ✅ **Performance:** Minimal overhead, async-first design
- ✅ **Rich Context:** Enrichers add machine name, thread ID, correlations automatically
- ✅ **Enterprise-Ready:** Widely used in production .NET applications

---

## Installation

Add the required NuGet packages:

```bash
dotnet add package Serilog
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Sinks.EventLog
dotnet add package Serilog.Sinks.Console
```

### Package Versions (for .NET 10)

```xml
<ItemGroup>
	<PackageReference Include="Serilog" Version="4.0.0" />
	<PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />
	<PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
	<PackageReference Include="Serilog.Sinks.EventLog" Version="3.2.0" />
	<PackageReference Include="Serilog.Sinks.Console" Version="5.0.0" />
	<PackageReference Include="Serilog.Settings.Configuration" Version="8.0.0" />
</ItemGroup>
```

---

## Minimum Configuration

### appsettings.json

```json
{
  "Serilog": {
	"Using": ["Serilog.Sinks.File", "Serilog.Sinks.EventLog"],
	"MinimumLevel": "Information",
	"WriteTo": [
	  {
		"Name": "File",
		"Args": {
		  "path": "C:\\Temp\\arcrouter-.log",
		  "rollingInterval": "Day",
		  "retainedFileCountLimit": 30,
		  "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
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
	"Enrich": [
	  "FromLogContext",
	  "WithMachineName",
	  "WithThreadId",
	  "WithProperty"
	]
  }
}
```

---

## Program.cs Integration

### Minimal Setup

```csharp
using Serilog;

var builder = Host.CreateDefaultBuilder(args);

// Configure Serilog
builder.UseSerilog((context, services, config) =>
{
	config
		.ReadFrom.Configuration(context.Configuration)
		.Enrich.FromLogContext();
});

builder
	.ConfigureServices(services =>
	{
		services.AddScoped<ProxyMiddleware>();
		services.AddSingleton<RouterMemory>();
	})
	.ConfigureWebHostDefaults(webBuilder =>
	{
		webBuilder.UseKestrel();
	});

var host = builder.Build();
await host.RunAsync();
```

### Full Setup with Error Handling

```csharp
using Serilog;
using Serilog.Core;

var builder = Host.CreateDefaultBuilder(args);

// Create logger for setup phase
Log.Logger = new LoggerConfiguration()
	.WriteTo.Console()
	.CreateBootstrapLogger();

try
{
	Log.Information("Starting TotallyHotArcRouter host...");

	// Configure Serilog from appsettings
	builder.UseSerilog((context, services, config) =>
	{
		config
			.ReadFrom.Configuration(context.Configuration)
			.Enrich.FromLogContext()
			.Enrich.WithProperty("Application", "TotallyHotArcRouter")
			.Enrich.WithProperty("Version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0");
	});

	builder
		.ConfigureServices(services =>
		{
			services.AddScoped<ProxyMiddleware>();
			services.AddSingleton<RouterMemory>();
			services.AddSingleton<SystemProxyManager>();
		})
		.ConfigureWebHostDefaults(webBuilder =>
		{
			webBuilder
				.UseKestrel()
				.ConfigureLogging(logging =>
				{
					// Disable default console logging (Serilog handles it)
					logging.ClearProviders();
				});
		});

	var host = builder.Build();

	Log.Information("TotallyHotArcRouter host configured successfully");
	await host.RunAsync();
}
catch (Exception ex)
{
	Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
	await Log.CloseAndFlushAsync();
}
```

---

## Configuration Examples

### Development Environment (appsettings.Development.json)

```json
{
  "Serilog": {
	"MinimumLevel": "Debug",
	"WriteTo": [
	  {
		"Name": "Console",
		"Args": {
		  "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"
		}
	  },
	  {
		"Name": "File",
		"Args": {
		  "path": "./logs/acrouter-.log",
		  "rollingInterval": "Day",
		  "retainedFileCountLimit": 7,
		  "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
		}
	  }
	],
	"Enrich": [
	  "FromLogContext",
	  "WithMachineName",
	  "WithThreadId",
	  "WithEnvironmentUserName",
	  "WithProperty"
	]
  }
}
```

### Production Environment (appsettings.Production.json)

```json
{
  "Serilog": {
	"MinimumLevel": "Information",
	"WriteTo": [
	  {
		"Name": "File",
		"Args": {
		  "path": "/var/log/acrouter/acrouter-.log",
		  "rollingInterval": "Day",
		  "retainedFileCountLimit": 90,
		  "fileSizeLimitBytes": 104857600,
		  "retainedFileCountLimit": 30,
		  "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj} {Properties:j}{NewLine}{Exception}"
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
	"Enrich": [
	  "FromLogContext",
	  "WithMachineName",
	  "WithThreadId",
	  "WithProperty"
	]
  }
}
```

### Windows with Event Viewer (appsettings.json)

```json
{
  "Serilog": {
	"MinimumLevel": {
	  "Default": "Information",
	  "Microsoft": "Warning",
	  "System": "Warning"
	},
	"WriteTo": [
	  {
		"Name": "File",
		"Args": {
		  "path": "./logs/acrouter-.log",
		  "rollingInterval": "Day",
		  "retainedFileCountLimit": 30,
		  "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
		}
	  },
	  {
		"Name": "EventLog",
		"Args": {
		  "source": "TotallyHotArcRouter",
		  "logName": "Application",
		  "restrictedToMinimumLevel": "Error",
		  "manageEventSource": true
		}
	  }
	],
	"Enrich": [
	  "FromLogContext",
	  "WithMachineName",
	  "WithThreadId",
	  "WithEnvironmentUserName",
	  "WithProperty"
	]
  }
}
```

---

## Usage Examples

### Structured Logging Pattern

❌ **Wrong (not structured):**
```csharp
_logger.LogInformation($"Model selected: {model} with score {score}");
```

✅ **Correct (structured):**
```csharp
_logger.LogInformation("Model selected: {SelectedModel} with score {Score}", model, score);
```

**Why this matters:**
- Structured logging captures `{SelectedModel}` and `{Score}` as separate, searchable fields
- You can query logs: "find all routing decisions for gpt-4-turbo"
- Tools like ELK, Splunk, and Azure Log Analytics can parse and analyze structured logs

### Routing Decision Logging

```csharp
public class ProxyMiddleware
{
	private readonly ILogger<ProxyMiddleware> _logger;
	private readonly TotallyHotArcRouter _router;

	public async Task InvokeAsync(HttpContext context, TotallyHotArcRouter router)
	{
		var requestId = context.TraceIdentifier;

		try
		{
			var task = ExtractRoutingTask(context.Request);

			_logger.LogInformation(
				"Routing request {RequestId} for dimension {Dimension} with {MessageCount} messages",
				requestId,
				task.Dimension,
				task.Messages.Count);

			var decision = await _router.RouteAsync(task);

			_logger.LogInformation(
				"Routing decision: Model={SelectedModel}, Confidence={Confidence:P}, Reasoning={Reasoning}",
				decision.ChosenModel,
				decision.Confidence,
				decision.Reasoning);

			// Forward request...
			var response = await ForwardAsync(decision.ChosenModel);

			// Log outcome
			_logger.LogInformation(
				"Request {RequestId} completed: Status={StatusCode}, Latency={Latency}ms",
				requestId,
				response.StatusCode,
				DateTime.UtcNow.Subtract(startTime).TotalMilliseconds);
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Request {RequestId} failed for dimension {Dimension}",
				requestId,
				task?.Dimension);
			throw;
		}
	}
}
```

### Memory Update Logging

```csharp
public class RouterMemory
{
	private readonly ILogger<RouterMemory> _logger;

	public async Task ObserveAsync(string dimension, string model, double score)
	{
		try
		{
			_logger.LogDebug(
				"Observing feedback: Dimension={Dimension}, Model={Model}, Score={Score:F3}",
				dimension,
				model,
				score);

			UpdateInMemory(dimension, model, score);

			await SaveAsync();

			var stats = GetStatistics(dimension, model);

			_logger.LogInformation(
				"Memory updated: Dimension={Dimension}, Model={Model}, AverageScore={Average:F3}, Count={Count}",
				dimension,
				model,
				stats.AverageScore,
				stats.Count);
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Failed to observe feedback: Dimension={Dimension}, Model={Model}",
				dimension,
				model);
			throw;
		}
	}
}
```

### Proxy Server Lifecycle Logging

```csharp
public class ProxyHostedService : IHostedService
{
	private readonly ILogger<ProxyHostedService> _logger;
	private readonly IHost _webHost;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		try
		{
			_logger.LogInformation("Starting proxy server on port {Port}", 5001);

			await _webHost.StartAsync(cancellationToken);

			_logger.LogInformation("Proxy server started successfully. Ready for connections.");
		}
		catch (Exception ex)
		{
			_logger.LogCritical(ex, "Failed to start proxy server");
			throw;
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		try
		{
			_logger.LogInformation("Stopping proxy server");

			await _webHost.StopAsync(cancellationToken);

			_logger.LogInformation("Proxy server stopped gracefully");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error during proxy server shutdown");
			throw;
		}
	}
}
```

---

## Log Levels and When to Use Them

| Level | When to Use | Example |
|-------|-------------|---------|
| **Verbose** | Extremely detailed diagnostic info | HTTP frame parsing, memory allocation |
| **Debug** | Detailed info for debugging | Request body content, vector store queries |
| **Information** | General informational messages | Startup, routing decisions, memory updates |
| **Warning** | Warning conditions | Vector store unavailable, fallback model used |
| **Error** | Error conditions | Request forwarding failed, proxy middleware crash |
| **Fatal** | Unrecoverable errors | Out of memory, port binding failed |

---

## Log Retention and Archival

### File Rotation Configuration

```json
{
  "Serilog": {
	"WriteTo": [
	  {
		"Name": "File",
		"Args": {
		  "path": "./logs/acrouter-.log",
		  "rollingInterval": "Day",              // Roll daily
		  "retainedFileCountLimit": 30,          // Keep 30 days
		  "fileSizeLimitBytes": 104857600,       // 100 MB per file
		  "retentionPolicy": "TotalSize",
		  "retentionTotalFileSizeLimit": 3221225472  // Keep 3 GB total
		}
	  }
	]
  }
}
```

### Log File Naming

- `acrouter-20250115.log` (daily rotation)
- `acrouter-20250114.log` (yesterday)
- `acrouter-20250113.log` (etc.)

---

## Windows Event Viewer Integration

### Creating Event Source (Admin PowerShell)

```powershell
New-EventLog -LogName Application -Source "TotallyHotArcRouter" -ErrorAction SilentlyContinue
```

### Viewing Events

1. Open **Event Viewer**
2. Navigate to **Windows Logs** → **Application**
3. Filter by **Source = "TotallyHotArcRouter"**

### Log Levels in Event Viewer

- **Information** → Event Type: Information
- **Warning** → Event Type: Warning
- **Error** → Event Type: Error
- **Critical** → Event Type: Error (highest severity)

---

## Performance Considerations

### Async Logging

Serilog writes asynchronously by default:

```json
{
  "Serilog": {
	"WriteTo": [
	  {
		"Name": "File",
		"Args": {
		  "path": "./logs/acrouter-.log",
		  "buffered": true,           // Batch writes
		  "flushOnClose": true
		}
	  }
	]
  }
}
```

### Impact on Latency

- ✅ **Minimal:** <1ms added latency (async/batched)
- ✅ **Non-blocking:** Doesn't block request processing
- ✅ **Scalable:** Efficient even at high request rates

---

## Testing with Serilog

### Unit Test Example

```csharp
[TestMethod]
public async Task RouteDecision_LogsCorrectStructuredData()
{
	// Arrange
	var logger = new LoggerConfiguration()
		.WriteTo.TestCorrelator()
		.CreateLogger();

	var router = new TotallyHotArcRouter(logger, memory);

	// Act
	var decision = await router.RouteAsync(task);

	// Assert
	var logEvents = logger.TestCorrelator.LogEvents;
	Assert.IsTrue(logEvents.Any(e => 
		e.MessageTemplate.Text.Contains("Model selected") &&
		e.Properties.ContainsKey("SelectedModel") &&
		(string)e.Properties["SelectedModel"].LiteralValue == "gpt-4-turbo"));
}
```

---

## Troubleshooting

### Issue: Event Log Source Not Found

**Solution:**
```powershell
# Run as Administrator
New-EventLog -LogName Application -Source "TotallyHotArcRouter"
```

### Issue: Log Files Growing Too Large

**Solution:** Configure file size limits and retention:
```json
{
  "fileSizeLimitBytes": 104857600,
  "retainedFileCountLimit": 30
}
```

### Issue: Performance Impact from Logging

**Solution:** Reduce log level in production or use sampling:
```json
{
  "MinimumLevel": "Information",
  "Destructure": {
	"ToMaximumCollectionCount": 10,
	"ToMaximumStringLength": 1000
  }
}
```

---

## Audit Trail Logging

For critical operations, log with additional context:

```csharp
using (LogContext.PushProperty("RequestId", requestId))
using (LogContext.PushProperty("UserId", userId))
using (LogContext.PushProperty("Action", "RouteDecision"))
{
	_logger.LogInformation(
		"Model decision: Selected={Model}, Alternative={Alternative}",
		decision.ChosenModel,
		decision.AlternativeModel);
}
```

**Result in log file:**
```
2025-01-15 14:32:15.123 [INF] Model decision: Selected=gpt-4-turbo, Alternative=gpt-4 
  {"RequestId": "abc123", "UserId": "user@example.com", "Action": "RouteDecision"}
```

---

## Summary

✅ **Required:** Serilog for all logging
✅ **Destinations:** File + Windows Event Viewer (configurable)
✅ **Configuration:** appsettings.json (no code changes)
✅ **Pattern:** Structured logging (named properties)
✅ **Levels:** Use appropriate levels (Debug, Info, Warning, Error)
✅ **Async:** Non-blocking, minimal latency impact
✅ **Retention:** Configurable file rotation and archival

Use Serilog for all new logging in TotallyHotArcRouter phases.

