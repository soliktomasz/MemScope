using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Comparison;

public sealed class SnapshotComparisonServiceTests
{
    private static readonly HeapSnapshotInfo SampleInfo = new(
        "/tmp/sample.dmp",
        "Sample.Process",
        4217,
        "10.0.0",
        new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
        100,
        8_192);

    private static HeapSnapshot Snapshot(params HeapTypeInfo[] types) =>
        new() { Info = SampleInfo, Types = types };

    private static HeapTypeInfo Type(
        string name,
        long count,
        ulong size,
        ulong? retainedSize = null) =>
        new(0x1000, name, "Sample", count, size, retainedSize);

    [Fact]
    public void ComputesCountAndSizeDeltasForSharedTypes()
    {
        var before = Snapshot(
            Type("MyApp.CacheEntry", 50_000, 118_400_000),
            Type("System.String", 381_235, 44_200_000));
        var after = Snapshot(
            Type("MyApp.CacheEntry", 100_000, 236_800_000),
            Type("System.String", 461_576, 56_200_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        var cacheEntry = Assert.Single(result.Deltas, delta => delta.TypeName == "MyApp.CacheEntry");
        Assert.Equal(50_000, cacheEntry.CountBefore);
        Assert.Equal(100_000, cacheEntry.CountAfter);
        Assert.Equal(50_000, cacheEntry.CountDelta);
        Assert.Equal(118_400_000, cacheEntry.SizeBefore);
        Assert.Equal(236_800_000, cacheEntry.SizeAfter);
        Assert.Equal(118_400_000, cacheEntry.SizeDelta);
    }

    [Fact]
    public void NewTypeSurfacesAsPositiveDelta()
    {
        var before = Snapshot(Type("System.String", 10, 1_000));
        var after = Snapshot(
            Type("System.String", 10, 1_000),
            Type("MyApp.LeakedCache", 4_000, 268_000_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        var leaked = Assert.Single(result.Deltas, delta => delta.TypeName == "MyApp.LeakedCache");
        Assert.Equal(0, leaked.CountBefore);
        Assert.Equal(4_000, leaked.CountAfter);
        Assert.Equal(4_000, leaked.CountDelta);
        Assert.Equal(0, leaked.SizeBefore);
        Assert.Equal(268_000_000, leaked.SizeAfter);
        Assert.Equal(268_000_000, leaked.SizeDelta);
    }

    [Fact]
    public void DisappearedTypeSurfacesAsNegativeDelta()
    {
        var before = Snapshot(
            Type("System.String", 10, 1_000),
            Type("MyApp.OldCache", 8_000, 536_000_000));
        var after = Snapshot(Type("System.String", 10, 1_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        var oldCache = Assert.Single(result.Deltas, delta => delta.TypeName == "MyApp.OldCache");
        Assert.Equal(8_000, oldCache.CountBefore);
        Assert.Equal(0, oldCache.CountAfter);
        Assert.Equal(-8_000, oldCache.CountDelta);
        Assert.Equal(536_000_000, oldCache.SizeBefore);
        Assert.Equal(0, oldCache.SizeAfter);
        Assert.Equal(-536_000_000, oldCache.SizeDelta);
    }

    [Fact]
    public void RetainedDeltaIsComputedWhenBothSidesCarryRetainedSizes()
    {
        var before = Snapshot(
            Type("System.Byte[]", 1_024, 67_108_864, retainedSize: 80_000_000));
        var after = Snapshot(
            Type("System.Byte[]", 10_000, 655_360_000, retainedSize: 700_000_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        var bytes = Assert.Single(result.Deltas);
        Assert.Equal(620_000_000, bytes.RetainedSizeDelta);
    }

    [Fact]
    public void RetainedDeltaIsNegativeWhenMemoryShrinks()
    {
        var before = Snapshot(
            Type("System.Byte[]", 10_000, 655_360_000, retainedSize: 700_000_000));
        var after = Snapshot(
            Type("System.Byte[]", 1_024, 67_108_864, retainedSize: 80_000_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        var bytes = Assert.Single(result.Deltas);
        Assert.Equal(-620_000_000, bytes.RetainedSizeDelta);
    }

    [Fact]
    public void RetainedDeltaIsNullWhenEitherSideLacksRetainedSizes()
    {
        var before = Snapshot(
            Type("System.Byte[]", 1_024, 67_108_864, retainedSize: 80_000_000));
        var after = Snapshot(
            Type("System.Byte[]", 10_000, 655_360_000, retainedSize: null));

        var result = new SnapshotComparisonService().Compare(before, after);

        Assert.Null(Assert.Single(result.Deltas).RetainedSizeDelta);
    }

    [Fact]
    public void SortsByBiggestGrowthFirstThenCountThenName()
    {
        var before = Snapshot(
            Type("MyApp.Zeta", 100, 10_000),
            Type("MyApp.Alpha", 100, 10_000),
            Type("MyApp.Mid", 50, 50_000));
        var after = Snapshot(
            Type("MyApp.Zeta", 150, 10_000),
            Type("MyApp.Alpha", 200, 10_000),
            Type("MyApp.Mid", 60, 300_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        // Biggest size growth leads; the two tied +0 size-growth types are
        // ordered by count growth, then by type name (Alpha before Zeta).
        Assert.Collection(
            result.Deltas,
            delta => Assert.Equal("MyApp.Mid", delta.TypeName),
            delta => Assert.Equal("MyApp.Alpha", delta.TypeName),
            delta => Assert.Equal("MyApp.Zeta", delta.TypeName));
    }

    [Fact]
    public void ClampsHugeSizesToLongRange()
    {
        var before = Snapshot(Type("System.Byte[]", 1, ulong.MaxValue));
        var after = Snapshot(Type("System.Byte[]", 1, 0));

        var result = new SnapshotComparisonService().Compare(before, after);

        var bytes = Assert.Single(result.Deltas);
        Assert.Equal(long.MaxValue, bytes.SizeBefore);
        Assert.Equal(0, bytes.SizeAfter);
        Assert.Equal(-long.MaxValue, bytes.SizeDelta);
    }

    [Fact]
    public void RetainedDeltaClampsToLongRange()
    {
        var before = Snapshot(
            Type("System.Byte[]", 1, 1_000, retainedSize: 0));
        var after = Snapshot(
            Type("System.Byte[]", 1, 1_000, retainedSize: ulong.MaxValue));

        var result = new SnapshotComparisonService().Compare(before, after);

        Assert.Equal(long.MaxValue, Assert.Single(result.Deltas).RetainedSizeDelta);
    }

    [Fact]
    public void EmptySnapshotsProduceEmptyDeltas()
    {
        var result = new SnapshotComparisonService().Compare(Snapshot(), Snapshot());

        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void RejectsMissingSnapshots()
    {
        var service = new SnapshotComparisonService();
        var snapshot = Snapshot(Type("System.String", 1, 24));

        Assert.Throws<ArgumentNullException>(() => service.Compare(null!, snapshot));
        Assert.Throws<ArgumentNullException>(() => service.Compare(snapshot, null!));
    }

    [Fact]
    public void TypeNamesAreMergedCaseSensitively()
    {
        var before = Snapshot(Type("MyApp.Widget", 10, 1_000));
        var after = Snapshot(Type("myapp.widget", 20, 2_000));

        var result = new SnapshotComparisonService().Compare(before, after);

        // Different names are different types: each side appears separately
        // (the after-only one as a new type, the before-only one as gone).
        Assert.Collection(
            result.Deltas,
            delta => Assert.Equal("myapp.widget", delta.TypeName),
            delta => Assert.Equal("MyApp.Widget", delta.TypeName));
    }
}
