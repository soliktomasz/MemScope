using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Dominators;

[Collection("Live diagnostics")]
public sealed class DominatorTreeServiceAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpReportsTheChunkOwnerAsTheDominantRetainedObject()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-dominators-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync(leakPhase: true);
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var client = new DiagnosticsClient(fixture.ProcessId);
            await client.WriteDumpAsync(
                DumpType.WithHeap,
                destination,
                WriteDumpFlags.None,
                timeout.Token);

            var snapshot = await new ClrMdHeapSnapshotLoader()
                .LoadAsync(destination, timeout.Token);
            var service = new DominatorTreeService();

            var result = await service.ComputeDominatorsAsync(
                snapshot,
                cancellationToken: timeout.Token);

            // The target keeps every allocated 64 KiB chunk in one
            // List<byte[]>, so that list dominates the whole chunk graph.
            // Runtime-owned objects may retain more memory on some platforms,
            // so locate the target's owner by type rather than global rank.
            var owner = Assert.Single(
                result.Dominators,
                dominator => dominator.TypeName ==
                    "System.Collections.Generic.List<System.Byte[]>");
            Assert.True(
                owner.RetainedSize >= 1_048_576,
                $"Expected the chunk owner to retain at least 1 MB, was {owner.RetainedSize}.");
            Assert.True(
                owner.RetainedObjectCount >= 16,
                $"Expected the chunk owner to dominate at least 16 objects, was {owner.RetainedObjectCount}.");

            // The same owner leads the per-type view (a chunk captured mid-Add
            // may briefly sit outside the list, so the type list is checked
            // for the owner's large retained size rather than its exact rank),
            // and the retained sizes are cached per snapshot: a second query
            // reuses the first result.
            var listType = Assert.Single(
                result.TypeRetainedSizes,
                type => type.TypeName == "System.Collections.Generic.List<System.Byte[]>");
            Assert.True(
                listType.RetainedSize >= 1_048_576,
                $"Expected the chunk-owner type to retain at least 1 MB, was {listType.RetainedSize}.");

            var cached = await service.ComputeDominatorsAsync(
                snapshot,
                cancellationToken: timeout.Token);
            Assert.Same(result, cached);
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
