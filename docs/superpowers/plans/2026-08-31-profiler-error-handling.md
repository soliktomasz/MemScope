# Profiler Error Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert expected profiler failures into consistent user-facing error states with expandable technical details.

**Architecture:** A presentation-only error model and context-aware factory live in the app project. Existing view models publish that model, while one native Avalonia control renders it everywhere.

**Tech Stack:** .NET 10, C#, Avalonia 12.1.1, xunit 2.9.3

**Spec:** `docs/superpowers/specs/2026-08-31-profiler-error-handling-design.md`

## Global Constraints

- Primary messages never expose raw exception text.
- Technical details retain exception diagnostics behind an expander.
- Long-running operations accept `CancellationToken`.
- No new package, motion, theme, accent, or shape system.

---

### Task 1: Typed error classification

**Files:**
- Create: `src/MemoryProfiler.App/Errors/ProfilerError.cs`
- Create: `src/MemoryProfiler.App/Errors/ProfilerErrorFactory.cs`
- Create: `tests/MemoryProfiler.App.Tests/Errors/ProfilerErrorFactoryTests.cs`

**Interfaces:**
- Produces: `ProfilerErrorKind`, `ProfilerError`, `ProfilerOperation`, and `ProfilerErrorFactory.Create(ProfilerOperation, Exception, int? processId = null)`.

- [x] Write parameterized tests that hand each required exception/context pair to `Create` and assert the literal category, title, safe explanation, and retained technical detail.
- [x] Run `rtk dotnet test tests/MemoryProfiler.App.Tests --filter FullyQualifiedName~ProfilerErrorFactoryTests` and verify the tests fail because the API is absent.
- [x] Implement the enum, immutable model, technical-detail formatter, context mapping, and disk-full HResult recognition.
- [x] Re-run the focused tests and verify they pass.

### Task 2: Publish errors from operations

**Files:**
- Modify: `src/MemoryProfiler.App/ViewModels/LiveSessionViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/SnapshotViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/ComparisonViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/ProcessPickerViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/StartViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/Objects/GcRootsViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/Objects/ObjectInstancesViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/Objects/ObjectReferencesViewModel.cs`
- Test: corresponding files under `tests/MemoryProfiler.App.Tests/ViewModels/`

**Interfaces:**
- Consumes: `ProfilerErrorFactory.Create(...)`.
- Produces: nullable `ProfilerError Error` properties and cancellation-aware async entry points.

- [x] Update focused view-model tests to assert safe primary copy, category, technical details, cancellation behavior, and non-throwing expected failures.
- [x] Run those tests and verify they fail against the string-only state.
- [x] Replace exception-message concatenation with typed errors and thread optional cancellation tokens through long-running entry points.
- [x] Re-run the focused view-model tests and verify they pass.

### Task 3: Reusable Avalonia error surface

**Files:**
- Create: `src/MemoryProfiler.App/Views/ErrorDetailsView.axaml`
- Create: `src/MemoryProfiler.App/Views/ErrorDetailsView.axaml.cs`
- Modify: `src/MemoryProfiler.App/MainWindow.axaml`
- Modify: `src/MemoryProfiler.App/Views/LiveSessionView.axaml`
- Modify: `src/MemoryProfiler.App/Views/SnapshotView.axaml`
- Modify: `src/MemoryProfiler.App/Views/ComparisonView.axaml`

**Interfaces:**
- Consumes: `ProfilerError` as `DataContext`.
- Produces: an assertive semantic error region and keyboard-operable technical-details expander.

- [x] Replace repeated error borders with `ErrorDetailsView`, retaining each existing visibility condition.
- [x] Run `rtk dotnet build MemoryProfiler.sln` and fix XAML binding or compile failures.
- [x] Run `rtk dotnet test MemoryProfiler.sln` and verify the complete suite passes.
- [x] Audit visible copy, focus behavior, semantic colors, light/dark resources, and technical-detail disclosure against the design.
- [x] Commit with `feat: harden profiler error handling`.
