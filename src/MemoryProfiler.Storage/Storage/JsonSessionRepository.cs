using System.Text.Json;

namespace MemoryProfiler.Storage.Storage;

public sealed class JsonSessionRepository : ISessionRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _catalogPath;

    public JsonSessionRepository(string? catalogPath = null)
    {
        _catalogPath = Path.GetFullPath(catalogPath ?? GetDefaultCatalogPath());
    }

    public async Task<SessionCatalog> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_catalogPath))
        {
            return SessionCatalog.Empty;
        }

        await using var stream = new FileStream(
            _catalogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var catalog = await JsonSerializer.DeserializeAsync<SessionCatalog>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (catalog is null)
        {
            throw new JsonException("The session catalog contains no data.");
        }

        return Normalize(catalog);
    }

    public async Task SaveAsync(
        SessionCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_catalogPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_catalogPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    catalog,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _catalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetDefaultCatalogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemScope",
        "sessions.json");

    private static SessionCatalog Normalize(SessionCatalog catalog)
    {
        var normalized = SessionCatalog.Empty;
        foreach (var dump in catalog.RecentDumps)
        {
            normalized = normalized.WithRecentDump(dump);
        }

        foreach (var investigation in catalog.RecentInvestigations)
        {
            normalized = normalized.WithRecentInvestigation(investigation);
        }

        foreach (var comparison in catalog.ComparisonPairs)
        {
            normalized = normalized.WithComparison(comparison);
        }

        return normalized;
    }
}
