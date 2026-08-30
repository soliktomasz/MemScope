# Task 15 — Snapshot Comparison — Plan

Issue #14. Branch `14-task-15-snapshot-comparison` already exists.

## Steps

1. **Design docs** — this plan + `docs/superpowers/specs/2026-09-02-snapshot-comparison-design.md` (design read per the mandated `design-taste-frontend` skill: `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 2`, `VISUAL_DENSITY: 8`).
2. **Contracts** — add `TypeMemoryDelta` under `src/MemoryProfiler.Contracts/Heap/`; extend the serialization round-trip theory in `ContractSerializationTests`.
3. **Analysis service** — add `src/MemoryProfiler.Analysis/Comparison/ISnapshotComparisonService.cs`, `SnapshotComparison.cs`, `SnapshotComparisonService.cs`: name-keyed merge, count/size deltas, optional retained deltas (null when either side lacks them), `ulong`→`long` clamping, checked arithmetic, biggest-growth-first default order.
4. **Analysis unit tests** — `tests/MemoryProfiler.Analysis.Tests/Comparison/SnapshotComparisonServiceTests.cs`: merge semantics, new/disappeared types, retained delta present/absent, sign correctness, clamping, default ordering + tie-breaks, validation, empty snapshots.
5. **Acceptance** — extend `LiveDiagnosticsTarget` with a `--leak` mode (baseline pre-allocated, `READY`, then stdin `LEAK` starts an unbounded chunk list); extend `LiveTargetFixture` with leak-phase start; add `tests/MemoryProfiler.Analysis.Tests/Comparison/SnapshotComparisonServiceAcceptanceTests.cs` asserting the controlled `byte[]` leak leads the delta table.
6. **View models** — `MetricFormatting.SignedBytes(long)`; `ViewModels/Comparison/TypeDeltaRowViewModel.cs`, `ComparisonTableViewModel.cs` (filters + sortable headers), `ComparisonViewModel.cs` (choose/load/enrich/compare, versioned cancellation, non-fatal retained failure).
7. **UI + composition** — `Views/ComparisonView.axaml` (+ code-behind); `StartViewModel` compare entry point + visibility; `MainWindow.axaml` start-screen button + panel; `App.axaml.cs` wires `SnapshotComparisonService`.
8. **App tests** — `TypeDeltaRowViewModelTests`, `ComparisonTableViewModelTests`, `ComparisonViewModelTests`, StartViewModel comparison gating/visibility additions.
9. **Verify** — build solution, run full test suite, review diff, update AGENTS.md test counts, commit as `feat: compare memory snapshots`.

## Test plan

- `dotnet test tests/MemoryProfiler.Contracts.Tests`.
- `dotnet test tests/MemoryProfiler.Analysis.Tests` (unit + acceptance; `"Live diagnostics"` collection).
- `dotnet test tests/MemoryProfiler.App.Tests`.
- `dotnet build MemoryProfiler.sln` clean.
- Full suite `dotnet test MemoryProfiler.sln`; re-verify counts from AGENTS.md and update the note.
