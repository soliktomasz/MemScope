# Object References Design

## Context

Issue #11 adds the third snapshot analysis surface to MemScope: from an object instance (Task 11, issue #10) the user can inspect what the object points to (**outgoing references**) and what points to the object (**incoming references**), then navigate from one heap object to another through those references. Issue #9 delivered the type browser, issue #10 delivered lazy instance loading; this issue adds **Snapshot → Types → Instances → References** with the same discipline: diagnostics behind interfaces, expensive walks off the UI thread, cancellation, and virtualized rendering.

The display contract from the issue:

```text
MyApp.Cache
  └── _entries
      └── Dictionary<...>
          └── Entry[]
```

Each hop shows a field name when available and the type name of the object at the other end, so the user can follow a reference chain object by object.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The references pane is a data table, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency (reuse the `App*` semantic brushes and corner radius), accessibility (automation names, native keyboard behavior, virtualized list), complete states (idle, loading, error, empty, table, disabled navigation for GC roots), density (compact mono rows), restrained motion (static transitions only, honor reduced motion), copy audit (plain labels, no decorative punctuation), and native Avalonia controls only.

## Scope

### In scope

- `Analysis/References/IObjectReferenceService` with `GetOutgoingReferencesAsync(HeapSnapshot, ulong objectAddress, CancellationToken)` and `GetIncomingReferencesAsync(...)`, implemented by `ClrMdObjectReferenceService`.
- Outgoing references: the object's fields (named) and array elements, mapped to `ObjectReference` rows with the target's type name.
- Incoming references: every heap object that references the target (field name when available), plus GC roots (handles and static fields) that keep the target alive, so the root causes of a leak are visible.
- `ObjectReference` (contract) gains optional `SourceTypeName` / `TargetTypeName` so the UI can render type names without a second heap walk.
- Snapshot view gains a references pane beside the instances pane: two context-menu actions on an instance row (**Show Outgoing References**, **Show Incoming References**) open it; selecting a reference row offers the same two actions on the object at the other end, so the user navigates object to object. Root rows (no heap object at the other end) disable navigation.
- Unit tests for the service, both view models, row formatting, snapshot wiring, and composition; one acceptance test that captures a live process and asserts outgoing and incoming reference enumeration across a real object graph.

### Out of scope

- Reference graphs / path-to-root analysis (Task 13), retained size (Task 14), snapshot comparison (Task 15).
- Static field value inspection, value-type field expansion, and cross-referenced heap indexes (would revisit if enumeration proves too slow).
- Reference filtering, sorting, or pagination beyond UI virtualization.

## Application Architecture

### Contracts

`ObjectReference` gains optional type names (positional defaults keep every existing call site and the serialization test compiling; JSON round-trip still preserves equality):

```csharp
public sealed record ObjectReference(
    ulong SourceAddress,
    ulong TargetAddress,
    ReferenceKind Kind,
    string? Name,
    string? SourceTypeName = null,
    string? TargetTypeName = null);
```

`ReferenceKind` already models the four shapes the UI must distinguish: `Field`, `ArrayElement`, `StaticField`, `Handle`.

### Analysis

Create:

```text
src/MemoryProfiler.Analysis/References/
  IObjectReferenceService.cs
  ClrMdObjectReferenceService.cs
```

`IObjectReferenceService` is the diagnostics-facing seam, exactly as the issue specifies:

```csharp
public interface IObjectReferenceService
{
    Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);
}
```

`ClrMdObjectReferenceService` mirrors `ClrMdHeapObjectRepository`:

- Reuses the internal `IHeapDumpSourceFactory` / `IHeapDumpSource` abstraction so unit tests stub the same way the loader and repository tests do.
- The dump source seam gains two members returning an internal `ObjectReferenceData` record (source address, target address, kind, name, source type name, target type name):
  - `EnumerateOutgoingReferences(ulong sourceAddress)` — `ClrHeap.GetObject`, then `ClrObject.EnumerateReferencesWithFields(carefully: true)`, mapping field refs to `ReferenceKind.Field` (with the field name) and array entries to `ReferenceKind.ArrayElement` (name `null`), carrying the target's type name; invalid, free, or null targets are skipped.
  - `EnumerateIncomingReferences(ulong targetAddress)` — walks every heap object and matches references whose target is `targetAddress` (field name when available), then adds roots from `ClrHeap.EnumerateRoots()` whose `Object` equals the target: `StaticVar` / `ThreadStaticVar` roots become `ReferenceKind.StaticField`, everything else becomes `ReferenceKind.Handle`; roots have `SourceAddress = 0` and a human-readable kind name ("GC handle", "Static field", "Stack", ...).
- `GetOutgoingReferencesAsync` / `GetIncomingReferencesAsync` validate the snapshot and address, then run the walk inside `Task.Run` (off the caller thread), check cancellation per object, and return rows in a stable order (outgoing by target address; incoming by source address with roots last, then by name). A heap that cannot be walked fails with `InvalidDataException`, same as the loader.

### Application

Create:

