using System.Runtime.CompilerServices;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class LiveSessionViewModelTests
{
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
    public async Task StartDrainsGcEventsWithoutRetainingThem()
    {
        const int eventCount = 10_000;
        var session = new StubSession(
            [CreateMetrics(managedHeapSize: 4096)],
            gcEventCount: eventCount);
        await using var viewModel = new LiveSessionViewModel(
            4217,
            "SampleService",
            new StubSessionFactory(session),
            ImmediateUiDispatcher.Instance);

        await viewModel.StartAsync();

        Assert.Equal(eventCount, session.GcEventsObserved);
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
        private readonly int _gcEventCount;
        private readonly Exception? _observationFailure;

        public StubSession(
            IReadOnlyList<MemoryMetrics>? metrics = null,
            bool waitForCancellation = false,
            int gcEventCount = 0,
            Exception? observationFailure = null)
        {
            _metrics = metrics ?? [];
            _waitForCancellation = waitForCancellation;
            _gcEventCount = gcEventCount;
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
            for (var index = 0; index < _gcEventCount; index++)
            {
                GcEventsObserved++;
                yield return new GcEvent(
                    DateTimeOffset.UtcNow,
                    0,
                    TimeSpan.Zero,
                    0,
                    0,
                    "Test");
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
}
