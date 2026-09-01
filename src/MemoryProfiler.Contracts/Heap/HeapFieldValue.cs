namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapFieldValue(
    string Name,
    string DeclaredTypeName,
    HeapValueKind Kind,
    string? ValueText,
    ulong? ReferencedObjectAddress,
    string? ReferencedObjectTypeName,
    bool IsTruncated,
    int? TotalLength,
    string? UnavailableReason);
