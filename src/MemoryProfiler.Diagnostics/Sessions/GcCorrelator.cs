using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.Diagnostics.Sessions;

/// <summary>
/// Correlates CLR GC observations into <see cref="GcEvent"/> records. A GC event is emitted when
/// the matching stop arrives; pause duration is the sum of complete suspension intervals, falling
/// back to elapsed start-to-stop time, and the after-heap uses the latest heap-stat for the
/// collection, falling back to the latest managed-heap metric.
/// </summary>
internal sealed class GcCorrelator
{
    private readonly Dictionary<int, ActiveGc> _active = [];
    private ulong _latestManagedHeapSize;
    private PendingSuspension? _pendingSuspension;

    public void AddHeapSize(CounterSample sample)
    {
        if (sample.Name == "gc-heap-size" &&
            ObservationNumbers.TryClampToUInt64(sample.Mean, out var heapSize))
        {
            _latestManagedHeapSize = heapSize;
        }
    }

    public void AddGcStart(GcStartObservation observation)
    {
        var gc = new ActiveGc(
            observation.Number,
            observation.Generation,
            observation.Reason,
            observation.Timestamp,
            _latestManagedHeapSize);

        if (_pendingSuspension is { } suspension && suspension.GcNumber == observation.Number)
        {
            gc.SuspensionBegin = suspension.Begin;
            _pendingSuspension = null;
        }

        _active[observation.Number] = gc;
    }

    public GcEvent? AddGcStop(GcStopObservation observation)
    {
        if (_active.Remove(observation.Number, out var gc) is false)
        {
            return null;
        }

        var pauseDuration = gc.SuspensionDurations.Count > 0
            ? TimeSpan.FromTicks(gc.SuspensionDurations.Sum(duration => duration.Ticks))
            : observation.Timestamp - gc.StartTimestamp;

        if (pauseDuration < TimeSpan.Zero)
        {
            pauseDuration = TimeSpan.Zero;
        }

        return new GcEvent(
            gc.StartTimestamp,
            gc.Generation,
            pauseDuration,
            gc.HeapSizeBefore,
            gc.HeapStatsAfter ?? _latestManagedHeapSize,
            gc.Reason);
    }

    public void AddGcHeapStats(GcHeapStatsObservation observation)
    {
        if (_active.Count == 0)
        {
            return;
        }

        var mostRecent = _active.Values.MaxBy(gc => gc.StartTimestamp)!;
        mostRecent.HeapStatsAfter = observation.HeapSize;
    }

    public void AddSuspensionBegin(GcSuspendBeginObservation observation)
    {
        if (_active.TryGetValue(observation.GcNumber, out var gc))
        {
            gc.SuspensionBegin = observation.Timestamp;
            return;
        }

        _pendingSuspension = new PendingSuspension(observation.GcNumber, observation.Timestamp);
    }

    public void AddRestartBegin(GcRestartBeginObservation observation)
    {
        _pendingSuspension = null;

        ActiveGc? withOpenSuspension = null;
        DateTimeOffset? earliestBegin = null;
        foreach (var gc in _active.Values)
        {
            if (gc.SuspensionBegin is not { } begin)
            {
                continue;
            }

            if (earliestBegin is null || begin < earliestBegin)
            {
                earliestBegin = begin;
                withOpenSuspension = gc;
            }
        }

        if (withOpenSuspension is null || earliestBegin is not { } suspensionBegin)
        {
            return;
        }

        withOpenSuspension.SuspensionBegin = null;
        var duration = observation.Timestamp - suspensionBegin;
        if (duration > TimeSpan.Zero)
        {
            withOpenSuspension.SuspensionDurations.Add(duration);
        }
    }

    private sealed class ActiveGc(
        int number,
        int generation,
        string reason,
        DateTimeOffset startTimestamp,
        ulong heapSizeBefore)
    {
        public int Number { get; } = number;

        public int Generation { get; } = generation;

        public string Reason { get; } = reason;

        public DateTimeOffset StartTimestamp { get; } = startTimestamp;

        public ulong HeapSizeBefore { get; } = heapSizeBefore;

        public ulong? HeapStatsAfter { get; set; }

        public DateTimeOffset? SuspensionBegin { get; set; }

        public List<TimeSpan> SuspensionDurations { get; } = [];
    }

    private readonly record struct PendingSuspension(int GcNumber, DateTimeOffset Begin);
}
