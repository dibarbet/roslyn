# OpenTelemetry Integration Plan for Roslyn Language Server

## Problem Statement

Implement OpenTelemetry (OTel) as the **single observability layer** in the Roslyn language server. All telemetry flows through OTel, which then uses **exporters** to route signals to their destinations (Aspire dashboard, VSTelemetry/DevKit, ETW, LSP output window, etc.). Visual Studio continues using its current `ILogger`/`TelemetryLogger`/ETW implementations unchanged — the OTel approach is language-server-only.

Fault reporting (`ReportFault`/Watson) is a separate system and remains unchanged.

## Current Architecture Summary

### Roslyn's Internal Logger System (`Microsoft.CodeAnalysis.Internal.Log`)
- **`ILogger`** (Roslyn's own, not M.E.Logging): `IsEnabled(FunctionId)`, `Log(FunctionId, LogMessage)`, `LogBlockStart(...)`, `LogBlockEnd(...)`
- **`Logger`** (static facade): Global `SetLogger()`/`GetLogger()` with `Logger.Log(FunctionId, ...)` and `Logger.LogBlock(FunctionId, ...)` APIs
- **`AggregateLogger`**: Composite pattern - multiplexes to multiple `ILogger` implementations
- **`FunctionId`** enum: 670+ categorized identifiers for every loggable operation
- **`LogMessage`**: Pooled, lazy-evaluated messages with LogLevel (Debug/Trace/Information)
- **`LogBlock`**: Pooled `IDisposable` scopes that capture duration (via `Environment.TickCount` delta)

### Existing ILogger (Roslyn's) Implementations
| Implementation | Host | Purpose |
|---|---|---|
| `EtwLogger` | Both | ETW events via `RoslynEventSource` |
| `TelemetryLogger` | VS only | VS Telemetry (TelemetrySession, operations, user tasks) |
| `CodeMarkerLogger` | VS only | VS performance code markers |
| `FileLogger` | VS only | File-based diagnostic logging |
| `RoslynLogger` | Lang Server only | Bridges to `ITelemetryReporter` (VSTelemetry via DevKit) |
| `EmptyLogger` | Any | No-op |

### TelemetryLogging API (`Microsoft.CodeAnalysis.Telemetry`)
- **`TelemetryLogging`** (static): `LogAggregatedHistogram()`, `LogAggregatedCounter()`, `LogBlockTime()`
- **`ITelemetryLogProvider`**: Factory for `ITelemetryLog`/`ITelemetryBlockLog` instances (histograms, counters)
- VS has `TelemetryLogProvider` (uses VS `TelemetrySession`); language server currently has no `ITelemetryLogProvider`

### Language Server M.E.Logging (`Microsoft.Extensions.Logging.ILogger`)
- `LspLogMessageLoggerProvider` → `LspLogMessageLogger`: Two-phase (console fallback → LSP `window/logMessage` notifications)
- Sends formatted log messages to VS Code output window via `IClientLanguageServerManager.SendNotificationAsync("window/logMessage", ...)`
- Scoped via `LspLoggingScope` (context name, language routing for Razor)

### LSP Request Telemetry
- `AbstractRequestScope` → `RequestTelemetryScope`: Per-request timing (queue wait, execution duration, success/fail/cancel)
- `RequestTelemetryLogger`: Logs via `TelemetryLogging.LogAggregatedHistogram/Counter` for `LSP_TimeInQueue`, `LSP_RequestDuration`, `LSP_RequestCounter`
- `Trace.CorrelationManager.ActivityId` used for cross-async correlation in `RequestExecutionQueue` — **required by VS LogHub** (`CorrelationManagerTracingStrategy` in VS's `VisualStudioLogHubLoggerFactory`)

### ITelemetryReporter (Language Server contract)
- Used by `RoslynLogger` to send events to DevKit/VSTelemetry
- Methods: `InitializeSession`, `Log`, `LogBlockStart`, `LogBlockEnd`, `ReportFault`
- Implemented externally (DevKit extension provides it via MEF)

## Proposed Approach: OTel as the Single Pipeline with Exporters

The key principle: **OTel is the only telemetry implementation in the language server**. Every signal goes through OTel, and exporters route data to each destination. No more parallel `AggregateLogger` with multiple Roslyn `ILogger` implementations — instead, a single OTel-backed `ILogger` (Roslyn's) plus OTel exporters that replicate what `EtwLogger`, `RoslynLogger`, etc. currently do.

VS keeps its existing implementations (`TelemetryLogger`, `EtwLogger`, `CodeMarkerLogger`, `FileLogger`). Shared helper code (e.g., `FunctionId` → event name mapping) can be extracted so both VS implementations and OTel exporters reuse it.

### Signal Mapping

| Roslyn API | OTel Signal | Source Name | Rationale |
|---|---|---|---|
| `Logger.Log(FunctionId, ...)` | **OTel Traces** (zero-duration Activity) | `Roslyn.Logger` | Single telemetry event with properties. VSTel exporter calls `ITelemetryReporter.Log()`. |
| `Logger.LogBlock(FunctionId, ...)` | **OTel Traces** (Activity with duration) | `Roslyn.Logger` | Paired start+end with duration = a span. VSTel exporter calls `ITelemetryReporter.LogBlockStart/End`. |
| `TelemetryLogging.LogAggregatedHistogram` | **OTel Metrics** (Histograms) | `Roslyn.Logger` (Meter) | Already conceptually histograms — aggregated durations |
| `TelemetryLogging.LogAggregatedCounter` | **OTel Metrics** (Counters) | `Roslyn.Logger` (Meter) | Already conceptually counters — aggregated counts |
| M.E.Logging `ILogger.Log<T>()` | **OTel Logs** | *(no source filtering)* | Structured log records. All log exporters see these. |
| LSP Request Scope (`RequestTelemetryScope`) | **OTel Traces** (Activity with duration) | `Roslyn.LanguageServer` | Per-request spans for debugging/Aspire. NOT sent to VSTelemetry (too high volume). |

### Source Naming Strategy

Two `ActivitySource` names and one `Meter` name control routing:

| Source/Meter | Name | What produces it | VSTel Exporter | ETW Exporter | OTLP/Aspire | LspLogMsg Exporter |
|---|---|---|---|---|---|---|
| ActivitySource | `Roslyn.Logger` | `Logger.Log` (zero-duration) + `Logger.LogBlock` (with duration) | ✅ Filters to this source | ✅ Filters to this source | ✅ | — |
| ActivitySource | `Roslyn.LanguageServer` | `RequestTelemetryScope` LSP request spans | ❌ Skips | ❌ Skips | ✅ | — |
| Meter | `Roslyn.Logger` | `TelemetryLogging` histograms/counters | ✅ | ✅ | ✅ | — |
| Logs | *(all logs, no source filter)* | M.E.Logging `ILogger` calls | ❌ Not on logs pipeline | ❌ | ✅ | ✅ |

**Key insight**: The VSTelemetry and ETW exporters only care about `Roslyn.Logger` source — this is the telemetry that maps to the existing VS events. LSP request spans (`Roslyn.LanguageServer`) go only to Aspire for debugging. LSP request *metrics* (aggregated duration/counts) still flow through `TelemetryLogging` → `Roslyn.Logger` Meter → VSTelemetry metric exporter, so VSTelemetry still gets aggregated request data.

### How VSTelemetry Exporter Differentiates Logger.Log vs LogBlock

The VSTelemetry trace exporter receives all completed Activities from `Roslyn.Logger` and distinguishes:

- **Zero-duration Activity** (from `Logger.Log`): Calls `ITelemetryReporter.Log(eventName, properties)` — a single event
- **Activity with duration** (from `Logger.LogBlock`): Calls `ITelemetryReporter.LogBlockStart(eventName, kind, blockId)` then `ITelemetryReporter.LogBlockEnd(blockId, properties, cancellationToken)` — preserving the exact same paired start+end events as today

Both carry `FunctionId` as a tag on the Activity, plus all properties from `LogMessage`.

### Architecture Diagram

```
                    ┌──────────────────────────────────────────────────────────────────────┐
                    │                    Language Server Process                            │
                    │                                                                       │
                    │  ┌──────────────────┐    ┌──────────────────────┐                    │
                    │  │  Logger.Log()     │    │  M.E.Logging ILogger │                    │
                    │  │  Logger.LogBlock()│    │  (operational logs)  │                    │
                    │  └────────┬─────────┘    └──────────┬───────────┘                    │
                    │           │                          │                                │
                    │  ┌────────▼─────────┐    ┌──────────▼───────────┐                    │
                    │  │ OTelRoslynLogger  │    │  OTel LoggerProvider │                    │
                    │  │ (sole Roslyn      │    │  (sole M.E.Logging   │                    │
                    │  │  ILogger impl)    │    │   provider)          │                    │
                    │  │                   │    └──────────┬───────────┘                    │
                    │  │ Log→Activity(0dur)│               │                                │
                    │  │ LogBlock→Activity │               │                                │
                    │  │ src:Roslyn.Logger │               │                                │
                    │  └────────┬─────────┘               │                                │
                    │           │                          │                                │
                    │  ┌────────▼──────────────────────────▼───────────────────────────┐   │
                    │  │              OTel SDK                                          │   │
                    │  │   TracerProvider  │  MeterProvider  │  LoggerProvider          │   │
                    │  └──────┬───────────┴──────┬──────────┴────────┬─────────────────┘   │
                    │         │                  │                   │                      │
                    │   ┌─────▼──────┐    ┌──────▼──────┐    ┌──────▼──────┐               │
                    │   │  Trace     │    │  Metrics    │    │  Logs       │               │
                    │   │  Exporters │    │  Exporters  │    │  Exporters  │               │
                    │   ├────────────┤    ├─────────────┤    ├─────────────┤               │
                    │   │ OTLP       │    │ OTLP        │    │ OTLP        │               │
                    │   │ (→Aspire)  │    │ (→Aspire)   │    │ (→Aspire)   │               │
                    │   │ *all srcs* │    │             │    ├─────────────┤               │
                    │   ├────────────┤    ├─────────────┤    │ LspLogMsg   │               │
                    │   │ VSTelemetry│    │ VSTelemetry │    │ Exporter    │               │
                    │   │ Exporter   │    │ Exporter    │    │(→OutputWin) │               │
                    │   │*Roslyn.Log │    │             │    └─────────────┘               │
                    │   │ ger only*  │    ├─────────────┤                                   │
                    │   ├────────────┤    │ ETW         │                                   │
                    │   │ ETW        │    │ Exporter    │                                   │
                    │   │ Exporter   │    │             │                                   │
                    │   │*Roslyn.Log │    └─────────────┘                                   │
                    │   │ ger only*  │                                                      │
                    │   └────────────┘                                                      │
                    │                                                                       │
                    │  ┌──────────────────────────────────────────┐                        │
                    │  │  LSP Request Processing                  │                        │
                    │  │  RequestTelemetryScope                   │                        │
                    │  │    ├─ Activity (src: Roslyn.LanguageServer)│                       │
                    │  │    │  (→ OTLP/Aspire only, not VSTel)    │                        │
                    │  │    └─ Trace.CorrelationManager.ActivityId │                        │
                    │  │       (kept for VS LogHub compat)         │                        │
                    │  │  RequestTelemetryLogger                   │                        │
                    │  │    └─ TelemetryLogging.LogAggregated*     │                        │
                    │  │       (→ Meter: Roslyn.Logger → all       │                        │
                    │  │        metric exporters incl VSTel)       │                        │
                    │  └──────────────────────────────────────────┘                        │
                    └──────────────────────────────────────────────────────────────────────┘

     VS Host (unchanged)
     ┌────────────────────────────┐
     │  Logger → AggregateLogger  │
     │    ├─ TelemetryLogger      │
     │    ├─ EtwLogger            │
     │    ├─ CodeMarkerLogger     │
     │    └─ FileLogger           │
     │  TelemetryLogProvider      │
     │    (VS TelemetrySession)   │
     └────────────────────────────┘
```

## Implementation Todos

### 1. Add OTel NuGet Packages to Language Server

**Project**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj`

Add packages:
- `OpenTelemetry` (core SDK)
- `OpenTelemetry.Extensions.Hosting` (for host integration)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` (OTLP exporter for Aspire)
- `OpenTelemetry.Exporter.Console` (for debug/development)

Do NOT add to any Workspaces/Features/VS projects. The OTel dependency is language-server-only.

---

### 2. Create `OTelRoslynLogger` — The Sole Roslyn `ILogger` in the Language Server

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/OTelRoslynLogger.cs`

Implement Roslyn's `Microsoft.CodeAnalysis.Internal.Log.ILogger` backed by OTel Traces (Activities). This **replaces** `RoslynLogger` (as the Roslyn ILogger) and `EtwLogger` in the language server — those backends become OTel exporters instead.

Uses `ActivitySource` named `"Roslyn.Logger"`:

```
ILogger.Log(FunctionId, LogMessage)
  → Start + immediately stop an Activity (zero-duration span)
  → Tags: roslyn.function_id, roslyn.function_name, roslyn.log_type, message properties
  → VSTel exporter sees this and calls ITelemetryReporter.Log(eventName, properties)

ILogger.LogBlockStart(FunctionId, LogMessage, blockId, cancellationToken)
  → Start an Activity, store in ConcurrentDictionary keyed by blockId
  → Tags: roslyn.function_id, roslyn.function_name, roslyn.log_type, roslyn.block_id

ILogger.LogBlockEnd(FunctionId, LogMessage, blockId, delta, cancellationToken)
  → Retrieve Activity from dictionary, add delta + properties as tags, stop Activity
  → Tags: roslyn.delta, roslyn.cancelled, message properties
  → VSTel exporter sees completed span and calls ITelemetryReporter.LogBlockStart/End

ILogger.IsEnabled(FunctionId) → true (always enabled; exporters decide what to consume)
```

Key decisions:
- `ActivitySource` name: `"Roslyn.Logger"` — this is the source VSTel/ETW exporters filter on
- `FunctionId` is stored as tags so exporters can reconstruct exact event names
- For `LogBlock`, the Activity naturally captures start time and duration
- Set as the **sole** logger via `Logger.SetLogger(otelRoslynLogger)` — no `AggregateLogger` needed

---

### 3. Create `OTelTelemetryLogProvider` (`ITelemetryLogProvider` Implementation)

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/OTelTelemetryLogProvider.cs`

Implement `ITelemetryLogProvider` backed by OTel Metrics via `Meter` named `"Roslyn.Logger"`, so that the `TelemetryLogging` static API (used by `RequestTelemetryLogger` for LSP metrics) routes to OTel:

```
GetHistogramLog(FunctionId) → return ITelemetryBlockLog backed by Meter.Histogram<long>
GetCounterLog(FunctionId) → return ITelemetryLog backed by Meter.Counter<long>
GetLog(FunctionId) → return ITelemetryBlockLog for non-aggregated events
Flush() → ForceFlush on MeterProvider
```

Set via `TelemetryLogging.SetLogProvider(otelTelemetryLogProvider)`. The existing `RequestTelemetryLogger.UpdateTelemetryData()` calls to `TelemetryLogging.LogAggregatedHistogram/Counter` will then flow to OTel metrics automatically.

---

### 4. Replace `LspLogMessageLoggerProvider` with OTel-based M.E.Logging

**Modify**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Program.cs` (LoggerFactory setup)

Replace the current `LspLogMessageLoggerProvider` with OpenTelemetry's built-in M.E.Logging integration as the **sole** log provider:

```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddOpenTelemetry(options => {
        // OTel log exporters handle routing:
        // - LspLogMessageExporter → window/logMessage (replaces LspLogMessageLoggerProvider)
        // - OTLP exporter → Aspire dashboard
    });
});
```

The existing `LspLogMessageLoggerProvider` is **removed** and replaced by a custom OTel log exporter (`LspLogMessageExporter`) that sends formatted log records to the VS Code output window via LSP `window/logMessage` notifications. This exporter replicates the current `LspLogMessageLogger` behavior (formatting, scope context, language routing for Razor).

A console fallback exporter can handle pre-initialization logging (before the LSP server starts).

---

### 5. Add OTel Tracing for LSP Requests via `System.Diagnostics.Activity`

**Modify**: `src/LanguageServer/Protocol/LspServices/RequestTelemetryScope.cs`

Add `ActivitySource` named `"Roslyn.LanguageServer"` for per-request tracing. These spans go to Aspire only (not VSTelemetry — too high volume). Keep `Trace.CorrelationManager.ActivityId` for VS LogHub compatibility.

```csharp
private static readonly ActivitySource s_activitySource = new("Roslyn.LanguageServer");
private readonly Activity? _activity;

// Construction:
_activity = s_activitySource.StartActivity($"lsp/{name}", ActivityKind.Server);
_activity?.SetTag("lsp.method", name);

// RecordExecutionStart:
_activity?.AddEvent(new ActivityEvent("execution_start"));
_activity?.SetTag("lsp.queue_duration_ms", _queuedDuration.TotalMilliseconds);

// RecordHandlerLanguage:
_activity?.SetTag("lsp.language", language);

// RecordException:
_activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
_activity?.RecordException(exception);

// Dispose:
_activity?.SetTag("lsp.result", _result.ToString());
_activity?.Dispose(); // ends the span
```

`Trace.CorrelationManager.ActivityId` in `RequestExecutionQueue` is kept unchanged. `Activity.Current` propagates automatically via `AsyncLocal`.

---

### 6. Create VSTelemetry Trace Exporter

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Exporters/VSTelemetryTraceExporter.cs`

Custom OTel `BaseExporter<Activity>` that forwards `Roslyn.Logger` spans to `ITelemetryReporter`. **Filters by source name** — only processes Activities from `"Roslyn.Logger"`, skips `"Roslyn.LanguageServer"`.

On `Export(Batch<Activity>)`, for each completed Activity from `Roslyn.Logger`:
- Read `roslyn.function_id`, `roslyn.function_name`, `roslyn.log_type`, `roslyn.block_id`, `roslyn.delta`, etc. from tags
- **Zero-duration Activity** (from `Logger.Log`): Call `ITelemetryReporter.Log(eventName, properties)` with `vs/ide/vbcs/` prefix naming
- **Activity with duration** (from `Logger.LogBlock`): Call `ITelemetryReporter.LogBlockStart(eventName, kind, blockId)` then `ITelemetryReporter.LogBlockEnd(blockId, properties, cancellationToken)` — preserving the exact same paired events as today

Shares naming helpers with VS `TelemetryLogger` (extracted to `TelemetryNaming`).

Only added when `ITelemetryReporter` is available (DevKit is loaded).

---

### 7. Create VSTelemetry Metrics Exporter

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Exporters/VSTelemetryMetricExporter.cs`

Custom OTel metric exporter that forwards `Roslyn.Logger` meter metrics to `ITelemetryReporter`:

- On `Export(Batch<Metric>)`:
  - For histogram metrics (from `TelemetryLogging.LogAggregatedHistogram`): Forward aggregated values with `vs/ide/vbcs/` naming
  - For counter metrics (from `TelemetryLogging.LogAggregatedCounter`): Forward aggregated counts
- Shares naming helpers via `TelemetryNaming`
- Only added when `ITelemetryReporter` is available

---

### 8. Create ETW Trace Exporter

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Exporters/EtwTraceExporter.cs`

Custom OTel `BaseExporter<Activity>` that writes `Roslyn.Logger` spans to `RoslynEventSource`. **Filters by source name** — only `"Roslyn.Logger"`.

- **Zero-duration Activity** (from `Logger.Log`): Call `RoslynEventSource.Instance.Log(message, functionId)`
- **Activity with duration** (from `Logger.LogBlock`): Call `RoslynEventSource.Instance.BlockStart(...)` then `BlockStop(functionId, delta, blockId)` or `BlockCanceled(...)` based on cancellation tag
- Replicates exact same ETW events as the current `EtwLogger`

---

### 9. Create ETW Metrics Exporter

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Exporters/EtwMetricExporter.cs`

Custom OTel metric exporter that writes `Roslyn.Logger` meter metrics to `RoslynEventSource`:

- Maps aggregated histogram/counter data to ETW events
- The `RoslynEventSource` class is in `Workspaces.Core.Portable` so it's accessible

---

### 10. Create LSP Log Message Exporter (Replaces `LspLogMessageLoggerProvider`)

**New file**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Exporters/LspLogMessageExporter.cs`

Custom OTel log exporter (`BaseExporter<LogRecord>`) that sends formatted logs to the VS Code output window. Sees **all logs** (no source filtering — M.E.Logging logs don't have ActivitySource).

- Replicates `LspLogMessageLogger` behavior:
  - Formats messages with `[context][categoryName]` prefix
  - Converts `LogLevel` → LSP `MessageType` 
  - Handles Razor language routing (sends to `razor/log` instead of `window/logMessage`)
  - Sends via `IClientLanguageServerManager.SendNotificationAsync("window/logMessage", LogMessageParams)`
- **Pre-initialization**: Uses console fallback until `LanguageServerHost.Instance` is available (same two-phase approach as current `LspLogMessageLogger`)
- Handles `ObjectDisposedException`/`ConnectionLostException` gracefully during shutdown

---

### 11. Configure OTel SDK in Language Server Startup

**Modify**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Program.cs`

Wire up the entire OTel pipeline:

```csharp
// 1. Build TracerProvider — subscribes to both ActivitySources
var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("Roslyn.Logger")              // Logger.Log + LogBlock spans
    .AddSource("Roslyn.LanguageServer")      // LSP request spans
    .AddOtlpExporter()                       // → Aspire (all spans)
    .AddProcessor(new VSTelemetryTraceExporter(telemetryReporter))  // filters to Roslyn.Logger only
    .AddProcessor(new EtwTraceExporter())                           // filters to Roslyn.Logger only
    .Build();

// 2. Build MeterProvider
var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("Roslyn.Logger")               // TelemetryLogging histograms/counters
    .AddOtlpExporter()                       // → Aspire
    .AddReader(new VSTelemetryMetricExporter(telemetryReporter))  // → DevKit VSTelemetry
    .AddReader(new EtwMetricExporter())       // → RoslynEventSource/ETW
    .Build();

// 3. LoggerFactory with OTel as sole provider
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddOpenTelemetry(options => {
        options.AddOtlpExporter();                                  // → Aspire
        options.AddProcessor(new LspLogMessageExporter(...));       // → VS Code output window
    });
});

