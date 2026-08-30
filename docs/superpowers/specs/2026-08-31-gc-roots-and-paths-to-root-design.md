# GC Roots and Paths to Root Design

## Context

Issue #12 adds the fourth snapshot analysis surface to MemScope: from any heap object the user can ask **why it is alive** — which GC root keeps it alive and through which chain of objects. Issue #10 delivered instance loading, issue #11 delivered incoming/outgoing references; this issue adds **Snapshot → Types → Instances → Path to Root** with the same discipline: diagnostics behind interfaces, expensive walks off the UI thread, cancellation, and virtualized rendering.

The display contract from the issue:

```text
GC Root
 └─ MyApp.Program._cache
     └─ MemoryCache
         └─ Dictionary
             └─ CacheEntry
                 └─ CustomerDto
```

Each hop shows the field name when available and the type name of the object at the other end, so the user can follow the reference chain from the root down to the retained object. The user must be able to select any object in the path and inspect it (show its outgoing/incoming references).

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The path-to-root pane is a data table/tree, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency (reuse the `App*` semantic brushes and corner radius), accessibility (automation names, native keyboard behavior, virtualized list), complete states (idle, loading, error, empty, table), density (compact mono rows with depth indentation), restrained motion (static transitions only, honor reduced motion), copy audit (plain labels, no decorative punctuation), and native Avalonia controls only.

## Scope

### In scope

- `Analysis/Roots/IGcRootService` with `FindRootsAsync(HeapSnapshot, ulong objectAddress, CancellationToken)` returning `IReadOnlyList<GcRootInfo>`, implemented by `GcRootService`.
- Root kinds supported: static references, GC handles, thread stacks (where the runtime exposes them), and finalizer roots — everything `ClrHeap.EnumerateRoots()` yields, mapped to human-readable kind labels (reusing the existing `RootKindLabel`).
- Static retention paths are made explicit: static and thread-static field values are resolved up front (`Type.field` names via `EnumerateTypesWithStaticFields` + value matching, guarded per field) and merged into the root set as `StaticVar`/`ThreadStaticVar` roots when the dump's root enumeration omitted them — on .NET Core, mutable statics are frequently absent from dump root sets, so without this merge static retention chains would be invisible.
- Path discovery: for every root whose referenced object can reach the target, a chain of `ObjectReference` hops from the root down to the object, found by breadth-first search with a shared "dead" visited set (nodes proven not to reach the target are never re-explored by later roots) and a depth limit. Traversal state (visited parents + edges) lives and dies with each root's search, so reference lists are never retained for the whole dump; the dead set keeps the union of unsuccessful searches bounded to roughly one heap walk.
- `GcRootInfo` (contract) gains an optional `Path` (the hop chain) so the UI can render the tree without a second heap walk; `RootAddress` is the root's referenced object, `ObjectAddress` the queried target.
- Snapshot view gains a **Path to Root** pane as a third column beside instances and references: a context-menu action (**Show Path to Root**) on an instance row, a reference row, or a path row opens it; selecting a path row and choosing Outgoing/Incoming (or double-clicking) inspects the object at that hop through the existing references pane.
- Unit tests for the service, the row formatting, the pane state machine, snapshot wiring, and composition; one acceptance test that captures a live process and asserts the chain keeping a deliberately retained chunk alive is discovered, ending at the chunk.

### Out of scope

- Retained size (Task 14), snapshot comparison (Task 15).
- Reference graph visualization beyond the single-path tree; path filtering/sorting controls.
- Live (non-dump) root discovery; static field value inspection.

## Application Architecture

### Contracts

`GcRootInfo` gains an optional hop chain (positional default keeps the serialization test compiling; JSON round-trip still preserves equality):

```csharp
public sealed record GcRootInfo(
    ulong RootAddress,
    ulong ObjectAddress,
    string Kind,
    string? Name,
    IReadOnlyList<ObjectReference>? Path = null);
```

Semantics: `RootAddress` is the address of the heap object the root directly references (0 for roots whose object is absent — those are skipped by the service anyway), `ObjectAddress` is the queried object, `Kind`/`Name` identify the root (e.g. `Static field` / `MyApp.Program._cache`), and `Path` is the chain of references from the root's object down to the queried object: `Path[0].SourceAddress == RootAddress`, `Path[^1].TargetAddress == ObjectAddress`, `null`/empty when the root references the object directly. Each hop carries field name (`Name`), `Kind` (`Field` / `ArrayElement`), and source/target type names for rendering.

### Analysis

Create:

```text
src/MemoryProfiler.Analysis/Roots/
  IGcRootService.cs
  GcRootService.cs
```

`IGcRootService` is the diagnostics-facing seam, exactly as the issue specifies:

```csharp
public interface IGcRootService
{
    Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);
}
```

`GcRootService` mirrors `ClrMdObjectReferenceService`:

- Reuses the internal `IHeapDumpSourceFactory` / `IHeapDumpSource` abstraction so unit tests stub the same way the loader, repository, and reference-service tests do.
- The dump source seam gains `EnumerateRoots()` returning an internal `ClrRootData` record (object address, object type name, `ClrRootKind`, root name), implemented against `ClrHeap.EnumerateRoots()`; roots with a null/invalid/free object or address 0 are skipped.
- `FindRootsAsync` validates the snapshot and address, then runs the search inside `Task.Run` (off the caller thread). A heap that cannot be walked fails with `InvalidDataException`, same as the loader.
- Search: for each root, a breadth-first search from the root's object through `EnumerateOutgoingReferences` (the existing seam member; each address is enumerated at most once per search, and traversal state is released when the search returns). The first time the target is discovered the path is reconstructed via parent pointers; roots that exhaust their reachable component mark every visited address as **dead**, and later roots skip dead addresses — the union of unsuccessful searches is therefore bounded by one heap walk. Successful searches share nothing (a node a successful search explored can still be a valid bridge for another root). A depth limit (`MaxPathHops = 500`) prevents unbounded memory; reaching it aborts that root's search without marking anything dead. Cancellation is checked per address and inside the static-field map walk. Results are ordered by path length, then root name, then kind.

