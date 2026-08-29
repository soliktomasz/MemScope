using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class MemoryMetricsAccumulatorTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HeapSizeSampleEmitsSnapshotWithLatestCounterValues()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(Counter("gen-0-size", 1_024));
        accumulator.Add(Counter("gen-1-size", 2_048));
        accumulator.Add(Counter("gen-2-size", 3_072));
        accumulator.Add(Counter("loh-size", 4_096));
        accumulator.Add(Counter("poh-size", 512));
        accumulator.Add(new CounterSample("alloc-rate", null, 124_195.0, 1.0, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 8_192)));

        Assert.NotNull(snapshot);
        Assert.Equal(Timestamp, snapshot.Timestamp);
        Assert.Equal(8_192ul, snapshot.ManagedHeapSize);
        Assert.Equal(1_024ul, snapshot.Generation0Size);
        Assert.Equal(2_048ul, snapshot.Generation1Size);
        Assert.Equal(3_072ul, snapshot.Generation2Size);
        Assert.Equal(4_096ul, snapshot.LargeObjectHeapSize);
        Assert.Equal(512ul, snapshot.PinnedObjectHeapSize);
        Assert.Equal(124_195.0, snapshot.AllocationRateBytesPerSecond);
        Assert.Equal(0, snapshot.Generation0Collections);
        Assert.Equal(0, snapshot.Generation1Collections);
        Assert.Equal(0, snapshot.Generation2Collections);
        Assert.Equal(0ul, snapshot.PromotedBytes);
    }

    [Fact]
    public void NonHeapSizeSamplesDoNotEmitSnapshots()
    {
        var accumulator = new MemoryMetricsAccumulator();

        var snapshot = accumulator.Add(Counter("gen-0-size", 1_024));

        Assert.Null(snapshot);
    }

    [Fact]
    public void CollectionCountsAccumulateForTheLifetimeOfTheSession()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(new CounterSample("gen-0-gc-count", null, 1.0, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, 2.0, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, 3.0, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-1-gc-count", null, 4.0, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-2-gc-count", null, 5.0, 1.0, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(6, snapshot.Generation0Collections);
        Assert.Equal(4, snapshot.Generation1Collections);
        Assert.Equal(5, snapshot.Generation2Collections);
    }

    [Fact]
    public void CollectionCountsIgnoreNegativeAndNonFiniteIncrements()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(new CounterSample("gen-0-gc-count", null, -1.0, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, double.NaN, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, double.PositiveInfinity, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, null, 1.0, Timestamp));
        accumulator.Add(new CounterSample("gen-0-gc-count", null, 2.0, 1.0, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(2, snapshot.Generation0Collections);
    }

    [Fact]
    public void PromotedBytesComeFromGcHeapStatObservations()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(Counter("gen-0-size", 512));

        var first = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 1_000)));
        Assert.Equal(0ul, first.PromotedBytes);

        accumulator.AddPromotedBytes(4_096);
        var second = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 2_000)));

        Assert.Equal(4_096ul, second.PromotedBytes);
    }

    [Fact]
    public void AllocationRateUsesIncrementOverTheSamplingInterval()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(new CounterSample("alloc-rate", null, 500.0, 0.5, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(1_000.0, snapshot.AllocationRateBytesPerSecond);
    }

    [Fact]
    public void AllocationRateFallsBackToOneSecondWhenIntervalIsInvalid()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(new CounterSample("alloc-rate", null, 250.0, 0.0, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(250.0, snapshot.AllocationRateBytesPerSecond);
    }

    [Fact]
    public void UnknownCountersAreIgnoredWithoutTerminatingTheSession()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(new CounterSample("working-set", 12_345.0, null, 1.0, Timestamp));
        accumulator.Add(new CounterSample("threadpool-queue-length", 7.0, null, 1.0, Timestamp));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.NotNull(snapshot);
        Assert.Equal(100ul, snapshot.ManagedHeapSize);
    }

    [Fact]
    public void NonFiniteHeapSizeSampleIsRejectedWithoutEmittingASnapshot()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(Counter("gen-0-size", 1_024));

        var rejected = accumulator.Add(Counter("gc-heap-size", double.PositiveInfinity));
        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 4_096)));

        Assert.Null(rejected);
        Assert.NotNull(snapshot);
        Assert.Equal(1_024ul, snapshot.Generation0Size);
    }

    [Fact]
    public void NegativeSizesAreClampedToZero()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(Counter("gen-0-size", -512));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(0ul, snapshot.Generation0Size);
    }

    [Fact]
    public void NonFiniteSizeSampleKeepsThePreviousValue()
    {
        var accumulator = new MemoryMetricsAccumulator();
        accumulator.Add(Counter("gen-0-size", 1_024));
        accumulator.Add(Counter("gen-0-size", double.NaN));

        var snapshot = Assert.IsType<MemoryMetrics>(accumulator.Add(Counter("gc-heap-size", 100)));

        Assert.Equal(1_024ul, snapshot.Generation0Size);
    }

    private static CounterSample Counter(string name, double value) =>
        new(name, value, null, 1.0, Timestamp);
}
