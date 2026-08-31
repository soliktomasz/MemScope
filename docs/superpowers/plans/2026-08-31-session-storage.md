# Session Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist local profiler history and restore actionable Recent Sessions after restart.

**Architecture:** The Storage project owns immutable session metadata plus a cancellable atomic JSON repository. Existing app view models report successful capture, snapshot, and comparison milestones to `StartViewModel`, which serializes catalog updates and projects them into compact recent-session rows.

**Tech Stack:** .NET 10, C#, System.Text.Json, Avalonia 12.1.1, xUnit 2.9.3

**Spec:** `docs/superpowers/specs/2026-08-31-session-storage-design.md`

## Global Constraints

- Store metadata only; never copy dump contents.
- Store at most 20 entries in each catalog collection.
- Use `OrdinalIgnoreCase` path equality on Windows and `Ordinal` on macOS and Linux.
- Keep persistence and analysis work off the UI thread and propagate cancellation.
- Persistence failures must not terminate diagnostics or analysis.
- Preserve the existing Avalonia FluentTheme, keyboard behavior, semantic resources, and dense layout.

---

### Task 1: Session catalog behavior

**Files:**
- Create: `src/MemoryProfiler.Storage/Storage/SessionCatalog.cs`
- Create: `tests/MemoryProfiler.Storage.Tests/Storage/SessionCatalogTests.cs`

**Interfaces:**
- Produces: `RecentDump`, `RecentInvestigation`, `ComparisonPair`, and immutable `SessionCatalog` with `WithRecentDump`, `WithRecentInvestigation`, and `WithComparison` methods.

- [ ] **Step 1: Write failing catalog tests**

Add literal fixtures proving a newer matching path replaces older metadata, comparisons preserve before/after order, results sort newest first, and a 21st entry evicts the oldest. Each test names the mutation it catches, for example:

```csharp
var catalog = SessionCatalog.Empty
    .WithRecentInvestigation(new("/dumps/a.dmp", "Api", Older))
    .WithRecentInvestigation(new("/dumps/a.dmp", "Api", Newer));

var item = Assert.Single(catalog.RecentInvestigations);
Assert.Equal(Newer, item.LastOpenedAt);
```

- [ ] **Step 2: Verify RED**

Run: `rtk dotnet test tests/MemoryProfiler.Storage.Tests --filter FullyQualifiedName~SessionCatalogTests`

Expected: compilation fails because the catalog types do not exist.

- [ ] **Step 3: Implement the immutable catalog**

Use these public shapes:

```csharp
public sealed record RecentDump(
    string Path,
    string? ProcessName,
    int? ProcessId,
    string? RuntimeVersion,
    DateTimeOffset CapturedAt,
    long? ObjectCount,
    ulong? HeapSize);

public sealed record RecentInvestigation(
    string Path,
    string? ProcessName,
    DateTimeOffset LastOpenedAt);

public sealed record ComparisonPair(
    string BeforePath,
    string AfterPath,
    DateTimeOffset LastComparedAt);

public sealed record SessionCatalog(
    IReadOnlyList<RecentDump> RecentDumps,
    IReadOnlyList<RecentInvestigation> RecentInvestigations,
    IReadOnlyList<ComparisonPair> ComparisonPairs);
```

Guard paths, use platform-aware equality, return new collections, and cap each at 20.

- [ ] **Step 4: Verify GREEN**

Run the focused command from Step 2 and expect all catalog tests to pass.

### Task 2: Atomic JSON repository

**Files:**
- Create: `src/MemoryProfiler.Storage/Storage/ISessionRepository.cs`
- Create: `src/MemoryProfiler.Storage/Storage/JsonSessionRepository.cs`
- Create: `tests/MemoryProfiler.Storage.Tests/Storage/JsonSessionRepositoryTests.cs`

**Interfaces:**
- Consumes: `SessionCatalog` from Task 1.
- Produces:

```csharp
public interface ISessionRepository
{
    Task<SessionCatalog> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SessionCatalog catalog, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 1: Write failing repository tests**

Use a per-test temporary directory and the real repository. Prove a missing file returns `SessionCatalog.Empty`, a saved catalog round-trips all fields, corrupt JSON throws `JsonException`, a pre-cancelled operation throws `OperationCanceledException`, and replacing a catalog leaves valid current JSON with no temporary file.

- [ ] **Step 2: Verify RED**

Run: `rtk dotnet test tests/MemoryProfiler.Storage.Tests --filter FullyQualifiedName~JsonSessionRepositoryTests`

Expected: compilation fails because repository types do not exist.

- [ ] **Step 3: Implement repository IO**

Provide `JsonSessionRepository(string? catalogPath = null)`. The default path is:

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MemScope",
    "sessions.json");
```

