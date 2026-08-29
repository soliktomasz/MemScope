namespace MemoryProfiler.Diagnostics.Sessions;

/// <summary>
/// A single parsed EventCounters sample. Mean-type counters carry their value in
/// <see cref="Mean"/>; Sum- and Count-type counters carry their per-interval delta in
/// <see cref="Increment"/>.
/// </summary>
internal readonly record struct CounterSample(
    string Name,
    double? Mean,
    double? Increment,
    double? IntervalSec,
    DateTimeOffset Timestamp);

internal readonly record struct GcStartObservation(
    int Number,
    int Generation,
    string Reason,
    DateTimeOffset Timestamp);

internal readonly record struct GcStopObservation(
    int Number,
    DateTimeOffset Timestamp);

internal readonly record struct GcHeapStatsObservation(
    ulong HeapSize,
    ulong PromotedBytes,
    DateTimeOffset Timestamp);

internal readonly record struct GcSuspendBeginObservation(
    int GcNumber,
    DateTimeOffset Timestamp);

internal readonly record struct GcRestartBeginObservation(
    DateTimeOffset Timestamp);

internal static class ObservationNumbers
{
    /// <summary>
    /// Converts a defensive CLR numeric value to a non-negative ulong. Non-finite values are
    /// rejected; values outside the unsigned contract are clamped to zero.
    /// </summary>
    public static bool TryClampToUInt64(double? value, out ulong result)
    {
        result = 0;
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return false;
        }

        result = value.Value is < 0 or > ulong.MaxValue ? 0 : (ulong)value.Value;
        return true;
    }
}
