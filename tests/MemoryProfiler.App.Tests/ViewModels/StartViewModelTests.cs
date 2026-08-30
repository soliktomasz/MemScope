using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Contracts.Heap;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Contracts.Processes;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Processes;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class StartViewModelTests
{
    [Theory]
    [InlineData("10.0.1", ".NET 10.0.1")]
    [InlineData(null, "Runtime unavailable")]
    public void ProcessRowsProvideReadableRuntimeLabels(
        string? runtimeVersion,
        string expectedLabel)
    {
        var row = new ProcessRowViewModel(
            new ProcessInfo(12451, "MyApi", runtimeVersion));

        Assert.Equal(expectedLabel, row.RuntimeDisplay);
    }

    [Fact]
    public async Task RefreshProjectsProcessesAndUsesPidOrderByDefault()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(12883, "WorkerService", "9.0.0"),
            new ProcessInfo(12451, "MyApi", "10.0.1"));
        using var picker = new ProcessPickerViewModel(discovery);

        await picker.RefreshAsync();

        Assert.Collection(
            picker.Processes,
            process => Assert.Equal(12451, process.ProcessId),
            process => Assert.Equal(12883, process.ProcessId));
        Assert.True(picker.HasProcesses);
        Assert.False(picker.IsLoading);
        Assert.False(picker.HasError);
    }

    [Fact]
    public async Task ColumnSortingUsesNaturalRuntimeOrderAndTogglesDirection()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(1, "Unknown", null),
            new ProcessInfo(2, "Ten", "10.0.0"),
            new ProcessInfo(3, "Nine", "9.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();

        picker.SortBy(ProcessSortColumn.Runtime);

        Assert.Equal(["Nine", "Ten", "Unknown"], picker.Processes.Select(row => row.Name));
        Assert.Equal("Runtime ascending", picker.RuntimeSortDescription);

        picker.SortBy(ProcessSortColumn.Runtime);

        Assert.Equal(["Ten", "Nine", "Unknown"], picker.Processes.Select(row => row.Name));
        Assert.Equal("Runtime descending", picker.RuntimeSortDescription);
    }

    [Fact]
    public async Task NameSortingIsCaseInsensitiveAndStableByProcessId()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(20, "worker", "10.0.0"),
            new ProcessInfo(30, "Api", "10.0.0"),
            new ProcessInfo(10, "api", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();

        picker.SortBy(ProcessSortColumn.Name);

        Assert.Equal([10, 30, 20], picker.Processes.Select(row => row.ProcessId));
    }

    [Fact]
    public async Task SortingMovesRowsWithoutResettingTheSelectedProcess()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(20, "Api", "10.0.0"),
            new ProcessInfo(10, "Worker", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();
        var selected = picker.Processes.Single(row => row.ProcessId == 10);
        picker.SelectedProcess = selected;
        var collectionActions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)picker.Processes).CollectionChanged += (_, eventArgs) =>
            collectionActions.Add(eventArgs.Action);

        picker.SortBy(ProcessSortColumn.Name);

        Assert.Same(selected, picker.SelectedProcess);
        Assert.NotEmpty(collectionActions);
        Assert.All(
            collectionActions,
            action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
    }

    [Fact]
    public async Task RefreshExposesLoadingStateWhileDiscoveryIsPending()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ProcessInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new StubDiscovery(_ => completion.Task);
        using var picker = new ProcessPickerViewModel(discovery);

        var refresh = picker.RefreshAsync();

        Assert.False(refresh.IsCompleted);
        Assert.True(picker.IsLoading);
        Assert.False(picker.RefreshCommand.CanExecute(null));

        completion.SetResult([]);
        await refresh;

        Assert.False(picker.IsLoading);
        Assert.True(picker.IsEmpty);
        Assert.True(picker.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshFailureBecomesAnInlineRetryableError()
    {
        var discovery = new StubDiscovery(
            _ => Task.FromException<IReadOnlyList<ProcessInfo>>(
                new InvalidOperationException("Diagnostics service unavailable.")));
        using var picker = new ProcessPickerViewModel(discovery);

        await picker.RefreshAsync();

        Assert.True(picker.HasError);
        Assert.Contains("Diagnostics service unavailable.", picker.ErrorMessage);
        Assert.True(picker.RefreshCommand.CanExecute(null));
        Assert.Empty(picker.Processes);
    }

    [Fact]
    public async Task ANewRefreshCancelsAndCannotBeOverwrittenByThePreviousRefresh()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var call = 0;
        var discovery = new StubDiscovery(async cancellationToken =>
        {
            call++;
            if (call == 1)
            {
                firstToken = cancellationToken;
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [new ProcessInfo(42, "Current", "10.0.0")];
        });
        using var picker = new ProcessPickerViewModel(discovery);

        var firstRefresh = picker.RefreshAsync();
        await firstStarted.Task;
        var secondRefresh = picker.RefreshAsync();

        await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.True(firstToken.IsCancellationRequested);
        var process = Assert.Single(picker.Processes);
        Assert.Equal("Current", process.Name);
    }

    [Fact]
    public async Task RefreshCoordinatorDoesNotPublishALeaseThatAnotherLeaseCanDispose()
    {
        using var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        var second = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(first.Token.IsCancellationRequested);
        first.Dispose();

        using var third = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(second.Token.IsCancellationRequested);
        Assert.True(third.IsCurrent);
        second.Dispose();
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotPreventAReplacementRefresh()
    {
        var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        using var registration = first.Token.Register(
            () => throw new InvalidOperationException("Callback failed."));
        RefreshLease? second = null;

        try
        {
            var exception = await Record.ExceptionAsync(
                async () => second = await coordinator.BeginAsync(CancellationToken.None));

            Assert.Null(exception);
            Assert.NotNull(second);
            Assert.True(second.IsCurrent);
        }
        finally
        {
            registration.Dispose();
            first.Dispose();
            second?.Dispose();
            coordinator.Dispose();
        }
    }

    [Fact]
    public async Task CancellationCallbackCanRetireItsLeaseDuringReplacement()
    {
        using var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        var firstToken = first.Token;
        using var registration = firstToken.Register(first.Dispose);

        using var second = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public async Task ReplacementWaitsForAnAcceptedStateMutationToFinish()
    {
        using var coordinator = new RefreshCoordinator();
        using var first = await coordinator.BeginAsync(CancellationToken.None);
        using var mutationStarted = new ManualResetEventSlim();
        using var releaseMutation = new ManualResetEventSlim();
        var mutation = Task.Run(async () =>
            await first.TryRunIfCurrentAsync(() =>
            {
                mutationStarted.Set();
                releaseMutation.Wait();
            }));
        Assert.True(mutationStarted.Wait(TimeSpan.FromSeconds(5)));

        var replacement = coordinator.BeginAsync(CancellationToken.None).AsTask();

        Assert.False(replacement.IsCompleted);
        releaseMutation.Set();
        Assert.True(await mutation);
        using var second = await replacement;
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public async Task AttachShowsThePickerBeforeDiscoveryCompletes()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ProcessInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new StubDiscovery(_ => completion.Task);
        using var start = new StartViewModel(new ProcessPickerViewModel(discovery));

        var showPicker = start.ShowProcessPickerAsync();

        Assert.True(start.IsProcessPickerVisible);
        Assert.True(start.ProcessPicker.IsLoading);

        completion.SetResult([]);
        await showPicker;
    }

    [Fact]
    public async Task AttachingTheSelectedProcessOpensAndStartsALiveSession()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(4217, "SampleService", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        var session = new StubLiveSession();
        var factory = new StubLiveSessionFactory(session);
        using var start = new StartViewModel(
            picker,
            factory,
            ImmediateUiDispatcher.Instance);
        await picker.RefreshAsync();

        Assert.False(start.AttachSelectedProcessCommand.CanExecute(null));
        picker.SelectedProcess = Assert.Single(picker.Processes);
        Assert.True(start.AttachSelectedProcessCommand.CanExecute(null));

        await start.StartLiveSessionAsync();

        Assert.Equal(4217, factory.ConnectedProcessId);
        Assert.NotNull(start.LiveSession);
        Assert.Equal("SampleService", start.LiveSession.ProcessName);
        Assert.True(start.IsLiveSessionVisible);
        Assert.False(start.IsStartVisible);

        await start.CloseLiveSessionAsync();

        Assert.Null(start.LiveSession);
        Assert.True(start.IsStartVisible);
        Assert.False(start.IsLiveSessionVisible);
    }

    [Fact]
    public async Task StartingLiveSessionSuppliesDumpCaptureDependencies()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(4217, "SampleService", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        var session = new BlockingLiveSession();
        var capture = new RecordingDumpCaptureService();
        await using var start = new StartViewModel(
            picker,
            new StubLiveSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            capture,
            new StubDumpDestinationPicker(Path.GetTempPath()));
        await picker.RefreshAsync();
        picker.SelectedProcess = Assert.Single(picker.Processes);

        var liveRun = start.StartLiveSessionAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await start.LiveSession!.CaptureSnapshotAsync();

        Assert.Equal(4217, capture.ProcessId);
        Assert.Equal(Path.GetTempPath(), capture.DestinationDirectory);
        await start.CloseLiveSessionAsync();
        await liveRun;
    }

    [Fact]
    public async Task OpenDumpIsDisabledUntilLoaderAndPickerAreSupplied()
    {
        using var start = new StartViewModel(new ProcessPickerViewModel(StubDiscovery.Returning()));

        Assert.False(start.OpenDumpCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenDumpIsDisabledWithoutTheGcRootService()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]));

        Assert.False(start.OpenDumpCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenDumpIsDisabledWithoutTheDominatorService()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        Assert.False(start.OpenDumpCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenDumpIsEnabledWhenTheDominatorServiceIsSupplied()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            new StubDominatorService(new DominatorAnalysisResult([], [])));

        Assert.True(start.OpenDumpCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompareSnapshotsIsDisabledUntilLoaderComparisonAndPickerAreSupplied()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"));

        Assert.False(start.CompareSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompareSnapshotsIsEnabledWhenTheComparisonServiceIsSupplied()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            new StubDominatorService(new DominatorAnalysisResult([], [])),
            new SnapshotComparisonService());

        Assert.True(start.CompareSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShowingComparisonHidesTheStartScreenAndClosesBack()
    {
        await using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            new StubDominatorService(new DominatorAnalysisResult([], [])),
            new SnapshotComparisonService());

        Assert.True(start.IsStartVisible);
        Assert.False(start.IsComparisonVisible);

        await start.ShowComparisonAsync();

        Assert.NotNull(start.Comparison);
        Assert.True(start.IsComparisonVisible);
        Assert.False(start.IsStartVisible);
        Assert.False(start.IsLiveSessionVisible);
        Assert.False(start.IsSnapshotVisible);
        Assert.False(start.CompareSnapshotsCommand.CanExecute(null));

        await start.CloseComparisonAsync();

        Assert.Null(start.Comparison);
        Assert.True(start.IsStartVisible);
        Assert.False(start.IsComparisonVisible);
        Assert.True(start.CompareSnapshotsCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenDumpIsDisabledWhileTheComparisonViewIsOpen()
    {
        await using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            new StubDominatorService(new DominatorAnalysisResult([], [])),
            new SnapshotComparisonService());

        await start.ShowComparisonAsync();

        Assert.False(start.OpenDumpCommand.CanExecute(null));
        Assert.False(start.AttachSelectedProcessCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseComparisonWithNoComparisonOpenIsANoOp()
    {
        await using var start = new StartViewModel(new ProcessPickerViewModel(StubDiscovery.Returning()));

        await start.CloseComparisonAsync();

        Assert.Null(start.Comparison);
        Assert.True(start.IsStartVisible);
    }

    [Fact]
    public async Task OpenDumpPickerCancellationLeavesTheStartScreenUnchanged()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker(null),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        await start.OpenDumpAsync();

        Assert.True(start.IsStartVisible);
        Assert.False(start.IsSnapshotVisible);
        Assert.Null(start.Snapshot);
        Assert.False(start.HasDumpError);
    }

    [Fact]
    public async Task OpenDumpLoadsTheSelectedDumpAndShowsTheSnapshot()
    {
        var loader = new StubHeapSnapshotLoader(SampleSnapshot());
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            loader,
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        await start.OpenDumpAsync();

        Assert.Equal("/tmp/sample.dmp", loader.Path);
        Assert.True(start.IsSnapshotVisible);
        Assert.False(start.IsStartVisible);
        Assert.NotNull(start.Snapshot);
        Assert.True(start.Snapshot.HasSnapshot);
        Assert.True(start.Snapshot.Types.HasTypes);
    }

    [Fact]
    public async Task OpenDumpPickerFailureShowsAnInlineRetryableError()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new ThrowingDumpFilePicker(new IOException("Picker unavailable.")),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        await start.OpenDumpAsync();

        Assert.True(start.HasDumpError);
        Assert.Contains("Picker unavailable.", start.DumpErrorMessage);
        Assert.True(start.IsStartVisible);
        Assert.Null(start.Snapshot);
    }

    [Fact]
    public async Task OpenDumpLoadFailureIsShownInsideTheSnapshotViewAndCloseReturnsToStart()
    {
        var loader = new StubHeapSnapshotLoader(
            _ => Task.FromException<HeapSnapshot>(
                new InvalidDataException("Not a dump.")));
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            loader,
            new StubDumpFilePicker("/tmp/sample.dmp"),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        await start.OpenDumpAsync();

        Assert.True(start.IsSnapshotVisible);
        Assert.True(start.Snapshot!.HasError);
        Assert.Contains("Not a dump.", start.Snapshot.ErrorMessage);

        await start.CloseSnapshotAsync();

        Assert.True(start.IsStartVisible);
        Assert.False(start.IsSnapshotVisible);
        Assert.Null(start.Snapshot);
    }

    [Fact]
    public async Task AnalyzingACapturedDumpOpensTheSnapshotAndKeepsTheLiveSession()
    {
        var picker = new ProcessPickerViewModel(
            StubDiscovery.Returning(new ProcessInfo(4217, "SampleService", "10.0.0")));
        var session = new BlockingLiveSession();
        await using var start = new StartViewModel(
            picker,
            new StubLiveSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker(null),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));
        await picker.RefreshAsync();
        picker.SelectedProcess = Assert.Single(picker.Processes);
        var liveRun = start.StartLiveSessionAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await start.AnalyzeCapturedDumpAsync("/tmp/captured.dmp");

        Assert.NotNull(start.LiveSession);
        Assert.False(start.IsLiveSessionVisible);
        Assert.True(start.IsSnapshotVisible);
        Assert.True(start.Snapshot!.HasSnapshot);

        await start.CloseSnapshotAsync();

        Assert.True(start.IsLiveSessionVisible);
        Assert.False(start.IsSnapshotVisible);
        Assert.Null(start.Snapshot);
        await start.CloseLiveSessionAsync();
        await liveRun;
    }

    [Fact]
    public async Task FailedAnalysisKeepsTheLiveSessionRunning()
    {
        var picker = new ProcessPickerViewModel(
            StubDiscovery.Returning(new ProcessInfo(4217, "SampleService", "10.0.0")));
        var session = new BlockingLiveSession();
        await using var start = new StartViewModel(
            picker,
            new StubLiveSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(
                _ => Task.FromException<HeapSnapshot>(
                    new InvalidDataException("Not a dump."))),
            new StubDumpFilePicker(null),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));
        await picker.RefreshAsync();
        picker.SelectedProcess = Assert.Single(picker.Processes);
        var liveRun = start.StartLiveSessionAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await start.AnalyzeCapturedDumpAsync("/tmp/captured.dmp");

        Assert.NotNull(start.LiveSession);
        Assert.True(start.Snapshot!.HasError);

        await start.CloseSnapshotAsync();
        await start.CloseLiveSessionAsync();
        await liveRun;
    }

    [Fact]
    public async Task AnalyzingIgnoresMissingPaths()
    {
        using var start = new StartViewModel(
            new ProcessPickerViewModel(StubDiscovery.Returning()),
            new StubLiveSessionFactory(new StubLiveSession()),
            ImmediateUiDispatcher.Instance,
            new RecordingDumpCaptureService(),
            new StubDumpDestinationPicker(Path.GetTempPath()),
            new StubHeapSnapshotLoader(SampleSnapshot()),
            new StubDumpFilePicker(null),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]));

        await start.AnalyzeCapturedDumpAsync(string.Empty);

        Assert.True(start.IsStartVisible);
        Assert.Null(start.Snapshot);
    }

    private static HeapSnapshot SampleSnapshot() =>
        new()
        {
            Info = new HeapSnapshotInfo(
                Path.GetFullPath("sample.dmp"),
                "Sample.Process",
                4217,
                "10.0.0",
                new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero),
                12_345,
                4_000_000),
            Types =
            [
                new HeapTypeInfo(0x1000, "System.String", "System.Private.CoreLib", 381_235, 44_200_000, null),
                new HeapTypeInfo(0x2000, "MyApp.CacheEntry", "MyApp", 50_000, 118_400_000, null)
            ]
        };

    private sealed class StubDiscovery(
        Func<CancellationToken, Task<IReadOnlyList<ProcessInfo>>> discover) : IDotNetProcessDiscovery
    {
        public static StubDiscovery Returning(params ProcessInfo[] processes) =>
            new(_ => Task.FromResult<IReadOnlyList<ProcessInfo>>(processes));

        public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(
            CancellationToken cancellationToken = default) =>
            discover(cancellationToken);
    }

    private sealed class StubLiveSessionFactory(ILiveDiagnosticsSession session)
        : ILiveDiagnosticsSessionFactory
    {
        public int? ConnectedProcessId { get; private set; }

        public Task<ILiveDiagnosticsSession> ConnectAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            ConnectedProcessId = processId;
            return Task.FromResult(session);
        }
    }

    private sealed class StubLiveSession : ILiveDiagnosticsSession
    {
        public int ProcessId => 4217;

        public async IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingLiveSession : ILiveDiagnosticsSession
    {
        public int ProcessId => 4217;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class StubDumpDestinationPicker(string? directory)
        : IDumpDestinationPicker
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

    private sealed class StubDumpFilePicker(string? path) : IDumpFilePicker
    {
        public Task<string?> PickAsync() => Task.FromResult(path);
    }

    private sealed class ThrowingDumpFilePicker(Exception exception) : IDumpFilePicker
    {
        public Task<string?> PickAsync() => Task.FromException<string?>(exception);
    }

    private sealed class StubHeapSnapshotLoader : IHeapSnapshotLoader
    {
        private readonly Func<CancellationToken, Task<HeapSnapshot>> _load;

        public StubHeapSnapshotLoader(HeapSnapshot snapshot)
            : this(_ => Task.FromResult(snapshot))
        {
        }

        public StubHeapSnapshotLoader(Func<CancellationToken, Task<HeapSnapshot>> load) =>
            _load = load;

        public string? Path { get; private set; }

        public Task<HeapSnapshot> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Path = path;
            return _load(cancellationToken);
        }
    }

    private sealed class StubHeapObjectRepository(IReadOnlyList<HeapObjectInfo> instances)
        : IHeapObjectRepository
    {
        public Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
            HeapSnapshot snapshot,
            ulong methodTable,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(instances);
    }

    private sealed class StubObjectReferenceService(IReadOnlyList<ObjectReference> references)
        : IObjectReferenceService
    {
        public Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(references);

        public Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(references);
    }

    private sealed class StubGcRootService(IReadOnlyList<GcRootInfo> roots) : IGcRootService
    {
        public Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(roots);
    }

    private sealed class StubDominatorService(DominatorAnalysisResult result)
        : IDominatorTreeService
    {
        public Task<DominatorAnalysisResult> ComputeDominatorsAsync(
            HeapSnapshot snapshot,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
