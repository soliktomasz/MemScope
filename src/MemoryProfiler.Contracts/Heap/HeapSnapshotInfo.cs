namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapSnapshotInfo(
    string Path,
    string? ProcessName,
    int? ProcessId,
    string? RuntimeVersion,
    DateTimeOffset CapturedAt,
    long ObjectCount,
    ulong HeapSize);
