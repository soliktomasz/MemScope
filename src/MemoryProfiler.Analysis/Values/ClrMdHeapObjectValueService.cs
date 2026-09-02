using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Values;

public sealed class ClrMdHeapObjectValueService : IHeapObjectValueService
{
    private readonly IHeapDumpSourceFactory _sourceFactory;

    public ClrMdHeapObjectValueService()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal ClrMdHeapObjectValueService(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<HeapObjectValueResult> ReadAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Info.Path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (objectAddress == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectAddress));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => Read(path, objectAddress, options, cancellationToken),
            cancellationToken);
    }

    private HeapObjectValueResult Read(
        string path,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = _sourceFactory.Open(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (!source.CanWalkHeap)
        {
            throw new InvalidDataException(
                "The dump was captured while the GC heap was not walkable.");
        }

        return source.ReadObjectValues(objectAddress, options, cancellationToken);
    }
}
