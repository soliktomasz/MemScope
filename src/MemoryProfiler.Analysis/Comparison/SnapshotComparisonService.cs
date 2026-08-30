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
        var merged = new Dictionary<string, DeltaAccumulator>(StringComparer.Ordinal);
        foreach (var type in before.Types)
        {
            merged[type.Name] = new DeltaAccumulator(type, isBefore: true);
        }

        foreach (var type in after.Types)
        {
            if (merged.TryGetValue(type.Name, out var accumulator))
            {
                accumulator.AddAfter(type);
            }
            else
            {
                merged[type.Name] = new DeltaAccumulator(type, isBefore: false);
            }
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

    private sealed class DeltaAccumulator
    {
        private readonly string _typeName;
        private long _countBefore;
        private long _countAfter;
        private long _sizeBefore;
        private long _sizeAfter;
        private ulong? _retainedBefore;
        private ulong? _retainedAfter;

        public DeltaAccumulator(HeapTypeInfo type, bool isBefore)
        {
            _typeName = type.Name;
            if (isBefore)
            {
                AddBefore(type);
            }
            else
            {
                AddAfter(type);
            }
        }

        public void AddAfter(HeapTypeInfo type)
        {
            _countAfter = type.ObjectCount;
            _sizeAfter = ToLong(type.ShallowSize);
            _retainedAfter = type.RetainedSize;
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

        private void AddBefore(HeapTypeInfo type)
        {
            _countBefore = type.ObjectCount;
            _sizeBefore = ToLong(type.ShallowSize);
            _retainedBefore = type.RetainedSize;
        }

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

        private static long ToLong(ulong value) =>
            value > (ulong)long.MaxValue ? long.MaxValue : (long)value;
    }
}
