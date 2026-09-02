using MemoryProfiler.App.ViewModels.Retainers;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Retainers;

public sealed class TopRetainerRowViewModelTests
{
    [Fact]
    public void FormatsMetricsIncludingRetainedPercentage()
    {
        var row = new TopRetainerRowViewModel(
            new DominatorInfo(0x2000, "MyApp.Cache", 64, 400, 12),
            totalReachableBytes: 1_000);

        Assert.Equal("MyApp.Cache", row.TypeName);
        Assert.Equal("0x000000002000", row.AddressDisplay);
        Assert.Equal("64 B", row.ShallowSizeDisplay);
        Assert.Equal("400 B", row.RetainedSizeDisplay);
        Assert.Equal("12", row.RetainedObjectCountDisplay);
        Assert.Equal("40.0%", row.RetainedPercentageDisplay);
    }

    [Fact]
    public void ZeroReachableBytesProducesZeroPercentage()
    {
        var row = new TopRetainerRowViewModel(
            new DominatorInfo(0x2000, "MyApp.Cache", 64, 400, 12),
            totalReachableBytes: 0);

        Assert.Equal("0.0%", row.RetainedPercentageDisplay);
    }

    [Fact]
    public void ExposesTheUnderlyingDominatorInfo()
    {
        var info = new DominatorInfo(0x2000, "MyApp.Cache", 64, 400, 12);
        var row = new TopRetainerRowViewModel(info, 1_000);

        Assert.Same(info, row.Info);
        Assert.Equal(0x2000UL, row.Address);
        Assert.Equal("MyApp.Cache", row.TypeName);
    }
}
