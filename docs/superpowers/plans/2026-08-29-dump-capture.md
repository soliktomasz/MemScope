# Dump Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture a cancellable, heap-bearing dump from Live Session into a user-selected folder and prove that ClrMD can immediately open it.

**Architecture:** Keep dump generation behind `IDumpCaptureService`, with internal adapters around `DiagnosticsClient` and filesystem/process/time operations for deterministic behavior tests. Compose a native Avalonia folder picker and the capture service into `LiveSessionViewModel`, which owns capture lifecycle and exposes compact progress, success, cancellation, and nonfatal error states.

**Tech Stack:** .NET 10, C# 14, Avalonia 12.1.1 FluentTheme, Microsoft.Diagnostics.NETCore.Client 0.2.661903, Microsoft.Diagnostics.Runtime 4.0.732401 in acceptance tests, xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-08-29-dump-capture-design.md`

## Global Constraints

- Preserve the issue's exact public `IDumpCaptureService.CaptureAsync` signature.
- Use `DiagnosticsClient.WriteDumpAsync` with `DumpType.WithHeap` and forward cancellation.
- Do not add production ClrMD loading or enable the start-screen **Open Dump** action; those belong to issue #8.
- Keep expensive capture work off the UI thread and capture failures nonfatal to the EventPipe session.
- Follow the approved native Fluent design read with `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 2`, and `VISUAL_DENSITY: 8`.
- Use `rtk` for every shell command.
- Follow strict red, green, refactor cycles and create the requested final feature commit `feat: capture managed process dumps` after the complete verification gate.

---

### Task 1: Dump Capture Service

**Files:**
- Create: `src/MemoryProfiler.Diagnostics/Dumps/IDumpCaptureService.cs`
- Create: `src/MemoryProfiler.Diagnostics/Dumps/DumpCaptureService.cs`
- Create: `tests/MemoryProfiler.Diagnostics.Tests/Dumps/DumpCaptureServiceTests.cs`

**Interfaces:**
- Consumes: `DiagnosticsClient.WriteDumpAsync(DumpType, string, WriteDumpFlags, CancellationToken)`.
- Produces: `IDumpCaptureService.CaptureAsync(int, string, CancellationToken) : Task<string>`.
- Internal test seams: `IDumpWriter.WriteAsync(int, string, CancellationToken)` and `IDumpCaptureEnvironment` for local time, process name, directory creation, file existence, and deletion.

- [ ] **Step 1: Write failing service contract tests**

Add focused xUnit tests that independently derive and assert these observable behaviors:

