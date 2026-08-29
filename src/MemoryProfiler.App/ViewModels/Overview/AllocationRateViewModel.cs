using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.App.ViewModels.Overview;

public sealed class AllocationRateViewModel : ViewModelBase
{
    private double _allocationRateBytesPerSecond;

    public double AllocationRateBytesPerSecond => _allocationRateBytesPerSecond;

    public string AllocationRateDisplay =>
        MetricFormatting.BytesPerSecond(_allocationRateBytesPerSecond);

    internal void Apply(MemoryMetrics metrics)
    {
        if (SetProperty(
                ref _allocationRateBytesPerSecond,
                metrics.AllocationRateBytesPerSecond,
                nameof(AllocationRateBytesPerSecond)))
        {
            OnPropertyChanged(nameof(AllocationRateDisplay));
        }
    }
}
