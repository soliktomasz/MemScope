# Top Retainers and Object Values Design

## Context

MemScope already computes an object-level dominator tree for every reachable managed heap object. `DominatorAnalysisResult.Dominators` contains each object's address, type, shallow size, retained size, and retained object count, ordered by retained size. The snapshot UI currently consumes only `TypeRetainedSizes`, so users can identify expensive types but cannot answer questions such as “which cache object retains 430 MB?”

The existing investigation workflow can enumerate instances, follow incoming and outgoing references, and find paths to GC roots. It does not expose immediate primitive or string values from a selected object. Consequently, a user can recognize a cache container and navigate its object graph, but cannot confirm its state from fields such as `_count`, `Name`, or a cache key.

This feature exposes the already-computed object-level retained metrics through a Top Retainers workflow and adds on-demand, immediate field-value inspection. Dump values are displayed as soon as Object Details loads. Because dumps may contain credentials, personal data, tokens, or other secrets, the design establishes explicit handling and resource limits for sensitive values.

## Goals

- Let users identify the individual objects that retain the most managed memory.
- Show exact shallow size, retained size, retained object count, and retained-heap percentage for a selected object.
- Let users inspect the selected object's immediate scalar, string, null, and reference fields without recursively expanding the graph.
- Preserve navigation between object details, references, and GC-root paths.
- Keep expensive dump access off the UI thread, cancellable, isolated, and bounded.
- Avoid retaining, logging, or serializing decoded dump values beyond the active Object Details surface.

## Non-Goals

- Recursive inline object-tree expansion.
- Reconstructing POCOs, `JsonDocument`, `JsonElement`, `JObject`, or arbitrary graphs as JSON text.
- Evaluating properties or executing target-process code.
- Inspecting native allocations, unmanaged buffers, memory-mapped files, or external cache services.
- Bulk value search, value export, or automatic clipboard operations.
- Live-process value inspection without a dump.
- Persisting decoded values in session storage or navigation history.
- Changing the dominator algorithm or the semantics of per-type retained size.

## Chosen Approach

Use the existing on-demand analysis architecture.

- `DominatorTreeService` remains the source of retained-memory metrics. `SnapshotViewModel` retains its completed `DominatorAnalysisResult` and publishes object-level rows through a dedicated Top Retainers view model.
- A new `IHeapObjectValueService` reopens the dump only when Object Details needs values. It reads the selected object's immediate fields or a bounded page of array elements and disposes the dump source after each request.
- A new `ObjectDetailsViewModel` composes the retained metrics already in memory with decoded values returned by the service.

This approach matches the existing object repository, reference service, root service, cancellation, and UI-dispatch patterns. It avoids increasing baseline snapshot-load cost and avoids keeping `DataTarget` or decoded values alive for the snapshot lifetime.

Rejected alternatives:

- Building a complete value index during snapshot loading would increase startup time, MemScope's own memory use, and the lifetime of sensitive values.
- Keeping ClrMD `DataTarget` instances open would improve repeated-read latency but complicate disposal, concurrency, dump file locking, and snapshot comparison.
- A cache-specific wizard would require application-semantic knowledge that is not reliably present in a raw managed dump. Top retainers generalize to caches, leaks, queues, buffers, and other retaining owners.

## Contracts

Create `MemoryProfiler.Contracts/Heap/HeapValueKind.cs`:

```csharp
namespace MemoryProfiler.Contracts.Heap;

public enum HeapValueKind
{
    Primitive,
    Enum,
    String,
    ObjectReference,
    ArrayElement,
    Null,
    Unavailable,
}
```

Create `MemoryProfiler.Contracts/Heap/HeapFieldValue.cs`:

```csharp
namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapFieldValue(
    string Name,
    string DeclaredTypeName,
    HeapValueKind Kind,
    string? ValueText,
    ulong? ReferencedObjectAddress,
    string? ReferencedObjectTypeName,
    bool IsTruncated,
    int? TotalLength,
    string? UnavailableReason);
```

Create `MemoryProfiler.Contracts/Heap/HeapObjectValueResult.cs`:

```csharp
namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapObjectValueResult(
    HeapObjectInfo Object,
    IReadOnlyList<HeapFieldValue> Fields,
    int TotalFieldOrElementCount,
    bool HasMoreElements);
```

`ValueText` uses invariant formatting in the analysis layer. Addresses remain numeric in contracts and are formatted by the application. `UnavailableReason` contains a controlled category such as `Unsupported value type` or `Value could not be read`; it must not contain decoded target values or an arbitrary exception message.

