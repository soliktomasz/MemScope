using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class HeapObjectRowViewModel
{
    public HeapObjectRowViewModel(HeapObjectInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Instance = instance;
    }

    public HeapObjectInfo Instance { get; }

    public ulong Address => Instance.Address;

    public string AddressDisplay => MetricFormatting.Address(Instance.Address);

    public string SizeDisplay => MetricFormatting.Bytes(Instance.Size);

    public string GenerationDisplay => Instance.Generation;
}
