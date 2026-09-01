using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;
using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class WorkloadDiagnosticsAcceptanceTests
{
    private const double HighAllocationThreshold = 10 * 1024 * 1024;

    [Fact]
    public async Task HighAllocationTargetReportsAllocationActivity()
    {
        var metrics = await ObserveUntilAsync(
            "HighAllocationTarget",
            sample => sample.AllocationRateBytesPerSecond >= HighAllocationThreshold);

        Assert.True(metrics.AllocationRateBytesPerSecond >= HighAllocationThreshold);
    }

    [Fact]
    public async Task GcPressureTargetReportsCollectionActivity()
    {
        var metrics = await ObserveUntilAsync(
            "GcPressureTarget",
            sample => sample.Generation0Collections +
                sample.Generation1Collections +
                sample.Generation2Collections > 0);

        Assert.True(
            metrics.Generation0Collections +
            metrics.Generation1Collections +
            metrics.Generation2Collections > 0);
    }

    private static async Task<MemoryMetrics> ObserveUntilAsync(
        string assemblyName,
        Func<MemoryMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var target = await WorkloadTargetFixture.StartAsync(
            assemblyName,
            cancellationToken: timeout.Token);
        await using var environment = await ProcessEnvironmentScope
            .EnterTempDirectoryAsync(target.SocketRoot, timeout.Token);
        await using var session = await new LiveDiagnosticsSessionFactory()
            .ConnectAsync(target.ProcessId, timeout.Token);
        await foreach (var metrics in session.ObserveMemoryAsync(timeout.Token))
        {
            if (predicate(metrics))
            {
                return metrics;
            }
        }

        throw new InvalidOperationException(
            $"Target '{assemblyName}' completed without matching metrics.");
    }
}
