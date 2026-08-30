namespace MemoryProfiler.Contracts.Heap;

public sealed record GcRootInfo(
    ulong RootAddress,
    ulong ObjectAddress,
    string Kind,
    string? Name,
    IReadOnlyList<ObjectReference>? Path = null)
{
    public bool Equals(GcRootInfo? other) =>
        other is not null &&
        RootAddress == other.RootAddress &&
        ObjectAddress == other.ObjectAddress &&
        Kind == other.Kind &&
        Name == other.Name &&
        SequenceEqual(Path, other.Path);

    public override int GetHashCode() => HashCode.Combine(
        RootAddress,
        ObjectAddress,
        Kind,
        Name,
        Path is null ? 0 : Path.Count);

    private static bool SequenceEqual(
        IReadOnlyList<ObjectReference>? left,
        IReadOnlyList<ObjectReference>? right)
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
