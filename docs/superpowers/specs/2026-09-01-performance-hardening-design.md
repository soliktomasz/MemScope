# Performance Hardening Design

## Context

GitHub issue #18 (Task 19) adds deterministic workload targets and integration coverage that exercises MemScope under large and repeated workloads. The goal is to catch correctness regressions and sustained profiler-memory growth without relying on brittle wall-clock benchmarks or machine-specific absolute memory limits.

## Scope

Add six `net10.0` console applications under `tests/TestTargets/`:

- `StableMemoryTarget`: retains a fixed object graph and then remains stable.
- `GrowingMemoryTarget`: retains new objects continuously to model a leak.
- `HighAllocationTarget`: allocates rapidly while retaining only a bounded working set.
- `LargeObjectHeapTarget`: retains arrays above the 85,000-byte LOH threshold.
- `GcPressureTarget`: creates short-lived allocations and explicit collection pressure.
- `RootedObjectTarget`: retains a named object graph from a static root.

Each target writes `READY` only after its initial workload is established and remains alive until terminated. Targets accept small command-line parameters where tests need deterministic scale, rather than maintaining separate test-only variants.

In scope are integration tests for 100,000 objects, 1,000,000 objects, LOH discovery, rapid allocation, repeated snapshot loading/comparison, repeated GC-root analysis, repeated navigation, and profiler-process memory stability. UI redesign, production benchmarking infrastructure, throughput targets, and absolute resident-memory budgets are out of scope.

## Test infrastructure

A shared fixture starts a named target assembly from the test output, waits for `READY` with a bounded timeout, assigns a short diagnostics socket root, exposes its process ID, and always terminates the complete process tree. A shared dump helper captures heap dumps with cancellation and deletes temporary artifacts on disposal.

The target projects are included in `MemoryProfiler.sln` and copied to the consuming test output through project references. Acceptance tests remain in the existing non-parallel `"Live diagnostics"` collection because they mutate `TMPDIR` and start real processes.

## Coverage

- Heap loading proves exact target marker types are present with at least 100,000 and 1,000,000 instances.
- LOH coverage uses retained arrays of at least 100,000 bytes and verifies their generation through the object repository.
- Live diagnostics observes allocation/GC activity from `HighAllocationTarget` and `GcPressureTarget` without losing session cancellation or shutdown behavior.
- Repeated snapshot coverage captures and loads a bounded sequence from `StableMemoryTarget`; comparison remains stable and loaded snapshots become collectible after references are released.
- Repeated GC-root coverage queries the same rooted marker multiple times and verifies well-formed root paths without accumulating retained analysis state.
- Repeated navigation performs a large deterministic back/forward cycle and proves history remains correct.

Large-count tests use compact marker objects and conservative timeouts. They assert functional outcomes rather than elapsed time so slower CI machines remain valid.

## Profiler memory stability

Memory assertions measure the current test-host process, which owns the MemScope analysis objects. Each scenario performs a warm-up, forces a full collection, records managed heap size with `GC.GetTotalMemory(forceFullCollection: true)`, repeats the operation in bounded batches while releasing results, forces another full collection, and compares retained managed memory.

The assertion detects sustained growth: the final retained-memory increase must stay within a small fixed noise allowance plus a fraction of the workload's peak materialized data. It does not impose an absolute process-memory ceiling and does not sample the target process. Tests also use weak references where practical to prove snapshots/results are not accidentally held alive.

## Error handling and cancellation

All process startup, dump capture, loading, and root-analysis operations use bounded cancellation tokens. Startup failures include target stderr. Cleanup is best-effort and must preserve the original failure. Production diagnostics and analysis APIs remain behind their existing interfaces, and expensive work continues on background tasks.

## Verification

Run focused tests during TDD, then `dotnet build MemoryProfiler.sln` and `dotnet test MemoryProfiler.sln`. The final commit message is `test: add profiler workload targets`.
