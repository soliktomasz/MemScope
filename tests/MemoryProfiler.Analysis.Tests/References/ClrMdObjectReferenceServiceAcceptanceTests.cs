using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.References;

[Collection("Live diagnostics")]
public sealed class ClrMdObjectReferenceServiceAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpSupportsNavigatingObjectToObjectThroughReferences()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-references-{Guid.NewGuid():N}.dmp");
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
            var service = new ClrMdObjectReferenceService();
            var repository = new ClrMdHeapObjectRepository();

            // A 64 KiB chunk the target allocated and pinned inside its
            // List<byte[]>: a byte[] has no outgoing references of its own.
            var byteArrayType = Assert.Single(
                snapshot.Types.Where(type => type.Name == "System.Byte[]"));
            var chunks = await repository.GetInstancesAsync(
                snapshot, byteArrayType.MethodTable, timeout.Token);
            var chunk = Assert.Single(
                chunks.Where(instance => instance.Size >= 64 * 1024));

            var chunkOutgoing = await service.GetOutgoingReferencesAsync(
                snapshot, chunk.Address, timeout.Token);
            Assert.Empty(chunkOutgoing);

            // The chunk must be kept alive by the List's backing array and
            // never be reported as referenced by nothing.
            var chunkIncoming = await service.GetIncomingReferencesAsync(
                snapshot, chunk.Address, timeout.Token);
            Assert.NotEmpty(chunkIncoming);
            var heapSources = chunkIncoming
                .Where(reference => reference.SourceAddress != 0)
                .ToArray();
            Assert.NotEmpty(heapSources);
            var itemsSlot = Assert.Single(
                heapSources.Where(reference => reference.Kind == ReferenceKind.ArrayElement));
            Assert.Equal(chunk.Address, itemsSlot.TargetAddress);

            // Navigate from the chunk to the List's backing array, then back.
            var itemsOutgoing = await service.GetOutgoingReferencesAsync(
                snapshot, itemsSlot.SourceAddress, timeout.Token);
            Assert.NotEmpty(itemsOutgoing);
            Assert.All(
                itemsOutgoing,
                reference => Assert.Equal(ReferenceKind.ArrayElement, reference.Kind));
            Assert.Contains(
                itemsOutgoing,
                reference => reference.TargetAddress == chunk.Address);

            // The List object exposes the same relationship as a named field.
            var listType = Assert.Single(
                snapshot.Types.Where(type =>
                    type.Name.Contains("List", StringComparison.Ordinal) &&
                    type.Name.Contains("Byte[]", StringComparison.Ordinal)));
            var lists = await repository.GetInstancesAsync(
                snapshot, listType.MethodTable, timeout.Token);
            var list = Assert.Single(lists);
            var listOutgoing = await service.GetOutgoingReferencesAsync(
                snapshot, list.Address, timeout.Token);
            Assert.Contains(
                listOutgoing,
                reference =>
                    reference.Kind == ReferenceKind.Field &&
                    reference.Name == "_items" &&
                    reference.SourceTypeName == listType.Name &&
                    reference.TargetAddress == itemsSlot.SourceAddress);
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
