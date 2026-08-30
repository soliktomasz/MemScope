using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Roots;

public sealed class GcRootServiceTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 28, 16, 25, 0, TimeSpan.Zero);

    private static HeapSnapshot Snapshot(string path = "sample.dmp") =>
        new()
        {
            Info = new HeapSnapshotInfo(
                path,
                "Sample.Process",
                4217,
                "10.0.0",
                CapturedAt,
                5,
                1_024),
            Types = [],
        };

    [Fact]
    public async Task FindRootsReturnsTheRootWhenItReferencesTheObjectDirectly()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x2000, ClrRootKind.StaticVar, "MyApp.Program._cache"),
        ]);
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x2000);

        var root = Assert.Single(roots);
        Assert.Equal(0x2000UL, root.RootAddress);
        Assert.Equal(0x2000UL, root.ObjectAddress);
        Assert.Equal("Static field", root.Kind);
        Assert.Equal("MyApp.Program._cache", root.Name);
        Assert.Null(root.Path);
    }

    [Fact]
    public async Task FindRootsReconstructsTheHopChainFromTheRootToTheObject()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._cache"),
        ]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_entries",
                SourceTypeName: "MyApp.Cache",
                TargetTypeName: "System.Collections.Generic.Dictionary"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_value",
                SourceTypeName: "System.Collections.Generic.Dictionary",
                TargetTypeName: "MyApp.CustomerDto"),
        ];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x3000);

        var root = Assert.Single(roots);
        Assert.Equal(0x1000UL, root.RootAddress);
        Assert.Equal("Static field", root.Kind);
        Assert.Equal("MyApp.Program._cache", root.Name);
        Assert.NotNull(root.Path);
        Assert.Collection(
            root.Path,
            first =>
            {
                Assert.Equal(0x1000UL, first.SourceAddress);
                Assert.Equal(0x2000UL, first.TargetAddress);
                Assert.Equal(ReferenceKind.Field, first.Kind);
                Assert.Equal("_entries", first.Name);
                Assert.Equal("MyApp.Cache", first.SourceTypeName);
                Assert.Equal("System.Collections.Generic.Dictionary", first.TargetTypeName);
            },
            second =>
            {
                Assert.Equal(0x2000UL, second.SourceAddress);
                Assert.Equal(0x3000UL, second.TargetAddress);
                Assert.Equal(ReferenceKind.Field, second.Kind);
                Assert.Equal("_value", second.Name);
                Assert.Equal("MyApp.CustomerDto", second.TargetTypeName);
            });
    }

    [Fact]
    public async Task FindRootsSupportsArrayElementHopsAndMultipleRoots()
    {
        var source = CreateSource();
        // Handle: pinned handle references the chunk directly.
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StaticVar, "MyApp.Program._chunks"),
            new ClrRootData(0x3000, ClrRootKind.PinnedHandle, null),
        ]);
        // Static: List -> byte[][] -> chunk.
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_items",
                SourceTypeName: "System.Collections.Generic.List<System.Byte[]>",
                TargetTypeName: "System.Byte[][]"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.ArrayElement, null,
                SourceTypeName: "System.Byte[][]",
                TargetTypeName: "System.Byte[]"),
        ];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x3000);

        Assert.Collection(
            roots,
            direct =>
            {
                Assert.Equal(0x3000UL, direct.RootAddress);
                Assert.Equal("Pinned handle", direct.Kind);
                Assert.Null(direct.Name);
                Assert.Null(direct.Path);
            },
            chain =>
            {
                Assert.Equal(0x1000UL, chain.RootAddress);
                Assert.Equal("Static field", chain.Kind);
                Assert.NotNull(chain.Path);
                Assert.Equal(ReferenceKind.Field, chain.Path[0].Kind);
                Assert.Equal("_items", chain.Path[0].Name);
                Assert.Equal(ReferenceKind.ArrayElement, chain.Path[1].Kind);
                Assert.Equal(0x3000UL, chain.Path[1].TargetAddress);
            });
    }

    [Fact]
    public async Task FindRootsOrdersPathsByLengthThenRootName()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            // Two-hop path, no name.
            new ClrRootData(0x1000, ClrRootKind.StrongHandle, null),
            // One-hop paths, names order the tie.
            new ClrRootData(0x5000, ClrRootKind.StaticVar, "MyApp.B"),
            new ClrRootData(0x2000, ClrRootKind.StaticVar, "MyApp.A"),
            // Two-hop path with a name sorts after the unnamed two-hop path.
            new ClrRootData(0x4000, ClrRootKind.StaticVar, "MyApp.C"),
        ]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_x", "A", "T"),
        ];
        source.Outgoing[0x3000] =
        [
            new ObjectReference(0x3000, 0x9000, ReferenceKind.Field, "_deep", "T", "T"),
        ];
        source.Outgoing[0x5000] =
        [
            new ObjectReference(0x5000, 0x9000, ReferenceKind.Field, "_direct", "T", "T"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x9000, ReferenceKind.Field, "_direct", "T", "T"),
        ];
        source.Outgoing[0x4000] =
        [
            new ObjectReference(0x4000, 0x6000, ReferenceKind.Field, "_mid", "T", "T"),
        ];
        source.Outgoing[0x6000] =
        [
            new ObjectReference(0x6000, 0x9000, ReferenceKind.Field, "_deep", "T", "T"),
        ];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x9000);

        Assert.Equal(4, roots.Count);
        // One hop first (ordered by name), two hops second (unnamed first).
        Assert.Single(roots[0].Path!);
        Assert.Equal("MyApp.A", roots[0].Name);
        Assert.Single(roots[1].Path!);
        Assert.Equal("MyApp.B", roots[1].Name);
        Assert.Equal(2, roots[2].Path!.Count);
        Assert.Null(roots[2].Name);
        Assert.Equal(0x1000UL, roots[2].RootAddress);
        Assert.Equal(2, roots[3].Path!.Count);
        Assert.Equal("MyApp.C", roots[3].Name);
        Assert.Equal(0x4000UL, roots[3].RootAddress);
    }

    [Fact]
    public async Task FindRootsReturnsEmptyWhenNoRootReachesTheObject()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.Stack, null),
            new ClrRootData(0x2000, ClrRootKind.FinalizerQueue, null),
        ]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_other", "A", "B"),
        ];
        source.Outgoing[0x3000] = [];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x9000);

        Assert.Empty(roots);
    }

    [Fact]
    public async Task FindRootsSkipsRootsWhoseObjectIsAbsent()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0, ClrRootKind.StrongHandle, null),
            new ClrRootData(0x1000, ClrRootKind.StrongHandle, null),
        ]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_x", "A", "A"),
        ];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x2000);

        var root = Assert.Single(roots);
        Assert.Equal(0x1000UL, root.RootAddress);
    }

    [Fact]
    public async Task FindRootsTerminatesOnReferenceCycles()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StrongHandle, null),
        ]);
        // A <-> B cycle that eventually reaches the target through C.
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_b", "A", "B"),
        ];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x1000, ReferenceKind.Field, "_a", "B", "A"),
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_c", "B", "C"),
        ];
        source.Outgoing[0x3000] = [];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x3000);

        var root = Assert.Single(roots);
        Assert.Equal(2, root.Path!.Count);
        Assert.Equal(0x2000UL, root.Path[0].TargetAddress);
        Assert.Equal(0x3000UL, root.Path[1].TargetAddress);
    }

    [Fact]
    public async Task FindRootsDoesNotReExploreAddressesProvenDeadByAnEarlierRoot()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            // Root A explores a component that cannot reach the target.
            new ClrRootData(0x1000, ClrRootKind.Stack, null),
            // Root B references the same dead component, then its own live one.
            new ClrRootData(0x2000, ClrRootKind.Stack, null),
        ]);
        source.Outgoing[0x1000] =
        [
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_shared", "A", "C"),
        ];
        source.Outgoing[0x3000] = [];
        source.Outgoing[0x2000] =
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_shared", "B", "C"),
            new ObjectReference(0x2000, 0x4000, ReferenceKind.Field, "_live", "B", "C"),
        ];
        source.Outgoing[0x4000] = [];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x9999);

        Assert.Empty(roots);
        // 0x1000 and 0x3000 were explored by root A; root B's search must not
        // re-enumerate them (0x3000 and 0x1000 each enumerated exactly once).
        Assert.Equal(1, source.OutgoingEnumerationCount(0x1000));
        Assert.Equal(1, source.OutgoingEnumerationCount(0x3000));
    }

    [Fact]
    public async Task FindRootsAbortsAtTheDepthLimitWithoutReturningAPath()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StrongHandle, null),
        ]);
        // A chain longer than MaxPathHops: 0x1000 -> ... -> 0x1000 + 501.
        var count = GcRootService.MaxPathHops + 2;
        for (var index = 0; index < count - 1; index++)
        {
            source.Outgoing[0x1000UL + (ulong)index] =
            [
                new ObjectReference(
                    0x1000UL + (ulong)index,
                    0x1000UL + (ulong)index + 1,
                    ReferenceKind.Field,
                    "_next",
                    "A",
                    "A"),
            ];
        }

        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        // The chain covers 0x1000 .. 0x1000 + (count - 1); the target at the end
        // of the chain is 501 hops away, beyond the 500-hop limit.
        var roots = await service.FindRootsAsync(
            Snapshot(), 0x1000UL + (ulong)(count - 1));

        Assert.Empty(roots);
    }

    [Fact]
    public async Task FindRootsFindsAPathOfExactlyMaxPathHops()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.StrongHandle, null),
        ]);
        // A chain of exactly MaxPathHops edges must be found.
        var count = GcRootService.MaxPathHops;
        for (var index = 0; index < count; index++)
        {
            source.Outgoing[0x1000UL + (ulong)index] =
            [
                new ObjectReference(
                    0x1000UL + (ulong)index,
                    0x1000UL + (ulong)index + 1,
                    ReferenceKind.Field,
                    "_next",
                    "A",
                    "A"),
            ];
        }

        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x1000UL + (ulong)count);

        var root = Assert.Single(roots);
        Assert.Equal(count, root.Path!.Count);
        Assert.Equal(0x1000UL + (ulong)count, root.Path[^1].TargetAddress);
    }

    [Fact]
    public async Task FindRootsDoesNotMarkDeadAddressesWhenTheDepthLimitCutsTheSearchShort()
    {
        var source = CreateSource();
        source.Roots.AddRange(
        [
            // Root A walks a chain that reaches the depth limit, so its search
            // is cut short: nothing it visited may be treated as dead.
            new ClrRootData(0xA000, ClrRootKind.Stack, null),
            // Root B reaches the target through the node at A's depth-limit
            // frontier (discovered by A, never expanded). If A's capped search
            // had marked it dead, B would skip it and miss the target.
            new ClrRootData(0xC000, ClrRootKind.Stack, null),
        ]);
        // Exactly MaxPathHops edges: the node 0xA000 + MaxPathHops sits on the
        // frontier A discovers but cannot expand.
        var frontier = 0xA000UL + (ulong)GcRootService.MaxPathHops;
        for (var index = 0; index < GcRootService.MaxPathHops; index++)
        {
            source.Outgoing[0xA000UL + (ulong)index] =
            [
                new ObjectReference(
                    0xA000UL + (ulong)index,
                    0xA000UL + (ulong)index + 1,
                    ReferenceKind.Field,
                    "_next",
                    "A",
                    "A"),
            ];
        }

        // B: 0xC000 -> frontier -> target.
        source.Outgoing[0xC000] =
        [
            new ObjectReference(0xC000, frontier, ReferenceKind.Field, "_hop", "B", "A"),
        ];
        source.Outgoing[frontier] =
        [
            new ObjectReference(frontier, 0x9999, ReferenceKind.Field, "_target", "A", "T"),
        ];
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        var roots = await service.FindRootsAsync(Snapshot(), 0x9999);

        var root = Assert.Single(roots);
        Assert.Equal(0xC000UL, root.RootAddress);
        Assert.Equal(2, root.Path!.Count);
        Assert.Equal(0x9999UL, root.Path[^1].TargetAddress);
    }

    [Fact]
    public async Task FindRootsObservesCancellationDuringTheSearchAndDisposesDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource();
        source.Roots.AddRange(
        [
            new ClrRootData(0x1000, ClrRootKind.Stack, null),
        ]);
        source.OnOutgoingEnumerated = cancellation.Cancel;
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FindRootsAsync(Snapshot(), 0x3000, cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task FindRootsRejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var service = new GcRootService(new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FindRootsAsync(Snapshot(), 0x1000));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task FindRootsDoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory();
        var service = new GcRootService(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FindRootsAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task FindRootsRejectsMissingSnapshot()
    {
        var service = new GcRootService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.FindRootsAsync(null!, 0x1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindRootsRejectsBlankSnapshotPath(string? path)
    {
        var service = new GcRootService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.FindRootsAsync(Snapshot(path!), 0x1000));
    }

    [Fact]
    public async Task FindRootsRejectsZeroObjectAddress()
    {
        var service = new GcRootService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => service.FindRootsAsync(Snapshot(), 0));
    }

    [Fact]
    public async Task FindRootsRejectsObjectAddressForASnapshotThatFailsToOpen()
    {
        var source = CreateSource();
        var factory = new StubHeapDumpSourceFactory(source)
        {
            OnOpen = () => throw new InvalidDataException("The dump has no CLR runtime."),
        };
        var service = new GcRootService(factory);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => service.FindRootsAsync(Snapshot(), 0x1000));

        Assert.Equal(Path.GetFullPath("sample.dmp"), factory.Path);
    }

    private static StubGcRootDumpSource CreateSource() =>
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
        public string? Path { get; private set; }
        public Action? OnOpen { get; init; }

        public IHeapDumpSource Open(string path)
        {
            Path = path;
            OnOpen?.Invoke();
            return source ?? CreateSource();
        }
    }

    private sealed class StubGcRootDumpSource : IHeapDumpSource
    {
        public string? ProcessName { get; init; }
        public int? ProcessId { get; init; }
        public string RuntimeVersion { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public bool CanWalkHeap { get; set; } = true;
        public bool Disposed { get; private set; }
        public Action? OnOutgoingEnumerated { get; set; }
        public List<ClrRootData> Roots { get; } = [];

        public Dictionary<ulong, List<ObjectReference>> Outgoing { get; } = [];

        private readonly Dictionary<ulong, int> _outgoingEnumerationCounts = [];

        public int OutgoingEnumerationCount(ulong address) =>
            _outgoingEnumerationCounts.TryGetValue(address, out var count) ? count : 0;

        public Generation? GetGeneration(ulong address) => null;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress)
        {
            _outgoingEnumerationCounts[sourceAddress] =
                _outgoingEnumerationCounts.GetValueOrDefault(sourceAddress) + 1;
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

        public IEnumerable<HeapObjectData> EnumerateObjects() => [];

        public void Dispose() => Disposed = true;
    }
}
