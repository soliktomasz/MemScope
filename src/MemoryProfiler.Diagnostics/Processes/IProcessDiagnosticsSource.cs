namespace MemoryProfiler.Diagnostics.Processes;

internal interface IProcessDiagnosticsSource
{
    IEnumerable<int> GetPublishedProcessIds();

    ValueTask<DiscoveredProcess> InspectAsync(
        int processId,
        CancellationToken cancellationToken);
}

internal sealed record DiscoveredProcess(
    string Name,
    string? RuntimeVersion);
