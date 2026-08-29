# Dump Capture Design

## Context

Issue #7 adds managed-process dump capture to the existing Live Session in MemScope. The feature must use the official .NET diagnostics API, remain responsive, support cancellation, expose capture state to the user, produce filenames that do not overwrite existing dumps, remove incomplete output after failure, and create a heap-bearing dump suitable for the ClrMD loader planned in issue #8.

Issue #6 is complete. Issue #8 owns production dump loading and heap enumeration, so this feature verifies ClrMD compatibility in tests without adding the future loader to the application.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The Live Session is a dense native product surface, outside the web-page focus of the frontend design skill. The applicable principles are token consistency, restrained motion, accessibility, complete states, concise copy, keyboard and focus preservation, and use of native Avalonia controls.

## Scope

### In scope

- `IDumpCaptureService` with the exact interface specified by issue #7.
- `DumpCaptureService` backed by `Microsoft.Diagnostics.NETCore.Client`.
- Heap-bearing, ClrMD-compatible dump capture.
- Unique, sanitized filenames based on the target process name and local capture time.
- Cancellation and incomplete-file cleanup.
- A native destination-folder picker.
- Live Session capture, cancel, progress, success, and error states.
- Unit tests and a real target-process acceptance test that opens the result with ClrMD.

### Out of scope

- The start-screen **Open Dump** flow.
- Production ClrMD integration, heap enumeration, type aggregation, or snapshot navigation.
- Capture history, recent-session persistence, configurable dump types, or configurable filename templates.
- Byte-level progress, which the diagnostics API does not expose.

## Diagnostics Architecture

Create:

```text
src/MemoryProfiler.Diagnostics/Dumps/
  IDumpCaptureService.cs
  DumpCaptureService.cs
```

The public interface remains:

```csharp
public interface IDumpCaptureService
{
    Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
```

`DumpCaptureService` validates a positive process ID and a nonblank destination directory. It resolves the process name, sanitizes invalid filename characters, and falls back to `process-{pid}` if no usable name remains. It creates the destination directory when necessary.

The base filename is `<process>-yyyy-MM-dd-HHmmss.dmp`, using local time to match the user-facing example. If that path already exists, numeric suffixes such as `-2` and `-3` are tried until an unused path is found. A small internal environment seam owns process-name lookup, clock access, file existence, directory creation, and deletion so filename and cleanup behavior remain deterministic in tests. Internal types stay in `DumpCaptureService.cs` to keep the public diagnostics surface to the two requested files.

The service calls `DiagnosticsClient.WriteDumpAsync` with `DumpType.WithHeap`, `WriteDumpFlags.None`, the selected output path, and the caller's cancellation token. `WithHeap` is the smallest official dump type intended to include the GC heaps needed by ClrMD. The async diagnostics API keeps the operation off the UI thread and observes cancellation.

The selected final path is treated as incomplete until `WriteDumpAsync` returns successfully. Any exception, including cancellation, triggers a best-effort deletion of that path before the original exception is rethrown. Cleanup failure must not hide the capture failure.

## Application Architecture

Add an internal application abstraction for selecting one destination folder and an Avalonia implementation backed by the active window's `StorageProvider`. The picker title is **Choose snapshot destination**, allows one folder, and returns `null` when dismissed. `App` supplies the picker and a `DumpCaptureService` while composing `StartViewModel`; `StartViewModel` passes both dependencies into each `LiveSessionViewModel`.

`LiveSessionViewModel` owns capture orchestration and exposes:

- `CaptureSnapshotCommand`
- `CancelCaptureCommand`
- `IsCapturing`
- `CanCaptureSnapshot`
- `HasCaptureStatus` and `CaptureStatusMessage`
- `HasCaptureError` and `CaptureErrorMessage`
- `CapturedDumpPath`

The capture command is available only while the diagnostics session is live and no capture is active. It opens the folder picker first. Dismissing the picker changes no capture state. After a folder is selected, the view model creates a capture-specific cancellation source and invokes `IDumpCaptureService.CaptureAsync`.

Capture state is published through the existing UI dispatcher. Starting capture clears the previous capture result and error, sets `IsCapturing`, and displays **Capturing snapshot**. Successful completion displays **Snapshot saved** together with the returned path. User cancellation clears the active progress state without presenting an error. Other failures display **Unable to capture snapshot.** followed by the diagnostics error message while leaving the live session connected.

Disconnecting, closing the Live Session, or disposing its view model cancels and awaits any active capture as part of orderly shutdown. Capture cancellation is independent of EventPipe session cancellation so cancelling a snapshot does not disconnect live monitoring.

## Live Session UI

Add **Capture Snapshot** beside **Disconnect** in the Live Session header. During capture, the capture button is disabled and **Cancel capture** is visible. The controls reuse the existing `dashboard-action` style, semantic accent, corner radius, and keyboard behavior.

An indeterminate two-pixel progress bar and a compact status row appear beneath the header during capture. The progress bar has an automation name and polite live setting. Success status uses the existing secondary text treatment and permits path trimming with a tooltip containing the full path. Capture errors use the existing error surface and assertive live region, separate from fatal live-session errors.

No decorative animation is added. The state transition itself supplies feedback, respects reduced-motion preferences by remaining static, and preserves the existing dense layout.

## Data Flow

1. The user selects **Capture Snapshot** while connected.
2. The native folder picker returns a destination or is dismissed.
3. `LiveSessionViewModel` enters capturing state and passes the process ID, destination, and capture token to `IDumpCaptureService`.
4. `DumpCaptureService` chooses an unused sanitized filename and requests a `WithHeap` dump through `DiagnosticsClient.WriteDumpAsync`.
5. On success, the service returns the absolute path and the view model publishes it to the Live Session.
6. On cancellation or failure, the service deletes incomplete output. The view model distinguishes user cancellation from actionable errors.

## Error Handling

- Invalid public arguments fail before diagnostics work begins.
- Process exit, permissions, unsupported runtime protocol, I/O, and diagnostics failures propagate from the service after cleanup.
- Capture errors are nonfatal to the live EventPipe session.
- Cancellation never appears as an error when initiated by the user or lifecycle shutdown.
- Cleanup is best effort and never replaces the original exception.
- The capture command prevents overlapping capture operations from one Live Session.

## Testing Strategy

Diagnostics unit tests use the internal environment and dump-writer seams to verify:

- argument validation;
- `DumpType.WithHeap`, destination path, and cancellation-token forwarding;
- process-name sanitization and fallback naming;
- deterministic timestamp formatting;
- collision suffixes and no overwrite;
- successful absolute-path return;
- deletion of partial output after failure and cancellation;
- preservation of the original exception if deletion also fails.

Application tests use stub picker and capture services to verify:

- picker dismissal is a no-op;
- capture is enabled only for a live, idle session;
- capturing, success, cancellation, and failure states;
- duplicate capture prevention;
- cancellation command behavior;
- capture failure does not disconnect the session;
- disconnect and disposal cancel and await capture;
- state changes are dispatched through `IUiDispatcher`.

The diagnostics acceptance test starts the existing `LiveDiagnosticsTarget`, captures a real dump to a temporary directory, loads it with a test-only `Microsoft.Diagnostics.Runtime` dependency, and verifies that ClrMD discovers at least one CLR runtime. The test deletes its temporary dump in `finally`. Production ClrMD integration remains deferred to issue #8.

## Source Basis

- [Microsoft diagnostics client library](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostics-client-library)
- [DiagnosticsClient source](https://github.com/dotnet/diagnostics/blob/main/src/Microsoft.Diagnostics.NETCore.Client/DiagnosticsClient/DiagnosticsClient.cs)