```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
public async Task CaptureRejectsNonPositiveProcessIds(int processId)
{
    var service = new DumpCaptureService(new StubWriter(), StubEnvironment.Default);

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
        () => service.CaptureAsync(processId, Path.GetTempPath()));
}

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("   ")]
public async Task CaptureRejectsMissingDestination(string? destination)
{
    var service = new DumpCaptureService(new StubWriter(), StubEnvironment.Default);

    await Assert.ThrowsAsync<ArgumentException>(
        () => service.CaptureAsync(4217, destination!));
}

[Fact]
public async Task CaptureUsesSanitizedTimestampedFilenameAndReturnsFullPath()
{
    var environment = new StubEnvironment("My/Api", new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.FromHours(2)));
    var writer = new StubWriter();
    var service = new DumpCaptureService(writer, environment);

    var result = await service.CaptureAsync(4217, "/captures");

    Assert.Equal(Path.GetFullPath("/captures/My_Api-2026-08-28-162500.dmp"), result);
    Assert.Equal(result, writer.Path);
}

[Fact]
public async Task CaptureAddsNumericSuffixInsteadOfOverwriting()
{
    var environment = new StubEnvironment("MyApi", new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero));
    environment.ExistingPaths.UnionWith([
        Path.GetFullPath("/captures/MyApi-2026-08-28-162500.dmp"),
        Path.GetFullPath("/captures/MyApi-2026-08-28-162500-2.dmp")]);
    var service = new DumpCaptureService(new StubWriter(), environment);

    var result = await service.CaptureAsync(4217, "/captures");

    Assert.Equal(Path.GetFullPath("/captures/MyApi-2026-08-28-162500-3.dmp"), result);
}

[Fact]
public async Task CaptureFallsBackToProcessIdWhenNameHasNoUsableCharacters()
{
    var environment = new StubEnvironment("///", new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero));
    var service = new DumpCaptureService(new StubWriter(), environment);

    var result = await service.CaptureAsync(4217, "/captures");

    Assert.Equal("process-4217-2026-08-28-162500.dmp", Path.GetFileName(result));
}

[Fact]
public async Task CaptureDeletesIncompleteOutputAndPreservesWriterFailure()
{
    var failure = new IOException("capture failed");
    var environment = StubEnvironment.Default;
    var writer = new StubWriter((path, _) =>
    {
        environment.ExistingPaths.Add(path);
        return Task.FromException(failure);
    });
    var service = new DumpCaptureService(writer, environment);

    var thrown = await Assert.ThrowsAsync<IOException>(() => service.CaptureAsync(4217, "/captures"));

    Assert.Same(failure, thrown);
    Assert.Equal([writer.Path], environment.DeletedPaths);
}

[Fact]
public async Task CaptureDeletesIncompleteOutputAfterCancellation()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var environment = StubEnvironment.Default;
    var writer = new StubWriter((path, token) =>
    {
        environment.ExistingPaths.Add(path);
        return Task.FromCanceled(token);
    });
    var service = new DumpCaptureService(writer, environment);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => service.CaptureAsync(4217, "/captures", cancellation.Token));

    Assert.Equal([writer.Path], environment.DeletedPaths);
}

[Fact]
public async Task CleanupFailureDoesNotReplaceCaptureFailure()
{
    var failure = new IOException("capture failed");
    var environment = StubEnvironment.Default with { DeleteFailure = new UnauthorizedAccessException("delete failed") };
    var writer = new StubWriter((path, _) =>
    {
        environment.ExistingPaths.Add(path);
        return Task.FromException(failure);
    });
    var service = new DumpCaptureService(writer, environment);

    var thrown = await Assert.ThrowsAsync<IOException>(() => service.CaptureAsync(4217, "/captures"));

    Assert.Same(failure, thrown);
}

private sealed class StubWriter(
    Func<string, CancellationToken, Task>? write = null) : IDumpWriter
{
    public string Path { get; private set; } = string.Empty;

    public Task WriteAsync(int processId, string path, CancellationToken cancellationToken)
    {
        Assert.Equal(4217, processId);
        Path = path;
        return write?.Invoke(path, cancellationToken) ?? Task.CompletedTask;
    }
}

private sealed record StubEnvironment(
    string ProcessName,
    DateTimeOffset LocalNow) : IDumpCaptureEnvironment
{
    public static StubEnvironment Default => new(
        "MyApi",
        new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero));

    public Exception? DeleteFailure { get; init; }
    public HashSet<string> ExistingPaths { get; } = [];
    public List<string> DeletedPaths { get; } = [];

    public string GetProcessName(int processId) => ProcessName;
    public void CreateDirectory(string path) { }
    public bool FileExists(string path) => ExistingPaths.Contains(path);
    public void DeleteFile(string path)
    {
        DeletedPaths.Add(path);
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        ExistingPaths.Remove(path);
    }
}
```

The fake writer records the process ID, full path, and token but assertions remain on `DumpCaptureService` output and cleanup behavior, not merely on the fake's existence.

- [ ] **Step 2: Run the service tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Diagnostics.Tests/MemoryProfiler.Diagnostics.Tests.csproj --filter FullyQualifiedName~DumpCaptureServiceTests
```

Expected: compile failure because `MemoryProfiler.Diagnostics.Dumps` and its service do not exist.

- [ ] **Step 3: Implement the minimal diagnostics service**

Create the public interface exactly:

```csharp
namespace MemoryProfiler.Diagnostics.Dumps;

