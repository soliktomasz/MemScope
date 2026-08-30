using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Loading;

public sealed class ClrMdHeapSnapshotLoaderTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 28, 16, 25, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadRejectsMissingPath(string? path)
    {
        var loader = new ClrMdHeapSnapshotLoader(new StubHeapDumpSourceFactory());

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => loader.LoadAsync(path!));
    }

    [Fact]
    public async Task LoadGroupsWalkableObjectsByMethodTable()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24),
            new HeapObjectData(0x2000, "System.Byte[]", "System.Private.CoreLib", 128),
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 40));
        var loader = new ClrMdHeapSnapshotLoader(
            new StubHeapDumpSourceFactory(source));

        var snapshot = await loader.LoadAsync("sample.dmp");

        Assert.Collection(
            snapshot.Types.OrderBy(type => type.MethodTable),
            strings =>
            {
                Assert.Equal(0x1000UL, strings.MethodTable);
                Assert.Equal("System.String", strings.Name);
                Assert.Equal("System.Private.CoreLib", strings.AssemblyName);
                Assert.Equal(2, strings.ObjectCount);
                Assert.Equal(64UL, strings.ShallowSize);
                Assert.Null(strings.RetainedSize);
            },
            bytes =>
            {
                Assert.Equal(0x2000UL, bytes.MethodTable);
                Assert.Equal("System.Byte[]", bytes.Name);
                Assert.Equal(1, bytes.ObjectCount);
                Assert.Equal(128UL, bytes.ShallowSize);
                Assert.Null(bytes.RetainedSize);
            });
    }

    [Fact]
    public async Task LoadBuildsSnapshotMetadataFromDumpAndAggregates()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24),
            new HeapObjectData(0x2000, "Example.Widget", "Example", 80));
        var factory = new StubHeapDumpSourceFactory(source);
        var loader = new ClrMdHeapSnapshotLoader(factory);

        var snapshot = await loader.LoadAsync("sample.dmp");

        Assert.Equal(Path.GetFullPath("sample.dmp"), snapshot.Info.Path);
        Assert.Equal("Sample.Process", snapshot.Info.ProcessName);
        Assert.Equal(4217, snapshot.Info.ProcessId);
        Assert.Equal("10.0.0", snapshot.Info.RuntimeVersion);
        Assert.Equal(CapturedAt, snapshot.Info.CapturedAt);
        Assert.Equal(2, snapshot.Info.ObjectCount);
        Assert.Equal(104UL, snapshot.Info.HeapSize);
        Assert.Equal(Path.GetFullPath("sample.dmp"), factory.Path);
    }

    [Fact]
    public async Task LoadSkipsInvalidFreeAndUntypedHeapEntries()
    {
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24),
            new HeapObjectData(0, null, null, 48, IsValid: false),
            new HeapObjectData(0x2000, "Free", null, 96, IsFree: true),
            new HeapObjectData(0, null, null, 32));
        var loader = new ClrMdHeapSnapshotLoader(
            new StubHeapDumpSourceFactory(source));

        var snapshot = await loader.LoadAsync("sample.dmp");

        var type = Assert.Single(snapshot.Types);
        Assert.Equal("System.String", type.Name);
        Assert.Equal(1, snapshot.Info.ObjectCount);
        Assert.Equal(24UL, snapshot.Info.HeapSize);
    }

    [Fact]
    public async Task LoadRejectsAHeapThatCannotBeWalked()
    {
        var source = CreateSource();
        source.CanWalkHeap = false;
        var loader = new ClrMdHeapSnapshotLoader(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.LoadAsync("sample.dmp"));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LoadObservesCancellationDuringEnumerationAndDisposesDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24));
        source.OnObjectEnumerated = cancellation.Cancel;
        var loader = new ClrMdHeapSnapshotLoader(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync("sample.dmp", cancellation.Token));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LoadDoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory();
        var loader = new ClrMdHeapSnapshotLoader(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync("sample.dmp", cancellation.Token));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task LoadDoesNotEnumerateWhenCancelledWhileOpeningDump()
    {
        using var cancellation = new CancellationTokenSource();
        var source = CreateSource(
            new HeapObjectData(0x1000, "System.String", "System.Private.CoreLib", 24));
        var factory = new StubHeapDumpSourceFactory(source)
        {
            OnOpen = cancellation.Cancel,
        };
        var loader = new ClrMdHeapSnapshotLoader(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync("sample.dmp", cancellation.Token));

        Assert.False(source.EnumerationStarted);
        Assert.True(source.Disposed);
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
        public bool EnumerationStarted { get; private set; }
        public Action? OnObjectEnumerated { get; set; }

        public IEnumerable<HeapObjectData> EnumerateObjects()
        {
            EnumerationStarted = true;
            foreach (var heapObject in objects)
            {
                yield return heapObject;
                OnObjectEnumerated?.Invoke();
            }
        }

        public Generation? GetGeneration(ulong address) => null;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress) => [];

        public IEnumerable<ObjectReference> EnumerateIncomingReferences(
            ulong targetAddress,
            CancellationToken cancellationToken) => [];

        public IEnumerable<ClrRootData> EnumerateRoots() => [];

        public void Dispose() => Disposed = true;
    }
}
