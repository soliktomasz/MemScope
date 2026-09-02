using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.Values;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Values;

[Collection("Live diagnostics")]
public sealed class ClrMdHeapObjectValueServiceAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpDecodesControlledCacheProbeFields()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-values-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync(LiveTargetMode.ObjectValues);
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
            var probeType = snapshot.Types.Single(
                type => type.Name == "LiveDiagnosticsTarget.CacheProbe");
            var probe = Assert.Single(
                await new ClrMdHeapObjectRepository()
                    .GetInstancesAsync(snapshot, probeType.MethodTable, timeout.Token));

            var service = new ClrMdHeapObjectValueService();
            var values = await service.ReadAsync(snapshot, probe.Address, new(), timeout.Token);

            Assert.Equal("42", Field(values, "Count").ValueText);
            Assert.Equal("True", Field(values, "Enabled").ValueText);
            Assert.Equal("'M'", Field(values, "Marker").ValueText);
            Assert.Equal("Ready (1)", Field(values, "State").ValueText);
            Assert.Equal("12", Field(values, "Limit").ValueText);
            Assert.Equal(HeapValueKind.Null, Field(values, "MissingLimit").Kind);
            Assert.Equal("1234.5", Field(values, "Price").ValueText);
            Assert.Equal("2026-09-01T12:30:00.0000000Z", Field(values, "CreatedAt").ValueText);
            Assert.Equal("00:15:00", Field(values, "Ttl").ValueText);
            Assert.Equal(
                "01234567-89ab-cdef-0123-456789abcdef",
                Field(values, "Identifier").ValueText);
            Assert.Equal("memscope-value-sentinel", Field(values, "Label").ValueText);
            Assert.True(Field(values, "LongLabel").IsTruncated);
            Assert.Equal(5_000, Field(values, "LongLabel").TotalLength);
            Assert.NotNull(Field(values, "Child").ReferencedObjectAddress);
            Assert.Equal(HeapValueKind.Null, Field(values, "Missing").Kind);

            // Expanded string read restores the full 5,000-character value.
            var expanded = await service.ReadAsync(
                snapshot,
                probe.Address,
                new ObjectValueReadOptions(StringLimit: 1_048_576),
                timeout.Token);
            var longLabel = Field(expanded, "LongLabel");
            Assert.Equal(5_000, longLabel.ValueText!.Length);
            Assert.False(longLabel.IsTruncated);

            // Array paging: 750 elements in stable 500-element pages.
            var numbersAddress = Field(values, "Numbers").ReferencedObjectAddress
                ?? throw new InvalidOperationException(
                    "The Numbers field must carry a referenced object address.");
            var page0 = await service.ReadAsync(
                snapshot,
                numbersAddress,
                new ObjectValueReadOptions(ArrayOffset: 0, ArrayLimit: 500),
                timeout.Token);
            Assert.Equal(500, page0.Fields.Count);
            Assert.Equal("[0]", page0.Fields[0].Name);
            Assert.Equal("[499]", page0.Fields[^1].Name);
            Assert.Equal(HeapValueKind.ArrayElement, page0.Fields[0].Kind);
            Assert.Equal("System.Int32", page0.Fields[0].DeclaredTypeName);
            Assert.Equal("0", page0.Fields[0].ValueText);
            Assert.True(page0.HasMoreElements);

            var payloadAddress = Field(values, "Payload").ReferencedObjectAddress
                ?? throw new InvalidOperationException(
                    "The Payload field must carry a referenced object address.");
            var payload = await service.ReadAsync(
                snapshot,
                payloadAddress,
                new ObjectValueReadOptions(ArrayOffset: 0, ArrayLimit: 1),
                timeout.Token);
            Assert.Equal(HeapValueKind.ArrayElement, payload.Fields[0].Kind);
            Assert.Equal("System.Byte[]", payload.Fields[0].DeclaredTypeName);
            Assert.NotNull(payload.Fields[0].ReferencedObjectAddress);
            Assert.Equal("System.Byte[]", payload.Fields[0].ReferencedObjectTypeName);

            var page500 = await service.ReadAsync(
                snapshot,
                numbersAddress,
                new ObjectValueReadOptions(ArrayOffset: 500, ArrayLimit: 500),
                timeout.Token);
            Assert.Equal(250, page500.Fields.Count);
            Assert.Equal("[500]", page500.Fields[0].Name);
            Assert.Equal("[749]", page500.Fields[^1].Name);
            Assert.False(page500.HasMoreElements);

            // The same snapshot reports the probe as a large retained owner, tying
            // the decoded field values to the Top Retainers retained metric.
            var dominators = await new DominatorTreeService()
                .ComputeDominatorsAsync(snapshot, progress: null, timeout.Token);
            var probeDominator = Assert.Single(
                dominators.Dominators,
                item => item.ObjectAddress == probe.Address);
            Assert.True(probeDominator.RetainedSize >= 1_048_576);
            Assert.True(probeDominator.RetainedObjectCount >= 32);
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

    private static HeapFieldValue Field(HeapObjectValueResult result, string name) =>
        result.Fields.Single(field => field.Name == name);
}
