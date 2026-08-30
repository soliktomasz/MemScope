using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Objects;

public sealed class ClrMdHeapObjectRepositoryTests
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
    public async Task GetInstancesReturnsOnlyObjectsOfTheRequestedMethodTable()
    {
        var source = CreateSource(
            new HeapObjectData(0x2000, "System.Byte[]", "System.Private.CoreLib", 128,
                Address: 0x2000),
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24,
                Address: 0x1000),
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 40,
                Address: 0x3000));
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        var instances = await repository.GetInstancesAsync(Snapshot(), 0x1000);

        Assert.Collection(
            instances,
            first =>
            {
                Assert.Equal(0x1000UL, first.Address);
                Assert.Equal("System.String", first.TypeName);
                Assert.Equal(24UL, first.Size);
            },
            second =>
            {
                Assert.Equal(0x3000UL, second.Address);
                Assert.Equal("System.String", second.TypeName);
                Assert.Equal(40UL, second.Size);
            });
    }

    [Fact]
    public async Task GetInstancesOrdersByAddressAscending()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "Example.Widget", "Example", 64,
                Address: 0x3000),
            new HeapObjectData(0x1000, "Example.Widget", "Example", 64,
                Address: 0x1000),
            new HeapObjectData(0x1000, "Example.Widget", "Example", 64,
                Address: 0x2000));
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        var instances = await repository.GetInstancesAsync(Snapshot(), 0x1000);

        Assert.Equal(
            [0x1000UL, 0x2000UL, 0x3000UL],
            instances.Select(instance => instance.Address));
    }

    [Theory]
    [InlineData(Generation.Generation0, "Gen0")]
    [InlineData(Generation.Generation1, "Gen1")]
    [InlineData(Generation.Generation2, "Gen2")]
    [InlineData(Generation.Large, "LOH")]
    [InlineData(Generation.Pinned, "Pinned")]
    [InlineData(Generation.Frozen, "Frozen")]
    [InlineData(null, "Unknown")]
    public void GenerationLabelMapsEveryValue(Generation? generation, string expected)
    {
        Assert.Equal(expected, ClrMdHeapObjectRepository.GenerationLabel(generation));
    }

    [Fact]
    public async Task GetInstancesCarriesTheGenerationLabel()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "Example.Widget", "Example", 128,
                Address: 0x1000));
        source.Generations[0x1000] = Generation.Generation2;
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        var instances = await repository.GetInstancesAsync(Snapshot(), 0x1000);

        var instance = Assert.Single(instances);
        Assert.Equal("Gen2", instance.Generation);
        Assert.Equal(128UL, instance.Size);
        Assert.Equal(0x1000UL, instance.Address);
    }

    [Fact]
    public async Task GetInstancesSkipsInvalidFreeAndUntypedEntries()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24,
                Address: 0x1000),
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 48,
                Address: 0x2000,
                IsValid: false),
            new HeapObjectData(0x1000, "Free", null, 96,
                Address: 0x3000,
                IsFree: true),
            new HeapObjectData(0, null, null, 32,
                Address: 0x4000));
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        var instances = await repository.GetInstancesAsync(Snapshot(), 0x1000);

        var instance = Assert.Single(instances);
        Assert.Equal(0x1000UL, instance.Address);
    }

    [Fact]
    public async Task GetInstancesRejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetInstancesAsync(Snapshot(), 0x1000));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetInstancesObservesCancellationDuringEnumerationAndDisposesDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24,
                Address: 0x1000));
        source.OnObjectEnumerated = cancellation.Cancel;
        var repository = new ClrMdHeapObjectRepository(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetInstancesAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetInstancesDoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory();
        var repository = new ClrMdHeapObjectRepository(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetInstancesAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task GetInstancesRejectsMissingSnapshot()
    {
        var repository = new ClrMdHeapObjectRepository(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.GetInstancesAsync(null!, 0x1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetInstancesRejectsBlankSnapshotPath(string? path)
    {
        var repository = new ClrMdHeapObjectRepository(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => repository.GetInstancesAsync(Snapshot(path!), 0x1000));
    }

    [Fact]
    public async Task GetInstancesRejectsZeroMethodTable()
    {
        var repository = new ClrMdHeapObjectRepository(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => repository.GetInstancesAsync(Snapshot(), 0));
    }

    [Fact]
    public async Task GetInstancesRejectsMethodTableForASnapshotThatFailsToOpen()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24,
                Address: 0x1000));
        var factory = new StubHeapDumpSourceFactory(source)
        {
            OnOpen = () => throw new InvalidDataException("The dump has no CLR runtime."),
        };
        var repository = new ClrMdHeapObjectRepository(factory);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => repository.GetInstancesAsync(Snapshot(), 0x1000));

        Assert.Equal(Path.GetFullPath("sample.dmp"), factory.Path);
    }

    private static StubHeapDumpSource CreateSource(params HeapObjectData[] objects) =>
        new(objects)
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

    private sealed class StubHeapDumpSource(
        IReadOnlyList<HeapObjectData> objects) : IHeapDumpSource
    {
        public string? ProcessName { get; init; }
        public int? ProcessId { get; init; }
        public string RuntimeVersion { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public bool CanWalkHeap { get; set; } = true;
        public bool Disposed { get; private set; }
        public Action? OnObjectEnumerated { get; set; }
        public Dictionary<ulong, Generation?> Generations { get; } = [];

        public Generation? GetGeneration(ulong address) =>
            Generations.TryGetValue(address, out var generation) ? generation : null;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress) => [];

        public IEnumerable<ObjectReference> EnumerateIncomingReferences(
            ulong targetAddress,
            CancellationToken cancellationToken) => [];

        public IEnumerable<ClrRootData> EnumerateRoots() => [];

        public IEnumerable<HeapObjectData> EnumerateObjects()
        {
            foreach (var heapObject in objects)
            {
                yield return heapObject;
                OnObjectEnumerated?.Invoke();
            }
        }

        public void Dispose() => Disposed = true;
    }
}
