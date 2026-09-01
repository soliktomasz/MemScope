using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Performance;

[Collection("Live diagnostics")]
public sealed class HeapWorkloadAcceptanceTests
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public async Task StableTargetExposesRequestedObjectCount(int objectCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var target = await WorkloadTargetFixture.StartAsync(
            "StableMemoryTarget",
            [objectCount.ToString(CultureInfo.InvariantCulture)],
            timeout.Token);
        await using var dump = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);

        var snapshot = await new ClrMdHeapSnapshotLoader()
            .LoadAsync(dump.Path, timeout.Token);

        var marker = Assert.Single(
            snapshot.Types,
            type => type.Name == "StableMemoryTarget.StableMarker");
        Assert.True(
            marker.ObjectCount >= objectCount,
            $"Expected at least {objectCount:N0} stable markers, found {marker.ObjectCount:N0}.");
    }

    [Fact]
    public async Task LargeObjectTargetPlacesRetainedArraysOnTheLoh()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var target = await WorkloadTargetFixture.StartAsync(
            "LargeObjectHeapTarget",
            cancellationToken: timeout.Token);
        await using var dump = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        var snapshot = await new ClrMdHeapSnapshotLoader()
            .LoadAsync(dump.Path, timeout.Token);
        var byteArrays = Assert.Single(
            snapshot.Types,
            type => type.Name == "System.Byte[]");

        var instances = await new ClrMdHeapObjectRepository().GetInstancesAsync(
            snapshot,
            byteArrays.MethodTable,
            timeout.Token);

        var retainedLargeObjects = instances
            .Where(instance => instance.Size >= 100_000 && instance.Generation == "LOH")
            .ToArray();
        Assert.True(
            retainedLargeObjects.Length >= 32,
            $"Expected at least 32 retained LOH arrays, found {retainedLargeObjects.Length}.");
    }
}
