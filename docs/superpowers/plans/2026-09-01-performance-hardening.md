# Performance Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic .NET workload targets and integration tests that exercise MemScope at scale and detect retained profiler-memory growth.

**Architecture:** Six isolated console targets expose deterministic heaps through a common `READY` protocol. A shared test-infrastructure library owns process, dump, and managed-memory measurement lifecycle; existing analysis, diagnostics, and navigation APIs are exercised without production API changes.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, EventPipe diagnostics client, ClrMD.

**Spec:** `docs/superpowers/specs/2026-09-01-performance-hardening-design.md`

## Global Constraints

- Keep diagnostics APIs behind existing interfaces.
- Keep expensive work off the UI thread and preserve cancellation.
- Serialize tests that mutate `TMPDIR` or launch diagnostic targets.
- Assert retained growth after warm-up, never an absolute process-memory ceiling.
- Final implementation commit: `test: add profiler workload targets`.

---

### Task 1: Workload targets and lifecycle harness

**Files:**
- Create: `tests/TestTargets/Directory.Build.props`
- Create: `tests/TestTargets/{StableMemoryTarget,GrowingMemoryTarget,HighAllocationTarget,LargeObjectHeapTarget,GcPressureTarget,RootedObjectTarget}/*.csproj`
- Create: `tests/TestTargets/{StableMemoryTarget,GrowingMemoryTarget,HighAllocationTarget,LargeObjectHeapTarget,GcPressureTarget,RootedObjectTarget}/Program.cs`
- Create: `tests/MemoryProfiler.TestInfrastructure/MemoryProfiler.TestInfrastructure.csproj`
- Create: `tests/MemoryProfiler.TestInfrastructure/WorkloadTargetFixture.cs`
- Create: `tests/MemoryProfiler.Analysis.Tests/Performance/WorkloadTargetSmokeTests.cs`
- Modify: `MemoryProfiler.sln`
- Modify: `tests/MemoryProfiler.Analysis.Tests/MemoryProfiler.Analysis.Tests.csproj`
- Modify: `tests/MemoryProfiler.Diagnostics.Tests/MemoryProfiler.Diagnostics.Tests.csproj`

**Interfaces:**
- Produces: `WorkloadTargetFixture.StartAsync(string assemblyName, IEnumerable<string>? arguments, CancellationToken)` with `ProcessId`, `SocketRoot`, and async disposal.

- [ ] **Step 1: Write the failing target smoke tests**

Add a theory that starts each assembly, waits for `READY`, and asserts the process remains alive. Add a fixture failure test using a nonexistent assembly and assert the error names it. This catches a missing target, a target that signals before initialization, and leaked child processes.

```csharp
[Theory]
[InlineData("StableMemoryTarget")]
[InlineData("GrowingMemoryTarget")]
[InlineData("HighAllocationTarget")]
[InlineData("LargeObjectHeapTarget")]
[InlineData("GcPressureTarget")]
[InlineData("RootedObjectTarget")]
public async Task TargetSignalsReadyAndRemainsAlive(string assembly)
{
    await using var target = await WorkloadTargetFixture.StartAsync(assembly);
    Assert.True(target.ProcessId > 0);
    Assert.False(target.HasExited);
}
```

- [ ] **Step 2: Verify RED**

Run `rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter WorkloadTargetSmokeTests`. Expected: compile failure because the infrastructure and targets do not exist.

- [ ] **Step 3: Add the minimal target implementations**

Use a shared `Directory.Build.props` for `OutputType=Exe`, `TargetFramework=net10.0`, nullable, and implicit usings. At this stage every target does only enough to satisfy the startup contract; workload behavior is introduced test-first in Tasks 2 and 3:

```csharp
Console.WriteLine("READY");
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
```

- [ ] **Step 4: Implement shared lifecycle helpers**

The process fixture redirects stdout/stderr, sets the short socket root, applies arguments, waits at most 30 seconds for `READY`, and kills the process tree on all failure/disposal paths.

- [ ] **Step 5: Verify GREEN**

Run `rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter WorkloadTargetSmokeTests`. Expected: all six cases pass.

---

### Task 2: Large heap, LOH, leak, and repeated-analysis acceptance tests

**Files:**
- Create: `tests/MemoryProfiler.Analysis.Tests/Performance/HeapWorkloadAcceptanceTests.cs`
- Create: `tests/MemoryProfiler.Analysis.Tests/Performance/RepeatedAnalysisAcceptanceTests.cs`
- Create: `tests/MemoryProfiler.TestInfrastructure/HeapDumpFixture.cs`
- Create: `tests/MemoryProfiler.TestInfrastructure/ProfilerMemoryProbe.cs`

**Interfaces:**
- Consumes: `WorkloadTargetFixture` from Task 1 plus `ClrMdHeapSnapshotLoader`, `ClrMdHeapObjectRepository`, `SnapshotComparisonService`, and `GcRootService`.
- Produces: `HeapDumpFixture.CaptureAsync(int processId, string socketRoot, CancellationToken)` with `Path` and async disposal.
- Produces: `ProfilerMemoryProbe.MeasureRetainedBytes()` and `IsGrowthWithin(long before, long after, long peakBytes, long fixedAllowanceBytes)`.
- Produces: acceptance coverage only; no production API.

- [ ] **Step 1: Write failing large-object and LOH tests**