public interface IDumpCaptureService
{
    Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
```

In `DumpCaptureService.cs`, add a public default constructor and an internal constructor taking the two seams. Implement validation, `Path.GetFullPath`, directory creation, sanitization, timestamp formatting with invariant culture, an application-owned random temporary path, atomic no-overwrite moves with collision suffix retries, writer invocation, and best-effort cleanup of only the temporary file. The real writer must contain only the external call:

```csharp
var client = new DiagnosticsClient(processId);
await client.WriteDumpAsync(
    DumpType.WithHeap,
    path,
    WriteDumpFlags.None,
    cancellationToken).ConfigureAwait(false);
```

Use a `catch` block that saves the original exception, attempts deletion, and rethrows with `throw;` so cleanup cannot alter the failure.

- [ ] **Step 4: Run the service tests and verify GREEN**

Run the filtered command from Step 2.

Expected: all `DumpCaptureServiceTests` pass with zero warnings or failures.

- [ ] **Step 5: Refactor while green**

Keep filename building and cleanup in small private methods, avoid leaking internal seams into the public API, then rerun the filtered tests.

---

### Task 2: Native Destination Picker and Composition

**Files:**
- Create: `src/MemoryProfiler.App/Services/IDumpDestinationPicker.cs`
- Create: `src/MemoryProfiler.App/Services/AvaloniaDumpDestinationPicker.cs`
- Modify: `src/MemoryProfiler.App/App.axaml.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/StartViewModel.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/StartViewModelTests.cs`

**Interfaces:**
- Consumes: Avalonia `TopLevel.StorageProvider.OpenFolderPickerAsync(FolderPickerOpenOptions)` and `IStorageItem.Path.LocalPath`.
- Produces: `IDumpDestinationPicker.PickAsync() : Task<string?>`.
- Passes `IDumpCaptureService` and `IDumpDestinationPicker` from application composition through `StartViewModel` into `LiveSessionViewModel`.

- [ ] **Step 1: Write failing composition tests**

Extend `StartViewModelTests` with deterministic stubs and verify that starting a selected process creates a `LiveSessionViewModel` using the same picker and capture service:

```csharp
[Fact]
public async Task StartingLiveSessionSuppliesDumpCaptureDependencies()
{
    var discovery = StubDiscovery.Returning(new ProcessInfo(4217, "SampleService", "10.0.0"));
    using var processPicker = new ProcessPickerViewModel(discovery);
    var session = new BlockingLiveSession();
    var capture = new RecordingDumpCaptureService();
    var destinationPicker = new StubDumpDestinationPicker("/captures");
    await using var start = new StartViewModel(
        processPicker,
        new StubLiveSessionFactory(session),
        capture,
        destinationPicker,
        ImmediateUiDispatcher.Instance);
    await processPicker.RefreshAsync();
    processPicker.SelectedProcess = Assert.Single(processPicker.Processes);

    var liveRun = start.StartLiveSessionAsync();
    await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await start.LiveSession!.CaptureSnapshotAsync();

    Assert.Equal(4217, capture.ProcessId);
    Assert.Equal("/captures", capture.DestinationDirectory);
    await start.CloseLiveSessionAsync();
    await liveRun;
}

private sealed class StubDumpDestinationPicker(string? directory) : IDumpDestinationPicker
{
    public Task<string?> PickAsync() => Task.FromResult(directory);
}

private sealed class RecordingDumpCaptureService : IDumpCaptureService
{
    public int ProcessId { get; private set; }
    public string? DestinationDirectory { get; private set; }

    public Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ProcessId = processId;
        DestinationDirectory = destinationDirectory;
        return Task.FromResult(Path.Combine(destinationDirectory, "snapshot.dmp"));
    }
}

