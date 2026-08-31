# Navigation History Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add back/forward investigation history to snapshot analysis.

**Architecture:** A UI-independent navigation service stores immutable investigation locations. `SnapshotViewModel` records and restores those locations while existing child view models retain responsibility for cancellable data loading.

**Tech Stack:** .NET 10, C#, Avalonia 12, xUnit

---

## Task 1: Navigation model and service

**Files:**
- Create: `src/MemoryProfiler.App/Navigation/InvestigationLocation.cs`
- Create: `src/MemoryProfiler.App/Navigation/InvestigationNavigationService.cs`
- Test: `tests/MemoryProfiler.App.Tests/Navigation/InvestigationNavigationServiceTests.cs`

1. Write tests for initial state, navigation, back/forward traversal, duplicate locations, and clearing forward history.
2. Run the focused tests and confirm they fail because the navigation types do not exist.
3. Implement immutable location records and the minimal stack service with `CurrentLocationChanged`.
4. Run the focused tests and confirm they pass.

## Task 2: Snapshot integration

**Files:**
- Modify: `src/MemoryProfiler.App/ViewModels/SnapshotViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/Types/TypeBrowserViewModel.cs`
- Test: `tests/MemoryProfiler.App.Tests/ViewModels/SnapshotViewModelTests.cs`

1. Write tests proving navigation actions create history and Back/Forward restore references and type selection.
2. Run the focused tests and confirm the expected failures.
3. Add commands, replay handling, and a method to select a type by method table.
4. Run the focused tests and confirm they pass.

## Task 3: Toolbar and verification

**Files:**
- Modify: `src/MemoryProfiler.App/Views/SnapshotView.axaml`

1. Add compact accessible Back and Forward buttons to the snapshot header.
2. Build the solution to verify compiled bindings and XAML.
3. Run the full test suite.
4. Commit with `feat: add investigation navigation history`.
