using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class GcCorrelatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GcStopEmitsAnEventCorrelatedWithItsStart()
    {
        var correlator = new GcCorrelator();
        correlator.AddHeapSize(Counter("gc-heap-size", 9_000));
        correlator.AddGcStart(new GcStartObservation(3, 1, "AllocSmall", Start));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(3, Start.AddMilliseconds(10))));

        Assert.Equal(Start, gcEvent.Timestamp);
        Assert.Equal(1, gcEvent.Generation);
        Assert.Equal("AllocSmall", gcEvent.Reason);
        Assert.Equal(9_000ul, gcEvent.HeapSizeBefore);
        Assert.Equal(9_000ul, gcEvent.HeapSizeAfter);
    }

    [Fact]
    public void HeapSizeBeforeUsesTheMostRecentManagedHeapSizeBeforeTheGc()
    {
        var correlator = new GcCorrelator();
        correlator.AddHeapSize(Counter("gc-heap-size", 4_000));
        correlator.AddHeapSize(Counter("gc-heap-size", 8_000));
        correlator.AddGcStart(new GcStartObservation(1, 0, "Induced", Start));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(5))));

        Assert.Equal(8_000ul, gcEvent.HeapSizeBefore);
    }

    [Fact]
    public void HeapSizeAfterUsesTheHeapStatObservedForTheCollection()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(1, 2, "LowMemory", Start));
        correlator.AddGcHeapStats(new GcHeapStatsObservation(6_000, 1_024, Start));
        correlator.AddHeapSize(Counter("gc-heap-size", 9_000));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(5))));

        Assert.Equal(6_000ul, gcEvent.HeapSizeAfter);
    }

    [Fact]
    public void HeapSizeAfterFallsBackToTheLatestManagedHeapMetric()
    {
        var correlator = new GcCorrelator();
        correlator.AddHeapSize(Counter("gc-heap-size", 9_000));
        correlator.AddGcStart(new GcStartObservation(1, 0, "AllocSmall", Start));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(5))));

        Assert.Equal(9_000ul, gcEvent.HeapSizeAfter);
    }

    [Fact]
    public void MissingHeapDataIsRepresentedAsZero()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(1, 0, "AllocSmall", Start));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(5))));

        Assert.Equal(0ul, gcEvent.HeapSizeBefore);
        Assert.Equal(0ul, gcEvent.HeapSizeAfter);
    }

    [Fact]
    public void PauseDurationSumsCompleteSuspensionIntervals()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(1, 0, "AllocSmall", Start));
        correlator.AddSuspensionBegin(new GcSuspendBeginObservation(1, Start.AddMilliseconds(1)));
        correlator.AddRestartBegin(new GcRestartBeginObservation(Start.AddMilliseconds(6)));
        correlator.AddSuspensionBegin(new GcSuspendBeginObservation(1, Start.AddMilliseconds(7)));
        correlator.AddRestartBegin(new GcRestartBeginObservation(Start.AddMilliseconds(11)));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(20))));

        Assert.Equal(TimeSpan.FromMilliseconds(9), gcEvent.PauseDuration);
    }

    [Fact]
    public void PauseDurationFallsBackToElapsedStartToStopTimeWithoutSuspensions()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(1, 0, "AllocSmall", Start));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(12))));

        Assert.Equal(TimeSpan.FromMilliseconds(12), gcEvent.PauseDuration);
    }

    [Fact]
    public void SuspensionStartingBeforeItsGcIsAttachedWhenTheGcStarts()
    {
        var correlator = new GcCorrelator();
        correlator.AddSuspensionBegin(new GcSuspendBeginObservation(1, Start.AddMilliseconds(1)));
        correlator.AddGcStart(new GcStartObservation(1, 0, "AllocSmall", Start));
        correlator.AddRestartBegin(new GcRestartBeginObservation(Start.AddMilliseconds(6)));

        var gcEvent = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(1, Start.AddMilliseconds(20))));

        Assert.Equal(TimeSpan.FromMilliseconds(5), gcEvent.PauseDuration);
    }

    [Fact]
    public void MismatchedStopIsIgnored()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(2, 0, "AllocSmall", Start));

        var gcEvent = correlator.AddGcStop(new GcStopObservation(7, Start.AddMilliseconds(5)));

        Assert.Null(gcEvent);
    }

    [Fact]
    public void ConcurrentGcsAreTrackedIndependentlyByNumber()
    {
        var correlator = new GcCorrelator();
        correlator.AddGcStart(new GcStartObservation(5, 2, "Induced", Start));
        correlator.AddGcStart(new GcStartObservation(6, 0, "AllocSmall", Start.AddMilliseconds(1)));

        var first = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(5, Start.AddMilliseconds(10))));
        var second = Assert.IsType<GcEvent>(correlator.AddGcStop(
            new GcStopObservation(6, Start.AddMilliseconds(12))));

        Assert.Equal(2, first.Generation);
        Assert.Equal("Induced", first.Reason);
        Assert.Equal(0, second.Generation);
    }

    private static CounterSample Counter(string name, double value) =>
        new(name, value, null, 1.0, Start);
}