private sealed class BlockingLiveSession : ILiveDiagnosticsSession
{
    public int ProcessId => 4217;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    public async IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

This exercises the real composition path rather than checking private fields.

- [ ] **Step 2: Run the composition test and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests/MemoryProfiler.App.Tests.csproj --filter FullyQualifiedName~StartingLiveSessionSuppliesDumpCaptureDependencies
```

Expected: compile failure because picker and capture dependencies are not part of application composition.

- [ ] **Step 3: Implement the picker and dependency flow**

Define:

```csharp
internal interface IDumpDestinationPicker
{
    Task<string?> PickAsync();
}
```

The Avalonia implementation receives `Func<TopLevel?>`, checks `StorageProvider.CanPickFolder`, opens a single-folder picker titled `Choose snapshot destination`, disposes the selected storage item, and returns `folder.Path.LocalPath`; an empty result returns `null`.

Update `StartViewModel` constructors so the public composition uses `DumpCaptureService` and the Avalonia picker, while the internal constructor accepts both interfaces. Pass them to every `LiveSessionViewModel`. In `App.axaml.cs`, use a deferred `() => desktop.MainWindow` accessor so the picker resolves the window only when invoked.

- [ ] **Step 4: Run the composition test and verify GREEN**

Run the filtered command from Step 2, then run all `StartViewModelTests` to catch constructor regressions.

---

### Task 3: Live Session Capture Lifecycle

**Files:**
- Modify: `src/MemoryProfiler.App/ViewModels/LiveSessionViewModel.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/LiveSessionViewModelTests.cs`

**Interfaces:**
- Consumes: `IDumpCaptureService`, `IDumpDestinationPicker`, and `IUiDispatcher`.
- Produces: capture/cancel commands plus `IsCapturing`, capture status, capture error, and captured path properties used by XAML.

- [ ] **Step 1: Write one failing test per lifecycle behavior**

Add tests in this order, running each before its matching implementation:

```csharp
[Fact]
public async Task DismissingDestinationPickerLeavesCaptureStateUnchanged()
{
    var capture = new ControllableDumpCaptureService();
    await using var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker(null));

    await viewModel.CaptureSnapshotAsync();

    Assert.False(viewModel.IsCapturing);
    Assert.False(viewModel.HasCaptureStatus);
    Assert.False(viewModel.HasCaptureError);
    Assert.False(capture.Started.Task.IsCompleted);
}

[Fact]
public async Task CapturePublishesProgressAndSuccessfulPathThroughUiDispatcher()
{
    var capture = new ControllableDumpCaptureService();
    var dispatcher = new RecordingUiDispatcher();
    await using var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker("/captures"), dispatcher);

    var operation = viewModel.CaptureSnapshotAsync();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.True(viewModel.IsCapturing);
    Assert.Equal("Capturing snapshot", viewModel.CaptureStatusMessage);

    capture.Completion.SetResult("/captures/MyApi-2026-08-28-162500.dmp");
    await operation;

    Assert.False(viewModel.IsCapturing);
    Assert.Equal("/captures/MyApi-2026-08-28-162500.dmp", viewModel.CapturedDumpPath);
    Assert.Equal("Snapshot saved", viewModel.CaptureStatusMessage);
    Assert.True(dispatcher.Invocations >= 2);
}

[Fact]
public async Task CancellingCaptureReturnsToIdleWithoutAnError()
{
    var capture = new ControllableDumpCaptureService();
    await using var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker("/captures"));
    var operation = viewModel.CaptureSnapshotAsync();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    viewModel.CancelCapture();
    await operation;

    Assert.True(capture.Token.IsCancellationRequested);
    Assert.False(viewModel.IsCapturing);
    Assert.False(viewModel.HasCaptureError);
}

[Fact]
public async Task CaptureFailureIsNonfatalToTheLiveSession()
{
    var session = new StubSession(waitForCancellation: true);
    var capture = new ControllableDumpCaptureService();
    await using var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker("/captures"), session: session);
    var run = viewModel.StartAsync();
    await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var operation = viewModel.CaptureSnapshotAsync();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    capture.Completion.SetException(new IOException("disk full"));
    await operation;

    Assert.True(viewModel.IsLive);
    Assert.True(viewModel.HasCaptureError);
    Assert.Contains("disk full", viewModel.CaptureErrorMessage);
    await viewModel.DisconnectAsync();
    await run;
}

[Fact]
public async Task DisconnectCancelsAndAwaitsAnActiveCapture()
{
    var session = new StubSession(waitForCancellation: true);
    var capture = new ControllableDumpCaptureService();
    await using var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker("/captures"), session: session);
    var run = viewModel.StartAsync();
    await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var operation = viewModel.CaptureSnapshotAsync();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await viewModel.DisconnectAsync();
    await Task.WhenAll(run, operation);

    Assert.True(capture.Token.IsCancellationRequested);
    Assert.True(viewModel.IsDisconnected);
}

