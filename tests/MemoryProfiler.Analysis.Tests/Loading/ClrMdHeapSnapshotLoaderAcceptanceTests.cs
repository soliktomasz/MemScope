using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Loading;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Loading;

[Collection("Live diagnostics")]
public sealed class ClrMdHeapSnapshotLoaderAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpProducesManagedHeapTypes()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-analysis-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync();
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var client = new DiagnosticsClient(fixture.ProcessId);
            var captureStarted = DateTimeOffset.UtcNow;
            await client.WriteDumpAsync(
                DumpType.WithHeap,
                destination,
                WriteDumpFlags.None,
                timeout.Token);
            var captureFinished = DateTimeOffset.UtcNow;

            var snapshot = await new ClrMdHeapSnapshotLoader()
                .LoadAsync(destination, timeout.Token);

            Assert.Equal(destination, snapshot.Info.Path);
            Assert.True(
                snapshot.Info.ProcessId is null ||
                snapshot.Info.ProcessId == fixture.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Info.RuntimeVersion));
            Assert.InRange(
                snapshot.Info.CapturedAt,
                captureStarted.AddSeconds(-1),
                captureFinished.AddSeconds(1));
            Assert.True(snapshot.Info.ObjectCount > 0);
            Assert.True(snapshot.Info.HeapSize > 0);
            Assert.NotEmpty(snapshot.Types);
            Assert.Contains(snapshot.Types, type => type.Name == "System.String");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambientTempDir);
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            try
            {
                File.Delete(destination);
            }
            catch
            {
                // Preserve any capture or analysis failure; cleanup is best effort.
            }
        }
    }
}
