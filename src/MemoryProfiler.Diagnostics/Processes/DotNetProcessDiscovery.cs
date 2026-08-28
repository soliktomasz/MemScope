using MemoryProfiler.Contracts.Processes;

namespace MemoryProfiler.Diagnostics.Processes;

public sealed class DotNetProcessDiscovery : IDotNetProcessDiscovery
{
    private readonly IProcessDiagnosticsSource _source;

    public DotNetProcessDiscovery()
        : this(new DiagnosticsProcessSource())
    {
    }

    internal DotNetProcessDiscovery(IProcessDiagnosticsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DiscoverAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<ProcessInfo>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = new List<ProcessInfo>();
        var seenProcessIds = new HashSet<int>();

        foreach (var processId in _source.GetPublishedProcessIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seenProcessIds.Add(processId))
            {
                continue;
            }

            try
            {
                var discovered = await _source
                    .InspectAsync(processId, cancellationToken)
                    .ConfigureAwait(false);
                processes.Add(new ProcessInfo(
                    processId,
                    discovered.Name,
                    discovered.RuntimeVersion));
            }
            catch (Exception exception) when (ProcessInspectionFailure.IsExpected(exception))
            {
                // The diagnostics endpoint can disappear between publication and inspection.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return processes.OrderBy(process => process.ProcessId).ToArray();
    }
}
