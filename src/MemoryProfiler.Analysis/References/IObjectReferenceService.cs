using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.References;

public interface IObjectReferenceService
{
    Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default);
}
