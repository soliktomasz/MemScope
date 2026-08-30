namespace MemoryProfiler.Contracts.Heap;

public sealed record DominatorInfo(
    ulong ObjectAddress,
    string TypeName,
    ulong ShallowSize,
    ulong RetainedSize,
    long RetainedObjectCount);
