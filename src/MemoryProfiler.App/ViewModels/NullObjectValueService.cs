using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Values;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels;

internal sealed class NullObjectValueService : IHeapObjectValueService
{
    public Task<HeapObjectValueResult> ReadAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HeapObjectValueResult(
            new HeapObjectInfo(objectAddress, 0, string.Empty, 0, "Unknown"),
            [],
            TotalFieldOrElementCount: 0,
            HasMoreElements: false));
    }
}
