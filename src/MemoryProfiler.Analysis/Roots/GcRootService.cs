using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Roots;

public sealed class GcRootService : IGcRootService
{
    internal const int MaxPathHops = 500;

    private readonly IHeapDumpSourceFactory _sourceFactory;

    public GcRootService()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal GcRootService(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(snapshot, objectAddress);
        var fullPath = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => FindRoots(fullPath, objectAddress, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<GcRootInfo> FindRoots(
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

        // Addresses from which the target is provably unreachable. A search that
        // exhausts its reachable component marks every visited address dead, so
        // the union of unsuccessful searches costs at most one heap walk.
        var dead = new HashSet<ulong>();
        // Outgoing references are enumerated at most once per address across all
        // roots of a single FindRoots call.
        var outgoingCache = new Dictionary<ulong, List<ObjectReference>>();
        var results = new List<GcRootInfo>();

        foreach (var root in source.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (root.ObjectAddress == 0 ||
                root.ObjectAddress == objectAddress)
            {
                // A root whose object is absent contributes nothing; a root that
                // references the target directly needs no hop chain.
                if (root.ObjectAddress != 0)
                {
                    results.Add(new GcRootInfo(
                        root.ObjectAddress,
                        objectAddress,
                        ClrMdHeapDumpSource.RootKindLabel(root.Kind),
                        root.Name,
                        Path: null));
                }

                continue;
            }

            var pathToObject = FindShortestPath(
                source,
                root.ObjectAddress,
                objectAddress,
                dead,
                outgoingCache,
                cancellationToken);
            if (pathToObject is not null)
            {
                results.Add(new GcRootInfo(
                    root.ObjectAddress,
                    objectAddress,
                    ClrMdHeapDumpSource.RootKindLabel(root.Kind),
                    root.Name,
                    pathToObject));
            }
        }

        results.Sort(static (left, right) =>
        {
            var byLength = (left.Path?.Count ?? 0).CompareTo(right.Path?.Count ?? 0);
            if (byLength != 0)
            {
                return byLength;
            }

            var byName = string.CompareOrdinal(left.Name, right.Name);
            if (byName != 0)
            {
                return byName;
            }

            return string.CompareOrdinal(left.Kind, right.Kind);
        });

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    private static IReadOnlyList<ObjectReference>? FindShortestPath(
        IHeapDumpSource source,
        ulong startAddress,
        ulong targetAddress,
        HashSet<ulong> dead,
        Dictionary<ulong, List<ObjectReference>> outgoingCache,
        CancellationToken cancellationToken)
    {
        // child address -> (parent address, the reference edge that discovered it)
        var visited = new Dictionary<ulong, (ulong Parent, ObjectReference Edge)>
        {
            [startAddress] = (0, new ObjectReference(0, 0, ReferenceKind.Field, null)),
        };
        var queue = new Queue<ulong>();
        queue.Enqueue(startAddress);

        for (var hops = 1; hops <= MaxPathHops && queue.Count > 0; hops++)
        {
            var levelCount = queue.Count;
            for (var index = 0; index < levelCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = queue.Dequeue();
                foreach (var reference in GetOutgoing(source, current, outgoingCache))
                {
                    var target = reference.TargetAddress;
                    if (target == 0 ||
                        dead.Contains(target) ||
                        visited.ContainsKey(target))
                    {
                        continue;
                    }

                    visited[target] = (current, reference);
                    if (target == targetAddress)
                    {
                        return ReconstructPath(targetAddress, visited);
                    }

                    queue.Enqueue(target);
                }
            }
        }

        // Only a search that exhausted its reachable component proves anything:
        // when the depth limit cut the frontier short, nothing may be marked
        // dead and the unexplored addresses stay live for other roots.
        if (queue.Count == 0)
        {
            foreach (var address in visited.Keys)
            {
                dead.Add(address);
            }
        }

        return null;
    }

    private static List<ObjectReference> GetOutgoing(
        IHeapDumpSource source,
        ulong address,
        Dictionary<ulong, List<ObjectReference>> outgoingCache)
    {
        if (outgoingCache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var references = source.EnumerateOutgoingReferences(address).ToList();
        outgoingCache[address] = references;
        return references;
    }

    private static List<ObjectReference> ReconstructPath(
        ulong targetAddress,
        IReadOnlyDictionary<ulong, (ulong Parent, ObjectReference Edge)> visited)
    {
        var path = new List<ObjectReference>();
        var current = targetAddress;
        while (visited.TryGetValue(current, out var hop) && hop.Parent != 0)
        {
            path.Add(hop.Edge);
            current = hop.Parent;
        }

        path.Reverse();
        return path;
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
