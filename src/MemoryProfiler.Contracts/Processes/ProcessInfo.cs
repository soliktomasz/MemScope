namespace MemoryProfiler.Contracts.Processes;

public sealed record ProcessInfo(
    int ProcessId,
    string Name,
    string? RuntimeVersion);
