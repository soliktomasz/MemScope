using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Objects;

public sealed class ClrMdHeapObjectRepository : IHeapObjectRepository
{
    private readonly IHeapDumpSourceFactory _sourceFactory;

    public ClrMdHeapObjectRepository()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal ClrMdHeapObjectRepository(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
        HeapSnapshot snapshot,
        ulong methodTable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Info.Path);
        if (methodTable == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(methodTable),
                "Method table must be non-zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => GetInstances(fullPath, methodTable, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<HeapObjectInfo> GetInstances(
        string path,
        ulong methodTable,
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

        var instances = new List<HeapObjectInfo>();
        foreach (var heapObject in source.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heapObject.MethodTable != methodTable ||
                !heapObject.IsValid ||
                heapObject.IsFree ||
                heapObject.MethodTable == 0)
            {
                continue;
            }

            instances.Add(new HeapObjectInfo(
                heapObject.Address,
                heapObject.MethodTable,
                heapObject.TypeName ?? string.Empty,
                heapObject.Size,
                GenerationLabel(source.GetGeneration(heapObject.Address))));
        }

        instances.Sort(static (left, right) => left.Address.CompareTo(right.Address));

        cancellationToken.ThrowIfCancellationRequested();
        return instances;
    }

    internal static string GenerationLabel(Generation? generation) =>
        generation switch
        {
            Generation.Generation0 => "Gen0",
            Generation.Generation1 => "Gen1",
            Generation.Generation2 => "Gen2",
            Generation.Large => "LOH",
            Generation.Pinned => "Pinned",
            Generation.Frozen => "Frozen",
            _ => "Unknown",
        };
}
