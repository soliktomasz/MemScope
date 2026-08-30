using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Comparison;

public sealed class SnapshotComparisonService : ISnapshotComparisonService
{
    public SnapshotComparison Compare(
        HeapSnapshot before,
        HeapSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // Types are keyed by name: a type present in only one snapshot
        // contributes zero counts/sizes for the missing side, so new types
        // surface as positive deltas and disappeared types as negative ones.
        // A name may appear more than once per snapshot (the same type name
        // from different method tables or assemblies), so every entry is
        // aggregated into its row rather than overwriting the previous one.
        var merged = new Dictionary<string, DeltaAccumulator>(StringComparer.Ordinal);
        foreach (var type in before.Types)
        {
            GetOrAdd(merged, type.Name).Add(type, isBefore: true);
        }

        foreach (var type in after.Types)
        {
            GetOrAdd(merged, type.Name).Add(type, isBefore: false);
        }

        var deltas = new List<TypeMemoryDelta>(merged.Count);
        foreach (var accumulator in merged.Values)
        {
            deltas.Add(accumulator.ToDelta());
        }

        // Default order: biggest growth first, then biggest count growth, then
        // type name — the UI can re-sort into any other order.
        deltas.Sort(static (left, right) =>
        {
            var bySize = right.SizeDelta.CompareTo(left.SizeDelta);
            if (bySize != 0)
            {
                return bySize;
            }

            var byCount = right.CountDelta.CompareTo(left.CountDelta);
            if (byCount != 0)
            {
                return byCount;
            }

            return string.CompareOrdinal(left.TypeName, right.TypeName);
        });
        return new SnapshotComparison(deltas);
    }

    private static DeltaAccumulator GetOrAdd(
        Dictionary<string, DeltaAccumulator> merged,
        string typeName)
    {
        if (!merged.TryGetValue(typeName, out var accumulator))
        {
            merged[typeName] = accumulator = new DeltaAccumulator(typeName);
        }

        return accumulator;
    }

    private sealed class DeltaAccumulator
    {
        private readonly string _typeName;
        private long _countBefore;
        private long _countAfter;
        private long _sizeBefore;
        private long _sizeAfter;
        private ulong? _retainedBefore;
        private bool _hasRetainedBefore;
        private ulong? _retainedAfter;
        private bool _hasRetainedAfter;

        public DeltaAccumulator(string typeName) => _typeName = typeName;

        public void Add(HeapTypeInfo type, bool isBefore)
        {
            if (isBefore)
            {
                checked
                {
                    _countBefore += type.ObjectCount;
                }

                _sizeBefore = AddSizes(_sizeBefore, type.ShallowSize);
                AddRetained(isBefore: true, type.RetainedSize);
            }
            else
            {
                checked
                {
                    _countAfter += type.ObjectCount;
                }

                _sizeAfter = AddSizes(_sizeAfter, type.ShallowSize);
                AddRetained(isBefore: false, type.RetainedSize);
            }
        }

        public TypeMemoryDelta ToDelta()
        {
            checked
            {
                return new TypeMemoryDelta(
                    _typeName,
                    _countBefore,
                    _countAfter,
                    _countAfter - _countBefore,
                    _sizeBefore,
                    _sizeAfter,
                    _sizeAfter - _sizeBefore,
                    RetainedDelta(_retainedBefore, _retainedAfter));
            }
        }

        // Sizes accumulate only forward from 0 (never negative), so the sum is
        // exact in ulong space and saturates at long.MaxValue instead of
        // wrapping or throwing.
        private static long AddSizes(long current, ulong additional)
        {
            var room = (ulong)long.MaxValue - (ulong)current;
            return additional >= room ? long.MaxValue : (long)((ulong)current + additional);
        }

        private void AddRetained(bool isBefore, ulong? retained)
        {
            if (isBefore)
            {
                if (_hasRetainedBefore)
                {
                    _retainedBefore = CombineRetained(_retainedBefore, retained);
                }
                else
                {
                    _retainedBefore = retained;
                    _hasRetainedBefore = true;
                }
            }
            else if (_hasRetainedAfter)
            {
                _retainedAfter = CombineRetained(_retainedAfter, retained);
            }
            else
            {
                _retainedAfter = retained;
                _hasRetainedAfter = true;
            }
        }

        // Retained sizes combine only when every contributing entry carries
        // one; a missing value on either side keeps the delta unavailable.
        private static ulong? CombineRetained(ulong? current, ulong? additional) =>
            current is null || additional is null
                ? null
                : checked(current.Value + additional.Value);

        // Retained sizes are optional: the delta is meaningful only when both
        // sides carry them (the dominator analysis produced them for both).
        private static long? RetainedDelta(ulong? before, ulong? after)
        {
            if (before is null || after is null)
            {
                return null;
            }

            // A heap cannot hold more than long.MaxValue bytes, but the cast
            // must still be safe on every path: clamp instead of wrapping.
            if (after >= before)
            {
                var growth = after.Value - before.Value;
                return growth > (ulong)long.MaxValue ? long.MaxValue : (long)growth;
            }

            var shrink = before.Value - after.Value;
            return shrink > (ulong)long.MaxValue ? long.MinValue : -(long)shrink;
        }
    }
}
