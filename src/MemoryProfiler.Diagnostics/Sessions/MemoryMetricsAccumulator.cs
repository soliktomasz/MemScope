using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.Diagnostics.Sessions;

/// <summary>
/// Accumulates <c>System.Runtime</c> EventCounter samples into immutable
/// <see cref="MemoryMetrics"/> snapshots. A valid <c>gc-heap-size</c> sample closes a logical
/// interval and emits a snapshot using the latest values of the other counters.
/// </summary>
internal sealed class MemoryMetricsAccumulator
{
    private ulong _heapSize;
    private ulong _generation0Size;
    private ulong _generation1Size;
    private ulong _generation2Size;
    private ulong _largeObjectHeapSize;
    private ulong _pinnedObjectHeapSize;
    private double _allocationRateBytesPerSecond;
    private long _generation0Collections;
    private long _generation1Collections;
    private long _generation2Collections;
    private ulong _promotedBytes;

    public void AddPromotedBytes(ulong promotedBytes) => _promotedBytes = promotedBytes;

    public MemoryMetrics? Add(CounterSample sample)
    {
        switch (sample.Name)
        {
            case "gc-heap-size":
                return ObservationNumbers.TryClampToUInt64(sample.Mean, out var heapSize)
                    ? Snapshot(_heapSize = heapSize, sample.Timestamp)
                    : null;
            case "gen-0-size":
                SetSize(sample.Mean, ref _generation0Size);
                break;
            case "gen-1-size":
                SetSize(sample.Mean, ref _generation1Size);
                break;
            case "gen-2-size":
                SetSize(sample.Mean, ref _generation2Size);
                break;
            case "loh-size":
                SetSize(sample.Mean, ref _largeObjectHeapSize);
                break;
            case "poh-size":
                SetSize(sample.Mean, ref _pinnedObjectHeapSize);
                break;
            case "alloc-rate":
                if (TryComputeAllocationRate(sample, out var rate))
                {
                    _allocationRateBytesPerSecond = rate;
                }

                break;
            case "gen-0-gc-count":
                AccumulateCount(sample.Increment, ref _generation0Collections);
                break;
            case "gen-1-gc-count":
                AccumulateCount(sample.Increment, ref _generation1Collections);
                break;
            case "gen-2-gc-count":
                AccumulateCount(sample.Increment, ref _generation2Collections);
                break;
        }

        return null;
    }

    private MemoryMetrics Snapshot(ulong heapSize, DateTimeOffset timestamp) => new(
        timestamp,
        heapSize,
        _generation0Size,
        _generation1Size,
        _generation2Size,
        _largeObjectHeapSize,
        _pinnedObjectHeapSize,
        _allocationRateBytesPerSecond,
        _generation0Collections,
        _generation1Collections,
        _generation2Collections,
        _promotedBytes);

    private static void SetSize(double? value, ref ulong target)
    {
        if (ObservationNumbers.TryClampToUInt64(value, out var clamped))
        {
            target = clamped;
        }
    }

    private static bool TryComputeAllocationRate(CounterSample sample, out double rate)
    {
        rate = 0;
        if (sample.Increment is not { } increment ||
            double.IsNaN(increment) ||
            double.IsInfinity(increment) ||
            increment < 0)
        {
            return false;
        }

        var intervalSeconds = sample.IntervalSec is { } interval &&
            interval > 0 &&
            !double.IsNaN(interval) &&
            !double.IsInfinity(interval)
                ? interval
                : 1.0;

        rate = increment / intervalSeconds;
        return true;
    }

    private static void AccumulateCount(double? increment, ref long total)
    {
        if (increment is not { } value ||
            double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= 0)
        {
            return;
        }

        total += (long)Math.Min(value, long.MaxValue - total);
    }
}