// 4. Set OTelRoslynLogger as sole Roslyn ILogger
Logger.SetLogger(new OTelRoslynLogger());

// 5. Set OTelTelemetryLogProvider for TelemetryLogging static API
TelemetryLogging.SetLogProvider(new OTelTelemetryLogProvider(meter, meterProvider));

// 6. Keep RoslynLogger.Initialize for fault reporting only
RoslynLogger.Initialize(telemetryReporter, ...);  // Only for ReportFault — not as ILogger
```

**Configuration**: Standard `OTEL_*` env vars (e.g., `OTEL_EXPORTER_OTLP_ENDPOINT`) for Aspire. Custom exporters (VSTelemetry, ETW, LspLogMessage) are always added. VSTelemetry exporters are conditionally added only when `ITelemetryReporter` is available.

---

### 12. Refactor `RoslynLogger` to Fault-Reporting-Only

**Modify**: `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/RoslynLogger.cs`

Remove the `ILogger` implementation from `RoslynLogger`. Keep only:
- `Initialize()` → `FatalError.SetHandlers(ReportFault)` and telemetry session init
- `ReportFault()` → unchanged (continues using `ITelemetryReporter.ReportFault`)
- `ShutdownAndReportSessionTelemetry()` → unchanged
- Remove: `Log()`, `LogBlockStart()`, `LogBlockEnd()`, `IsEnabled()`, `ILogger` interface

---

### 13. Extract Shared Telemetry Naming Helpers

**New file**: `src/Workspaces/Core/Portable/Telemetry/TelemetryNaming.cs` (or similar shared location)

Extract the `FunctionId` → event name / property name mapping logic that's currently duplicated between `RoslynLogger` (language server) and `TelemetryLogger` (VS):

```csharp
internal static class TelemetryNaming
{
    public const string EventPrefix = "vs/ide/vbcs/";
    public const string PropertyPrefix = "vs.ide.vbcs.";
    
