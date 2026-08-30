namespace MemoryProfiler.Contracts.Heap;

public sealed record TypeMemoryDelta(
    string TypeName,
    long CountBefore,
    long CountAfter,
    long CountDelta,
    long SizeBefore,
    long SizeAfter,
    long SizeDelta,
    long? RetainedSizeDelta);
