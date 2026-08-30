using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Dominators;

public sealed class DominatorTreeServiceTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static HeapSnapshot Snapshot(string path = "sample.dmp") =>
        Snapshot(path, CapturedAt);

    private static HeapSnapshot Snapshot(string path, DateTimeOffset capturedAt) =>
        new()
        {
            Info = new HeapSnapshotInfo(
                path,
                "Sample.Process",
                4217,
                "10.0.0",
                capturedAt,
                8,
                4_096),
            Types = [],
        };

    private static HeapObjectData Object(
        ulong address,
        string typeName,
        ulong size,
        ulong methodTable) =>
        new(methodTable, typeName, "Sample", size, address, IsValid: true, IsFree: false);

    [Fact]
    public async Task ComputesRetainedSizesForAChain()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 20, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
            Object(0x3000, "MyApp.B", 60, 0x300),
            Object(0x4000, "MyApp.C", 40, 0x400),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_b", "MyApp.A", "MyApp.B"),
        ];
        source.Outgoing[0x3000] =
        [
            new ObjectReference(0x3000, 0x4000, ReferenceKind.Field, "_c", "MyApp.B", "MyApp.C"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        // The root dominates A, B and C, so it retains the whole chain; A
        // retains B and C; B retains C. The list is ordered by retained size.
        Assert.Collection(
            result.Dominators,
            root =>
            {
                Assert.Equal(0x1000UL, root.ObjectAddress);
                Assert.Equal("MyApp.Root", root.TypeName);
                Assert.Equal(20UL, root.ShallowSize);
                Assert.Equal(220UL, root.RetainedSize);
                Assert.Equal(4, root.RetainedObjectCount);
            },
            a =>
            {
                Assert.Equal(0x2000UL, a.ObjectAddress);
                Assert.Equal("MyApp.A", a.TypeName);
                Assert.Equal(100UL, a.ShallowSize);
                Assert.Equal(200UL, a.RetainedSize);
                Assert.Equal(3, a.RetainedObjectCount);
            },
            b =>
            {
                Assert.Equal(0x3000UL, b.ObjectAddress);
                Assert.Equal(100UL, b.RetainedSize);
                Assert.Equal(2, b.RetainedObjectCount);
            },
            c =>
            {
                Assert.Equal(0x4000UL, c.ObjectAddress);
                Assert.Equal(40UL, c.RetainedSize);
                Assert.Equal(1, c.RetainedObjectCount);
            });
    }

    [Fact]
    public async Task MultipleRootsShareTheDominatedNodeThroughTheSyntheticRoot()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.R1", 10, 0x100),
            Object(0x2000, "MyApp.R2", 20, 0x200),
            Object(0x3000, "MyApp.A", 100, 0x300),
            Object(0x4000, "MyApp.B", 50, 0x400),
        ]);
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.R1"),
            new ClrRootData(0x2000, ClrRootKind.StaticVar, "MyApp.R2"),
        ]);
        // Diamond: both roots reach A, so neither root dominates A; the
        // synthetic root does. A dominates B.
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_a", "MyApp.R1", "MyApp.A"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_a", "MyApp.R2", "MyApp.A"),
        ];
        source.Outgoing[0x3000] =
        [
            new ObjectReference(0x3000, 0x4000, ReferenceKind.Field, "_b", "MyApp.A", "MyApp.B"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        var a = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x3000);
        Assert.Equal(150UL, a.RetainedSize); // A + B, not the roots' memory.
        Assert.Equal(2, a.RetainedObjectCount);
        var r1 = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x1000);
        Assert.Equal(10UL, r1.RetainedSize);
        var r2 = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x2000);
        Assert.Equal(20UL, r2.RetainedSize);
    }

    [Fact]
    public async Task CyclesTerminateAndAccumulateOnce()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
            Object(0x3000, "MyApp.B", 50, 0x300),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        // A <-> B cycle reachable from the root.
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_b", "MyApp.A", "MyApp.B"),
        ];
        source.Outgoing[0x3000] =
        [
            new ObjectReference(0x3000, 0x2000, ReferenceKind.Field, "_a", "MyApp.B", "MyApp.A"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        var a = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x2000);
        Assert.Equal(150UL, a.RetainedSize);
        Assert.Equal(2, a.RetainedObjectCount);
        var b = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x3000);
        Assert.Equal(50UL, b.RetainedSize);
        Assert.Equal(1, b.RetainedObjectCount);
    }

    [Fact]
    public async Task UnreachableObjectsAreExcludedFromDominatorsAndContributeZeroToTheirType()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
            Object(0x3000, "MyApp.Garbage", 500, 0x300),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        Assert.DoesNotContain(result.Dominators, info => info.ObjectAddress == 0x3000);
        var garbageType = Assert.Single(
            result.TypeRetainedSizes, type => type.MethodTable == 0x300);
        Assert.Equal(0UL, garbageType.RetainedSize);
        var liveType = Assert.Single(
            result.TypeRetainedSizes, type => type.MethodTable == 0x200);
        Assert.Equal(100UL, liveType.RetainedSize);
    }

    [Fact]
    public async Task SameTypeDominanceDoesNotDoubleCountTypeRetainedSize()
    {
        var source = CreateSource();
        // Outer byte[][] dominates inner byte[]s: a naive sum would count the
        // inner chunks twice (once via the outer object, once by themselves).
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "System.Byte[]", 100, 0x200),
            Object(0x3000, "System.Byte[]", 60, 0x200),
            Object(0x4000, "System.Byte[]", 40, 0x200),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_chunks", "MyApp.Root", "System.Byte[]"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.ArrayElement, null, "System.Byte[]", "System.Byte[]"),
            new ObjectReference(0x2000, 0x4000, ReferenceKind.ArrayElement, null, "System.Byte[]", "System.Byte[]"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        var byteArrays = Assert.Single(
            result.TypeRetainedSizes, type => type.MethodTable == 0x200);
        Assert.Equal(200UL, byteArrays.RetainedSize); // 100 + 60 + 40, no doubling.
    }

    [Fact]
    public async Task TypeRetainedSizeCountsCrossTypeSubtreesOfTheDominator()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Program", 10, 0x100),
            Object(0x2000, "System.Collections.Generic.List<System.Byte[]>", 20, 0x200),
            Object(0x3000, "System.Byte[]", 64, 0x300),
            Object(0x4000, "System.Byte[]", 64, 0x300),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._allocations")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_allocations", "MyApp.Program", "System.Collections.Generic.List<System.Byte[]>"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.ArrayElement, null, "System.Collections.Generic.List<System.Byte[]>", "System.Byte[]"),
            new ObjectReference(0x2000, 0x4000, ReferenceKind.ArrayElement, null, "System.Collections.Generic.List<System.Byte[]>", "System.Byte[]"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        var list = Assert.Single(
            result.TypeRetainedSizes, type => type.MethodTable == 0x200);
        Assert.Equal(148UL, list.RetainedSize); // 20 + 64 + 64.
        var chunks = Assert.Single(
            result.TypeRetainedSizes, type => type.MethodTable == 0x300);
        Assert.Equal(128UL, chunks.RetainedSize);
    }

    [Fact]
    public async Task OrdersDominatorsByRetainedSizeThenTypeName()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.Zeta", 50, 0x200),
            Object(0x3000, "MyApp.Alpha", 50, 0x300),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_z", "MyApp.Root", "MyApp.Zeta"),
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.Alpha"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        // The root retains both objects, so it leads; the tie between the two
        // 50-byte objects is broken by type name.
        Assert.Equal("MyApp.Root", result.Dominators[0].TypeName);
        Assert.Equal("MyApp.Alpha", result.Dominators[1].TypeName);
        Assert.Equal("MyApp.Zeta", result.Dominators[2].TypeName);
    }

    [Fact]
    public async Task DanglingReferencesAreDropped()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        // A references 0x9999, which was never enumerated as an object.
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x9999, ReferenceKind.Field, "_missing", "MyApp.A", "Unknown"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        Assert.DoesNotContain(result.Dominators, info => info.ObjectAddress == 0x9999);
        var a = Assert.Single(result.Dominators, info => info.ObjectAddress == 0x2000);
        Assert.Equal(100UL, a.RetainedSize);
    }

    [Fact]
    public async Task CachesTheResultPerSnapshot()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        var factory = new StubHeapDumpSourceFactory(source);
        var service = new DominatorTreeService(factory);
        var snapshot = Snapshot();

        var first = await service.ComputeDominatorsAsync(snapshot);
        var second = await service.ComputeDominatorsAsync(snapshot);

        Assert.Same(first, second);
        Assert.Equal(1, factory.OpenCount);

        // A re-captured dump to the same path has a different timestamp and is
        // a different snapshot: it must be recomputed.
        var recaptured = Snapshot("sample.dmp", CapturedAt.AddMinutes(1));
        await service.ComputeDominatorsAsync(recaptured);

        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task ReportsMonotonicProgressFromZeroToOne()
    {
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
            Object(0x3000, "MyApp.B", 50, 0x300),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_a", "MyApp.Root", "MyApp.A"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_b", "MyApp.A", "MyApp.B"),
        ];
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));
        var reports = new List<double>();

        await service.ComputeDominatorsAsync(
            Snapshot(),
            new Progress<double>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], precision: 10);
        for (var index = 1; index < reports.Count; index++)
        {
            Assert.True(reports[index] >= reports[index - 1] - 1e-12);
            Assert.InRange(reports[index], 0.0, 1.0);
        }
    }

    [Fact]
    public async Task ObservedCancellationDisposesTheDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource();
        source.Objects.AddRange(
        [
            Object(0x1000, "MyApp.Root", 10, 0x100),
            Object(0x2000, "MyApp.A", 100, 0x200),
        ]);
        source.Roots.AddRange([new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._root")]);
        source.OnOutgoingEnumerated = cancellation.Cancel;
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ComputeDominatorsAsync(Snapshot(), cancellationToken: cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task RejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ComputeDominatorsAsync(Snapshot()));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task DoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory();
        var service = new DominatorTreeService(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ComputeDominatorsAsync(Snapshot(), cancellationToken: cancellation.Token));

        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task RejectsMissingSnapshot()
    {
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ComputeDominatorsAsync(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsBlankSnapshotPath(string? path)
    {
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.ComputeDominatorsAsync(Snapshot(path!)));
    }

    [Fact]
    public async Task EmptyHeapProducesEmptyResults()
    {
        var source = CreateSource();
        var service = new DominatorTreeService(new StubHeapDumpSourceFactory(source));

        var result = await service.ComputeDominatorsAsync(Snapshot());

        Assert.Empty(result.Dominators);
        Assert.Empty(result.TypeRetainedSizes);
    }

    private static StubDominatorDumpSource CreateSource() =>
        new()
        {
            ProcessName = "Sample.Process",
            ProcessId = 4217,
            RuntimeVersion = "10.0.0",
            CapturedAt = CapturedAt,
        };

    private sealed class StubHeapDumpSourceFactory(
        IHeapDumpSource? source = null) : IHeapDumpSourceFactory
    {
        public int OpenCount { get; private set; }

        public IHeapDumpSource Open(string path)
        {
            OpenCount++;
            return source ?? CreateSource();
        }
    }

    private sealed class StubDominatorDumpSource : IHeapDumpSource
    {
        public string? ProcessName { get; init; }
        public int? ProcessId { get; init; }
        public string RuntimeVersion { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public bool CanWalkHeap { get; set; } = true;
        public bool Disposed { get; private set; }
        public Action? OnOutgoingEnumerated { get; set; }
        public List<HeapObjectData> Objects { get; } = [];
        public List<ClrRootData> Roots { get; } = [];
        public Dictionary<ulong, List<ObjectReference>> Outgoing { get; } = [];

        public Generation? GetGeneration(ulong address) => null;

        public IEnumerable<HeapObjectData> EnumerateObjects() => Objects;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress)
        {
            OnOutgoingEnumerated?.Invoke();
            return Outgoing.TryGetValue(sourceAddress, out var references)
                ? references
                : [];
        }

        public IEnumerable<ObjectReference> EnumerateIncomingReferences(
            ulong targetAddress,
            CancellationToken cancellationToken) => [];

        public IEnumerable<ClrRootData> EnumerateRoots(
            CancellationToken cancellationToken) => Roots;

        public void Dispose() => Disposed = true;
    }
}
