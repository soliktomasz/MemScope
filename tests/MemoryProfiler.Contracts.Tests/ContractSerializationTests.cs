using System.Text.Json;
using MemoryProfiler.Contracts.Heap;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Contracts.Processes;
using Xunit;

namespace MemoryProfiler.Contracts.Tests;

public sealed class ContractSerializationTests
{
    public static TheoryData<object> SerializableContracts => new()
    {
        new ProcessInfo(42, "WorkerService", "10.0.0"),
        new HeapTypeInfo(0x1000, "Example.Widget", "Example", 12, 1_024, 2_048),
        new HeapObjectInfo(0x2000, 0x1000, "Example.Widget", 64, "Gen2"),
        new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_child",
            "Example.Container", "Example.Widget"),
        new GcRootInfo(0x4000, 0x2000, "Stack", "main"),
        new GcRootInfo(
            0x4000,
            0x3000,
            "Static field",
            "MyApp.Program._cache",
            [
                new ObjectReference(0x4000, 0x2000, ReferenceKind.Field, "_entries",
                    "MyApp.Cache", "System.Collections.Generic.Dictionary"),
                new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_value",
                    "System.Collections.Generic.Dictionary", "MyApp.CustomerDto"),
            ]),
        new HeapSnapshotInfo("/tmp/example.dmp", "WorkerService", 42, "10.0.0",
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero), 100, 8_192),
        new DominatorInfo(0x5000, "Example.GlobalCache", 4_096, 440_401_920, 12_345),
        new TypeRetainedSize(0x1000, "Example.GlobalCache", 440_401_920),
        new MemoryMetrics(new DateTimeOffset(2026, 8, 28, 12, 0, 1, TimeSpan.Zero),
            8_192, 1_024, 2_048, 3_072, 4_096, 512, 128.5, 1, 2, 3, 512),
        new GcEvent(new DateTimeOffset(2026, 8, 28, 12, 0, 2, TimeSpan.Zero),
            2, TimeSpan.FromMilliseconds(12), 9_000, 6_000, "Induced")
    };

    [Theory]
    [MemberData(nameof(SerializableContracts))]
    public void JsonRoundTripPreservesContract(object expected)
    {
        var json = JsonSerializer.Serialize(expected, expected.GetType());

        var actual = JsonSerializer.Deserialize(json, expected.GetType());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GcRootInfoEqualityComparesPathsStructurally()
    {
        var left = new GcRootInfo(
            0x4000,
            0x3000,
            "Static field",
            "MyApp.Program._cache",
            [
                new ObjectReference(0x4000, 0x2000, ReferenceKind.Field, "_entries",
                    "MyApp.Cache", "System.Collections.Generic.Dictionary"),
            ]);
        var right = new GcRootInfo(
            0x4000,
            0x3000,
            "Static field",
            "MyApp.Program._cache",
            new List<ObjectReference>
            {
                new(0x4000, 0x2000, ReferenceKind.Field, "_entries",
                    "MyApp.Cache", "System.Collections.Generic.Dictionary"),
            });
        var different = new GcRootInfo(
            0x4000,
            0x3000,
            "Static field",
            "MyApp.Program._cache",
            []);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }
}
