using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Types;

public sealed class TypeRowViewModel
{
    private ulong? _retainedSize;

    public TypeRowViewModel(HeapTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        _retainedSize = type.RetainedSize;
    }

    public HeapTypeInfo Type { get; }

    public ulong MethodTable => Type.MethodTable;

    public string TypeName => Type.Name;

    public string AssemblyName => Type.AssemblyName ?? "Unknown assembly";

    public string CountDisplay => Type.ObjectCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ShallowSizeDisplay => MetricFormatting.Bytes(Type.ShallowSize);

    public ulong? RetainedSize => _retainedSize;

    public bool IsRetainedSizeAvailable => _retainedSize is not null;

    public bool IsRetainedSizeUnavailable => !IsRetainedSizeAvailable;

    public string RetainedSizeDisplay =>
        _retainedSize is null
            ? "N/A"
            : MetricFormatting.Bytes(_retainedSize.Value);

    public void SetRetainedSize(ulong? value) => _retainedSize = value;
}
