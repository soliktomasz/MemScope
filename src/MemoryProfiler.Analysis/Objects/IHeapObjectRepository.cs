using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Objects;

public interface IHeapObjectRepository
{
    Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
        HeapSnapshot snapshot,
        ulong methodTable,
        CancellationToken cancellationToken = default);
}
