using MemoryProfiler.Analysis.Loading;

namespace MemoryProfiler.Analysis.Comparison;

public interface ISnapshotComparisonService
{
    SnapshotComparison Compare(
        HeapSnapshot before,
        HeapSnapshot after);
}
