using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Retainers;

public sealed class TopRetainerRowViewModel
{
    private readonly ulong _totalReachableBytes;

    public TopRetainerRowViewModel(DominatorInfo info, ulong totalReachableBytes)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        _totalReachableBytes = totalReachableBytes;
    }

    public DominatorInfo Info { get; }

    public ulong Address => Info.ObjectAddress;

    public string TypeName => Info.TypeName;

    public string AddressDisplay => MetricFormatting.Address(Info.ObjectAddress);

    public string ShallowSizeDisplay => MetricFormatting.Bytes(Info.ShallowSize);

    public string RetainedSizeDisplay => MetricFormatting.Bytes(Info.RetainedSize);

    public string RetainedObjectCountDisplay => MetricFormatting.Count(Info.RetainedObjectCount);

    public string RetainedPercentageDisplay =>
        _totalReachableBytes == 0
            ? "0.0%"
            : $"{((double)Info.RetainedSize / _totalReachableBytes * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";
}
