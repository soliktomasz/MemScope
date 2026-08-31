using System.Runtime.CompilerServices;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class LiveSessionViewModelTests
{
    [Fact]
    public async Task DismissingDestinationPickerLeavesCaptureStateUnchanged()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new RecordingDumpCaptureService();
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: capture,
            dumpDestinationPicker: new StubDumpDestinationPicker(null));
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.CaptureSnapshotAsync();

        Assert.False(viewModel.IsCapturing);
        Assert.False(viewModel.HasCaptureStatus);
        Assert.False(viewModel.HasCaptureError);
        Assert.False(capture.WasCalled);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task DestinationPickerFailureIsNonfatalToTheLiveSession()
    {
        var session = new StubSession(waitForCancellation: true);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new RecordingDumpCaptureService(),
            dumpDestinationPicker: new ThrowingDumpDestinationPicker(
                new IOException("Picker unavailable.")));
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.CaptureSnapshotAsync();

        Assert.True(viewModel.IsLive);
        Assert.True(viewModel.HasCaptureError);
        Assert.Contains("Picker unavailable.", viewModel.CaptureErrorMessage);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CapturePublishesProgressAndSuccessfulPathThroughUiDispatcher()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new ControllableDumpCaptureService();
        var dispatcher = new RecordingUiDispatcher();
        await using var viewModel = CreateCaptureViewModel(session, capture, dispatcher);
        Assert.False(viewModel.CaptureSnapshotCommand.CanExecute(null));
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.CaptureSnapshotCommand.CanExecute(null));

        var operation = viewModel.CaptureSnapshotAsync();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsCapturing);
        Assert.Equal("Capturing snapshot", viewModel.CaptureStatusMessage);
        Assert.False(viewModel.CaptureSnapshotCommand.CanExecute(null));
        Assert.True(viewModel.CancelCaptureCommand.CanExecute(null));
        var path = Path.Combine(Path.GetTempPath(), "snapshot.dmp");
        capture.Completion.SetResult(path);
        await operation;

        Assert.False(viewModel.IsCapturing);
        Assert.Equal(path, viewModel.CapturedDumpPath);
        Assert.Equal("Snapshot saved", viewModel.CaptureStatusMessage);
        Assert.False(viewModel.HasCaptureError);
        Assert.True(dispatcher.Invocations >= 3);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CancellingCaptureReturnsToIdleWithoutAnError()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new ControllableDumpCaptureService();
        await using var viewModel = CreateCaptureViewModel(session, capture);
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = viewModel.CaptureSnapshotAsync();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.CancelCapture();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capture.Token.IsCancellationRequested);
        Assert.False(viewModel.IsCapturing);
        Assert.False(viewModel.HasCaptureError);
        Assert.Equal(string.Empty, viewModel.CaptureStatusMessage);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CaptureFailureIsNonfatalToTheLiveSession()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new ControllableDumpCaptureService();
        await using var viewModel = CreateCaptureViewModel(session, capture);
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = viewModel.CaptureSnapshotAsync();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capture.Completion.SetException(new IOException("Disk full."));
        await operation;

        Assert.True(viewModel.IsLive);
        Assert.True(viewModel.HasCaptureError);
        Assert.Contains("Disk full.", viewModel.CaptureErrorMessage);
        Assert.False(viewModel.HasError);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task SuccessfulCaptureReportsTheCapturedPathOnce()
    {
        var session = new StubSession(waitForCancellation: true);
        var capturedPaths = new List<string>();
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new RecordingDumpCaptureService(),
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()),
            snapshotCaptured: path =>
            {
                capturedPaths.Add(path);
                return Task.CompletedTask;
            });
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.CaptureSnapshotAsync();

        Assert.Collection(
            capturedPaths,
            path => Assert.EndsWith("snapshot.dmp", path));
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task FailedCaptureDoesNotReportAPath()
    {
        var session = new StubSession(waitForCancellation: true);
        var callbackCount = 0;
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new ThrowingDumpCaptureService(
                new IOException("Disk full.")),
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()),
            snapshotCaptured: _ =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.CaptureSnapshotAsync();

        Assert.Equal(0, callbackCount);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task AnalyzeIsUnavailableWithoutACapturedSnapshotOrCallback()
    {
        var session = new StubSession(waitForCancellation: true);
        await using var viewModel = CreateCaptureViewModel(
            session,
            new RecordingDumpCaptureService());
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.CaptureSnapshotAsync();

        Assert.False(viewModel.CanAnalyzeSnapshot);
        Assert.False(viewModel.AnalyzeSnapshotCommand.CanExecute(null));
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task AnalyzeInvokesTheCallbackWithTheCapturedPath()
    {
        var session = new StubSession(waitForCancellation: true);
        var analyzedPath = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new RecordingDumpCaptureService(),
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()),
            analyzeSnapshot: path =>
            {
                analyzedPath.SetResult(path);
                return Task.CompletedTask;
            });
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.CanAnalyzeSnapshot);

        await viewModel.CaptureSnapshotAsync();

        Assert.True(viewModel.CanAnalyzeSnapshot);
        Assert.True(viewModel.AnalyzeSnapshotCommand.CanExecute(null));

        viewModel.AnalyzeSnapshotCommand.Execute(null);

        var path = await analyzedPath.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.EndsWith("snapshot.dmp", path);
        Assert.True(viewModel.IsLive);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task AnalyzeFailureIsNonfatalToTheLiveSession()
    {
        var session = new StubSession(waitForCancellation: true);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new RecordingDumpCaptureService(),
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()),
            analyzeSnapshot: _ => Task.FromException(
                new InvalidDataException("Not a dump.")));
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.CaptureSnapshotAsync();

        viewModel.AnalyzeSnapshotCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(viewModel.IsLive);
        Assert.True(viewModel.HasCaptureError);
        Assert.Contains("Unable to open the snapshot.", viewModel.CaptureErrorMessage);
        Assert.Contains("Not a dump.", viewModel.CaptureErrorMessage);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task AnalyzeRetryClearsThePreviousFailureMessageAndKeepsTheAnalyzeAction()
    {
        var session = new StubSession(waitForCancellation: true);
        var attempt = 0;
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: new RecordingDumpCaptureService(),
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()),
            analyzeSnapshot: _ =>
            {
                attempt++;
                return attempt == 1
                    ? Task.FromException(new InvalidDataException("Not a dump."))
                    : Task.CompletedTask;
            });
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.CaptureSnapshotAsync();

        viewModel.AnalyzeSnapshotCommand.Execute(null);

        Assert.True(viewModel.HasCaptureError);
        Assert.Contains("Not a dump.", viewModel.CaptureErrorMessage);
        Assert.Equal("Snapshot saved", viewModel.CaptureStatusMessage);
        Assert.True(viewModel.CanAnalyzeSnapshot);

        viewModel.AnalyzeSnapshotCommand.Execute(null);

        Assert.False(viewModel.HasCaptureError);
        Assert.Equal(string.Empty, viewModel.CaptureErrorMessage);
        Assert.Equal("Snapshot saved", viewModel.CaptureStatusMessage);
        Assert.True(viewModel.CanAnalyzeSnapshot);
        Assert.Equal(2, attempt);
        await viewModel.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task DisconnectCancelsAndAwaitsAnActiveCapture()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new ControllableDumpCaptureService();
        await using var viewModel = CreateCaptureViewModel(session, capture);
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = viewModel.CaptureSnapshotAsync();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(run, operation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capture.Token.IsCancellationRequested);
        Assert.True(viewModel.IsDisconnected);
        Assert.False(viewModel.IsCapturing);
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsAnActiveCapture()
    {
        var capture = new ControllableDumpCaptureService();
        var session = new StubSession(waitForCancellation: true);
        var viewModel = CreateCaptureViewModel(session, capture);
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = viewModel.CaptureSnapshotAsync();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(run, operation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capture.Token.IsCancellationRequested);
        Assert.False(viewModel.HasCaptureError);
    }

    [Fact]
    public async Task DisposalDuringDestinationSelectionDoesNotStartCapture()
    {
        var session = new StubSession(waitForCancellation: true);
        var capture = new RecordingDumpCaptureService();
        var picker = new ControllableDumpDestinationPicker();
        var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            dumpCaptureService: capture,
            dumpDestinationPicker: picker);
        var run = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var captureOperation = viewModel.CaptureSnapshotAsync();
        await picker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();
        picker.Completion.SetResult(Path.GetTempPath());
        await Task.WhenAll(run, captureOperation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(capture.WasCalled);
    }

    [Fact]
    public void ApplyingMetricsProjectsEveryDashboardValue()
    {
        var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(),
            ImmediateUiDispatcher.Instance);
        var metrics = CreateMetrics(
            managedHeapSize: 900,
            allocationRate: 125.5,
            generation0Size: 100,
            generation1Size: 200,
            generation2Size: 300,
            largeObjectHeapSize: 175,
            pinnedObjectHeapSize: 25,
            generation0Collections: 11,
            generation1Collections: 7,
            generation2Collections: 3,
            promotedBytes: 88);

        viewModel.ApplyMetrics(metrics);

        Assert.Equal((ulong)900, viewModel.Heap.ManagedHeapSize);
        Assert.Equal((ulong)175, viewModel.Heap.LargeObjectHeapSize);
        Assert.Equal((ulong)25, viewModel.Heap.PinnedObjectHeapSize);
        Assert.Equal((ulong)88, viewModel.Heap.PromotedBytes);
        Assert.Equal(125.5, viewModel.Allocation.AllocationRateBytesPerSecond);
        Assert.Equal((ulong)100, viewModel.Generations.Generation0Size);
        Assert.Equal((ulong)200, viewModel.Generations.Generation1Size);
        Assert.Equal((ulong)300, viewModel.Generations.Generation2Size);
        Assert.Equal(11, viewModel.Generations.Generation0Collections);
        Assert.Equal(7, viewModel.Generations.Generation1Collections);
        Assert.Equal(3, viewModel.Generations.Generation2Collections);
    }

    [Fact]
    public void MetricHistoryEvictsTheOldestSamplesAtItsCapacity()
    {
        var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(),
            ImmediateUiDispatcher.Instance,
            maximumMetricSamples: 3);

        viewModel.ApplyMetrics(CreateMetrics(managedHeapSize: 1));
        viewModel.ApplyMetrics(CreateMetrics(managedHeapSize: 2));
        viewModel.ApplyMetrics(CreateMetrics(managedHeapSize: 3));
        viewModel.ApplyMetrics(CreateMetrics(managedHeapSize: 4));

        Assert.Equal([2UL, 3UL, 4UL], viewModel.MetricHistory.Select(x => x.ManagedHeapSize));
    }

    [Fact]
    public async Task StartConnectsAndPublishesMetricsThroughTheUiDispatcher()
    {
        var session = new StubSession([CreateMetrics(managedHeapSize: 4096)]);
        var factory = new StubSessionFactory(session);
        var dispatcher = new RecordingUiDispatcher();
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            factory,
            dispatcher);

        await viewModel.StartAsync();

        Assert.Equal(4217, factory.ConnectedProcessId);
        Assert.Equal(3, dispatcher.Invocations);
        Assert.Equal((ulong)4096, viewModel.Heap.ManagedHeapSize);
        Assert.True(viewModel.IsDisconnected);
        Assert.False(viewModel.IsConnecting);
    }

    [Fact]
    public async Task StartPublishesGcEventsToTheBoundedTimeline()
    {
        var gcEvents = Enumerable.Range(0, 4)
            .Select(index => new GcEvent(
                DateTimeOffset.UtcNow.AddSeconds(index),
                index % 3,
                TimeSpan.FromMilliseconds(index + 1),
                1024,
                768,
                "Test"))
            .ToArray();
        var session = new StubSession(
            [CreateMetrics(managedHeapSize: 4096)],
            gcEvents: gcEvents);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance,
            maximumGcEvents: 3);

        await viewModel.StartAsync();

        Assert.Equal(4, session.GcEventsObserved);
        Assert.Equal([2d, 3d, 4d], viewModel.GcTimeline.FilteredEvents.Select(x => x.PauseMilliseconds));
        Assert.Single(viewModel.MetricHistory);
    }

    [Fact]
    public async Task DisconnectCancelsObservationAndDisposesTheDiagnosticsSession()
    {
        var session = new StubSession(waitForCancellation: true);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance);
        var started = viewModel.StartAsync();
        await session.ObservationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisconnectAsync();
        await started;

        Assert.True(session.ObservationToken.IsCancellationRequested);
        Assert.True(session.IsDisposed);
        Assert.True(viewModel.IsDisconnected);
    }

    [Fact]
    public async Task ConnectionFailureBecomesAnActionableErrorState()
    {
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(new IOException("Endpoint unavailable.")),
            ImmediateUiDispatcher.Instance);

        await viewModel.StartAsync();

        Assert.True(viewModel.HasError);
        Assert.Contains("Endpoint unavailable.", viewModel.ErrorMessage);
        Assert.False(viewModel.IsConnecting);
        Assert.True(viewModel.IsDisconnected);
    }

    [Fact]
    public async Task DisposalDuringConnectionJoinsTheRunAndSuppressesLateUiUpdates()
    {
        var factory = new PausedSessionFactory();
        var dispatcher = new RecordingUiDispatcher();
        var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            factory,
            dispatcher);
        var run = viewModel.StartAsync();
        await factory.ConnectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDisposal = viewModel.DisposeAsync().AsTask();
        var secondDisposal = viewModel.DisposeAsync().AsTask();
        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        var session = new StubSession([]);
        factory.Connection.SetResult(session);
        await Task.WhenAll(firstDisposal, secondDisposal);
        await run;

        Assert.True(session.IsDisposed);
        Assert.Equal(0, dispatcher.Invocations);
    }

    [Fact]
    public async Task DisposalAllowsAnAlreadyQueuedUiPublicationToDrain()
    {
        var dispatcher = new QueuedUiDispatcher();
        var session = new StubSession([], waitForCancellation: true);
        var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            dispatcher);
        var run = viewModel.StartAsync();
        await dispatcher.InvocationQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = viewModel.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        dispatcher.RunQueuedAction();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await run;

        Assert.Equal(1, dispatcher.ActionsApplied);
        Assert.False(viewModel.IsLive);
    }

    [Fact]
    public async Task ObservationFailureUsesEndedStateAndMessage()
    {
        var session = new StubSession(
            [CreateMetrics(managedHeapSize: 4096)],
            observationFailure: new IOException("Transport closed."));
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance);

        await viewModel.StartAsync();

        Assert.True(viewModel.HasError);
        Assert.True(viewModel.IsDisconnected);
        Assert.Contains("session ended unexpectedly", viewModel.ErrorMessage);
    }

    private static MemoryMetrics CreateMetrics(
        ulong managedHeapSize = 0,
        double allocationRate = 0,
        ulong generation0Size = 0,
        ulong generation1Size = 0,
        ulong generation2Size = 0,
        ulong largeObjectHeapSize = 0,
        ulong pinnedObjectHeapSize = 0,
        long generation0Collections = 0,
        long generation1Collections = 0,
        long generation2Collections = 0,
        ulong promotedBytes = 0) =>
        new(
            DateTimeOffset.UtcNow,
            managedHeapSize,
            generation0Size,
            generation1Size,
            generation2Size,
            largeObjectHeapSize,
            pinnedObjectHeapSize,
            allocationRate,
            generation0Collections,
            generation1Collections,
            generation2Collections,
            promotedBytes);

    private static LiveSessionViewModel CreateCaptureViewModel(
        StubSession session,
        IDumpCaptureService capture,
        IUiDispatcher? dispatcher = null) =>
        new(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            dispatcher ?? ImmediateUiDispatcher.Instance,
            dumpCaptureService: capture,
            dumpDestinationPicker: new StubDumpDestinationPicker(Path.GetTempPath()));

    private sealed class StubSessionFactory : ILiveDiagnosticsSessionFactory
    {
        private readonly ILiveDiagnosticsSession? _session;
        private readonly Exception? _exception;

        public StubSessionFactory()
        {
        }

        public StubSessionFactory(ILiveDiagnosticsSession session) => _session = session;

        public StubSessionFactory(Exception exception) => _exception = exception;

        public int? ConnectedProcessId { get; private set; }

        public Task<ILiveDiagnosticsSession> ConnectAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            ConnectedProcessId = processId;
            return _exception is null
                ? Task.FromResult(_session ?? new StubSession([]))
                : Task.FromException<ILiveDiagnosticsSession>(_exception);
        }
    }

    private sealed class StubSession : ILiveDiagnosticsSession
    {
        private readonly IReadOnlyList<MemoryMetrics> _metrics;
        private readonly bool _waitForCancellation;
        private readonly IReadOnlyList<GcEvent> _gcEvents;
        private readonly Exception? _observationFailure;

        public StubSession(
            IReadOnlyList<MemoryMetrics>? metrics = null,
            bool waitForCancellation = false,
            IReadOnlyList<GcEvent>? gcEvents = null,
            Exception? observationFailure = null)
        {
            _metrics = metrics ?? [];
            _waitForCancellation = waitForCancellation;
            _gcEvents = gcEvents ?? [];
            _observationFailure = observationFailure;
        }

        public int ProcessId => 4217;

        public TaskCompletionSource ObservationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservationToken { get; private set; }

        public bool IsDisposed { get; private set; }

        public int GcEventsObserved { get; private set; }

        public async IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObservationToken = cancellationToken;
            ObservationStarted.TrySetResult();
            foreach (var metric in _metrics)
            {
                yield return metric;
            }

            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_observationFailure is not null)
            {
                throw _observationFailure;
            }
        }

        public async IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var gcEvent in _gcEvents)
            {
                GcEventsObserved++;
                yield return gcEvent;
            }

            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PausedSessionFactory : ILiveDiagnosticsSessionFactory
    {
        public TaskCompletionSource ConnectionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ILiveDiagnosticsSession> Connection { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ILiveDiagnosticsSession> ConnectAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            ConnectionStarted.SetResult();
            return Connection.Task;
        }
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public int Invocations { get; private set; }

        public Task InvokeAsync(Action action)
        {
            Invocations++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _queuedAction;

        public TaskCompletionSource InvocationQueued { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActionsApplied { get; private set; }

        public Task InvokeAsync(Action action)
        {
            _queuedAction = action;
            InvocationQueued.SetResult();
            return _completion.Task;
        }

        public void RunQueuedAction()
        {
            _queuedAction?.Invoke();
            ActionsApplied++;
            _completion.SetResult();
        }
    }

    private sealed class StubDumpDestinationPicker(string? directory)
        : IDumpDestinationPicker
    {
        public Task<string?> PickAsync() => Task.FromResult(directory);
    }

    private sealed class ControllableDumpDestinationPicker : IDumpDestinationPicker
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string?> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> PickAsync()
        {
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class ThrowingDumpDestinationPicker(Exception exception)
        : IDumpDestinationPicker
    {
        public Task<string?> PickAsync() => Task.FromException<string?>(exception);
    }

    private sealed class RecordingDumpCaptureService : IDumpCaptureService
    {
        public bool WasCalled { get; private set; }

        public Task<string> CaptureAsync(
            int processId,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(Path.Combine(destinationDirectory, "snapshot.dmp"));
        }
    }

    private sealed class ThrowingDumpCaptureService(Exception exception)
        : IDumpCaptureService
    {
        public Task<string> CaptureAsync(
            int processId,
            string destinationDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(exception);
    }

    private sealed class ControllableDumpCaptureService : IDumpCaptureService
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public async Task<string> CaptureAsync(
            int processId,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            return await Completion.Task.WaitAsync(cancellationToken);
        }
    }
}
