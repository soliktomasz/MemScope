using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.App.ViewModels.GcTimeline;

public sealed class GcEventRowViewModel
{
    public GcEventRowViewModel(GcEvent gcEvent)
    {
        Event = gcEvent;
        ReclaimedBytes = gcEvent.HeapSizeBefore > gcEvent.HeapSizeAfter
            ? gcEvent.HeapSizeBefore - gcEvent.HeapSizeAfter
            : 0;
        var chartMaximum = Math.Max(gcEvent.HeapSizeBefore, gcEvent.HeapSizeAfter);
        HeapBeforeRatio = chartMaximum == 0
            ? 0
            : (double)gcEvent.HeapSizeBefore / chartMaximum;
        HeapAfterRatio = chartMaximum == 0
            ? 0
            : (double)gcEvent.HeapSizeAfter / chartMaximum;
        ReclaimedPercentage = gcEvent.HeapSizeBefore == 0
            ? 0
            : (double)ReclaimedBytes / gcEvent.HeapSizeBefore * 100;
    }

    public GcEvent Event { get; }

    public DateTimeOffset Timestamp => Event.Timestamp;

    public int Generation => Event.Generation;

    public double PauseMilliseconds => Event.PauseDuration.TotalMilliseconds;

    public ulong HeapSizeBefore => Event.HeapSizeBefore;

    public ulong HeapSizeAfter => Event.HeapSizeAfter;

    public string Reason => Event.Reason;

    public ulong ReclaimedBytes { get; }

    public double ReclaimedPercentage { get; }

    public double HeapBeforeRatio { get; }

    public double HeapAfterRatio { get; }

    public bool IsIneffective => ReclaimedPercentage < 5;

    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);

    public string GenerationDisplay => $"Gen {Generation}";

    public string PauseDisplay => $"{PauseMilliseconds.ToString(PauseMilliseconds < 10 ? "N1" : "N0", CultureInfo.CurrentCulture)} ms";

    public string HeapBeforeDisplay => MetricFormatting.Bytes(HeapSizeBefore);

    public string HeapAfterDisplay => MetricFormatting.Bytes(HeapSizeAfter);

    public string ReclaimedDisplay => $"{MetricFormatting.Bytes(ReclaimedBytes)} ({ReclaimedPercentage.ToString("N1", CultureInfo.CurrentCulture)}%)";

    public string EffectivenessDisplay => IsIneffective
        ? "Low"
        : $"{ReclaimedPercentage.ToString("N1", CultureInfo.CurrentCulture)}%";
}
