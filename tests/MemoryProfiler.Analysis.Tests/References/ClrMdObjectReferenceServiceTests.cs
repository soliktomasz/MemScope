using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.References;

public sealed class ClrMdObjectReferenceServiceTests
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
    public async Task GetOutgoingReferencesReturnsFieldAndArrayElementReferences()
    {
        var source = CreateSource();
        source.Outgoing.AddRange(
        [
            new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_child",
                SourceTypeName: "MyApp.Container", TargetTypeName: "MyApp.Widget"),
            new ObjectReference(0x2000, 0x4000, ReferenceKind.ArrayElement, null,
                SourceTypeName: "MyApp.Container", TargetTypeName: "System.String"),
        ]);
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        var references = await service.GetOutgoingReferencesAsync(Snapshot(), 0x2000);

        Assert.Collection(
            references,
            first =>
            {
                Assert.Equal(0x2000UL, first.SourceAddress);
                Assert.Equal(0x3000UL, first.TargetAddress);
                Assert.Equal(ReferenceKind.Field, first.Kind);
                Assert.Equal("_child", first.Name);
                Assert.Equal("MyApp.Container", first.SourceTypeName);
                Assert.Equal("MyApp.Widget", first.TargetTypeName);
            },
            second =>
            {
                Assert.Equal(0x4000UL, second.TargetAddress);
                Assert.Equal(ReferenceKind.ArrayElement, second.Kind);
                Assert.Null(second.Name);
                Assert.Equal("System.String", second.TargetTypeName);
            });
    }

    [Fact]
    public async Task GetOutgoingReferencesSortsByTargetAddressAscending()
    {
        var source = CreateSource();
        source.Outgoing.AddRange(
        [
            new ObjectReference(0x1000, 0x3000, ReferenceKind.Field, "_third", null, "C"),
            new ObjectReference(0x1000, 0x1000, ReferenceKind.Field, "_first", null, "A"),
            new ObjectReference(0x1000, 0x2000, ReferenceKind.ArrayElement, null, null, "B"),
        ]);
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        var references = await service.GetOutgoingReferencesAsync(Snapshot(), 0x1000);

        Assert.Equal(
            [0x1000UL, 0x2000UL, 0x3000UL],
            references.Select(reference => reference.TargetAddress));
    }

    [Fact]
    public async Task GetOutgoingReferencesReturnsEmptyWhenTheObjectHasNoReferences()
    {
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(CreateSource()));

        var references = await service.GetOutgoingReferencesAsync(Snapshot(), 0x1000);

        Assert.Empty(references);
    }

    [Fact]
    public async Task GetIncomingReferencesReturnsHeapObjectAndRootReferences()
    {
        var source = CreateSource();
        source.Incoming.AddRange(
        [
            new ObjectReference(0x3000, 0x2000, ReferenceKind.Field, "_owner",
                SourceTypeName: "MyApp.Owner", TargetTypeName: "MyApp.Widget"),
            new ObjectReference(0, 0x2000, ReferenceKind.Handle, null,
                SourceTypeName: null, TargetTypeName: "MyApp.Widget"),
            new ObjectReference(0, 0x2000, ReferenceKind.StaticField, null,
                SourceTypeName: null, TargetTypeName: "MyApp.Widget"),
        ]);
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        var references = await service.GetIncomingReferencesAsync(Snapshot(), 0x2000);

        Assert.Equal(3, references.Count);
        var heapRow = references[0];
        Assert.Equal(0x3000UL, heapRow.SourceAddress);
        Assert.Equal(0x2000UL, heapRow.TargetAddress);
        Assert.Equal(ReferenceKind.Field, heapRow.Kind);
        Assert.Equal("_owner", heapRow.Name);
        Assert.Equal("MyApp.Owner", heapRow.SourceTypeName);

        var kinds = references.Select(reference => reference.Kind);
        Assert.Contains(ReferenceKind.Handle, kinds);
        Assert.Contains(ReferenceKind.StaticField, kinds);
    }

    [Fact]
    public async Task GetIncomingReferencesOrdersHeapObjectsBeforeRoots()
    {
        var source = CreateSource();
        source.Incoming.AddRange(
        [
            new ObjectReference(0, 0x5000, ReferenceKind.StaticField, null, null, "T"),
            new ObjectReference(0x2000, 0x5000, ReferenceKind.Field, "_a", "A", "T"),
            new ObjectReference(0, 0x5000, ReferenceKind.Handle, null, null, "T"),
            new ObjectReference(0x1000, 0x5000, ReferenceKind.ArrayElement, null, "B", "T"),
        ]);
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        var references = await service.GetIncomingReferencesAsync(Snapshot(), 0x5000);

        Assert.Equal(
            [0x1000UL, 0x2000UL, 0UL, 0UL],
            references.Select(reference => reference.SourceAddress));
        Assert.Equal(
            [ReferenceKind.ArrayElement, ReferenceKind.Field, ReferenceKind.StaticField, ReferenceKind.Handle],
            references.Select(reference => reference.Kind));
    }

    [Fact]
    public async Task GetIncomingReferencesReturnsEmptyWhenNothingReferencesTheObject()
    {
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(CreateSource()));

        var references = await service.GetIncomingReferencesAsync(Snapshot(), 0x1000);

        Assert.Empty(references);
    }

    [Fact]
    public async Task GetOutgoingReferencesRejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetOutgoingReferencesAsync(Snapshot(), 0x1000));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetIncomingReferencesRejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetIncomingReferencesAsync(Snapshot(), 0x1000));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetOutgoingReferencesObservesCancellationDuringEnumerationAndDisposesDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource();
        source.OnOutgoingEnumerated = cancellation.Cancel;
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetOutgoingReferencesAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetIncomingReferencesObservesCancellationDuringEnumerationAndDisposesDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource();
        source.OnIncomingEnumerated = cancellation.Cancel;
        var service = new ClrMdObjectReferenceService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetIncomingReferencesAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task GetOutgoingReferencesDoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory();
        var service = new ClrMdObjectReferenceService(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetOutgoingReferencesAsync(Snapshot(), 0x1000, cancellation.Token));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task GetOutgoingReferencesRejectsMissingSnapshot()
    {
        var service = new ClrMdObjectReferenceService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.GetOutgoingReferencesAsync(null!, 0x1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOutgoingReferencesRejectsBlankSnapshotPath(string? path)
    {
        var service = new ClrMdObjectReferenceService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.GetOutgoingReferencesAsync(Snapshot(path!), 0x1000));
    }

    [Fact]
    public async Task GetIncomingReferencesRejectsZeroObjectAddress()
    {
        var service = new ClrMdObjectReferenceService(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => service.GetIncomingReferencesAsync(Snapshot(), 0));
    }

    [Fact]
    public async Task GetOutgoingReferencesRejectsObjectAddressForASnapshotThatFailsToOpen()
    {
        var source = CreateSource();
        var factory = new StubHeapDumpSourceFactory(source)
        {
            OnOpen = () => throw new InvalidDataException("The dump has no CLR runtime."),
        };
        var service = new ClrMdObjectReferenceService(factory);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => service.GetOutgoingReferencesAsync(Snapshot(), 0x1000));

        Assert.Equal(Path.GetFullPath("sample.dmp"), factory.Path);
    }

    private static StubReferenceDumpSource CreateSource() =>
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

    private sealed class StubReferenceDumpSource : IHeapDumpSource
    {
        public string? ProcessName { get; init; }
        public int? ProcessId { get; init; }
        public string RuntimeVersion { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public bool CanWalkHeap { get; set; } = true;
        public bool Disposed { get; private set; }
        public Action? OnOutgoingEnumerated { get; set; }
        public Action? OnIncomingEnumerated { get; set; }
        public List<ObjectReference> Outgoing { get; } = [];
        public List<ObjectReference> Incoming { get; } = [];

        public Generation? GetGeneration(ulong address) => null;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress)
        {
            OnOutgoingEnumerated?.Invoke();
            return Outgoing;
        }

        public IEnumerable<ObjectReference> EnumerateIncomingReferences(ulong targetAddress)
        {
            OnIncomingEnumerated?.Invoke();
            return Incoming;
        }

        public IEnumerable<HeapObjectData> EnumerateObjects() => [];

        public void Dispose() => Disposed = true;
    }
}
