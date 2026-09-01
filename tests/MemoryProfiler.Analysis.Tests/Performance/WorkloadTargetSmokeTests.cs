using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Performance;

public sealed class WorkloadTargetSmokeTests
{
    [Theory]
    [InlineData("StableMemoryTarget")]
    [InlineData("GrowingMemoryTarget")]
    [InlineData("HighAllocationTarget")]
    [InlineData("LargeObjectHeapTarget")]
    [InlineData("GcPressureTarget")]
    [InlineData("RootedObjectTarget")]
    public async Task TargetSignalsReadyAndRemainsAlive(string assemblyName)
    {
        await using var target = await WorkloadTargetFixture.StartAsync(assemblyName);

        Assert.True(target.ProcessId > 0);
        Assert.False(target.HasExited);
    }

    [Fact]
    public async Task MissingTargetFailureNamesTheAssembly()
    {
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => WorkloadTargetFixture.StartAsync("MissingWorkloadTarget"));

        Assert.Contains("MissingWorkloadTarget", exception.Message, StringComparison.Ordinal);
    }
}
