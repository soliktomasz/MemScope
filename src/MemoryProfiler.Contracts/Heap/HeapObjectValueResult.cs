namespace MemoryProfiler.Contracts.Heap;

public sealed record HeapObjectValueResult(
    HeapObjectInfo Object,
    IReadOnlyList<HeapFieldValue> Fields,
    int TotalFieldOrElementCount,
    bool HasMoreElements)
{
    public bool Equals(HeapObjectValueResult? other) =>
        other is not null &&
        Object == other.Object &&
        TotalFieldOrElementCount == other.TotalFieldOrElementCount &&
        HasMoreElements == other.HasMoreElements &&
        SequenceEqual(Fields, other.Fields);

    public override int GetHashCode() => HashCode.Combine(
        Object,
        TotalFieldOrElementCount,
        HasMoreElements,
        Fields is null ? 0 : Fields.Count);

    private static bool SequenceEqual(
        IReadOnlyList<HeapFieldValue>? left,
        IReadOnlyList<HeapFieldValue>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}
