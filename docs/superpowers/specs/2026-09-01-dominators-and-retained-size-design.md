# Dominators and Retained Size Design

## Context

Issue #13 adds retained-memory analysis to MemScope: for every live heap object, which objects does it dominate, and how much memory would be freed if the object itself were collected. Issue #12 delivered paths to GC roots; this issue adds **Snapshot → Types → Retained Size** with the same discipline: diagnostics behind interfaces, expensive walks off the UI thread, cancellation, progress reporting, and a per-snapshot result cache.

The issue contract:

```csharp
public sealed record DominatorInfo(
    ulong ObjectAddress,
    string TypeName,
    ulong ShallowSize,
    ulong RetainedSize,
    long RetainedObjectCount);
```

The UI sketch from the issue is a table of dominant objects/types with shallow and retained columns — the type browser already renders exactly `Type | Count | Shallow Size | Retained Size` (the retained column shipped as `N/A` in issue #10 and was left for this task), so this task fills that column with real per-type retained sizes computed from the object-level dominator tree.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The retained-size surface is a data table plus a background-computation progress strip, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency (reuse the `App*` semantic brushes and corner radius), accessibility (automation names, native keyboard behavior, virtualized list), complete states (table stays usable while retained sizes compute; a quiet non-fatal status for computation failure), density (compact mono rows, thin 2 px progress bar), restrained motion (no animations beyond the existing indeterminate bars, honor reduced motion), copy audit (plain labels, no decorative punctuation), and native Avalonia controls only. No new web stack, no new design system, no marketing-page patterns.

## Scope

### In scope

- `Analysis/Dominators/IDominatorTreeService` + `DominatorTreeService` that build the heap reference graph, compute dominators, and derive per-object retained sizes and counts.
- Contracts gain `DominatorInfo` (per the issue) and `TypeRetainedSize` (per-type aggregation for the type browser column).
- Result caching per snapshot: the expensive computation runs once per distinct dump and is reused by later queries against the same snapshot.
- Progress reporting (`IProgress<double>`, 0.0–1.0 across the computation phases) and cancellation at every phase.
- Snapshot view: after a snapshot loads, retained sizes are computed off the UI thread in the background; the type browser's **Retained Size** column fills in as computation finishes (in place — filters, search, and sort survive), with a thin progress strip while computing.
- Unit tests for the service (stub dump source), the row/type-browser updates, the snapshot view-model state machine, and composition; one acceptance test that captures a live process and asserts the known owner of a large object graph (the target's `List<byte[]>`) appears as the dominant retained-memory object.

### Out of scope

- Snapshot comparison (Task 15); a dedicated per-object dominator pane beyond the type-level column; live (non-dump) retained analysis; memory-limit tuning of the internal graph beyond what correctness needs.

## Algorithm

### 1. Reference graph

One pass over `IHeapDumpSource.EnumerateObjects()` collects every valid, non-free object (skipping the same cases as the loader: `!IsValid`, `IsFree`, `MethodTable == 0`, blank type name) into `Dictionary<ulong, NodeData>` where `NodeData = (ulong Size, string TypeName, ulong MethodTable)`. A second concern in the same pass: each object's `EnumerateOutgoingReferences(address)` feeds a reverse-edge index `Dictionary<ulong, List<ulong>> predecessors` (source address → list of target addresses). Edges whose target is not a node (dangling references) are dropped after the pass.

For a heap of ~1M objects this is a few hundred MB of managed memory for the duration of the computation; that is the inherent cost of a whole-heap dominator analysis and is released when the computation returns (only the aggregated results are cached).

### 2. Reachability from roots

`EnumerateRoots` (which already merges unreported static/thread-static roots per issue #12) yields the root object addresses. A BFS from those roots through the outgoing edges computes:

- the **reachable set** (unreachable objects are garbage and excluded from the dominator tree),
- a **BFS order** and per-node **BFS depth** used by the dominator algorithm below.

### 3. Dominators — Cooper–Harvey–Kennedy

A synthetic start node connects to every reachable root (`idom[root] = start`). The classic iterative algorithm runs over the reachable subgraph in BFS order:

```text
idom[start] = start
changed = true
while changed:
    changed = false
    for n in reachable \ {start} in BFS order:
        new_idom = first predecessor of n with a defined idom
        for each other predecessor p of n:
            new_idom = intersect(p, new_idom)
        if idom[n] != new_idom:
            idom[n] = new_idom
            changed = true
```

`intersect(b1, b2)` walks both fingers up their `idom` chains using the BFS depth (a dominates b implies `depth(a) < depth(b)`, so the finger walk terminates at the common dominator). The synthetic start is never reported.

### 4. Retained sizes

With `idom` fixed, retained sizes are accumulated bottom-up in reverse BFS order (every parent precedes its children in the BFS order):

```text
retainedSize[n]  = shallowSize[n]  + Σ retainedSize[c]   over children c
retainedCount[n] = 1               + Σ retainedCount[c]  over children c
```

`DominatorInfo` is emitted for every reachable object, ordered by `RetainedSize` descending then type name (Ordinal).

### 5. Per-type retained sizes

A naive sum of per-object retained sizes double-counts nested same-type objects: if a `Node` dominates a `Node[]` that dominates another `Node`, the inner `Node`'s subtree lies inside the outer `Node`'s retained size too. Each object therefore contributes its retained size only when no same-type ancestor exists in the dominator tree — equivalently, an object's retained size covers all of its **nearest same-type descendants** (reached by traversing through different-type nodes, stopping beneath each same-type descendant), and those descendants are not counted again. One forward pass over the BFS order tracks whether a same-type ancestor exists:

```text
contribution(o) = retainedSize(o)  if no same-type ancestor of o exists in the dominator tree
typeRetained[type(o)] += contribution(o)
```

Types present in the full node set but with no reachable objects contribute `0`. `TypeRetainedSize(MethodTable, TypeName, RetainedSize)` is emitted for every type, so with a dominator service present every type row shows a value.

## Application Architecture

### Contracts (`MemoryProfiler.Contracts/Heap`)

```csharp
public sealed record DominatorInfo(
    ulong ObjectAddress,
    string TypeName,
    ulong ShallowSize,
    ulong RetainedSize,
    long RetainedObjectCount);

public sealed record TypeRetainedSize(
    ulong MethodTable,
    string TypeName,
    ulong RetainedSize);
```

Both are plain DTOs following `GcRootInfo`'s placement; JSON round-trip is covered by the serialization theory.

### Analysis (`MemoryProfiler.Analysis/Dominators`)

```csharp
public interface IDominatorTreeService
{
    Task<DominatorAnalysisResult> ComputeDominatorsAsync(
        HeapSnapshot snapshot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DominatorAnalysisResult(
    IReadOnlyList<DominatorInfo> Dominators,      // reachable objects, retained size desc
    IReadOnlyList<TypeRetainedSize> TypeRetainedSizes);
```

`DominatorTreeService` mirrors the other analysis services:

- Reuses the internal `IHeapDumpSourceFactory` / `IHeapDumpSource` seam (stubbed the same way in unit tests), runs inside `Task.Run`, validates snapshot/path, and fails with `InvalidDataException` when the heap cannot be walked.
- **Cache**: `ConditionalWeakTable<HeapSnapshot, DominatorAnalysisResult>` keyed on the snapshot instance, so a result lives exactly as long as the snapshot it was computed for: closing and releasing a snapshot makes its entry (and the `DominatorInfo` graph it holds) eligible for collection, and the cache cannot grow across the whole application session. A cache hit skips the heap entirely; a re-captured dump is a different snapshot instance and is recomputed.
- **Progress**: `IProgress<double>` reported as 0.0→1.0 across graph build (0.00–0.35), reachability (0.35–0.45), dominator iterations (0.45–0.90), and retained accumulation (0.90–1.00); throttled to periodic reports, `null` progress is a no-op.
- **Cancellation**: checked per object/node in every phase; the dump source is disposed on all paths.

### Application

`TypeRowViewModel` gains a settable retained size (`SetRetainedSize`) initialized from `HeapTypeInfo.RetainedSize`; `IsRetainedSizeAvailable` / `RetainedSizeDisplay` / the retained-size sort read the mutable value. `TypeBrowserViewModel.SetRetainedSizes(IReadOnlyList<TypeRetainedSize>)` updates rows in place (by method table) and re-sorts without resetting search/assembly/min-size filters or selection.

`SnapshotViewModel` gains the optional `IDominatorTreeService? dominatorService` (last constructor parameter; `null` keeps today's `N/A` behavior and leaves all existing call sites compiling). After a successful load it starts a background phase:

1. `ComputeDominatorsAsync(snapshot, progress, cancellationToken)` where `progress` publishes `RetainedSizeProgress` (0.0–1.0) through `IUiDispatcher`, and the token links to the snapshot's disposal cancellation.
2. On success: `Types.SetRetainedSizes(result.TypeRetainedSizes)` and the strip hides.
3. On failure: the snapshot stays fully usable; a non-fatal status message ("Retained sizes unavailable") is shown in the strip's place. On cancellation (new load / dispose): nothing is published.

New state: `IsComputingRetainedSizes`, `RetainedSizeProgress`, `RetainedSizeStatusText` (e.g. "Computing retained sizes… 42%").

`StartViewModel` and `App` gain the `IDominatorTreeService` dependency (`DominatorTreeService`); Open Dump stays disabled without it, matching the `IGcRootService` gate.

### Snapshot view UI

The type-browser table area gains a thin overlay strip at the top (visible only while retained sizes compute): a 2 px determinate `ProgressBar` bound to `RetainedSizeProgress` plus a small secondary status label, both with automation names. The existing table, loading, empty, and error states are untouched; the retained column already renders values or `N/A`.

### Acceptance

`DominatorTreeServiceAcceptanceTests` (in the `"Live diagnostics"` collection) captures the target process with `LiveTargetFixture`, loads the dump, and asserts:

- the top dominator (largest retained size) is the target's `System.Collections.Generic.List<System.Byte[]>` instance — the known owner of the chunk graph,
- its `RetainedSize` is at least the size of the captured chunk set (≥ 1 MB of 64 KiB chunks) and its `RetainedObjectCount` counts the list plus the chunks,
- the per-type result reports a large retained size for `System.Collections.Generic.List<System.Byte[]>`.
