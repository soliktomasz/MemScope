using System.Text.Json;
using MemoryProfiler.Storage.Storage;
using Xunit;

namespace MemoryProfiler.Storage.Tests.Storage;

public sealed class JsonSessionRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"memscope-storage-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingCatalogLoadsAsEmpty()
    {
        var repository = Repository();

        var catalog = await repository.LoadAsync();

        Assert.Empty(catalog.RecentDumps);
        Assert.Empty(catalog.RecentInvestigations);
        Assert.Empty(catalog.ComparisonPairs);
    }

    [Fact]
    public async Task SavedCatalogRoundTripsEveryMetadataField()
    {
        var timestamp = new DateTimeOffset(2026, 8, 31, 12, 30, 0, TimeSpan.Zero);
        var expected = SessionCatalog.Empty
            .WithRecentDump(new RecentDump(
                "/dumps/api.dmp", "Api", 42, "10.0.0", timestamp, 123, 456))
            .WithRecentInvestigation(new RecentInvestigation(
                "/dumps/api.dmp", "Api", timestamp.AddMinutes(1)))
            .WithComparison(new ComparisonPair(
                "/dumps/before.dmp", "/dumps/after.dmp", timestamp.AddMinutes(2)));
        var repository = Repository();

        await repository.SaveAsync(expected);
        var actual = await repository.LoadAsync();

        Assert.Equal(expected.RecentDumps, actual.RecentDumps);
        Assert.Equal(expected.RecentInvestigations, actual.RecentInvestigations);
        Assert.Equal(expected.ComparisonPairs, actual.ComparisonPairs);
    }

    [Fact]
    public async Task CorruptJsonIsReportedInsteadOfSilentlyResettingHistory()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(CatalogPath(), "{not-json");
        var repository = Repository();

        await Assert.ThrowsAsync<JsonException>(() => repository.LoadAsync());
    }

    [Fact]
    public async Task PreCancelledSaveDoesNotCreateAFile()
    {
        var repository = Repository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.SaveAsync(SessionCatalog.Empty, cancellation.Token));

        Assert.False(File.Exists(CatalogPath()));
    }

    [Fact]
    public async Task ReplacingCatalogLeavesOnlyValidCurrentJson()
    {
        var repository = Repository();
        await repository.SaveAsync(SessionCatalog.Empty.WithRecentInvestigation(
            new RecentInvestigation("old.dmp", "Old", DateTimeOffset.UnixEpoch)));
        await repository.SaveAsync(SessionCatalog.Empty.WithRecentInvestigation(
            new RecentInvestigation("new.dmp", "New", DateTimeOffset.UnixEpoch.AddDays(1))));

        var catalog = await repository.LoadAsync();

        Assert.Equal("new.dmp", Assert.Single(catalog.RecentInvestigations).Path);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    private JsonSessionRepository Repository() => new(CatalogPath());

    private string CatalogPath() => Path.Combine(_directory, "sessions.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
