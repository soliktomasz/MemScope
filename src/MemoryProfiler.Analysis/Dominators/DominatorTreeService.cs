using System.Runtime.CompilerServices;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Dominators;

public sealed class DominatorTreeService : IDominatorTreeService
{
    // Progress phase boundaries across the computation: graph build, root
    // reachability, dominator iterations, retained-size accumulation.
    private const double GraphProgressEnd = 0.35;
    private const double ReachabilityProgressEnd = 0.45;
    private const double DominatorsProgressEnd = 0.90;
    private const long ProgressReportStride = 1_024;

    // Address 0 never identifies a heap object (the loader skips such objects),
    // so it is a safe sentinel for the synthetic root that dominates every GC root.
    private const ulong SyntheticRoot = 0;

    private readonly IHeapDumpSourceFactory _sourceFactory;

    // Keyed on the HeapSnapshot instance so a result lives exactly as long as
    // the snapshot it was computed for: closing and releasing a snapshot makes
    // its entry (and the DominatorInfo graph it holds) eligible for collection,
    // so the cache cannot retain every snapshot across the application session.
    private readonly ConditionalWeakTable<HeapSnapshot, DominatorAnalysisResult> _cache = new();

    public DominatorTreeService()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal DominatorTreeService(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<DominatorAnalysisResult> ComputeDominatorsAsync(
        HeapSnapshot snapshot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cache.TryGetValue(snapshot, out var cached))
        {
            progress?.Report(1.0);
            return Task.FromResult(cached);
        }

        // The computation is expensive: off the caller thread, cancellable,
        // with the result cached per snapshot so later queries reuse it.
        var fullPath = Path.GetFullPath(snapshot.Info.Path);
        return Task.Run(
            () => Compute(fullPath, snapshot, snapshot.Info.ObjectCount, progress, cancellationToken),
            cancellationToken);
    }

    private DominatorAnalysisResult Compute(
        string path,
        HeapSnapshot snapshot,
        long totalObjects,
        IProgress<double>? progress,
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

        // Phase 1 — reference graph: every valid object with its size and type,
        // plus the forward and reverse edge sets (self-references are dropped;
        // they cannot change dominance and would poison the predecessor scan).
        var nodes = new Dictionary<ulong, NodeData>();
        var outgoing = new Dictionary<ulong, List<ulong>>();
        var predecessors = new Dictionary<ulong, List<ulong>>();
        long enumerated = 0;
        foreach (var heapObject in source.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnalyzed(heapObject))
            {
                continue;
            }

            var address = heapObject.Address;
            nodes[address] = new NodeData(heapObject.Size, heapObject.TypeName!, heapObject.MethodTable);
            foreach (var reference in source.EnumerateOutgoingReferences(address))
            {
                var target = reference.TargetAddress;
                if (target == 0 || target == address)
                {
                    continue;
                }

                AddEdge(outgoing, address, target);
                AddEdge(predecessors, target, address);
            }

            enumerated++;
            if ((enumerated & (ProgressReportStride - 1)) == 0)
            {
                ReportProgress(progress, GraphProgressEnd * Fraction(enumerated, totalObjects));
            }
        }

        // A reference to an address that was never enumerated as an object
        // (absent, free, or filtered) is dangling: it carries no heap edge.
        TrimEdges(outgoing, nodes);
        TrimEdges(predecessors, nodes);
        ReportProgress(progress, GraphProgressEnd);

