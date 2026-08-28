namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapTypeInfo(
    ulong MethodTable,
    string Name,
    string? AssemblyName,
    long ObjectCount,
    ulong ShallowSize,
    ulong? RetainedSize);