The existing `DominatorInfo` contract remains unchanged.

## Analysis Architecture

### Service interface

Create `MemoryProfiler.Analysis/Values/IHeapObjectValueService.cs`:

```csharp
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Values;

public interface IHeapObjectValueService
{
    Task<HeapObjectValueResult> ReadAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken = default);
}
```

Create `MemoryProfiler.Analysis/Values/ObjectValueReadOptions.cs`:

```csharp
namespace MemoryProfiler.Analysis.Values;

public sealed record ObjectValueReadOptions(
    int ArrayOffset = 0,
    int ArrayLimit = 500,
    int StringLimit = 4096);
```

`ClrMdHeapObjectValueService` validates the snapshot path, non-zero address, and options, then runs the read inside `Task.Run`. It uses `IHeapDumpSourceFactory` so deterministic unit tests can exercise decoding without loading a real dump.

### Dump-source seam

Extend the internal `IHeapDumpSource` abstraction with a focused object-value operation rather than exposing ClrMD types outside the loader implementation. The seam returns internal raw field records containing:

- Field name or array index.
- Declared type name.
- Raw value category.
- Invariant scalar text when successfully decoded.
- Referenced object address and type name for object references.
- String truncation metadata.
- A controlled unavailability category.

The concrete `ClrMdHeapDumpSource` resolves the object through `ClrHeap.GetObject`. It rejects null, invalid, free, or zero-method-table objects. It returns a fresh `HeapObjectInfo` so Object Details can show authoritative type, size, and generation even when opened from a reference or navigation-history location.

### Supported values

The first release decodes:

- Boolean and numeric CLR primitives using invariant culture.
- `char` with an escaped display representation.
- Enums as `Name (numeric value)` when the name is recoverable, otherwise the numeric value.
- Nullable supported scalars as their contained value or `null`.
- Strings with original length and truncation metadata.
- `decimal`, `DateTime`, `TimeSpan`, and `Guid` using invariant, round-trippable formatting.
- Object-reference fields as address plus target type.
- Null object references as `HeapValueKind.Null`.
- Array elements as `HeapValueKind.ArrayElement`; reference elements carry address and type, and supported scalar elements carry `ValueText`.

Other value-type fields return `HeapValueKind.Unavailable` with `Unsupported value type`. They are not recursively expanded.

The service reads instance fields only. Static and thread-static roots remain visible through the existing incoming-reference and GC-root workflows; static-field value inspection is outside this release.

### String and array bounds

- Normal reads use a 4,096-character string limit.
- The Object Details **Show more** action repeats the read with a maximum string limit of 1,048,576 characters for the selected object. MemScope never requests or renders an unbounded string.
- Arrays return at most 500 elements per request.
- `ArrayOffset` must be non-negative; `ArrayLimit` must be between 1 and 500; `StringLimit` must be between 1 and 1,048,576.
- Array paging uses stable zero-based indices as field names (`[0]`, `[1]`, and so on).
- Cancellation is checked before opening the dump and between every decoded field or array element.

### Retained metrics

`DominatorTreeService` and `DominatorAnalysisResult` remain unchanged. The result is already sorted by retained size descending, making it the source for Top Retainers.

`SnapshotViewModel` stores the completed result for as long as the snapshot is open. It passes the same result to `TypeBrowserViewModel.SetRetainedSizes` and `TopRetainersViewModel.SetResult`. Closing or replacing the snapshot clears both consumers and releases the result through the existing snapshot lifetime.

For a selected object outside the currently materialized Top Retainers window, `ObjectDetailsViewModel` finds the matching `DominatorInfo` in the completed result off the UI thread. A missing address means retained metrics are unavailable, which can occur for unreachable garbage excluded from dominator analysis; value inspection remains available.

## Application Architecture

### Top Retainers

Create:

```text
src/MemoryProfiler.App/ViewModels/Retainers/
  TopRetainerRowViewModel.cs
  TopRetainersViewModel.cs
```

`TopRetainerRowViewModel` wraps `DominatorInfo` and exposes formatted address, shallow size, retained size, retained object count, and percentage of the reachable managed heap.

`TopRetainersViewModel` owns:

- The current `DominatorAnalysisResult`.
- A bounded, virtualized/windowed row source rather than one Avalonia view model per heap object.
- Search by ordinal-ignore-case type name or hexadecimal address.
- Selected row and an `InspectObjectCommand`.
- Loading, unavailable, empty, and table states tied to the existing background dominator computation.

The default window contains the first 500 retained-size-ordered rows. Scrolling or an explicit **Load more** action adds the next 500 rows. Search operates on the underlying `DominatorInfo` list off the UI thread and publishes only a bounded result window.

