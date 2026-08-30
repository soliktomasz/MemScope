using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Objects;

public sealed class ClrMdHeapObjectRepositoryAcceptanceTests
{
    private static readonly string[] KnownGenerationLabels =
        ["Gen0", "Gen1", "Gen2", "LOH", "Pinned", "Frozen", "Unknown"];

    [Fact]
    public async Task CapturedDumpProducesEveryInstanceOfARequestedType()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-objects-{Guid.NewGuid():N}.dmp");
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
            var strings = snapshot.Types.Single(type => type.Name == "System.String");

            var instances = await new ClrMdHeapObjectRepository()
                .GetInstancesAsync(snapshot, strings.MethodTable, timeout.Token);

            Assert.Equal(strings.ObjectCount, instances.Count);
            Assert.All(
                instances,
                instance =>
                {
                    Assert.Equal(strings.MethodTable, instance.MethodTable);
                    Assert.Equal("System.String", instance.TypeName);
                    Assert.True(instance.Address > 0, "Instance address must be non-zero.");
                    Assert.True(instance.Size > 0, "Instance size must be non-zero.");
                    Assert.Contains(instance.Generation, KnownGenerationLabels);
                });
            Assert.True(
                instances.Zip(instances.Skip(1)).All(pair => pair.First.Address <= pair.Second.Address),
                "Instances must be ordered by address ascending.");
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
