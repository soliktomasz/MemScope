using MemoryProfiler.Diagnostics.Sessions;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

internal sealed class StubEventPipeSessionAdapter : IEventPipeSessionAdapter
{
    private readonly TaskCompletionSource _processGate = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProcessId => 42;

    public bool ProcessThrows { get; set; }

    public bool StopAsyncThrows { get; set; }

    public int StopAsyncCalls { get; private set; }

    public int DisposeAsyncCalls { get; private set; }

    public event Action<CounterSample>? CounterSample;
    public event Action<GcStartObservation>? GcStart;
    public event Action<GcStopObservation>? GcStop;
    public event Action<GcHeapStatsObservation>? GcHeapStats;
    public event Action<GcSuspendBeginObservation>? GcSuspendBegin;
    public event Action<GcRestartBeginObservation>? GcRestartBegin;

    public void Process()
    {
        _processGate.Task.GetAwaiter().GetResult();
        if (ProcessThrows)
        {
            throw new IOException("The target process terminated unexpectedly.");
        }
    }

    public void StopProcessing() => _processGate.TrySetResult();

    public void Release() => _processGate.TrySetResult();

    public Task StopAsync()
    {
        StopAsyncCalls++;
        return StopAsyncThrows
            ? Task.FromException(new IOException("The target is already gone."))
            : Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeAsyncCalls++;
        return ValueTask.CompletedTask;
    }

    public void RaiseCounter(CounterSample sample) => CounterSample?.Invoke(sample);

    public void RaiseGcStart(GcStartObservation observation) => GcStart?.Invoke(observation);

    public void RaiseGcStop(GcStopObservation observation) => GcStop?.Invoke(observation);

    public void RaiseGcHeapStats(GcHeapStatsObservation observation) =>
        GcHeapStats?.Invoke(observation);

    public void RaiseGcSuspendBegin(GcSuspendBeginObservation observation) =>
        GcSuspendBegin?.Invoke(observation);

    public void RaiseGcRestartBegin(GcRestartBeginObservation observation) =>
        GcRestartBegin?.Invoke(observation);
}
