namespace MemoryProfiler.Contracts.Heap;

public sealed record TypeRetainedSize(
    ulong MethodTable,
    string TypeName,
    ulong RetainedSize);
