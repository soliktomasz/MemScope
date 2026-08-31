namespace MemoryProfiler.Storage.Storage;

public interface ISessionRepository
{
    Task<SessionCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        SessionCatalog catalog,
        CancellationToken cancellationToken = default);
}
