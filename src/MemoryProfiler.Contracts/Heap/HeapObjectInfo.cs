namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapObjectInfo(
    ulong Address,
    ulong MethodTable,
    string TypeName,
    ulong Size,
    string Generation);
