using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Values;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Values;

public sealed class ClrMdHeapObjectValueServiceTests
{
    [Fact]
    public async Task ReadAsyncForwardsBoundedOptionsAndDisposesDump()
    {
        var expected = Result(Field("_count", "System.Int32", "42"));
        var source = new StubValueDumpSource(expected);
        var service = new ClrMdHeapObjectValueService(
            new StubHeapDumpSourceFactory(source));

        var actual = await service.ReadAsync(
            Snapshot(),
            0x2000,
            new ObjectValueReadOptions(ArrayOffset: 500, ArrayLimit: 250, StringLimit: 8192));

        Assert.Same(expected, actual);
        Assert.Equal(0x2000UL, source.Address);
        Assert.Equal(new ObjectValueReadOptions(500, 250, 8192), source.Options);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReadAsyncNormalizesTheSnapshotPath()
    {
        var source = new StubValueDumpSource(
            Result(Field("_count", "System.Int32", "42")));
        var factory = new StubHeapDumpSourceFactory(source);
        var service = new ClrMdHeapObjectValueService(factory);

        await service.ReadAsync(Snapshot(), 0x2000, new());

        Assert.Equal(Path.GetFullPath("sample.dmp"), factory.Path);
    }

    [Fact]
    public async Task ReadAsyncDoesNotOpenDumpWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new StubHeapDumpSourceFactory(new StubValueDumpSource(
            Result(Field("_count", "System.Int32", "42"))));
        var service = new ClrMdHeapObjectValueService(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadAsync(Snapshot(), 0x2000, new(), cancellation.Token));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task ReadAsyncRejectsANonWalkableDump()
    {
        var source = new StubValueDumpSource(
            Result(Field("_count", "System.Int32", "42")));
        source.CanWalkHeap = false;
        var service = new ClrMdHeapObjectValueService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ReadAsync(Snapshot(), 0x2000, new()));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReadAsyncDisposesTheDumpWhenTheReadThrows()
    {
        var source = new StubValueDumpSource(
            Result(Field("_count", "System.Int32", "42")));
        source.ThrowOnRead = true;
        var service = new ClrMdHeapObjectValueService(
            new StubHeapDumpSourceFactory(source));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReadAsync(Snapshot(), 0x2000, new()));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReadAsyncRejectsZeroObjectAddress()
    {
        var factory = new StubHeapDumpSourceFactory(new StubValueDumpSource(
            Result(Field("_count", "System.Int32", "42"))));
        var service = new ClrMdHeapObjectValueService(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ReadAsync(Snapshot(), 0, new()));

        Assert.Null(factory.Path);
    }

    [Fact]
    public async Task ReadAsyncRejectsMissingSnapshot()
    {
        var service = new ClrMdHeapObjectValueService(new StubHeapDumpSourceFactory(
            new StubValueDumpSource(Result())));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ReadAsync(null!, 0x2000, new()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsyncRejectsBlankSnapshotPath(string? path)
    {
        var service = new ClrMdHeapObjectValueService(new StubHeapDumpSourceFactory(
            new StubValueDumpSource(Result())));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.ReadAsync(Snapshot(path!), 0x2000, new()));
    }

    [Fact]
    public async Task ReadAsyncRejectsMissingOptions()
    {
        var service = new ClrMdHeapObjectValueService(new StubHeapDumpSourceFactory(
            new StubValueDumpSource(Result())));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ReadAsync(Snapshot(), 0x2000, null!));
    }

    [Theory]
    [InlineData(-1, 500, 4096)]
    [InlineData(0, 0, 4096)]
    [InlineData(0, 501, 4096)]
    [InlineData(0, 500, 0)]
    [InlineData(0, 500, 1_048_577)]
    public void ValidateRejectsOutOfRangeOptions(
        int arrayOffset,
        int arrayLimit,
        int stringLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObjectValueReadOptions(arrayOffset, arrayLimit, stringLimit).Validate());
    }

    private static HeapSnapshot Snapshot(string path = "sample.dmp") =>
        new()
        {
            Info = new HeapSnapshotInfo(
                path, "Sample", 42, "10.0.0", DateTimeOffset.UtcNow, 1, 64),
            Types = [],
        };

    private static HeapFieldValue Field(string name, string type, string value) =>
        new(name, type, HeapValueKind.Primitive, value, null, null, false, null, null);

    private static HeapObjectValueResult Result(params HeapFieldValue[] fields) =>
        new(
            new HeapObjectInfo(0x2000, 0x1000, "Example.Cache", 64, "Gen2"),
            fields,
            fields.Length,
            false);

    private sealed class StubHeapDumpSourceFactory(
        IHeapDumpSource? source = null) : IHeapDumpSourceFactory
    {
        public string? Path { get; private set; }

        public IHeapDumpSource Open(string path)
        {
            Path = path;
            return source ?? new StubValueDumpSource(Result());
        }
    }

    private sealed class StubValueDumpSource(
        HeapObjectValueResult result) : IHeapDumpSource
    {
        public string? ProcessName { get; init; }
        public int? ProcessId { get; init; }
        public string RuntimeVersion { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public bool CanWalkHeap { get; set; } = true;
        public bool Disposed { get; private set; }
        public bool ThrowOnRead { get; set; }
        public ulong Address { get; private set; }
        public ObjectValueReadOptions Options { get; private set; } = new();

        public Generation? GetGeneration(ulong address) => null;

        public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress) => [];

        public IEnumerable<ObjectReference> EnumerateIncomingReferences(
            ulong targetAddress,
            CancellationToken cancellationToken) => [];

        public IEnumerable<ClrRootData> EnumerateRoots(
            CancellationToken cancellationToken) => [];

        public IEnumerable<HeapObjectData> EnumerateObjects() => [];

        public HeapObjectValueResult ReadObjectValues(
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("read failed");
            }

            Address = objectAddress;
            Options = options;
            return result;
        }

        public void Dispose() => Disposed = true;
    }
}