Start `StableMemoryTarget` with `100000` and `1000000`, capture/load each dump, and assert `StableMarker.ObjectCount >= requested`. Start `LargeObjectHeapTarget`, enumerate `System.Byte[]`, and assert at least 32 instances of size `>= 100_000` report generation label `"LOH"`.

- [ ] **Step 2: Verify RED**

Run `rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter HeapWorkloadAcceptanceTests`. Expected: failures until target marker types/arguments and LOH assertions are wired correctly.

- [ ] **Step 3: Implement the dump/memory helpers and deterministic target contracts**

Reference `Microsoft.Diagnostics.NETCore.Client` from the infrastructure project. The dump fixture temporarily sets `TMPDIR`, calls `DiagnosticsClient.WriteDumpAsync(DumpType.WithHeap, ...)`, restores the ambient value, and deletes the dump. The memory probe forces full GC twice before returning `GC.GetTotalMemory(true)`; `IsGrowthWithin` returns whether growth is at most the fixed allowance plus 10% of the measured peak.

Use public marker type names (`StableMemoryTarget.StableMarker`, `GrowingMemoryTarget.LeakPayload`, `RootedObjectTarget.RootNode`) so ClrMD assertions are exact. Implement stable retained markers from the count argument, 32 retained 100,000-byte arrays, a static 64-node rooted chain, and a growing local list that retains one 1 MiB `LeakPayload` every 500 ms. Keep retained values live through static holders or `GC.KeepAlive`; avoid timing-based exact counts except for the deliberate leak.

- [ ] **Step 4: Write failing repeated-work tests**

Cover these behaviors:

```csharp
// Growing target: later snapshot must be led by LeakPayload/byte[] growth.
Assert.True(comparison.Deltas.Single(x => x.TypeName.Contains("LeakPayload")).CountDelta > 0);

// Stable target: capture/load three snapshots, release them, and bound retained growth.
Assert.True(ProfilerMemoryProbe.IsGrowthWithin(before, after, peak, 8 * 1024 * 1024));

// Root target: locate RootNode and repeat FindRootsAsync five times.
Assert.All(results, roots => Assert.Contains(roots, root => root.Path is { Count: > 0 }));
Assert.True(ProfilerMemoryProbe.IsGrowthWithin(before, after, peak, 8 * 1024 * 1024));
```

Warm up once before measuring, release all result references between measured batches, and use weak references to prove snapshots/results can be collected.

- [ ] **Step 5: Verify RED then GREEN**

Run the filtered repeated-analysis tests before any target/harness adjustment and confirm the expected behavioral failure. Make only the minimal harness/target corrections, rerun, and expect all filtered tests to pass.

---

### Task 3: Allocation/GC pressure and navigation endurance

**Files:**
- Create: `tests/MemoryProfiler.Diagnostics.Tests/Sessions/WorkloadDiagnosticsAcceptanceTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/Navigation/InvestigationNavigationServiceTests.cs`

**Interfaces:**
- Consumes: `LiveDiagnosticsSessionFactory`, `MemoryMetrics`, `WorkloadTargetFixture`, and `InvestigationNavigationService`.
- Produces: diagnostics and navigation performance-regression coverage only.

- [ ] **Step 1: Write failing EventPipe workload tests**

For `HighAllocationTarget`, observe until `AllocationRateBytesPerSecond > 0`. For `GcPressureTarget`, observe until the sum of generation collection counters is positive. Give each loop a 45-second cancellation token and assert on real emitted metrics.

- [ ] **Step 2: Verify RED then implement the minimal workloads**

Run `rtk dotnet test tests/MemoryProfiler.Diagnostics.Tests --filter WorkloadDiagnosticsAcceptanceTests`; confirm both metric assertions fail against the idle target skeletons. Implement `HighAllocationTarget` with continuous 4 KiB allocation and a 256-item bounded ring; implement `GcPressureTarget` with batches of short-lived 1 KiB arrays followed by generation-0 collections and `Task.Yield()`. Rerun and require both tests green.

- [ ] **Step 3: Write the repeated-navigation test**

Warm up, navigate through 20,000 distinct `TypeLocation` values, traverse all the way back and forward, and assert the endpoint, event count, and retained-memory growth below 8 MiB after reset and full collection. This catches incorrect history and history retained after `Reset`.

- [ ] **Step 4: Verify navigation RED then GREEN**

Run `rtk dotnet test tests/MemoryProfiler.App.Tests --filter RepeatedNavigation`. If existing production behavior already passes, confirm the test detects the intended mutation by temporarily omitting `_back.Clear()` from `Reset`, observe failure, restore it, and rerun green.

---

### Task 4: Final integration and commit

**Files:**
- Modify: `AGENTS.md` only if the verified full-suite count changed and the project convention requires the current count.

- [ ] **Step 1: Format and inspect**

Run `rtk dotnet format MemoryProfiler.sln --verify-no-changes` after formatting changed files, `rtk git diff --check`, and inspect `rtk git diff --stat` plus the focused diff.

- [ ] **Step 2: Verify the complete solution**

Run `rtk dotnet build MemoryProfiler.sln` and `rtk dotnet test MemoryProfiler.sln`. Require exit code 0 with no failed tests.

- [ ] **Step 3: Commit**

Stage only the task files and commit with `test: add profiler workload targets`. Do not push or create a pull request unless explicitly requested.
