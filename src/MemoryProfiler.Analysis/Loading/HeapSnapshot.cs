using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Loading;

public sealed class HeapSnapshot
{
    public required HeapSnapshotInfo Info { get; init; }

    public required IReadOnlyList<HeapTypeInfo> Types { get; init; }
}