### Object Details

Create:

```text
src/MemoryProfiler.App/ViewModels/Objects/
  HeapFieldValueRowViewModel.cs
  ObjectDetailsViewModel.cs
```

`ObjectDetailsViewModel` follows the existing cancellable pane pattern:

- `ShowAsync(HeapSnapshot, ulong address, string typeName, DominatorAnalysisResult?, CancellationToken)` cancels the previous load, increments a version, publishes known retained metrics, and starts value decoding.
- `LoadNextArrayPageAsync` requests the next page and appends it only if the inspected object and version still match.
- `ShowMoreStringsAsync` repeats the object read with the 1 MiB string limit.
- `ClearAsync` cancels work and clears decoded values immediately.
- `DisposeAsync` cancels and disposes all owned tokens.

The view model exposes separate retained-metric and value-loading states so either half can succeed independently. A failed value request does not remove retained metrics; a failed or unavailable dominator result does not block values.

`HeapFieldValueRowViewModel` exposes field name, declared type, kind, formatted value, optional referenced address/type, truncation state, copy command eligibility, and navigation eligibility.

### Navigation

Add:

```csharp
public sealed record ObjectDetailsLocation(
    ulong ObjectAddress,
    string ObjectTypeName) : InvestigationLocation;
```

Only the address and type name enter history. Decoded values, retained metrics, and strings never enter history. Replaying a location reloads values from the dump and reuses the current dominator result when available.

Object Details can be opened from:

- A Top Retainers row.
- An object instance through **Inspect Object**.
- A navigable incoming or outgoing reference.
- An object represented in a GC-root path.
- Back and Forward navigation.

Referenced fields preserve the existing commands for outgoing references, incoming references, and paths to root. Double-clicking a referenced field opens that target in Object Details.

### Composition

`App.axaml.cs` constructs `ClrMdHeapObjectValueService` and passes it through `StartViewModel` to `SnapshotViewModel`. Constructor parameters remain optional in unit-test-friendly view models where existing conventions require graceful disabled behavior. Production composition supplies both the dominator and value services.

## User Interface

The upper snapshot analysis table gains a compact **Types / Top Retainers** mode switch.

Top Retainers columns:

- Type
- Address
- Shallow size
- Retained size
- Retained objects
- Retained heap percentage

The current retained-size progress and failure state apply to both the Types retained column and the Top Retainers mode. Before dominator completion, Top Retainers shows the existing compact loading treatment. On nonfatal failure, it shows `Top retainers unavailable` while type and object-value investigation remain usable.

The lower-left pane switches between Instances and Object Details according to the active investigation location. References and Paths to Root remain in the other two lower panes.

Object Details contains:

- Type name and object address.
- Generation.
- Shallow size, retained size, retained object count, and retained-heap percentage.
- A persistent warning: `Dump values may contain credentials, personal data, or other secrets.`
- An immediate-fields table with Field, Declared Type, Kind, Value, and Referenced Address columns.
- A **Show more** action when at least one string is truncated.
- A **Load more elements** action when an array has another page.
- Existing context-menu actions for navigable references and explicit value/address copying.

The warning does not require confirmation because the selected product behavior displays values immediately. It stays visible whenever Object Details is active.

All tables use native Avalonia virtualization, sorting where the underlying set is fully materialized, keyboard selection, and accessible automation names. No decorative animation or new design dependency is introduced.

## Data Flow

1. A snapshot loads and the existing background dominator analysis begins.
2. On completion, `SnapshotViewModel` publishes type retained sizes and the object-level result to Top Retainers.
3. The user selects a Top Retainers row or chooses **Inspect Object** elsewhere.
4. Navigation records `ObjectDetailsLocation(address, typeName)`.
5. `ObjectDetailsViewModel` publishes available `DominatorInfo` metrics immediately.
6. `IHeapObjectValueService.ReadAsync` reopens the dump and decodes immediate values off the UI thread.
7. The view model publishes rows through `IUiDispatcher` only if its navigation version remains current.
8. Array pagination and expanded string reads repeat bounded service requests.
9. Navigating away cancels pending reads and clears decoded values before loading the next location.

## Sensitive-Value Handling

- Values are visible immediately in Object Details, accompanied by the persistent warning.
- Decoded values exist only in the service result and active Object Details view model.
- Values are never stored in navigation locations, session storage, logs, telemetry, exception text, or diagnostic status messages.
- Clipboard copying requires an explicit user command on one selected value.
- Bulk copy and export are not provided.
- Service error categories are controlled strings and never include arbitrary target values.
- Test failure messages use synthetic sentinel values and do not print full decoded objects.
- Closing, replacing, or navigating away from an object clears value rows and releases their strings.

