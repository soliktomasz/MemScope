using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.Diagnostics.Sessions;

public interface ILiveDiagnosticsSession : IAsyncDisposable
{
    int ProcessId { get; }

    IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
        CancellationToken cancellationToken = default);
}
