using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Roots;

public interface IGcRootService
{
    Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);
}
