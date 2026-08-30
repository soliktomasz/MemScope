using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Loading;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Comparison;

[Collection("Live diagnostics")]
public sealed class SnapshotComparisonServiceAcceptanceTests
{
    [Fact]
    public async Task ControlledLeakAppearsNearTheTopOfComparisonResults()
    {
        var beforePath = Path.Combine(
            Path.GetTempPath(),
            $"memscope-compare-before-{Guid.NewGuid():N}.dmp");
        var afterPath = Path.Combine(
            Path.GetTempPath(),
            $"memscope-compare-after-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync(leakPhase: true);
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var client = new DiagnosticsClient(fixture.ProcessId);

            // Dump A: the target holds only its fixed 128-chunk baseline.
            await client.WriteDumpAsync(
                DumpType.WithHeap,
                beforePath,
                WriteDumpFlags.None,
                timeout.Token);

            // Introduce the controlled leak and let it grow: one 64 KiB chunk
            // every 50 ms, so ~60 chunks (~3.8 MB) after 3 s.
            await fixture.StartLeakAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Dump B: the baseline plus the leaked chunks.
            await client.WriteDumpAsync(
                DumpType.WithHeap,
                afterPath,
                WriteDumpFlags.None,
                timeout.Token);

            var loader = new ClrMdHeapSnapshotLoader();
            var before = await loader.LoadAsync(beforePath, timeout.Token);
            var after = await loader.LoadAsync(afterPath, timeout.Token);

            var result = new SnapshotComparisonService().Compare(before, after);

            // The leak allocates only byte[] chunks, so System.Byte[] must be
            // the single biggest grower and lead the default (size-delta-desc)
            // order — the controlled leak "appears near the top".
            var top = result.Deltas[0];
            Assert.Equal("System.Byte[]", top.TypeName);
            Assert.True(
                top.SizeDelta >= 1_048_576,
                $"Expected the leaked chunks to grow System.Byte[] by at least 1 MB, was {top.SizeDelta}.");
            Assert.True(
                top.CountDelta >= 16,
                $"Expected at least 16 leaked chunks, was {top.CountDelta}.");
            Assert.True(
                top.SizeBefore > 0,
                "Expected the baseline chunks to be present in the before dump.");

            // Everything else grew by orders of magnitude less (runtime noise),
            // so the leaked byte arrays are not merely present but dominant.
            Assert.True(
                result.Deltas.Count == 1 ||
                top.SizeDelta >= 10 * result.Deltas[1].SizeDelta,
                "Expected the byte[] growth to dominate every other type's growth.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambientTempDir);
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            foreach (var path in new[] { beforePath, afterPath })
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Preserve any capture or analysis failure; cleanup is best effort.
                }
            }
        }
    }
}
