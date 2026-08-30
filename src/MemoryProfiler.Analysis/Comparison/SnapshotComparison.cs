using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Comparison;

public sealed record SnapshotComparison(
    IReadOnlyList<TypeMemoryDelta> Deltas);
