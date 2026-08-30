using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Types;

public sealed class TypeRowViewModel
{
    public TypeRowViewModel(HeapTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    public HeapTypeInfo Type { get; }

    public ulong MethodTable => Type.MethodTable;

    public string TypeName => Type.Name;

    public string AssemblyName => Type.AssemblyName ?? "Unknown assembly";

    public string CountDisplay => Type.ObjectCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ShallowSizeDisplay => MetricFormatting.Bytes(Type.ShallowSize);

    public bool IsRetainedSizeAvailable => Type.RetainedSize is not null;

    public bool IsRetainedSizeUnavailable => !IsRetainedSizeAvailable;

    public string RetainedSizeDisplay =>
        IsRetainedSizeAvailable
            ? MetricFormatting.Bytes(Type.RetainedSize!.Value)
            : "N/A";
}
