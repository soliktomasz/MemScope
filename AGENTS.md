# AGENTS.md

MemScope — cross-platform desktop app (Avalonia) that monitors managed memory in running .NET processes via EventPipe and analyzes captured memory dumps offline with ClrMD.

## Project

- Stack: .NET 10 (net10.0), C# with `<Nullable>enable</Nullable>` + implicit usings, Avalonia 12.1.1 (FluentTheme, MVVM), xunit 2.9.3.
- Key packages: `Microsoft.Diagnostics.NETCore.Client` + `Microsoft.Diagnostics.Tracing.TraceEvent` (live diagnostics), `Microsoft.Diagnostics.Runtime` (ClrMD, dump analysis).
- Entry point: `src/MemoryProfiler.App/Program.cs` → `App.axaml.cs` (composition root wires concrete services into `StartViewModel`).
- App name derives from "Memory Profiler"; solution is `MemoryProfiler.sln`.

## Commands

```bash
dotnet build MemoryProfiler.sln          # build everything
dotnet test MemoryProfiler.sln           # full suite (acceptance tests spawn real target processes)
dotnet run --project src/MemoryProfiler.App   # launch the app
```

## Architecture

- `src/MemoryProfiler.App` — Avalonia desktop UI. `Views/` (.axaml), `ViewModels/` (MVVM), `Services/` (Avalonia pickers). References all other projects.
- `src/MemoryProfiler.Contracts` — shared DTOs only, zero dependencies (`Heap/`, `Live/`, `Processes/`). Other projects reference this.
- `src/MemoryProfiler.Analysis` — offline dump analysis. `Loading/` (`IHeapSnapshotLoader`, `ClrMdHeapSnapshotLoader`, `HeapSnapshot`), `Objects/` (`IHeapObjectRepository`, `ClrMdHeapObjectRepository`). Depends on Contracts + ClrMD.
- `src/MemoryProfiler.Diagnostics` — live diagnostics. `Processes/` (discovery), `Sessions/` (EventPipe session, `MemoryMetricsAccumulator`, `GcCorrelator`), `Dumps/` (`DumpCaptureService`). Depends on Contracts + NETCore.Client + TraceEvent.
- `src/MemoryProfiler.Storage` — empty skeleton (csproj only, no source yet).

Tests mirror src under `tests/`: `MemoryProfiler.App.Tests` (view-model tests), `MemoryProfiler.Analysis.Tests` (loader/repository + acceptance), `MemoryProfiler.Contracts.Tests` (serialization), `MemoryProfiler.Diagnostics.Tests` (includes `LiveDiagnosticsTarget/` console helper app), `MemoryProfiler.Storage.Tests` (skeleton).

## Conventions

- File-scoped namespaces, `sealed` classes, primary constructors, collection expressions (`[]`). No external MVVM framework — `ViewModelBase.SetProperty`, `RelayCommand`/`AsyncCommand` (in `ViewModels/Commands.cs`).
- Guard clauses: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`. Use `checked` for size/count accumulation.
- Cancellation: thread `CancellationToken` everywhere, check `ThrowIfCancellationRequested`, run CPU-bound work via `Task.Run` off the UI thread, guard superseded async loads with a version counter. UI marshaling goes through `UiDispatcher` (`AvaloniaUiDispatcher` in app, `ImmediateUiDispatcher` in tests).
- Heap walking (loader + object repository): skip objects where `!IsValid`, `IsFree`, `MethodTable == 0`, or blank `TypeName`; order types by `ShallowSize` desc then name (Ordinal).
- Tests: xunit `[Fact]`/`[Theory]`, classes named `<Type>Tests`. Acceptance tests that launch real processes live in the `"Live diagnostics"` collection (`DisableParallelization = true` — they mutate TMPDIR for the diagnostics socket) and use `LiveTargetFixture`.
- Sorting/filtering uses `StringComparer.Ordinal` / `OrdinalIgnoreCase`; UI counts/sizes formatted with `CultureInfo.CurrentCulture`.

## Workflow

- Task-driven: one branch per task named `<pr-num>-task-<task-num>-<slug>` (e.g. `11-task-12-object-references`), merged to `main` via PR with a Summary + Test Plan body (created with `gh pr create`).
- Design-first: each task gets a spec under `docs/superpowers/specs/YYYY-MM-DD-<name>-design.md` and a plan under `docs/superpowers/plans/`.
- `reasonix.toml` (gitignored) holds per-task sandbox permissions; throwaway probe targets live under `/private/tmp`.

## Notes

- Full suite: 219 tests (113 app, 8 contracts, 32 analysis, 66 diagnostics) as of branch `11-task-12-object-references` — re-verify counts after changes.