```text
src/MemoryProfiler.App/ViewModels/Objects/
  ObjectReferenceRowViewModel.cs
  ObjectReferencesViewModel.cs
```

`ObjectReferencesViewModel` owns the references pane state machine:

- `ShowAsync(HeapSnapshot snapshot, HeapObjectInfo objectInfo, ReferenceDirection direction)` — cancels any in-flight load, publishes the loading state through `IUiDispatcher`, awaits the service (which runs off the UI thread), then publishes the rows. A version counter plus per-load cancellation guarantee a stale (superseded) selection can never overwrite a newer one.
- `ClearAsync()` cancels in-flight loads and returns the pane to the idle state.
- `DisposeAsync` cancels and releases the load tokens.
- Exposes `References` (read-only observable collection), `ObjectTypeName`, `AddressDisplay`, `Direction`, `SummaryDisplay` ("N outgoing references · kind counts"), and the state flags `ShowIdle`, `ShowLoading`, `ShowError`, `ShowEmpty`, `ShowTable`.
- Failures become an inline error state; cancellation never appears as an error.

`ObjectReferenceRowViewModel` formats one reference row and exposes the navigable endpoint:

- Outgoing rows: the endpoint is the **target** (`TargetAddress`, `TargetTypeName`).
- Incoming rows: the endpoint is the **source** (`SourceAddress`, `SourceTypeName`); root rows (source address `0`) expose `CanNavigate = false`.
- Displays: field name (or "array element" for `ArrayElement`, or the root kind name for roots), the kind label (`Field` / `Array element` / `Static field` / `GC handle`), mono address (or "root" for roots), and the endpoint type name.

`SnapshotViewModel` gains an `ObjectReferences` property and two commands (**Show Outgoing References**, **Show Incoming References**) that accept an instance row or a reference row as the parameter, resolve the endpoint object, and drive `ObjectReferences.ShowAsync` with the requested direction. Disposal cancels the references pane.

`StartViewModel` and `App` gain the `IObjectReferenceService` dependency (`ClrMdObjectReferenceService`), passed through to every `SnapshotViewModel`.

### References pane UI

The instances pane becomes a horizontal master-detail split:

- Left: the existing instances list (unchanged behavior) with a context menu on each row (**Show Outgoing References**, **Show Incoming References**).
- Right: a bordered references pane separated by a vertical native `GridSplitter` (user-resizable, no decorative motion).
- Pane header: **REFERENCES** section label, the inspected object's type name and address, the direction (Outgoing / Incoming), and a summary with an inline indeterminate progress bar while loading.
- Column headers: Field, Kind, Address, Type.
- States:
  - idle — *Select an object and choose Show Outgoing or Show Incoming References* (secondary text);
  - loading — indeterminate progress bar plus skeleton rows;
  - error — the existing error surface style;
  - empty — *No references* with a direction-specific hint;
  - table — virtualized `ListBox` (field name primary, kind secondary, address mono, type name secondary), rows selectable, double-click or the context-menu actions navigate to the object at the other end; root rows render "root" instead of an address and disable navigation.
- All interactive controls keep native keyboard behavior and `AutomationProperties.Name`.

## Data Flow

1. The user loads a snapshot, selects a type, and right-clicks an instance row, choosing Show Outgoing References (or Show Incoming References).
2. `SnapshotViewModel` resolves the endpoint and calls `ObjectReferences.ShowAsync(snapshot, objectInfo, direction)`.
3. `ClrMdObjectReferenceService` re-opens the dump and enumerates references off the UI thread; incoming additionally scans the whole heap and the root set.
4. `ObjectReferencesViewModel` publishes the rows through the dispatcher; the virtualized list renders only visible rows.
5. Selecting a reference row and choosing an action (or double-clicking) navigates to the object at the other end, replacing the inspected object and reloading in the requested direction.

## Error Handling

- Load failures are nonfatal, shown inside the references pane, and recoverable by navigating elsewhere.
- Cancellation (rapid navigation, clearing the selection, or closing the snapshot) never appears as an error.
- A stale load that completes after a newer navigation started is discarded by the version guard.
- Disposal cancels any in-flight reference load before returning.

## Testing Strategy

- Analysis unit tests stub `IHeapDumpSourceFactory` (the same pattern as the loader tests): outgoing field/array-element mapping with names and target type names, empty and unknown-address cases, incoming heap-object matches, incoming root rows (Handle / StaticField with source address 0), ordering, cancellation during enumeration, non-walkable heap, path and address validation, and dump disposal.
- Analysis acceptance test captures a live process, loads the snapshot, then asserts outgoing references from a `List<byte[]>` instance reach `byte[]` targets as array elements, and incoming references to a `byte[]` instance come back through that list.
- Application unit tests use a stub reference service:
  - row formatting (field name, kind label, mono address, type name, root rows, navigation availability);
  - loading state, success population, error state, empty state, idle state, summary, direction switching;
  - rapid navigation: the newer selection wins, a stale load is discarded;
  - `SnapshotViewModel` routes the two actions to the references pane and clears/disposes it;
  - `StartViewModel` composition passes the service through and keeps Open Dump disabled without it.