[Fact]
public async Task DisposalCancelsAndAwaitsAnActiveCapture()
{
    var capture = new ControllableDumpCaptureService();
    var viewModel = CreateViewModel(capture, new StubDumpDestinationPicker("/captures"));
    var operation = viewModel.CaptureSnapshotAsync();
    await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await viewModel.DisposeAsync();
    await operation;

    Assert.True(capture.Token.IsCancellationRequested);
    Assert.False(viewModel.HasCaptureError);
}

private static LiveSessionViewModel CreateViewModel(
    IDumpCaptureService capture,
    IDumpDestinationPicker picker,
    IUiDispatcher? dispatcher = null,
    ILiveDiagnosticsSession? session = null) =>
    new(
        4217,
        "SampleService",
        new StubSessionFactory(session ?? new StubSession([])),
        capture,
        picker,
        dispatcher ?? ImmediateUiDispatcher.Instance);

private sealed class StubDumpDestinationPicker(string? path) : IDumpDestinationPicker
{
    public Task<string?> PickAsync() => Task.FromResult(path);
}

private sealed class ControllableDumpCaptureService : IDumpCaptureService
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token { get; private set; }

    public async Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(4217, processId);
        Token = cancellationToken;
        Started.TrySetResult();
        return await Completion.Task.WaitAsync(cancellationToken);
    }
}
```

Use a real `CancellationToken` observed by a task-based fake service. Assert user-visible view-model state and session liveness, not call counts alone.

- [ ] **Step 2: Verify the first lifecycle test is RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests/MemoryProfiler.App.Tests.csproj --filter FullyQualifiedName~LiveSessionViewModelTests
```

Expected: compile failures for the new capture API.

- [ ] **Step 3: Implement capture state and commands incrementally**

Add private capture dependencies, a capture-specific `CancellationTokenSource`, tracked capture task, and state fields. Expose:

```csharp
public ICommand CaptureSnapshotCommand { get; }
public ICommand CancelCaptureCommand { get; }
public bool IsCapturing { get; }
public bool CanCaptureSnapshot => IsLive && !IsCapturing;
public bool HasCaptureStatus => CaptureStatusMessage.Length > 0;
public string CaptureStatusMessage { get; }
public bool HasCaptureError => CaptureErrorMessage.Length > 0;
public string CaptureErrorMessage { get; }
public string CapturedDumpPath { get; }
```

Implement `CaptureSnapshotAsync` as picker, begin-state publication, service invocation, success/cancel/error publication, and final capture-resource cleanup. Implement `CancelCapture` as a nonblocking token cancellation request. Update command availability whenever live or capture state changes. Extend disconnect/disposal to cancel and await the tracked capture without turning expected cancellation into an error.

- [ ] **Step 4: Complete each red-green cycle**

After implementing the minimum for each test, rerun the filtered `LiveSessionViewModelTests`; do not add the next behavior until the current test fails for the expected reason and then passes.

- [ ] **Step 5: Run all application view-model tests**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests/MemoryProfiler.App.Tests.csproj
```

Expected: all application tests pass.

---

### Task 4: Live Session Capture UI

**Files:**
- Modify: `src/MemoryProfiler.App/Views/LiveSessionView.axaml`

**Interfaces:**
- Consumes: capture commands and properties from Task 3.
- Produces: accessible native capture, cancel, progress, success, and error surfaces.

- [ ] **Step 1: Add the approved native controls**

In the header action stack, insert **Capture Snapshot** before **Disconnect** and show **Cancel capture** only while `IsCapturing`. Bind commands directly and rely on command `CanExecute` for disabled state.

Change the status row to a compact stack that can independently show:

```xml
<ProgressBar Height="2"
             AutomationProperties.Name="Capturing managed process snapshot"
             AutomationProperties.LiveSetting="Polite"
             IsVisible="{Binding IsCapturing}"
             IsIndeterminate="True" />
