namespace MemoryProfiler.Contracts.Live;

public sealed record MemoryMetrics(
    DateTimeOffset Timestamp,
    ulong ManagedHeapSize,
    ulong Generation0Size,
    ulong Generation1Size,
    ulong Generation2Size,
    ulong LargeObjectHeapSize,
    ulong PinnedObjectHeapSize,
    double AllocationRateBytesPerSecond,
    long Generation0Collections,
    long Generation1Collections,
    long Generation2Collections,
    ulong PromotedBytes);
