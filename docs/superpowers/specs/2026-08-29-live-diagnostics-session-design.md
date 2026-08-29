# Live Diagnostics Session Design

## Context

Issue #4 introduces the first live connection to a target .NET process. The repository already contains normalized `MemoryMetrics` and `GcEvent` records in `MemoryProfiler.Contracts`, process discovery behind `IDotNetProcessDiscovery`, and `Microsoft.Diagnostics.NETCore.Client` in the Diagnostics project. The new subsystem must keep EventPipe and TraceEvent implementation types inside `MemoryProfiler.Diagnostics`, remain cross-platform, support cancellation, and avoid blocking the UI thread.

## Scope

This task adds a diagnostics session API, an EventPipe-backed implementation, and automated acceptance coverage against a small .NET target process.

Included:

- eager connection and runtime validation;
- live managed-memory metrics at approximately one-second intervals;
- live garbage-collection events;
- target termination, user disconnect, cancellation, unsupported runtime, and transport failure handling;
- unit tests for mapping and lifecycle behavior;
- a cross-platform integration target that proves live metrics are received.

Excluded:

- wiring process selection to a live-session screen;
- reconnect or retry policy;
- persistence of metrics or GC events;
- heap snapshots and dump analysis;
- multiple simultaneous consumers of the same output stream.

## Public API

`ILiveDiagnosticsSession` follows the issue contract:

```csharp
public interface ILiveDiagnosticsSession : IAsyncDisposable
{
    int ProcessId { get; }

    IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
        CancellationToken cancellationToken = default);
}
```

`ILiveDiagnosticsSessionFactory` makes connection timing explicit:

```csharp
public interface ILiveDiagnosticsSessionFactory
{
    Task<ILiveDiagnosticsSession> ConnectAsync(
        int processId,
        CancellationToken cancellationToken = default);
}
```

`ConnectAsync` returns only after EventPipe accepts the requested providers. Invalid process identifiers fail argument validation before any connection attempt. The factory exposes no DiagnosticsClient, EventPipe, or TraceEvent types.

Each observation method allows one active enumerator. A second active enumerator for the same stream throws `InvalidOperationException`. The memory and GC streams may be consumed concurrently because they are separate outputs from one shared EventPipe session.

## Architecture

The implementation is split into four responsibilities:

1. `LiveDiagnosticsSessionFactory` validates input, configures providers, starts EventPipe asynchronously, translates connection errors, and creates the session.
2. `LiveDiagnosticsSession` owns the EventPipe connection, cancellation source, parser task, and output channels. It implements the public lifecycle.
3. An internal EventPipe adapter contains all DiagnosticsClient and TraceEvent types. It converts callbacks into internal, testable runtime observations.
4. Internal accumulators map runtime observations to the public `MemoryMetrics` and `GcEvent` records.

The required files live under `src/MemoryProfiler.Diagnostics/Sessions/`. Small internal files may be added there to keep parsing, accumulation, and lifecycle concerns independent. Tests mirror this structure under `tests/MemoryProfiler.Diagnostics.Tests/Sessions/`.

The Diagnostics project adds `Microsoft.Diagnostics.Tracing.TraceEvent` version `3.2.6`. EventPipe parsing uses `EventPipeEventSource`, matching Microsoft guidance for real-time DiagnosticsClient streams.

## EventPipe Configuration

One EventPipe session enables two providers:

- `System.Runtime` at informational level with `EventCounterIntervalSec` set to `1`;
- `Microsoft-Windows-DotNETRuntime` at informational level with the CLR GC keyword.

Rundown is disabled because this is a low-overhead live session, not a trace file intended for post-processing. The parser runs on a background task because `EventPipeEventSource.Process()` is synchronous and blocking.

## Memory Metrics Data Flow

The dynamic parser consumes `System.Runtime` `EventCounters` payloads. An internal accumulator stores the latest value for:

- `gc-heap-size`;
- `gen-0-size`;
- `gen-1-size`;
- `gen-2-size`;
- `loh-size`;
- `poh-size`;
- `alloc-rate`;
- `gen-0-gc-count`;
- `gen-1-gc-count`;
- `gen-2-gc-count`.

