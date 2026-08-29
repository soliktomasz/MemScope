using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.App.ViewModels.Overview;

public sealed class HeapSummaryViewModel : ViewModelBase
{
    private ulong _managedHeapSize;
    private ulong _largeObjectHeapSize;
    private ulong _pinnedObjectHeapSize;
    private ulong _promotedBytes;

    public ulong ManagedHeapSize => _managedHeapSize;

    public ulong LargeObjectHeapSize => _largeObjectHeapSize;

    public ulong PinnedObjectHeapSize => _pinnedObjectHeapSize;

    public ulong PromotedBytes => _promotedBytes;

    public string ManagedHeapSizeDisplay => MetricFormatting.Bytes(_managedHeapSize);

    public string LargeObjectHeapSizeDisplay => MetricFormatting.Bytes(_largeObjectHeapSize);

    public string PinnedObjectHeapSizeDisplay => MetricFormatting.Bytes(_pinnedObjectHeapSize);

    public string PromotedBytesDisplay => MetricFormatting.Bytes(_promotedBytes);

    internal void Apply(MemoryMetrics metrics)
    {
        SetMetric(ref _managedHeapSize, metrics.ManagedHeapSize, nameof(ManagedHeapSize), nameof(ManagedHeapSizeDisplay));
        SetMetric(ref _largeObjectHeapSize, metrics.LargeObjectHeapSize, nameof(LargeObjectHeapSize), nameof(LargeObjectHeapSizeDisplay));
        SetMetric(ref _pinnedObjectHeapSize, metrics.PinnedObjectHeapSize, nameof(PinnedObjectHeapSize), nameof(PinnedObjectHeapSizeDisplay));
        SetMetric(ref _promotedBytes, metrics.PromotedBytes, nameof(PromotedBytes), nameof(PromotedBytesDisplay));
    }

    private void SetMetric(ref ulong field, ulong value, string propertyName, string displayPropertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(displayPropertyName);
        }
    }
}
