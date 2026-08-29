using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.App.ViewModels.Overview;

public sealed class GenerationSummaryViewModel : ViewModelBase
{
    private ulong _generation0Size;
    private ulong _generation1Size;
    private ulong _generation2Size;
    private long _generation0Collections;
    private long _generation1Collections;
    private long _generation2Collections;

    public ulong Generation0Size => _generation0Size;

    public ulong Generation1Size => _generation1Size;

    public ulong Generation2Size => _generation2Size;

    public long Generation0Collections => _generation0Collections;

    public long Generation1Collections => _generation1Collections;

    public long Generation2Collections => _generation2Collections;

    public string Generation0SizeDisplay => MetricFormatting.Bytes(_generation0Size);

    public string Generation1SizeDisplay => MetricFormatting.Bytes(_generation1Size);

    public string Generation2SizeDisplay => MetricFormatting.Bytes(_generation2Size);

    internal void Apply(MemoryMetrics metrics)
    {
        SetSize(ref _generation0Size, metrics.Generation0Size, nameof(Generation0Size), nameof(Generation0SizeDisplay));
        SetSize(ref _generation1Size, metrics.Generation1Size, nameof(Generation1Size), nameof(Generation1SizeDisplay));
        SetSize(ref _generation2Size, metrics.Generation2Size, nameof(Generation2Size), nameof(Generation2SizeDisplay));
        SetProperty(ref _generation0Collections, metrics.Generation0Collections, nameof(Generation0Collections));
        SetProperty(ref _generation1Collections, metrics.Generation1Collections, nameof(Generation1Collections));
        SetProperty(ref _generation2Collections, metrics.Generation2Collections, nameof(Generation2Collections));
    }

    private void SetSize(ref ulong field, ulong value, string propertyName, string displayPropertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(displayPropertyName);
        }
    }
}