        // Phase 2 — reachability from the GC roots. Unreachable objects are
        // garbage: they dominate nothing and are excluded from the results.
        var reachable = new HashSet<ulong>();
        var order = new List<ulong>();
        var depths = new Dictionary<ulong, int> { [SyntheticRoot] = 0 };
        var queue = new Queue<ulong>();
        foreach (var root in source.EnumerateRoots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootAddress = root.ObjectAddress;
            if (rootAddress == 0 || !nodes.ContainsKey(rootAddress) || !reachable.Add(rootAddress))
            {
                continue;
            }

            // The synthetic root is a predecessor of every GC root, which seeds
            // idom[root] = synthetic root in the first dominator iteration.
            AddEdge(predecessors, rootAddress, SyntheticRoot);
            depths[rootAddress] = 1;
            queue.Enqueue(rootAddress);
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            order.Add(current);
            if (outgoing.TryGetValue(current, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!reachable.Contains(target))
                    {
                        reachable.Add(target);
                        depths[target] = depths[current] + 1;
                        queue.Enqueue(target);
                    }
                }
            }
        }

        ReportProgress(progress, ReachabilityProgressEnd);

        // Phase 3 — immediate dominators (Cooper–Harvey–Kennedy). idom[n] starts
        // undefined; the intersect walks both predecessor chains to their first
        // common ancestor using the BFS depth, which strictly decreases along
        // every dominator chain.
        var idom = new Dictionary<ulong, ulong>(order.Count + 1) { [SyntheticRoot] = SyntheticRoot };
        var changed = true;
        var iteration = 1;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            var processed = 0;
            foreach (var address in order)
            {
                if (!predecessors.TryGetValue(address, out var preds))
                {
                    continue;
                }

                ulong newIdom = 0;
                var found = false;
                foreach (var pred in preds)
                {
                    if (!idom.ContainsKey(pred))
                    {
                        continue;
                    }

                    newIdom = found ? Intersect(pred, newIdom, depths, idom) : pred;
                    found = true;
                }

                if (!found || (idom.TryGetValue(address, out var previous) && previous == newIdom))
                {
                    continue;
                }

                idom[address] = newIdom;
                changed = true;

                if ((++processed & (ProgressReportStride - 1)) == 0)
                {
                    ReportProgress(
                        progress,
                        DominatorsProgress(iteration, processed, order.Count));
                }
            }

            ReportProgress(progress, DominatorsProgress(iteration, order.Count, order.Count));
            iteration++;
        }

        // The dominator tree is a BFS-ordered hierarchy: every parent precedes
        // its children, so the reverse pass accumulates retained sizes
        // bottom-up (each node's children have already added themselves).
        var retainedSize = new Dictionary<ulong, ulong>(order.Count + 1);
        var retainedCount = new Dictionary<ulong, long>(order.Count + 1);
        foreach (var address in order)
        {
            retainedSize[address] = nodes[address].Size;
            retainedCount[address] = 1;
        }

        retainedSize[SyntheticRoot] = 0;
        retainedCount[SyntheticRoot] = 0;
        for (var index = order.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = order[index];
            var parent = idom[address];
            checked
            {
                retainedSize[parent] += retainedSize[address];
                retainedCount[parent] += retainedCount[address];
            }

            if ((index & (ProgressReportStride - 1)) == 0)
            {
                ReportProgress(
                    progress,
                    RetainedProgress(order.Count - index, order.Count));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var dominators = new List<DominatorInfo>(order.Count);
        foreach (var address in order)
        {
            dominators.Add(new DominatorInfo(
                address,
                nodes[address].TypeName,
                nodes[address].Size,
                retainedSize[address],
                retainedCount[address]));
        }

        dominators.Sort(static (left, right) =>
        {
            var byRetained = right.RetainedSize.CompareTo(left.RetainedSize);
            if (byRetained != 0)
            {
                return byRetained;
            }

            return string.CompareOrdinal(left.TypeName, right.TypeName);
        });

        var typeRetained = BuildTypeRetainedSizes(nodes, order, idom, retainedSize);
        var result = new DominatorAnalysisResult(dominators, typeRetained);
        _cache.GetValue(snapshot, _ => result);
        ReportProgress(progress, 1.0);
        return result;
    }

    // Per-type retained size: the memory that would be freed if every reachable
    // instance of the type were collected. A naive sum of per-object retained
    // sizes double-counts nested same-type objects: if a Node dominates a Node[]
    // that dominates another Node, the inner Node's subtree lies inside the
    // outer Node's retained size too. Each object therefore contributes its
    // retained size only when no same-type ancestor exists in the dominator
    // tree — equivalently, an object's retained size covers all of its nearest
    // same-type descendants (reached by traversing through different-type
    // nodes, stopping beneath each same-type descendant), and those descendants
    // are not counted again. Types whose objects are all garbage contribute 0
    // (nothing keeps them alive and they keep nothing alive).
    private static IReadOnlyList<TypeRetainedSize> BuildTypeRetainedSizes(
        IReadOnlyDictionary<ulong, NodeData> nodes,
        IReadOnlyList<ulong> order,
        IReadOnlyDictionary<ulong, ulong> idom,
        IReadOnlyDictionary<ulong, ulong> retainedSize)
    {
        var contribution = new Dictionary<ulong, ulong>();
        var typeNames = new Dictionary<ulong, string>();
        foreach (var node in nodes.Values)
        {
            contribution.TryAdd(node.MethodTable, 0);
            typeNames.TryAdd(node.MethodTable, node.TypeName);
        }

        var children = new Dictionary<ulong, List<ulong>>();
        foreach (var address in order)
        {
            var parent = idom[address];
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(address);
        }

        // One dominator-tree walk keeps the set of types on the current
        // root-to-node path (maintained incrementally with enter/exit markers,
        // so the whole pass is O(nodes)). A node whose type already appears on
        // the path is nested beneath a same-type ancestor — possibly through
        // different-type nodes, e.g. Node -> Node[] -> Node — and its retained
        // size is already covered by that ancestor, so it contributes nothing.
        var pathTypeCounts = new Dictionary<ulong, int>();
        var stack = new Stack<(ulong Node, bool Exit)>();
        if (children.TryGetValue(SyntheticRoot, out var rootChildren))
        {
            foreach (var child in rootChildren)
            {
                stack.Push((child, Exit: false));
            }
        }

        while (stack.Count > 0)
        {
            var (node, exit) = stack.Pop();
            if (exit)
            {
                var type = nodes[node].MethodTable;
                if (pathTypeCounts[type] == 1)
                {
                    pathTypeCounts.Remove(type);
                }
                else
                {
                    pathTypeCounts[type]--;
                }

                continue;
            }

            var nodeType = nodes[node].MethodTable;
            if (!pathTypeCounts.ContainsKey(nodeType))
            {
                contribution[nodeType] += retainedSize[node];
            }

            pathTypeCounts[nodeType] = pathTypeCounts.GetValueOrDefault(nodeType) + 1;
            stack.Push((node, Exit: true));
            if (children.TryGetValue(node, out var list))
            {
                foreach (var child in list)
                {
                    stack.Push((child, Exit: false));
                }
            }
        }

        var types = new List<TypeRetainedSize>(contribution.Count);
        foreach (var pair in contribution)
        {
            types.Add(new TypeRetainedSize(pair.Key, typeNames[pair.Key], pair.Value));
        }

        types.Sort(static (left, right) =>
        {
            var byRetained = right.RetainedSize.CompareTo(left.RetainedSize);
            if (byRetained != 0)
            {
                return byRetained;
            }

            return string.CompareOrdinal(left.TypeName, right.TypeName);
        });
        return types;
    }

    private static ulong Intersect(
        ulong left,
        ulong right,
        IReadOnlyDictionary<ulong, int> depths,
        IReadOnlyDictionary<ulong, ulong> idom)
    {
        var finger1 = left;
        var finger2 = right;
        while (finger1 != finger2)
        {
            while (depths[finger1] > depths[finger2])
            {
                finger1 = idom[finger1];
            }

            while (depths[finger2] > depths[finger1])
            {
                finger2 = idom[finger2];
            }

            if (finger1 != finger2)
            {
                finger1 = idom[finger1];
                finger2 = idom[finger2];
            }
        }

        return finger1;
    }

    private static void AddEdge(
        Dictionary<ulong, List<ulong>> edges,
        ulong source,
        ulong target)
    {
        if (!edges.TryGetValue(source, out var list))
        {
            edges[source] = list = [];
        }

        list.Add(target);
    }

    private static void TrimEdges(
        Dictionary<ulong, List<ulong>> edges,
        IReadOnlyDictionary<ulong, NodeData> nodes)
    {
        foreach (var pair in edges)
        {
            pair.Value.RemoveAll(target => !nodes.ContainsKey(target));
        }
    }

    private static bool IsAnalyzed(HeapObjectData heapObject) =>
        heapObject.IsValid &&
        !heapObject.IsFree &&
        heapObject.MethodTable != 0 &&
        !string.IsNullOrWhiteSpace(heapObject.TypeName);

    private static double Fraction(long part, long total) =>
        total <= 0 ? 1.0 : Math.Min(1.0, (double)part / total);

    // Each dominator iteration covers the remaining progress gap by half, so
    // the reported value approaches the phase end monotonically and reaches it
    // exactly on the final iteration.
    private static double DominatorsProgress(int iteration, int processed, int total)
    {
        var gap = DominatorsProgressEnd - ReachabilityProgressEnd;
        var remaining = Math.Pow(0.5, iteration - 1) * (1.0 - 0.5 * Fraction(processed, total));
        return DominatorsProgressEnd - gap * remaining;
    }

    private static double RetainedProgress(int processed, int total) =>
        DominatorsProgressEnd + (1.0 - DominatorsProgressEnd) * Fraction(processed, total);

    private static void ReportProgress(IProgress<double>? progress, double value)
    {
        progress?.Report(Math.Clamp(value, 0.0, 1.0));
    }

    private static void Validate(HeapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Info.Path);
    }

    private readonly record struct NodeData(ulong Size, string TypeName, ulong MethodTable);
}