This model reduces accidental persistence but cannot make opening an untrusted dump safe for shoulder-surfing or screen capture. The warning communicates that limitation without blocking the chosen immediate-display workflow.

## Error Handling and Cancellation

- Invalid path, zero address, invalid options, or non-walkable heap fail before field enumeration using the existing validation conventions.
- An unknown, invalid, free, or zero-method-table object produces a recoverable Object Details error.
- A failure decoding one field produces an `Unavailable` row and does not fail other fields.
- A dump-open or object-resolution failure produces the existing inline error details surface.
- Dominator failure leaves Object Details values usable and retained metrics marked unavailable.
- Value failure leaves known retained metrics visible.
- Cancellation caused by navigation, snapshot replacement, or disposal is silent.
- Version checks prevent stale reads, expanded strings, or later array pages from overwriting the active object.
- Every dump source is disposed on success, cancellation, and failure.

## Performance and Memory

- No values are decoded during baseline snapshot loading.
- Top Retainers consumes the already-cached `DominatorInfo` list and materializes row view models in 500-row windows.
- Search scans the underlying list off the UI thread, checks cancellation, and returns a bounded window.
- Selected-object retained lookup runs off the UI thread and does not introduce a second million-entry dictionary.
- Normal value reads are bounded to 500 array elements and 4,096 characters per string.
- Expanded reads cap strings at 1 MiB.
- Only one Object Details value request is active per snapshot view.
- Decoded values are not cached after navigation.

## Testing Strategy

### Contracts

- JSON round-trip for `HeapFieldValue` and `HeapObjectValueResult`.
- Every `HeapValueKind` value remains serializable and stable.
- Null reference, unavailable reason, truncation metadata, and referenced-object metadata round-trip exactly.

### Analysis unit tests

- Supported Boolean, integer, floating-point, character, enum, nullable, string, decimal, `DateTime`, `TimeSpan`, and `Guid` values use invariant text.
- Null and non-null object references map to the correct kind, address, and type.
- Scalar and reference arrays page with stable indices.
- Strings at, below, and above 4,096 characters set length and truncation correctly.
- The 1 MiB expanded limit is enforced.
- Unsupported structs and individually unreadable fields return controlled `Unavailable` rows.
- Unknown, free, invalid, and zero-method-table objects fail predictably.
- Non-walkable heaps, invalid paths, zero addresses, and invalid options are rejected.
- Cancellation interrupts large arrays and field walks.
- Dump sources are disposed on success, partial failure, cancellation, and exceptions.

### Application unit tests

- Top Retainers keeps retained ordering, formats metrics, calculates percentages, windows rows, filters by type/address, and preserves the selected object when possible.
- Top Retainers exposes loading, unavailable, empty, and table states.
- Object Details publishes retained metrics before values complete.
- Values populate automatically and show the persistent warning.
- Partial field failures render alongside successful rows.
- Array pages append once and stale pages are discarded.
- Expanded strings replace previews only for the active object.
- Rapid navigation guarantees the latest object wins.
- Navigating away and disposing clear sensitive rows and cancel pending work.
- Referenced rows route Object Details, incoming/outgoing references, roots, copy-address, and copy-value commands correctly.
- Back and Forward restore addresses without storing decoded values.
- `StartViewModel` and production composition pass the new service through.
- Snapshot XAML exposes accessible names and all idle/loading/error/empty/table states.

### Acceptance

Extend the live diagnostics target with a deterministic cache wrapper containing:

- A known integer count.
- A known synthetic string sentinel.
- A list or dictionary that owns at least 1 MiB of byte arrays.

Capture a dump and assert:

- The wrapper appears among the largest dominators.
- Its retained size covers the known payload graph.
- Its retained object count includes the owner and payload objects.
- Immediate scalar and string fields decode to the controlled sentinel values.
- Its collection field is navigable by address and type.

Acceptance tests remain in the nonparallel `Live diagnostics` collection and never use real secrets.

## Delivery Boundaries

The implementation should be reviewable in independent increments:

1. Contracts and ClrMD value service with unit and acceptance coverage.
2. Top Retainers application model and snapshot integration.
3. Object Details state machine, navigation, and copy behavior.
4. Snapshot UI, accessibility, composition, and end-to-end verification.

Each increment leaves the solution buildable and its scoped tests passing. No migration or persisted-data compatibility work is required because decoded values and Top Retainers state are not stored.
