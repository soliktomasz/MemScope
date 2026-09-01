using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Values;

public interface IHeapObjectValueService
{
    Task<HeapObjectValueResult> ReadAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken = default);
}
