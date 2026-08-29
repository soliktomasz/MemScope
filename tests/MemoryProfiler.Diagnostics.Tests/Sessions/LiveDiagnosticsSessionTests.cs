using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class LiveDiagnosticsSessionTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SessionExposesTheConnectedProcessId()
    {
        await using var session = new LiveDiagnosticsSession(new StubEventPipeSessionAdapter());

        Assert.Equal(42, session.ProcessId);
    }

    [Fact]
    public async Task MemoryAndGcStreamsCanBeObservedConcurrently()
    {
        var adapter = new StubEventPipeSessionAdapter();
        await using var session = new LiveDiagnosticsSession(adapter);
        var memoryTask = ReadFirstAsync(session.ObserveMemoryAsync());
        var gcTask = ReadFirstAsync(session.ObserveGcEventsAsync());
        await Task.Delay(100);

        adapter.RaiseCounter(new CounterSample("gc-heap-size", 8_192, null, 1.0, Timestamp));
        adapter.RaiseGcStart(new GcStartObservation(1, 0, "Induced", Timestamp));
        adapter.RaiseGcStop(new GcStopObservation(1, Timestamp.AddMilliseconds(2)));

        var memory = await memoryTask.WaitAsync(TimeSpan.FromSeconds(5));
        var gcEvent = await gcTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8_192ul, memory.ManagedHeapSize);
        Assert.Equal(0, gcEvent.Generation);
        Assert.Equal("Induced", gcEvent.Reason);
        adapter.Release();
    }

    [Fact]
    public async Task ASecondMemoryObserverOnTheSameStreamIsRejected()
    {
        var adapter = new StubEventPipeSessionAdapter();
        var session = new LiveDiagnosticsSession(adapter);
        try
        {
            _ = session.ObserveMemoryAsync();
            Assert.Throws<InvalidOperationException>(() => session.ObserveMemoryAsync());
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellingAnObserverEndsOnlyThatEnumeration()
    {
        var adapter = new StubEventPipeSessionAdapter();
        await using var session = new LiveDiagnosticsSession(adapter);
        using var cancellation = new CancellationTokenSource();

        await using var enumerator = session.ObserveMemoryAsync(cancellation.Token).GetAsyncEnumerator();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
            }
        });

        adapter.RaiseCounter(new CounterSample("gc-heap-size", 1_024, null, 1.0, Timestamp));
        var metrics = await ReadFirstAsync(session.ObserveMemoryAsync()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1_024ul, metrics.ManagedHeapSize);
        adapter.Release();
    }

    [Fact]
    public async Task ObserversCompleteNormallyWhenTheTargetTerminates()
    {
        var adapter = new StubEventPipeSessionAdapter();
        await using var session = new LiveDiagnosticsSession(adapter);
        adapter.Release();

        await using var enumerator = session.ObserveMemoryAsync().GetAsyncEnumerator();
        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ObserversCompleteWithIOExceptionWhenTheParserFails()
    {
        var adapter = new StubEventPipeSessionAdapter { ProcessThrows = true };
        await using var session = new LiveDiagnosticsSession(adapter);
        adapter.Release();

        await using var enumerator = session.ObserveMemoryAsync().GetAsyncEnumerator();
        var exception = await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
            }
        });

        Assert.Contains("ended unexpectedly", exception.Message);
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentAndStopsTheEventPipeSession()
    {
        var adapter = new StubEventPipeSessionAdapter();
        var session = new LiveDiagnosticsSession(adapter);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, adapter.StopAsyncCalls);
        Assert.Equal(1, adapter.DisposeAsyncCalls);
    }

    [Fact]
    public async Task DisposeAsyncDoesNotSurfaceBestEffortStopFailures()
    {
        var adapter = new StubEventPipeSessionAdapter { StopAsyncThrows = true };
        var session = new LiveDiagnosticsSession(adapter);

        await session.DisposeAsync();

        Assert.Equal(1, adapter.StopAsyncCalls);
    }

    [Fact]
    public async Task DisposeAsyncCompletesActiveEnumerationsNormally()
    {
        var adapter = new StubEventPipeSessionAdapter();
        var session = new LiveDiagnosticsSession(adapter);
        await using var enumerator = session.ObserveMemoryAsync().GetAsyncEnumerator();
        var moveTask = enumerator.MoveNextAsync().AsTask();

        await session.DisposeAsync();

        Assert.False(await moveTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<T> ReadFirstAsync<T>(IAsyncEnumerable<T> source)
    {
        await using var enumerator = source.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        return enumerator.Current;
    }
}
