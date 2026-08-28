using MemoryProfiler.Contracts.Processes;

namespace MemoryProfiler.Diagnostics.Processes;

public interface IDotNetProcessDiscovery
{
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}