```

Add a success/status `TextBlock` with path trimming and tooltip, plus a separate error `Border` using `AppErrorSurfaceBrush`, `AppErrorTextBrush`, and assertive live setting. Preserve the existing connection progress and fatal session error.

- [ ] **Step 2: Compile-check bindings**

Run:

```bash
rtk dotnet build src/MemoryProfiler.App/MemoryProfiler.App.csproj
```

Expected: compiled bindings and XAML build succeed with zero errors.

- [ ] **Step 3: Run the frontend design preflight relevant to native UI**

Confirm: one existing Fluent token system, one accent and radius system, no decorative motion, readable button text, concise copy, keyboard commands preserved, disabled/loading/error/success/cancel states present, automation names/live settings present, and no web dependencies or marketing patterns introduced.

---

### Task 5: Real Dump Acceptance and Repository Verification

**Files:**
- Modify: `tests/MemoryProfiler.Diagnostics.Tests/MemoryProfiler.Diagnostics.Tests.csproj`
- Create: `tests/MemoryProfiler.Diagnostics.Tests/Dumps/DumpCaptureAcceptanceTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: real `DumpCaptureService`, existing `LiveDiagnosticsTargetFixture`, and ClrMD `DataTarget.LoadDump`.
- Produces: evidence that a captured dump contains a CLR runtime ClrMD can open.

- [ ] **Step 1: Add the failing ClrMD acceptance test**

Add test-only package reference:

```xml
<PackageReference Include="Microsoft.Diagnostics.Runtime" Version="4.0.732401" />
```

Create a test that starts `LiveDiagnosticsTargetFixture`, temporarily points `TMPDIR` at its short socket root as the existing acceptance test does, captures into a unique temporary directory, then verifies:

```csharp
using var dataTarget = DataTarget.LoadDump(dumpPath);
Assert.NotEmpty(dataTarget.ClrVersions);
```

Always restore `TMPDIR`, dispose the target, dispose `DataTarget`, and delete the explicit temporary directory in `finally`.

- [ ] **Step 2: Run the acceptance test and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Diagnostics.Tests/MemoryProfiler.Diagnostics.Tests.csproj --filter FullyQualifiedName~DumpCaptureAcceptanceTests
```

Before production capture exists, expected failure is compile-time. If dependency restore needs network access, request the narrow package-restore escalation rather than changing package versions.

- [ ] **Step 3: Run the real capture acceptance test to GREEN**

After Tasks 1-4, rerun the filtered command with a 120-second command timeout. Expected: one heap-bearing dump is captured and ClrMD reports at least one CLR version.

- [ ] **Step 4: Update README scope**

Add dump capture to Features and replace the statement that dump files are unsupported with a precise boundary: captured dumps are supported, while opening and offline heap analysis remain planned.

- [ ] **Step 5: Run full fresh verification**

Run all of these separately and inspect exit codes:

```bash
rtk dotnet test MemoryProfiler.sln
rtk dotnet build MemoryProfiler.sln
rtk git diff --check
rtk git status --short
```

Expected: zero test failures, zero build errors, no whitespace errors, and only the planned issue #7 files plus this plan are changed.

- [ ] **Step 6: Commit the implementation**

Stage only the planned files and commit with the issue's required message:

```bash
rtk git add README.md docs/superpowers/plans/2026-08-29-dump-capture.md src/MemoryProfiler.Diagnostics/Dumps src/MemoryProfiler.App/App.axaml.cs src/MemoryProfiler.App/Services src/MemoryProfiler.App/ViewModels/StartViewModel.cs src/MemoryProfiler.App/ViewModels/LiveSessionViewModel.cs src/MemoryProfiler.App/Views/LiveSessionView.axaml tests/MemoryProfiler.Diagnostics.Tests/MemoryProfiler.Diagnostics.Tests.csproj tests/MemoryProfiler.Diagnostics.Tests/Dumps tests/MemoryProfiler.App.Tests/ViewModels/StartViewModelTests.cs tests/MemoryProfiler.App.Tests/ViewModels/LiveSessionViewModelTests.cs
rtk git commit -m "feat: capture managed process dumps"
```

- [ ] **Step 7: Verify the committed state**

Run:

```bash
rtk git status --short --branch
rtk git show --stat --oneline --summary HEAD
```

Expected: clean branch and `feat: capture managed process dumps` at `HEAD`.
