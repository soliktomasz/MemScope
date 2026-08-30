using System.Globalization;
using MemoryProfiler.App.ViewModels.Comparison;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Comparison;

public sealed class TypeDeltaRowViewModelTests
{
    private static TypeMemoryDelta Delta(
        string typeName,
        long countBefore,
        long countAfter,
        long sizeBefore,
        long sizeAfter,
        long? retainedDelta = null) =>
        new(
            typeName,
            countBefore,
            countAfter,
            countAfter - countBefore,
            sizeBefore,
            sizeAfter,
            sizeAfter - sizeBefore,
            retainedDelta);

    [Fact]
    public void CountDeltaFormatsWithSignAndGrouping()
    {
        Assert.Equal($"+{50_000.ToString("N0", CultureInfo.CurrentCulture)}",
            new TypeDeltaRowViewModel(Delta("MyApp.CacheEntry", 0, 50_000, 0, 1)).CountDeltaDisplay);
        Assert.Equal($"-{1_234.ToString("N0", CultureInfo.CurrentCulture)}",
            new TypeDeltaRowViewModel(Delta("System.String", 10_000, 8_766, 1, 1)).CountDeltaDisplay);
        Assert.Equal("0",
            new TypeDeltaRowViewModel(Delta("System.Int32", 100, 100, 1, 1)).CountDeltaDisplay);
    }

    [Fact]
    public void SizeDeltaFormatsAsSignedBytes()
    {
        Assert.Equal("+118 MB",
            new TypeDeltaRowViewModel(Delta("MyApp.CacheEntry", 0, 1, 0, 123_731_968)).SizeDeltaDisplay);
        Assert.Equal("-118 MB",
            new TypeDeltaRowViewModel(Delta("System.String", 1, 0, 123_731_968, 0)).SizeDeltaDisplay);
        Assert.Equal("0 B",
            new TypeDeltaRowViewModel(Delta("System.Int32", 1, 1, 24, 24)).SizeDeltaDisplay);
    }

    [Fact]
    public void RetainedDeltaFormatsAsSignedBytesOrNa()
    {
        var withValue = new TypeDeltaRowViewModel(
            Delta("MyApp.CacheEntry", 1, 2, 1, 1, retainedDelta: 138_412_032));
        Assert.Equal("+132 MB", withValue.RetainedDeltaDisplay);
        Assert.True(withValue.IsRetainedDeltaAvailable);
        Assert.False(withValue.IsRetainedDeltaUnavailable);

        var withoutValue = new TypeDeltaRowViewModel(
            Delta("System.String", 1, 2, 1, 1, retainedDelta: null));
        Assert.Equal("N/A", withoutValue.RetainedDeltaDisplay);
        Assert.False(withoutValue.IsRetainedDeltaAvailable);
        Assert.True(withoutValue.IsRetainedDeltaUnavailable);
    }

    [Fact]
    public void NewAndDisappearedTypesAreDetected()
    {
        var newType = new TypeDeltaRowViewModel(Delta("MyApp.NewCache", 0, 4_000, 0, 268_000_000));
        Assert.True(newType.IsNewType);
        Assert.False(newType.IsDisappearedType);

        var goneType = new TypeDeltaRowViewModel(Delta("MyApp.OldCache", 8_000, 0, 536_000_000, 0));
        Assert.False(goneType.IsNewType);
        Assert.True(goneType.IsDisappearedType);

        var stableType = new TypeDeltaRowViewModel(Delta("System.String", 100, 100, 1_000, 1_000));
        Assert.False(stableType.IsNewType);
        Assert.False(stableType.IsDisappearedType);
    }
}
