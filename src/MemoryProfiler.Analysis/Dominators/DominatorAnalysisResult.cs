using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Dominators;

public sealed record DominatorAnalysisResult(
    IReadOnlyList<DominatorInfo> Dominators,
    IReadOnlyList<TypeRetainedSize> TypeRetainedSizes);
