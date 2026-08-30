# Object Instances Design

## Context

Issue #10 adds the second snapshot analysis surface to MemScope: selecting a type in the type browser (Task 10, issue #9) opens the individual heap object instances of that type. Issue #8 delivered the ClrMD loader and the `HeapSnapshot` / `HeapTypeInfo` model; issue #9 delivered **Snapshot → Types** with sorting, filtering, and UI virtualization. This issue adds **Snapshot → Types → Instances** with lazy loading: instances are fetched only when a type is selected, off the UI thread, with cancellation, and rendered through a virtualized list so types with hundreds of thousands of instances stay responsive.

The display contract from the issue:

```text
Address             Size       Generation
0x000001A832...     128 B      Gen2
0x000001A833...     128 B      Gen2
```

Instances are never preloaded for every type; the repository walks the heap only for the requested method table.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The instances pane is a data table, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency (reuse the `App*` semantic brushes and corner radius), accessibility (automation names, native keyboard behavior, virtualized list), complete states (idle, loading, error, empty, table), density (compact mono rows, right-aligned numerals), restrained motion (static transitions only, honor reduced motion), copy audit (plain labels, no decorative punctuation), and native Avalonia controls only.

## Scope

### In scope

- `Analysis/Objects/IHeapObjectRepository` with `GetInstancesAsync(HeapSnapshot, ulong methodTable, CancellationToken)`.
- ClrMD implementation that re-opens the dump from `snapshot.Info.Path`, walks the heap, filters to the requested method table, and returns `HeapObjectInfo` rows (address, size, generation) ordered by address.
- `HeapObjectInfo` gains a `Generation` label (`Gen0`, `Gen1`, `Gen2`, `LOH`, `Pinned`, `Frozen`, `Unknown`).
- Snapshot view gains a master-detail instances pane: selecting a type loads its instances lazily; deselecting returns to an idle state.
- Instances pane columns: Address, Size, Generation, with header, summary (count and total shallow size), and idle / loading / error / empty / table states.
- Grid splitter between the type table and the instances pane so the user controls the split.
- Unit tests for the repository, the instances view model, row formatting, snapshot wiring, and navigation composition; one acceptance test that captures a live process and asserts instance enumeration matches the type browser counts.

### Out of scope

- GC roots (Task 13), retained size (Task 14), reference graphs.
- Snapshot comparison (Task 15), capture history, persisted sessions.
- Instance filtering, sorting, or pagination beyond UI virtualization.

## Application Architecture

### Contracts

`HeapObjectInfo` gains a `Generation` label, matching the `GcRootInfo.Kind` string precedent in the contracts project:

```csharp
public sealed record HeapObjectInfo(
    ulong Address,
    ulong MethodTable,
    string TypeName,
    ulong Size,
    string Generation);
```

### Analysis

Create:

```text
src/MemoryProfiler.Analysis/Objects/
  IHeapObjectRepository.cs
  ClrMdHeapObjectRepository.cs
```

`IHeapObjectRepository` is the diagnostics-facing seam (behind an interface so the app can stub it in tests):

```csharp
public interface IHeapObjectRepository
{
    Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
        HeapSnapshot snapshot,
        ulong methodTable,
        CancellationToken cancellationToken = default);
}
```

`ClrMdHeapObjectRepository` mirrors `ClrMdHeapSnapshotLoader`:

- Reuses the internal `IHeapDumpSourceFactory` / `IHeapDumpSource` abstraction so unit tests stub the same way the loader tests do.
- `HeapObjectData` gains `ulong Address` and `Generation? Generation` (the ClrMD enum) with defaults, so existing call sites and stubs keep compiling; `ClrMdHeapDumpSource.EnumerateObjects` populates both (`ClrObject.Address`, `heap.GetSegmentByAddress(address)?.GetGeneration(address)`).
- `GetInstancesAsync` validates the snapshot and method table, then runs the walk inside `Task.Run` (off the caller thread, like the loader), checks cancellation per object, skips invalid/free/untyped entries, filters by method table, maps `Generation` to a label, and orders by address ascending so the list is stable and scannable.
- A heap that cannot be walked fails with `InvalidDataException`, same as the loader.

### Application

Create:

