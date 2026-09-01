namespace MemoryProfiler.Contracts.Heap;

public enum HeapValueKind
{
    Primitive,
    Enum,
    String,
    ObjectReference,
    ArrayElement,
    Null,
    Unavailable,
}