    public static string GetEventName(FunctionId id) => ...;
    public static string GetPropertyName(FunctionId id, string name) => ...;
    public static string GetTelemetryName(FunctionId id, char separator) => ...;
}
```

Both VS `TelemetryLogger` and the new OTel VSTelemetry exporters can use these shared helpers.

---

### 14. Keep `RequestExecutionQueue` CorrelationManager

**No changes** to `src/LanguageServer/Microsoft.CommonLanguageServerProtocol.Framework/RequestExecutionQueue.cs`.

`Trace.CorrelationManager.ActivityId` is kept as-is for VS LogHub compatibility. The new `Activity` from `RequestTelemetryScope` propagates automatically via `AsyncLocal` (`Activity.Current`), so no queue changes are needed.

---

### 15. Testing

Use the existing LSP test framework (`AbstractLanguageServerProtocolTests` / `TestLspServer`) to test the full OTel pipeline end-to-end rather than testing exporters in isolation. This matches the existing Roslyn test patterns where tests start a real language server, send requests, and verify behavior.

**Approach**: Create a custom test LSP request handler that exercises the various Roslyn logging APIs (`Logger.Log`, `Logger.LogBlock`, `TelemetryLogging.LogAggregated*`, M.E.Logging `ILogger`). Wire up the OTel pipeline with in-memory/test exporters. Send requests and verify the expected outputs arrived at each exporter.

**Test scenarios**:

1. **VSTelemetry trace exporter receives `Roslyn.Logger` events only**:
   - Handler calls `Logger.Log(FunctionId, ...)` → verify mock `ITelemetryReporter.Log()` was called with correct event name and properties
   - Handler calls `Logger.LogBlock(FunctionId, ...)` → verify `ITelemetryReporter.LogBlockStart/End` called with correct FunctionId, kind, delta
   - Verify LSP request span (`Roslyn.LanguageServer`) is NOT forwarded to `ITelemetryReporter`

2. **VSTelemetry metric exporter receives aggregated metrics**:
   - Handler calls `TelemetryLogging.LogAggregatedHistogram/Counter` → flush → verify mock `ITelemetryReporter` received aggregated values

3. **LSP log message exporter receives M.E.Logging logs**:
   - Handler calls `ILogger.LogInformation(...)` → verify `window/logMessage` notification sent to client with correct formatting, category, and log level

4. **ETW exporters receive `Roslyn.Logger` events**:
   - Verify `RoslynEventSource` calls for Logger.Log (`.Log()`) and LogBlock (`.BlockStart/.BlockStop/.BlockCanceled`)

5. **OTLP/Aspire receives everything**:
   - Use `InMemoryExporter` (from `OpenTelemetry.Exporter.InMemory`) to capture all traces/metrics/logs
   - Verify both `Roslyn.Logger` and `Roslyn.LanguageServer` spans appear
   - Verify metrics and logs appear
   - Verify `TraceId`/`SpanId` correlation on log records emitted during an active request span

6. **Request span lifecycle**:
   - Send an LSP request → verify `Roslyn.LanguageServer` Activity has correct tags (`lsp.method`, `lsp.language`, `lsp.result`), events (`execution_start`), and duration

7. **VS host isolation**:
   - Verify no OTel packages are referenced from Workspaces/Features/VS projects (build-time check)

## Key Design Decisions

1. **OTel is the single pipeline** — In the language server, all telemetry goes through OTel. No more `AggregateLogger` with parallel `EtwLogger` + `RoslynLogger`. Instead, a single `OTelRoslynLogger` creates Activities, and exporters fan out to destinations.

2. **Two ActivitySources for routing** — `Roslyn.Logger` for Roslyn Logger API events (VSTelemetry/ETW care about these). `Roslyn.LanguageServer` for LSP request spans (Aspire-only, too high volume for VSTelemetry). VSTel/ETW exporters filter to `Roslyn.Logger` only.

3. **Logger.Log = zero-duration Activity, LogBlock = Activity with duration** — Both use the `Roslyn.Logger` ActivitySource. VSTelemetry trace exporter differentiates by duration: zero-duration → `ITelemetryReporter.Log()`, with duration → `LogBlockStart/End`. `FunctionId` and all properties carried as Activity tags.

4. **TelemetryLogging aggregated metrics → OTel Metrics** — Histograms and counters from `TelemetryLogging.LogAggregated*` map to OTel Meter `"Roslyn.Logger"`. VSTelemetry metric exporter forwards these to `ITelemetryReporter`. This is how LSP request metrics reach VSTelemetry (aggregated, not per-request).

5. **OTel packages only in the LanguageServer executable project** — No OTel deps in Protocol, Workspaces, Features, or VS projects. `System.Diagnostics.Activity`/`ActivitySource` are .NET BCL types and can be used anywhere.

6. **VS is unchanged** — VS continues using `AggregateLogger` with `TelemetryLogger`, `EtwLogger`, `CodeMarkerLogger`, `FileLogger`. Shared naming helpers extracted so both VS and OTel exporters use consistent event names.

7. **`Trace.CorrelationManager.ActivityId` is kept** — Required by VS LogHub's `CorrelationManagerTracingStrategy`. The new `Activity` from `RequestTelemetryScope` propagates via `AsyncLocal` alongside it.

8. **`LspLogMessageLoggerProvider` becomes an OTel log exporter** — Log output to the VS Code window flows through OTel as a custom `BaseExporter<LogRecord>`.

9. **Fault reporting stays separate** — `RoslynLogger.ReportFault` → `ITelemetryReporter.ReportFault` continues as-is. Not routed through OTel.

10. **Standard OTel configuration** — Use standard `OTEL_*` environment variables. Aspire integration is zero-config when launched from Aspire.

## File Change Summary

| Action | File | Description |
|--------|------|-------------|
| Modify | `Microsoft.CodeAnalysis.LanguageServer.csproj` | Add OTel NuGet packages |
| Create | `Logging/OTelRoslynLogger.cs` | Sole Roslyn `ILogger` → Activities on `Roslyn.Logger` source |
| Create | `Logging/OTelTelemetryLogProvider.cs` | `ITelemetryLogProvider` → OTel Metrics on `Roslyn.Logger` meter |
| Create | `Exporters/VSTelemetryTraceExporter.cs` | `Roslyn.Logger` traces → `ITelemetryReporter.Log/LogBlockStart/End` |
| Create | `Exporters/VSTelemetryMetricExporter.cs` | `Roslyn.Logger` metrics → `ITelemetryReporter` aggregated events |
| Create | `Exporters/EtwTraceExporter.cs` | `Roslyn.Logger` traces → `RoslynEventSource` ETW events |
| Create | `Exporters/EtwMetricExporter.cs` | `Roslyn.Logger` metrics → `RoslynEventSource` ETW |
| Create | `Exporters/LspLogMessageExporter.cs` | OTel logs → LSP `window/logMessage` (replaces `LspLogMessageLoggerProvider`) |
| Create | `Telemetry/TelemetryNaming.cs` (Workspaces) | Shared `FunctionId` → event name helpers |
| Modify | `Program.cs` | Wire OTel SDK, all providers, all exporters |
| Modify | `Logging/RoslynLogger.cs` | Remove `ILogger` impl, keep fault reporting only |
| Modify | `Protocol/.../RequestTelemetryScope.cs` | Add `ActivitySource("Roslyn.LanguageServer")` + `Activity` |
| Keep | `RequestExecutionQueue.cs` | Keep `Trace.CorrelationManager.ActivityId` unchanged |
| Remove | `Logging/LspLogMessageLoggerProvider.cs` | Replaced by `LspLogMessageExporter` |
| Remove | `Logging/LspLogMessageLogger.cs` | Replaced by `LspLogMessageExporter` |
