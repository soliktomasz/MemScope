namespace MemoryProfiler.Contracts.Heap;

public sealed record GcRootInfo(
    ulong RootAddress,
    ulong ObjectAddress,
    string Kind,
    string? Name);
