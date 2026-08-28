using MemoryProfiler.Diagnostics.Processes;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Processes;

public sealed class DotNetProcessDiscoveryTests
{
    [Fact]
    public async Task EndpointProbeCancellationDoesNotWaitForSynchronousIpcToFinish()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = new DiagnosticsClientEndpointProbe(_ =>
        {
            started.Set();
            release.Wait();
        });
        using var cancellation = new CancellationTokenSource();

        var validationTask = probe
            .ValidateAsync(Environment.ProcessId, cancellation.Token)
            .AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validationTask);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task DiagnosticsSourceRejectsAProcessWhoseEndpointIsUnavailable()
    {
        var source = new DiagnosticsProcessSource(new UnavailableEndpointProbe());

        await Assert.ThrowsAsync<IOException>(
            async () => await source.InspectAsync(Environment.ProcessId, CancellationToken.None));
    }

    [Fact]
    public async Task DiagnosticsSourceRejectsMetadataWhenThePidIdentityChanges()
    {
        var originalStartTime = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var process = new MutableProcessHandle("Original", originalStartTime);
        var endpoint = new MutatingEndpointProbe(() =>
            process.StartTime = originalStartTime.AddSeconds(1));
        var source = new DiagnosticsProcessSource(
            endpoint,
            new StubProcessHandleSource(process));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await source.InspectAsync(42, CancellationToken.None));
    }

    [Fact]
    public void RuntimeVersionSelectionUsesCoreClrRatherThanTheAppHost()
    {
        RuntimeModuleMetadata[] modules =
        [
            new("ExampleApp", "18.9.0"),
            new("libcoreclr.dylib", "10.0.1+abcdef")
        ];

        var runtimeVersion = DiagnosticsProcessSource.SelectRuntimeVersion(modules);

        Assert.Equal("10.0.1", runtimeVersion);
    }

    [Fact]
    public async Task GetProcessesAsyncReturnsEmptyCollectionWhenNoRuntimeIsPublished()
    {
        var discovery = new DotNetProcessDiscovery(new StubProcessSource([]));

        var processes = await discovery.GetProcessesAsync();

        Assert.Empty(processes);
    }

    [Fact]
    public async Task GetProcessesAsyncSkipsAnInaccessibleProcess()
    {
        var source = new StubProcessSource(
            [10, 20],
            new Dictionary<int, object>
            {
                [10] = new UnauthorizedAccessException(),
                [20] = new DiscoveredProcess("WorkerService", "10.0.0")
            });
        var discovery = new DotNetProcessDiscovery(source);

        var processes = await discovery.GetProcessesAsync();

        var process = Assert.Single(processes);
        Assert.Equal(20, process.ProcessId);
        Assert.Equal("WorkerService", process.Name);
        Assert.Equal("10.0.0", process.RuntimeVersion);
    }

    [Fact]
    public async Task GetProcessesAsyncSkipsAProcessThatExitsDuringInspection()
    {
        var source = new StubProcessSource(
            [30, 40],
            new Dictionary<int, object>
            {
                [30] = new InvalidOperationException("Process has exited."),
                [40] = new DiscoveredProcess("Api", null)
            });
        var discovery = new DotNetProcessDiscovery(source);

        var processes = await discovery.GetProcessesAsync();

        var process = Assert.Single(processes);
        Assert.Equal(40, process.ProcessId);
    }

    [Fact]
    public async Task GetProcessesAsyncReturnsEachProcessIdOnlyOnce()
    {
        var source = new StubProcessSource(
            [50, 50, 50],
            new Dictionary<int, object>
            {
                [50] = new DiscoveredProcess("Unique", "9.0.0")
            });
        var discovery = new DotNetProcessDiscovery(source);

        var processes = await discovery.GetProcessesAsync();

        var process = Assert.Single(processes);
        Assert.Equal(50, process.ProcessId);
    }

    [Fact]
    public async Task GetProcessesAsyncPropagatesCancellation()
    {
        var discovery = new DotNetProcessDiscovery(new StubProcessSource([]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.GetProcessesAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetProcessesAsyncCancelsInspectionAlreadyInProgress()
    {
        var source = new BlockingProcessSource();
        var discovery = new DotNetProcessDiscovery(source);
        using var cancellation = new CancellationTokenSource();

        var discoveryTask = discovery.GetProcessesAsync(cancellation.Token);
        await source.InspectionStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discoveryTask);
    }

    [Fact]
    public async Task GetProcessesAsyncDoesNotHideUnexpectedInspectionFailures()
    {
        var source = new StubProcessSource(
            [70],
            new Dictionary<int, object>
            {
                [70] = new NullReferenceException("Adapter bug.")
            });
        var discovery = new DotNetProcessDiscovery(source);

        await Assert.ThrowsAsync<NullReferenceException>(
            () => discovery.GetProcessesAsync());
    }

    private sealed class StubProcessSource(
        IReadOnlyList<int> processIds,
        IReadOnlyDictionary<int, object>? inspections = null) : IProcessDiagnosticsSource
    {
        public IEnumerable<int> GetPublishedProcessIds() => processIds;

        public ValueTask<DiscoveredProcess> InspectAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            if (inspections is null || !inspections.TryGetValue(processId, out var inspection))
            {
                throw new InvalidOperationException($"No inspection configured for PID {processId}.");
            }

            if (inspection is Exception exception)
            {
                throw exception;
            }

            return ValueTask.FromResult((DiscoveredProcess)inspection);
        }
    }

    private sealed class BlockingProcessSource : IProcessDiagnosticsSource
    {
        private readonly TaskCompletionSource _inspectionStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InspectionStarted => _inspectionStarted.Task;

        public IEnumerable<int> GetPublishedProcessIds() => [60];

        public async ValueTask<DiscoveredProcess> InspectAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            _inspectionStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class UnavailableEndpointProbe : IProcessEndpointProbe
    {
        public ValueTask ValidateAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Diagnostics endpoint unavailable."));
    }

    private sealed class MutatingEndpointProbe(Action mutation) : IProcessEndpointProbe
    {
        public ValueTask ValidateAsync(int processId, CancellationToken cancellationToken)
        {
            mutation();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubProcessHandleSource(IProcessHandle process) : IProcessHandleSource
    {
        public IProcessHandle Open(int processId) => process;
    }

    private sealed class MutableProcessHandle(
        string name,
        DateTimeOffset startTime) : IProcessHandle
    {
        public string Name => name;

        public DateTimeOffset StartTime { get; set; } = startTime;

        public bool HasExited { get; set; }

        public IEnumerable<RuntimeModuleMetadata> Modules => [];

        public void Refresh()
        {
        }

        public void Dispose()
        {
        }
    }
}
