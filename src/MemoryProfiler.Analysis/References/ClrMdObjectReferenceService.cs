using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.References;

public sealed class ClrMdObjectReferenceService : IObjectReferenceService
{
    private readonly IHeapDumpSourceFactory _sourceFactory;

    public ClrMdObjectReferenceService()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal ClrMdObjectReferenceService(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(snapshot, objectAddress);
        var fullPath = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => GetOutgoingReferences(fullPath, objectAddress, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(snapshot, objectAddress);
        var fullPath = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => GetIncomingReferences(fullPath, objectAddress, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<ObjectReference> GetOutgoingReferences(
        string path,
        ulong objectAddress,
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

        var references = new List<ObjectReference>();
        foreach (var reference in source.EnumerateOutgoingReferences(objectAddress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Add(reference);
        }

        references.Sort(static (left, right) => left.TargetAddress.CompareTo(right.TargetAddress));

        cancellationToken.ThrowIfCancellationRequested();
        return references;
    }

    private IReadOnlyList<ObjectReference> GetIncomingReferences(
        string path,
        ulong objectAddress,
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

        var references = new List<ObjectReference>();
        foreach (var reference in source.EnumerateIncomingReferences(objectAddress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Add(reference);
        }

        // Heap objects first (by source address), GC roots last so the rows
        // the user can navigate into lead the list.
        references.Sort(static (left, right) =>
        {
            var leftIsRoot = left.SourceAddress == 0 ? 1 : 0;
            var rightIsRoot = right.SourceAddress == 0 ? 1 : 0;
            var bySource = leftIsRoot.CompareTo(rightIsRoot);
            if (bySource != 0)
            {
                return bySource;
            }

            var byAddress = left.SourceAddress.CompareTo(right.SourceAddress);
            if (byAddress != 0)
            {
                return byAddress;
            }

            return string.CompareOrdinal(left.Name, right.Name);
        });

        cancellationToken.ThrowIfCancellationRequested();
        return references;
    }

    private static void Validate(HeapSnapshot snapshot, ulong objectAddress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Info.Path);
        if (objectAddress == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectAddress),
                "Object address must be non-zero.");
        }
    }
}