Counter payloads are parsed defensively from numeric CLR values rather than by converting through the current culture. Incrementing GC counters are accumulated for the lifetime of the session. The latest promoted-byte total comes from CLR GC heap-stat observations when available and remains zero until observed.

The arrival of a valid `gc-heap-size` sample closes a logical interval and emits one immutable `MemoryMetrics` snapshot using the latest values of the other counters. Malformed or unknown counters are ignored without terminating the session. Values that cannot be represented by the unsigned contract are clamped to zero, and floating-point non-finite values are rejected.

Memory samples use a bounded channel with capacity one and drop-oldest behavior. A slow UI therefore receives the latest state without creating an unbounded backlog of obsolete one-second samples.

## GC Event Data Flow

The CLR parser feeds a stateful correlator with GC start, stop, heap-stat, suspension-begin, and restart-end observations.

For each active GC, the correlator records:

- GC number and generation;
- start timestamp and reason;
- the most recent managed-heap size before the GC;
- suspension intervals associated with the collection.

When the matching GC stop arrives, the correlator emits `GcEvent`. `PauseDuration` is the sum of observed suspension intervals. If no complete suspension interval is available, it falls back to elapsed GC start-to-stop time. `HeapSizeAfter` uses the latest heap-stat value observed for that collection, falling back to the latest managed-heap metric. Missing size data is represented as zero. Runtime reason values are converted to stable descriptive strings without exposing TraceEvent enums.

GC events use an unbounded single-reader channel. GC frequency is low relative to memory samples, and losing a collection would be misleading. The parser remains the only writer.

## Lifecycle and Failure Semantics

The factory calls the package's asynchronous EventPipe start API with the caller's cancellation token.

Connection outcomes:

- cancellation propagates as `OperationCanceledException`;
- an unsupported runtime or unsupported EventPipe command becomes `NotSupportedException` with an actionable message;
- endpoint, IPC, permission, or startup failures become `IOException` with the process identifier and original exception as the inner exception.

After connection:

- cancelling a memory or GC enumerator ends only that enumeration;
- target termination or normal EventPipe end completes both channels normally;
- an unexpected parser or transport failure completes both channels with `IOException`;
- `DisposeAsync` is idempotent, cancels the parser, sends a best-effort EventPipe stop, waits for the parser task, completes both channels, and releases all resources;
- disposing from any state must not surface shutdown races caused by an already terminated target.

The session never blocks a UI thread. Connection is asynchronous, parsing runs in the background, and consumers use asynchronous channel enumeration.

## Testing Strategy

Tests are written first for each behavior.

Unit tests cover:

- factory argument validation and eager connection;
- normalization of unsupported-runtime, transport, and cancellation failures;
- memory-counter parsing and snapshot projection;
- cumulative collection counts and promoted bytes;
- malformed counter payload tolerance;
- GC correlation, pause calculation, heap sizes, and reason projection;
- concurrent memory and GC observation;
- rejection of a second consumer on one stream;
- per-observer cancellation without disconnecting the session;
- normal completion when the target terminates;
- exceptional completion on parser failure;
- idempotent asynchronous disposal and best-effort stop.

An internal adapter interface lets unit tests feed deterministic observations without constructing EventPipe or TraceEvent objects.

The acceptance test launches a small console fixture as a child `dotnet` process. The fixture stays alive, allocates managed memory periodically, and flushes a ready signal. The test connects through the real `LiveDiagnosticsSessionFactory`, reads a non-zero `MemoryMetrics` sample within a bounded timeout, disposes the session, and terminates the fixture in a `finally` block. The fixture and test contain no operating-system-specific APIs, so the same test can run on macOS, Windows, and Linux.

## Completion Criteria

The task is complete when:

- the public interfaces compile without EventPipe implementation types;
- all unit tests pass;
- the real-process acceptance test receives live managed-memory data;
- solution build and tests complete without warnings or errors;
- cancellation and disposal leave no parser task or target fixture running;
- the implementation is committed with `feat: add live diagnostics session`.
