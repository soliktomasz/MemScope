# Snapshot Comparison Design

## Context

Issue #14 (Task 15) adds snapshot comparison to MemScope: given two heap snapshots captured from the same process at different times, show per-type memory deltas so a leak between A and B is immediately visible. Issue #13 delivered dominators and per-type retained sizes; this task reuses that machinery to make the retained column of the comparison meaningful.

The issue contract:

```csharp
public interface ISnapshotComparisonService
{
    SnapshotComparison Compare(
        HeapSnapshot before,
        HeapSnapshot after);
}

public sealed record TypeMemoryDelta(
    string TypeName,
    long CountBefore,
    long CountAfter,
    long CountDelta,
    long SizeBefore,
    long SizeAfter,
    long SizeDelta,
    long? RetainedSizeDelta);
```

The UI sketch from the issue is a delta table — `Type | Count Δ | Size Δ | Retained Δ` — sorted with the biggest growth first, with four filters: **Growing only**, **New types**, **Disappeared types**, **Minimum delta**.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The comparison surface is a data table plus a pick/load/progress flow, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency (reuse the existing `App*` semantic brushes and `AppCornerRadius`), accessibility (automation names, native keyboard behavior, virtualized list, focus preserved), complete states (choose → loading → error → empty → table; every state has a home), density (compact mono rows, thin 2 px progress bar, tight right-aligned numeric columns), restrained motion (no animations, honor reduced motion), copy audit (plain labels: "Choose before snapshot", "Comparing snapshots…", no decorative punctuation), and native Avalonia controls only. No new web stack, no new design system, no marketing-page patterns.

## Scope

### In scope

