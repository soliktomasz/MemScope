using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Dominators;

public interface IDominatorTreeService
{
    Task<DominatorAnalysisResult> ComputeDominatorsAsync(
        HeapSnapshot snapshot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
