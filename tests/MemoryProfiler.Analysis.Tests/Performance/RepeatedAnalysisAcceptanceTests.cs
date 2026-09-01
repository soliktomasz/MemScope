using System.Runtime.CompilerServices;
using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Performance;

[Collection("Live diagnostics")]
public sealed class RepeatedAnalysisAcceptanceTests
{
    [Fact]
    public async Task GrowingTargetProducesAPositiveLeakDelta()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var target = await WorkloadTargetFixture.StartAsync(
            "GrowingMemoryTarget",
            cancellationToken: timeout.Token);
        await using var beforeDump = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        await using var afterDump = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        var loader = new ClrMdHeapSnapshotLoader();

        var before = await loader.LoadAsync(beforeDump.Path, timeout.Token);
        var after = await loader.LoadAsync(afterDump.Path, timeout.Token);
        var comparison = new SnapshotComparisonService().Compare(before, after);

        var leak = Assert.Single(
            comparison.Deltas,
            delta => delta.TypeName == "GrowingMemoryTarget.LeakPayload");
        Assert.True(leak.CountDelta > 0);
        Assert.True(leak.SizeDelta > 0);
    }

    [Fact]
    public async Task RepeatedSnapshotLoadsReleaseProfilerMemory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var target = await WorkloadTargetFixture.StartAsync(
            "StableMemoryTarget",
            ["100000"],
            timeout.Token);
        await using var first = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        await using var second = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        await using var third = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        var paths = new[] { first.Path, second.Path, third.Path };
        var loader = new ClrMdHeapSnapshotLoader();
        _ = await loader.LoadAsync(first.Path, timeout.Token);
        var before = ProfilerMemoryProbe.MeasureRetainedBytes();

        var (references, peak) = await LoadSnapshotsAsync(loader, paths, timeout.Token);
        var after = ProfilerMemoryProbe.MeasureRetainedBytes();

        Assert.InRange(references.Count(reference => reference.IsAlive), 0, 1);
        Assert.True(
            ProfilerMemoryProbe.IsGrowthWithin(
                before,
                after,
                peak,
                fixedAllowanceBytes: 8 * 1024 * 1024),
            $"Retained profiler memory grew by {after - before:N0} bytes.");
    }

    [Fact]
    public async Task RepeatedRootAnalysisReleasesResults()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var target = await WorkloadTargetFixture.StartAsync(
            "RootedObjectTarget",
            cancellationToken: timeout.Token);
        await using var dump = await HeapDumpFixture.CaptureAsync(
            target.ProcessId,
            target.SocketRoot,
            timeout.Token);
        var snapshot = await new ClrMdHeapSnapshotLoader()
            .LoadAsync(dump.Path, timeout.Token);
        var payloadType = Assert.Single(
            snapshot.Types,
            type => type.Name == "RootedObjectTarget.RootPayload");
        var payload = Assert.Single(
            await new ClrMdHeapObjectRepository().GetInstancesAsync(
                snapshot,
                payloadType.MethodTable,
                timeout.Token));
        var service = new GcRootService();
        _ = await service.FindRootsAsync(snapshot, payload.Address, timeout.Token);
        var before = ProfilerMemoryProbe.MeasureRetainedBytes();

        var (references, peak) = await QueryRootsAsync(
            service,
            snapshot,
            payload.Address,
            timeout.Token);
        var after = ProfilerMemoryProbe.MeasureRetainedBytes();

        Assert.InRange(references.Count(reference => reference.IsAlive), 0, 1);
        Assert.True(
            ProfilerMemoryProbe.IsGrowthWithin(
                before,
                after,
                peak,
                fixedAllowanceBytes: 8 * 1024 * 1024),
            $"Retained profiler memory grew by {after - before:N0} bytes.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IReadOnlyList<WeakReference> References, long Peak)>
        LoadSnapshotsAsync(
            IHeapSnapshotLoader loader,
            IEnumerable<string> paths,
            CancellationToken cancellationToken)
    {
        var references = new List<WeakReference>();
        long peak = 0;
        foreach (var path in paths)
        {
            var snapshot = await loader.LoadAsync(path, cancellationToken);
            Assert.True(snapshot.Info.ObjectCount >= 100_000);
            references.Add(new WeakReference(snapshot));
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            snapshot = null!;
        }

        return (references, peak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IReadOnlyList<WeakReference> References, long Peak)>
        QueryRootsAsync(
            IGcRootService service,
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken)
    {
        var references = new List<WeakReference>();
        long peak = 0;
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var roots = await service.FindRootsAsync(snapshot, objectAddress, cancellationToken);
            Assert.Contains(roots, root => root.Path is { Count: > 0 });
            references.Add(new WeakReference(roots));
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            roots = null!;
        }

        return (references, peak);
    }
}
