namespace MemoryProfiler.Analysis.Loading;

public interface IHeapSnapshotLoader
{
    Task<HeapSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default);
}
