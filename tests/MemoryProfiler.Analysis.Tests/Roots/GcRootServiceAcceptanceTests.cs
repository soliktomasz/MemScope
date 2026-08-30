using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.Roots;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Roots;

[Collection("Live diagnostics")]
public sealed class GcRootServiceAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpIdentifiesTheChainKeepingARetainedObjectAlive()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-gc-roots-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync();
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
            var service = new GcRootService();
            var repository = new ClrMdHeapObjectRepository();

            // A 64 KiB chunk the target allocated and retained inside its
            // List<byte[]>: it must be reported as reachable from a GC root,
            // never as garbage. The target may have produced several chunks
            // before capture, so the largest one is used.
            var byteArrayType = Assert.Single(
                snapshot.Types, type => type.Name == "System.Byte[]");
            var chunks = await repository.GetInstancesAsync(
                snapshot, byteArrayType.MethodTable, timeout.Token);
            var matching = chunks
                .Where(instance => instance.Size >= 64 * 1024)
                .OrderByDescending(instance => instance.Size)
                .ToArray();
            Assert.NotEmpty(matching);
            var chunk = matching[0];

            var roots = await service.FindRootsAsync(
                snapshot, chunk.Address, timeout.Token);

            Assert.NotEmpty(roots);

            Assert.All(
                roots,
                root =>
                {
                    Assert.Equal(chunk.Address, root.ObjectAddress);
                    Assert.False(string.IsNullOrWhiteSpace(root.Kind));
                });

            // The target's main thread keeps the chunk alive through its local
            // list: the profiler must surface a well-formed chain from a root
            // down to the chunk.
            var chainRoots = roots
                .Where(root => root.Path is { Count: > 0 })
                .ToArray();
            Assert.NotEmpty(chainRoots);
            var chainRoot = chainRoots[0];
            Assert.Equal(chainRoot.RootAddress, chainRoot.Path![0].SourceAddress);
            Assert.Equal(chunk.Address, chainRoot.Path[^1].TargetAddress);
            for (var index = 1; index < chainRoot.Path.Count; index++)
            {
                Assert.Equal(
                    chainRoot.Path[index - 1].TargetAddress,
                    chainRoot.Path[index].SourceAddress);
            }
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
