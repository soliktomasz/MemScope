# Task 14 — Dominators and Retained Size — Plan

Issue #13. Branch `13-task-14-dominators-and-retained-size` already exists.

## Steps

1. **Contracts** — add `DominatorInfo` and `TypeRetainedSize` records under `src/MemoryProfiler.Contracts/Heap/`; extend the serialization round-trip theory in `ContractSerializationTests`.
2. **Analysis service** — add `src/MemoryProfiler.Analysis/Dominators/IDominatorTreeService.cs`, `DominatorAnalysisResult.cs`, `DominatorTreeService.cs`: reference-graph build, root reachability, CHK dominators, retained sizes, per-type aggregation, per-snapshot cache, `IProgress<double>` reporting, cancellation.
3. **Analysis unit tests** — `tests/MemoryProfiler.Analysis.Tests/Dominators/DominatorTreeServiceTests.cs` with a stub `IHeapDumpSource`: diamond dominance, cycles, garbage exclusion, same-type double-count exclusion, ordering, cache hit, progress, cancellation, validation.
4. **Acceptance test** — `tests/MemoryProfiler.Analysis.Tests/Dominators/DominatorTreeServiceAcceptanceTests.cs` in the `"Live diagnostics"` collection asserting the `List<byte[]>` owner dominates the chunk graph.
5. **Type browser** — `TypeRowViewModel` mutable retained size; `TypeBrowserViewModel.SetRetainedSizes` in place (filters/sort survive).
6. **Snapshot view model + composition** — background dominator phase in `SnapshotViewModel` (progress/status/error state, cancellation on dispose/new load); `StartViewModel` + `App` wire `DominatorTreeService`.
7. **UI** — thin progress strip over the type table while retained sizes compute.
8. **App tests** — type browser retained-size updates, snapshot view-model dominator phase (success/failure/cancel/progress), start view-model gating.
9. **Verify** — build solution, run full test suite, review diff, commit as `feat: calculate dominators and retained memory`.

## Test plan

- `dotnet test tests/MemoryProfiler.Analysis.Tests` (unit + acceptance).
- `dotnet test tests/MemoryProfiler.App.Tests`.
- `dotnet test tests/MemoryProfiler.Contracts.Tests`.
- `dotnet build MemoryProfiler.sln` clean.
- Full suite `dotnet test MemoryProfiler.sln` and re-verify test counts from AGENTS.md.