Use asynchronous `FileStream`, `JsonSerializer.SerializeAsync` and `DeserializeAsync`, a same-directory temporary file, and `File.Move(tempPath, catalogPath, overwrite: true)`. Delete only the repository-owned temporary file in `finally`; validate a deserialized catalog before returning it.

- [ ] **Step 4: Verify GREEN**

Run: `rtk dotnet test tests/MemoryProfiler.Storage.Tests`

Expected: all Storage tests pass.

### Task 3: Record successful profiler activity

**Files:**
- Modify: `src/MemoryProfiler.App/ViewModels/LiveSessionViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/SnapshotViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/ComparisonViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/StartViewModel.cs`
- Create: `src/MemoryProfiler.App/ViewModels/RecentSessionRowViewModel.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/LiveSessionViewModelTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/Comparison/ComparisonViewModelTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/StartViewModelTests.cs`

**Interfaces:**
- Consumes: `ISessionRepository` and catalog records.
- Produces: `StartViewModel.InitializeAsync`, recent-session state, and reopen commands.
- Adds optional callbacks `Func<string, Task>? snapshotCaptured` and `Func<string, string, Task>? comparisonCompleted` without changing default behavior.
- Adds `HeapSnapshotInfo? SnapshotInfo` and `ComparisonViewModel.LoadAsync(string beforePath, string afterPath)`.

- [ ] **Step 1: Write the capture callback test and verify RED**

Assert a successful real `CaptureSnapshotAsync` invokes the callback with the captured path exactly once, while cancellation and capture failure do not. Run the focused `LiveSessionViewModelTests` filter and confirm compilation fails on the missing callback parameter.

- [ ] **Step 2: Implement the capture callback and verify GREEN**

Invoke the callback only after `DumpCaptureResult` has been published successfully. Keep callback failures outside the capture error path because `StartViewModel` absorbs repository errors. Re-run the focused tests.

- [ ] **Step 3: Write comparison completion and predefined-path tests, then verify RED**

Assert `LoadAsync("before.dmp", "after.dmp")` runs a successful comparison and invokes the completion callback once; loader failure invokes it zero times. Run the focused comparison tests.

- [ ] **Step 4: Implement comparison completion and verify GREEN**

Set both paths through a public cancellable entry point, reuse `CompareAsync`, and invoke the callback only for the current successfully published comparison. Re-run the focused tests.

- [ ] **Step 5: Write StartViewModel persistence tests and verify RED**

Use an in-memory `ISessionRepository` double with complete catalog behavior. Prove initialization restores sorted rows, successful snapshot analysis enriches dump metadata and records an investigation, successful capture and comparison record their entries, save failures set storage error state without closing the active workflow, and row commands reopen snapshots or comparisons.

- [ ] **Step 6: Implement StartViewModel catalog coordination and verify GREEN**

Add a `SemaphoreSlim`-guarded mutation path:

```csharp
private async Task UpdateCatalogAsync(
    Func<SessionCatalog, SessionCatalog> update,
    CancellationToken cancellationToken = default);
```

Publish row changes through `IUiDispatcher`, expose loading/empty/error properties, and make row commands use the existing snapshot and comparison flows. Re-run focused StartViewModel tests.

### Task 4: Compose and render Recent Sessions

**Files:**
- Modify: `src/MemoryProfiler.App/App.axaml.cs`
- Modify: `src/MemoryProfiler.App/MainWindow.axaml`

**Interfaces:**
- Consumes: `JsonSessionRepository`, `StartViewModel.InitializeAsync`, and recent-session row properties.

- [ ] **Step 1: Wire the repository and startup load**

Construct `JsonSessionRepository`, pass it to `StartViewModel`, show the window, and start `InitializeAsync` without blocking the UI thread. All exceptions remain represented by view-model state.

- [ ] **Step 2: Replace the Recent Sessions placeholder**

Bind a compact `ItemsControl` to recent rows. Each row uses existing semantic brushes, exposes the full path in a tooltip, formats metadata in the view model, and provides a focused `Open` button with `AutomationProperties.Name`. Add explicit loading, empty, error, populated, and disabled visibility states with no animation.

- [ ] **Step 3: Run focused verification**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Storage.Tests
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "FullyQualifiedName~StartViewModelTests|FullyQualifiedName~LiveSessionViewModelTests|FullyQualifiedName~ComparisonViewModelTests"
rtk dotnet build MemoryProfiler.sln
```

Expected: all commands exit 0 with no failures and no warnings beyond the five established by the baseline run.

- [ ] **Step 4: Run full verification**

Run: `rtk dotnet test MemoryProfiler.sln`

Expected: all tests pass, including live diagnostics acceptance tests.

- [ ] **Step 5: Commit the task**

```bash
rtk git add src tests docs/superpowers/plans/2026-08-31-session-storage.md
rtk git commit -m "feat: persist profiler sessions"
```