```text
src/MemoryProfiler.App/ViewModels/Objects/
  HeapObjectRowViewModel.cs
  ObjectInstancesViewModel.cs
```

`ObjectInstancesViewModel` owns the instances pane state machine:

- `ShowAsync(HeapSnapshot snapshot, HeapTypeInfo type)`: cancels any in-flight load, publishes the loading state through `IUiDispatcher`, awaits the repository (which runs off the UI thread), then publishes the rows. A version counter plus per-load cancellation guarantee a stale (superseded) selection can never overwrite a newer one.
- `ClearAsync()`: cancels in-flight loads and returns the pane to the idle state (deselection, filter clears selection, or a new snapshot loads).
- `DisposeAsync` cancels and releases the load tokens.
- Exposes `Instances` (read-only observable collection), `TypeName`, `SummaryDisplay` ("N instances · total size"), and the state flags `ShowIdle`, `ShowLoading`, `ShowError`, `ShowEmpty`, `ShowTable`.
- Failures become an inline error state; cancellation never appears as an error.

`SnapshotViewModel` gains an `ObjectInstances` property and subscribes to `TypeBrowserViewModel.SelectedType` changes: a selection with a loaded snapshot triggers `ShowAsync`, a cleared selection triggers `ClearAsync`. Disposal cancels the instances pane.

`StartViewModel` and `App` gain the `IHeapObjectRepository` dependency (`ClrMdHeapObjectRepository`), passed through to every `SnapshotViewModel`.

### Instances pane UI

The Snapshot view's table row becomes a vertical master-detail split:

- Top: the existing type table (unchanged behavior).
- Bottom: a bordered instances pane separated by a native `GridSplitter` (user-resizable, no decorative motion).
- Pane header: **INSTANCES** section label, the selected type name (trimmed, full name on hover), and a summary (instance count and total shallow size) with an inline indeterminate progress bar while loading.
- Column headers: Address, Size, Generation (numerals right-aligned, mono).
- States:
  - idle — *Select a type to view its instances* (secondary text);
  - loading — indeterminate progress bar plus skeleton rows;
  - error — the existing error surface style;
  - empty — *No instances found for this type* (defensive; a type row exists only when the dump walk counted it);
  - table — virtualized `ListBox` (address mono left, size mono right, generation secondary).
- All interactive controls keep native keyboard behavior and `AutomationProperties.Name`.

## Data Flow

1. The user loads a snapshot (existing flow) and selects a type row.
2. `SnapshotViewModel` observes the `SelectedType` change and calls `ObjectInstances.ShowAsync(snapshot, type)`.
3. `ClrMdHeapObjectRepository.GetInstancesAsync` re-opens the dump, walks the heap off the UI thread, filters by method table, and returns instances ordered by address.
4. `ObjectInstancesViewModel` publishes the rows through the dispatcher; the virtualized list renders only visible rows.
5. Deselecting the type (or filtering it out) cancels any in-flight load and returns the pane to idle.

## Error Handling

- Load failures are nonfatal, shown inside the instances pane, and recoverable by selecting another type.
- Cancellation (rapid selection change, deselection, or closing the snapshot) never appears as an error.
- A stale load that completes after a newer selection started is discarded by the version guard.
- Disposal cancels any in-flight instance load before returning.

## Testing Strategy

- Analysis unit tests stub `IHeapDumpSourceFactory` (the same pattern as the loader tests): method-table filtering, address/size/generation mapping for every generation label, sorting, invalid/free/untyped skipping, non-walkable heap, cancellation during enumeration, path and method-table validation, and dump disposal.
- Analysis acceptance test captures a live process, loads the snapshot, then asserts the repository returns exactly `type.ObjectCount` instances for `System.String` with non-zero addresses, valid sizes, and valid generation labels.
- Application unit tests use a stub repository:
  - row formatting (address `0x` + 12-digit hex, size, generation label);
  - loading state, success population, error state, empty state, idle state, summary;
  - rapid selection: the newer selection wins, a stale load is discarded;
  - deselection cancels and returns to idle;
  - `SnapshotViewModel` loads instances on selection and clears on deselection/disposal;
  - `StartViewModel` composition passes the repository through and keeps `Open Dump` disabled without it.