### Application

Create:

```text
src/MemoryProfiler.App/ViewModels/Objects/
  GcRootRowViewModel.cs
  GcRootsViewModel.cs
```

`GcRootsViewModel` owns the path pane state machine, mirroring `ObjectReferencesViewModel`:

- `ShowAsync(HeapSnapshot snapshot, string objectTypeName, ulong objectAddress)` — cancels any in-flight load, publishes the loading state through `IUiDispatcher`, awaits the service (which runs off the UI thread), then publishes the rows. A version counter plus per-load cancellation guarantee a stale (superseded) selection can never overwrite a newer one.
- `ClearAsync()` cancels in-flight loads and returns the pane to the idle state; `DisposeAsync` cancels and releases the load tokens.
- Exposes `Rows` (read-only observable collection of flattened `GcRootRowViewModel`), `ObjectTypeName`, `AddressDisplay`, `SummaryDisplay` ("N paths to root"), and the state flags `ShowIdle`, `ShowLoading`, `ShowError`, `ShowEmpty`, `ShowTable`.

`GcRootRowViewModel` formats one node of the flattened tree and exposes the navigable endpoint:

- Each found path flattens to: a root row (depth 0 — kind label + root name, endpoint = the root's referenced object) followed by one row per hop (depth 1..n — field name or "array element", kind label, mono address, endpoint type name); the last hop row is the queried target (`IsTarget`).
- A root that references the object directly contributes a root row plus a synthesized target row carrying the inspected object's type name.
- All object rows expose `EndpointAddress` / `EndpointTypeName` and `CanNavigate = true`; depth is rendered as indentation so the tree reads top-down without custom panels.

`SnapshotViewModel` gains a `GcRoots` property and a **Show Path to Root** command that accepts an instance row, a reference row, or a path row, resolves the endpoint object, and drives `GcRoots.ShowAsync`. The existing Outgoing/Incoming actions on a path row (context menu + double-click) inspect the object at that hop through the references pane. Disposal cancels the path pane.

`StartViewModel` and `App` gain the `IGcRootService` dependency (`GcRootService`), passed through to every `SnapshotViewModel`; Open Dump stays disabled without it.

### Path to Root pane UI

The bottom master-detail row becomes a three-column split:

- Left: the existing instances list (unchanged) with a context menu gaining **Show Path to Root**.
- Middle: the existing references pane (unchanged) with a context menu gaining **Show Path to Root**.
- Right: a bordered **PATH TO ROOT** pane separated by a vertical native `GridSplitter` (user-resizable, no decorative motion). Header shows the section label, the inspected object's type name and address, and a summary with an inline indeterminate progress bar while loading.
- Column headers: Depth-independent flat rows — the path list renders one `ListBox` with indented rows (field name primary, kind secondary, address mono, type name secondary); root rows render "root" styling and the kind label.
- States:
  - idle — *Select an object and choose Show Path to Root* (secondary text);
  - loading — indeterminate progress bar plus skeleton rows;
  - error — the existing error surface style;
  - empty — *No path to a GC root* with a hint that the object is unreachable from any root;
  - table — virtualized `ListBox`, rows selectable, double-click or the context-menu actions inspect the object at that hop.
- All interactive controls keep native keyboard behavior and `AutomationProperties.Name`.

## Data Flow

1. The user loads a snapshot, selects a type, and right-clicks an instance row, choosing Show Path to Root.
2. `SnapshotViewModel` resolves the endpoint and calls `GcRoots.ShowAsync(snapshot, typeName, address)`.
3. `GcRootService` re-opens the dump, enumerates the root set, and runs the BFS search off the UI thread.
4. `GcRootsViewModel` flattens the found paths into depth-indented rows and publishes them through the dispatcher; the virtualized list renders only visible rows.
5. Selecting a path row and choosing Outgoing/Incoming (or double-clicking) inspects the object at that hop in the references pane; choosing Show Path to Root on a path row re-searches from that hop.

## Error Handling

- Load failures are nonfatal, shown inside the path pane, and recoverable by navigating elsewhere.
- Cancellation (rapid navigation, clearing the selection, or closing the snapshot) never appears as an error.
- A stale load that completes after a newer navigation started is discarded by the version guard.
- Disposal cancels any in-flight path search before returning.

## Testing Strategy

- Analysis unit tests stub `IHeapDumpSourceFactory` (the same pattern as the loader tests): direct-root path (empty hop chain), one-hop and multi-hop chains with field names and type names, multiple roots all returned and ordered, no reachable root (empty), cycle termination, dead-set sharing (a node explored by a failed search is not re-enumerated for a later root), depth-limit abort, cancellation during enumeration, non-walkable heap, path and address validation, and dump disposal.
- Analysis acceptance test captures a live process, loads the snapshot, then asserts the deliberately retained 64 KiB chunk inside the target's `List<byte[]>` yields at least one path whose final hop targets the chunk and whose head is a root with a human-readable kind.
- Application unit tests use a stub root service: row flattening and formatting (root row, hop rows, target row, depths, addresses, type names), loading/success/error/empty/idle states and summary, rapid navigation staleness, `SnapshotViewModel` routing of the action, and `StartViewModel` composition keeping Open Dump disabled without the service.
- Contracts serialization test gains a `GcRootInfo` with a path and asserts JSON round-trip equality.
