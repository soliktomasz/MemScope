# Type Browser Design

## Context

Issue #9 adds the first snapshot analysis surface to MemScope: opening a memory dump and browsing the managed heap grouped by type. Issue #8 delivered the ClrMD loader (`IHeapSnapshotLoader`, `ClrMdHeapSnapshotLoader`) and the `HeapSnapshot` / `HeapTypeInfo` model in the analysis project. This issue brings that data into the application as **Snapshot → Types**, with sorting, namespace search, assembly filter, minimum-size filter, and UI virtualization.

Retained size is intentionally displayed as unavailable (`N/A`) until Task 14 computes dominators.

## Design Read

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like developer-tool language, leaning on the existing Avalonia FluentTheme and semantic tokens.

- `DESIGN_VARIANCE: 4`
- `MOTION_INTENSITY: 2`
- `VISUAL_DENSITY: 8`

The type browser is a data table, which the frontend design skill explicitly places outside its web-page scope. The applicable principles are token consistency, accessibility, complete states, density (compact rows, mono numerals), restrained motion (static transitions only), copy audit (plain labels, no em-dashes), keyboard preservation, and use of native Avalonia controls.

## Scope

### In scope

- Snapshot analysis view with a virtualized type table.
- Columns: Type, Assembly, Count, Shallow Size, Retained Size (unavailable until Task 14).
- Column sorting with direction toggle and sort descriptions.
- Namespace search (substring over the fully qualified type name).
- Assembly filter (distinct assemblies, alphabetical, plus *All assemblies*).
- Minimum-size filter (bytes or `KB`/`MB`/`GB`/`TB` suffix, invalid input treated as no filter).
- Loading, error, empty, no-match, and selected states.
- Open Dump entry point on the start screen.
- Analyze entry point for a dump captured in a live session.
- Unit tests for browser behavior, snapshot load states, and navigation.

### Out of scope

- Object instances (Task 11), GC roots (Task 13), retained size (Task 14).
- Snapshot comparison (Task 15) and capture history.
- Persisted recent sessions.

## Application Architecture

### Snapshot analysis view

Create:

```text
src/MemoryProfiler.App/ViewModels/
  SnapshotViewModel.cs
  Types/
    TypeBrowserViewModel.cs
    TypeRowViewModel.cs
    SizeParsing.cs

src/MemoryProfiler.App/Views/
  SnapshotView.axaml
  SnapshotView.axaml.cs

src/MemoryProfiler.App/Services/
  IDumpFilePicker.cs
  AvaloniaDumpFilePicker.cs
```

`SnapshotViewModel` owns one analysis session. It takes `IHeapSnapshotLoader` and `IUiDispatcher`, plus an optional close callback. `LoadAsync` clears prior state, publishes loading through the dispatcher, awaits the loader (which already runs off the UI thread), then publishes the loaded `HeapSnapshot` into `TypeBrowserViewModel`. Failures become an inline error state; cancellation (user close or disposal) clears loading without an error. A linked cancellation source keeps disposal and close responsive during long heap walks.

`TypeBrowserViewModel` holds all `HeapTypeInfo` rows once and exposes a filtered, sorted `ReadOnlyObservableCollection<TypeRowViewModel>` for the UI. Every filter change rebuilds the collection, and the ListBox uses a `VirtualizingStackPanel`, so a snapshot with millions of objects (or tens of thousands of types) never materializes a control per row.

Behaviors:

- Default sort: shallow size descending (matches the loader ordering).
- Clicking the active column toggles direction; clicking another column sorts ascending first.
- Sorting is stable with a type-name tiebreaker; retained-size sorting keeps `N/A` rows last.
- Search matches a case-insensitive substring of the fully qualified type name, which covers namespace search (`MyCompany.Cache` immediately narrows the table).
- The assembly filter list is distinct and alphabetical, with `Unknown assembly` as the fallback label for missing names.
- The minimum-size filter parses a plain number or a number with a `B`/`KB`/`MB`/`GB`/`TB` suffix; blank or invalid input means no filter.
- Selection survives re-sorts and is cleared when the selected row is filtered out.

### Navigation

`StartViewModel` gains `OpenDumpCommand`, a `Snapshot` property, and `IsSnapshotVisible`. The start screen's **Open Dump** button is enabled when a loader and file picker are wired; it opens the native file picker (`IDumpFilePicker`, backed by `StorageProvider`), then navigates to the Snapshot view and loads the dump. Picker failures surface as an inline, retryable banner on the start screen; load failures surface inside the Snapshot view with a Close path back to the start screen.

`LiveSessionViewModel` gains an `analyzeSnapshot` callback and an **Analyze** action next to the captured snapshot path. Invoking it opens the Snapshot view for the captured dump while the live session keeps running in the background; closing the snapshot returns to the live session. A failed analysis therefore never loses the live diagnostics session. Analyze is available only after a successful capture and is disabled while capturing.

`App` composes `ClrMdHeapSnapshotLoader` and `AvaloniaDumpFilePicker` into `StartViewModel`.

## Snapshot UI

The Snapshot view is a dense native surface:

- Header: **Snapshot** title, process description, **Close**.
- Metadata strip: runtime, captured-at time, object count, heap size, source path (trimmed with full path tooltip).
- Toolbar: search box (watermark *Search type or namespace*), assembly combo, minimum-size box (watermark *e.g. 100 KB*), and an *N of M types* counter.
- Table: sortable column headers with `↑` / `↓` indicators and sort tooltips, followed by a virtualized list. Numbers use the mono data style; the retained-size column shows `N/A` in the secondary color until Task 14.
- States: indeterminate progress bar with skeleton rows while analyzing, an error surface on failure, an empty state when the dump has no managed types, a distinct no-match state when filters exclude everything, and the table otherwise.
- No decorative motion is added; state transitions are static and honor reduced-motion preferences. All interactive controls keep native keyboard behavior and automation names.

## Data Flow

1. The user selects **Open Dump** (start screen) or **Analyze** (live session after capture).
2. `StartViewModel` opens the file picker or receives the captured path.
3. A `SnapshotViewModel` is created, published as the active view, and `LoadAsync` runs the loader off the UI thread with cancellation.
4. The loader returns `HeapSnapshot`; `TypeBrowserViewModel.SetTypes` builds rows, assembly filters, and the default sort.
5. Filter or sort changes rebuild the filtered collection; the virtualized list renders only visible rows.

## Error Handling

- Picker failures are nonfatal and shown as an inline start-screen banner.
- Load failures are nonfatal, shown inside the Snapshot view, and dismissible via Close.
- Cancellation (closing the snapshot or disposing the app) never appears as an error.
- Disposal cancels any in-flight analysis before returning.

## Testing Strategy

Application unit tests use stub loaders, pickers, and sessions to verify:

- default ordering, sort toggles, headers and descriptions, and stable tie-breakers;
- search, assembly filter, minimum-size filter (bytes and suffixes), and combined filters;
- no-match and empty states, and summary text;
- row formatting (count, shallow size, retained `N/A`), including culture-independent assertions;
- selection survival across re-sorts and clearing when filtered out;
- snapshot load success, loading state, failure, cancellation, close, and disposal;
- Open Dump enablement, picker cancellation, navigation, picker failure, and load failure;
- the Analyze action after a successful capture, including its failure path.
