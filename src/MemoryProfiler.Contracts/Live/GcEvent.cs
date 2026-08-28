namespace MemoryProfiler.Contracts.Live;

public sealed record GcEvent(
    DateTimeOffset Timestamp,
    int Generation,
    TimeSpan PauseDuration,
    ulong HeapSizeBefore,
    ulong HeapSizeAfter,
    string Reason);
