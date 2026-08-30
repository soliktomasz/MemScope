namespace MemoryProfiler.Contracts.Heap;

public sealed record ObjectReference(
    ulong SourceAddress,
    ulong TargetAddress,
    ReferenceKind Kind,
    string? Name,
    string? SourceTypeName = null,
    string? TargetTypeName = null);