- `Analysis/Comparison/ISnapshotComparisonService` + `SnapshotComparisonService`: merge two `HeapSnapshot`s by type name and emit `TypeMemoryDelta` rows (count/size deltas always; retained deltas when both snapshots carry retained sizes).
- Contracts gain `TypeMemoryDelta` (per the issue). `SnapshotComparison` is the service result container in `Analysis/Comparison`.
- Comparison view (`ComparisonViewModel` + `ComparisonTableViewModel` + `TypeDeltaRowViewModel` + `ComparisonView`): pick a before and after dump, load both off the UI thread, optionally enrich both with dominator retained sizes (reusing the per-snapshot cache), compare, and show the delta table with the four filters and sign-aware sorting.
- `StartViewModel` gains a "Compare Snapshots" entry point (mutually exclusive with live session / snapshot views), and `App` wires `SnapshotComparisonService`.
- Acceptance test: a controlled leak introduced between A and B appears near the top of the comparison results (the target's `byte[]` chunks dominate the delta table).
- Docs: this spec plus a plan.

### Out of scope

- Live (non-dump) comparison; per-object (instance-level) deltas; export of comparison results; multi-snapshot (3+) trending; GC-triggered or time-triggered automatic comparison.

## Comparison service

`SnapshotComparisonService.Compare(before, after)` is a pure, CPU-cheap in-memory merge of the two snapshots' type lists (thousands of entries), so the interface is synchronous exactly as the issue specifies. The expensive parts — loading the dumps and running dominator analysis — stay off the UI thread in the view model.

Merge semantics:

- Types are keyed by `TypeName` (`StringComparer.Ordinal`). A type present in only one snapshot contributes `0` counts/sizes for the missing side, so new types surface as positive deltas and disappeared types as negative ones.
- `CountBefore`/`CountAfter` come from `HeapTypeInfo.ObjectCount`; `SizeBefore`/`SizeAfter` from `ShallowSize` (clamped from `ulong` to `long` — heap sizes can never reach `long.MaxValue`, but the cast must not wrap).
- `CountDelta = CountAfter - CountBefore`, `SizeDelta = SizeAfter - SizeBefore` (checked).
- `RetainedSizeDelta`: `null` when either side's `HeapTypeInfo.RetainedSize` is `null` (dominators not yet computed / unavailable); otherwise the signed difference, clamped to `long` range.
- Default order: `SizeDelta` descending (biggest growth first), then `CountDelta` descending, then `TypeName` ascending (Ordinal) — the UI can re-sort.

```csharp
// Contracts/Heap
public sealed record TypeMemoryDelta(
    string TypeName,
    long CountBefore,
    long CountAfter,
    long CountDelta,
    long SizeBefore,
    long SizeAfter,
    long SizeDelta,
    long? RetainedSizeDelta);

// Analysis/Comparison
public interface ISnapshotComparisonService
{
    SnapshotComparison Compare(HeapSnapshot before, HeapSnapshot after);
}

public sealed record SnapshotComparison(IReadOnlyList<TypeMemoryDelta> Deltas);
```

`Compare` validates both arguments (`ArgumentNullException.ThrowIfNull`) and tolerates empty type lists.

## Application architecture

### Comparison view model (`ViewModels/Comparison/ComparisonViewModel`)

Dependencies: `IHeapSnapshotLoader`, `ISnapshotComparisonService`, `IDumpFilePicker`, `IUiDispatcher`, optional `IDominatorTreeService?`, optional close callback. Mirrors `SnapshotViewModel`'s discipline: all awaits `ConfigureAwait(false)`, UI publishes route through `IUiDispatcher`, a version counter + cancellation guarantee a superseded comparison never publishes, and disposal cancels in-flight work.

Flow:

1. **Choose** — `PickBeforeCommand` / `PickAfterCommand` ask the picker for a path. When a path is chosen and the other side is already set, comparison starts automatically (immediate feedback); a `CompareCommand` re-runs/retries (enabled when both paths are set and nothing is running).
2. **Load** — `_loader.LoadAsync` for before then after (each off the UI thread, cancellable). Status: "Loading before snapshot…" / "Loading after snapshot…"; determinate progress milestones.
3. **Retained sizes** — when a dominator service is present, run `ComputeDominatorsAsync` on both snapshots (off the UI thread, cancellable, per-snapshot cache reused), report combined progress, and enrich the snapshot copies: `HeapTypeInfo with { RetainedSize = perMethodTable[methodTable] }`. A dominator failure is **non-fatal**: the comparison completes with `RetainedSizeDelta = null` everywhere and a quiet note ("Retained sizes unavailable") — the same posture as the type browser's retained column.
4. **Compare** — `_comparisonService.Compare(before, after)` runs on the thread-pool continuation (never the UI thread), then publishes `Table.SetDeltas(...)`.

State: `BeforePath`/`AfterPath` (+ `HasBefore`/`HasAfter`), `IsLoading`, `Progress` (0–1), `StatusText`, `HasError`/`ErrorMessage`, `HasCompared` (to distinguish "choose a snapshot" from "no changes found"), and the table.

### Comparison table (`ComparisonTableViewModel`) and rows (`TypeDeltaRowViewModel`)

`TypeDeltaRowViewModel` wraps a `TypeMemoryDelta` and formats display strings with `CultureInfo.CurrentCulture`:

- `CountDeltaDisplay`: `+50,000` / `-1,234` / `0` (N0, sign only when positive).
- `SizeDeltaDisplay` / `RetainedDeltaDisplay`: `MetricFormatting.SignedBytes(long)` — signed byte formatting (new helper) — or `N/A` for an unavailable retained delta.
- `IsNewType` (`CountBefore == 0 && CountAfter > 0`) and `IsDisappearedType` (`CountAfter == 0`) back the filters.

`ComparisonTableViewModel` mirrors `TypeBrowserViewModel`'s structure: a `SetDeltas` that replaces rows and resets filters/sort to defaults (growing-size-descending), the four filters, sign-aware sortable column headers (`Type`, `Count Δ`, `Size Δ`, `Retained Δ`; retained sorts with nulls last), and a "X of Y types" summary. Filters:

- **Growing only** — `SizeDelta > 0`.
- **New types** — `CountBefore == 0`.
- **Disappeared types** — `CountAfter == 0`.
- **Minimum delta** — `|SizeDelta| >=` parsed bytes (reuses `SizeParsing`).

### Comparison view (`Views/ComparisonView.axaml`)

Same token language as `SnapshotView`: `dashboard-title`, `section-label`, `metric-value`, `mono-data`, `table-header` styles, `App*` brushes. Layout:

- Header: "Compare Snapshots" + process/help subtitle + Close button.
- Pickers row: two cards ("BEFORE" / "AFTER") each with the chosen path (`mono-data`, ellipsis-trimmed, tooltip) and a "Choose…" button.
- Status area: 2 px determinate `ProgressBar` + status label while loading/computing; error banner on failure (automation `LiveSetting="Assertive"`).
- Filters row: three `CheckBox`es ("Growing only", "New types", "Disappeared types"), a "MIN Δ" `TextBox` (watermark "e.g. 100 KB"), and the "X of Y types" summary right-aligned.
- Table: sortable header buttons + virtualized `ListBox` rows; empty/choose/error states with automation names.

The start screen (`MainWindow.axaml`) gains a "Compare Snapshots" action button, and a `ComparisonView` panel bound like `SnapshotView`.

### Composition

`StartViewModel` gains an optional trailing `ISnapshotComparisonService? comparisonService` (null keeps every existing call site compiling). `CompareSnapshotsCommand` is enabled when no view is open and loader + comparison service + picker are all present. `App.axaml.cs` wires `new SnapshotComparisonService()`.

## Acceptance

`SnapshotComparisonServiceAcceptanceTests` (in the `"Live diagnostics"` collection, `DisableParallelization` as before):

1. The target process starts in leak mode: it pre-allocates a fixed baseline chunk set, prints `READY`, then waits on stdin for `LEAK`.
2. Capture dump A (baseline only), send `LEAK`, wait for the leak loop to add ~60 chunks, capture dump B.
3. Load both snapshots, `Compare`.
4. Assert `System.Byte[]` is the top delta row (largest `SizeDelta`, > 1 MB of new chunks) with a matching positive `CountDelta`.

The existing target behavior (ring-buffer loop, no stdin) stays byte-for-byte, so all earlier acceptance tests are unaffected.

## Test plan

- `dotnet test tests/MemoryProfiler.Contracts.Tests` — `TypeMemoryDelta` round-trip.
- `dotnet test tests/MemoryProfiler.Analysis.Tests` — service unit tests (merge, new/disappeared, retained deltas, clamping, ordering, validation) + the leak acceptance test.
- `dotnet test tests/MemoryProfiler.App.Tests` — row formatting, table filters/sorting, comparison view model (load/error/cancel/enrich/versioning), start view model gating/visibility.
- `dotnet build MemoryProfiler.sln` clean; full `dotnet test MemoryProfiler.sln`; re-verify counts in AGENTS.md.
- Commit as `feat: compare memory snapshots`.
