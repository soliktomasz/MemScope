using MemoryProfiler.Storage.Storage;
using Xunit;

namespace MemoryProfiler.Storage.Tests.Storage;

public sealed class SessionCatalogTests
{
    private static readonly DateTimeOffset Older = new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewerInvestigationForTheSamePathReplacesOlderMetadata()
    {
        var catalog = SessionCatalog.Empty
            .WithRecentInvestigation(new RecentInvestigation("/dumps/api.dmp", "Old Api", Older))
            .WithRecentInvestigation(new RecentInvestigation("/dumps/api.dmp", "Current Api", Newer));

        var investigation = Assert.Single(catalog.RecentInvestigations);
        Assert.Equal("Current Api", investigation.ProcessName);
        Assert.Equal(Newer, investigation.LastOpenedAt);
    }

    [Fact]
    public void UpdatingADumpKeepsTheEnrichedSnapshotMetadata()
    {
        var catalog = SessionCatalog.Empty
            .WithRecentDump(new RecentDump(
                "/dumps/api.dmp", "Api", 42, "10.0.0", Older, null, null))
            .WithRecentDump(new RecentDump(
                "/dumps/api.dmp", "Api", 42, "10.0.0", Newer, 123, 456));

        var dump = Assert.Single(catalog.RecentDumps);
        Assert.Equal(Newer, dump.CapturedAt);
        Assert.Equal(123, dump.ObjectCount);
        Assert.Equal(456UL, dump.HeapSize);
    }

    [Fact]
    public void ComparisonPathOrderIsPartOfItsIdentity()
    {
        var catalog = SessionCatalog.Empty
            .WithComparison(new ComparisonPair("before.dmp", "after.dmp", Older))
            .WithComparison(new ComparisonPair("after.dmp", "before.dmp", Newer));

        Assert.Collection(
            catalog.ComparisonPairs,
            pair =>
            {
                Assert.Equal("after.dmp", pair.BeforePath);
                Assert.Equal("before.dmp", pair.AfterPath);
            },
            pair =>
            {
                Assert.Equal("before.dmp", pair.BeforePath);
                Assert.Equal("after.dmp", pair.AfterPath);
            });
    }

    [Fact]
    public void EveryCollectionKeepsOnlyTheTwentyNewestEntries()
    {
        var catalog = SessionCatalog.Empty;
        for (var index = 0; index < 21; index++)
        {
            var timestamp = Older.AddMinutes(index);
            catalog = catalog
                .WithRecentDump(new RecentDump(
                    $"/dumps/{index}.dmp", null, null, null, timestamp, null, null))
                .WithRecentInvestigation(new RecentInvestigation(
                    $"/dumps/{index}.dmp", null, timestamp))
                .WithComparison(new ComparisonPair(
                    $"/dumps/{index}-before.dmp",
                    $"/dumps/{index}-after.dmp",
                    timestamp));
        }

        Assert.Equal(20, catalog.RecentDumps.Count);
        Assert.Equal("/dumps/20.dmp", catalog.RecentDumps[0].Path);
        Assert.DoesNotContain(catalog.RecentDumps, dump => dump.Path == "/dumps/0.dmp");
        Assert.Equal(20, catalog.RecentInvestigations.Count);
        Assert.Equal("/dumps/20.dmp", catalog.RecentInvestigations[0].Path);
        Assert.Equal(20, catalog.ComparisonPairs.Count);
        Assert.Equal("/dumps/20-before.dmp", catalog.ComparisonPairs[0].BeforePath);
    }

    [Fact]
    public void ConstructorNormalizesEveryCollectionToCatalogInvariants()
    {
        var dumps = Enumerable.Range(0, 21)
            .Select(index => new RecentDump(
                $"/dumps/{index}.dmp",
                null,
                null,
                null,
                Older.AddMinutes(index),
                null,
                null))
            .Append(new RecentDump(
                "/dumps/20.dmp", "Newest", null, null, Newer, null, null))
            .ToArray();
        var investigations = Enumerable.Range(0, 21)
            .Select(index => new RecentInvestigation(
                $"/dumps/{index}.dmp",
                null,
                Older.AddMinutes(index)))
            .Append(new RecentInvestigation("/dumps/20.dmp", "Newest", Newer))
            .ToArray();
        var comparisons = Enumerable.Range(0, 21)
            .Select(index => new ComparisonPair(
                $"/dumps/{index}-before.dmp",
                $"/dumps/{index}-after.dmp",
                Older.AddMinutes(index)))
            .Append(new ComparisonPair(
                "/dumps/20-before.dmp",
                "/dumps/20-after.dmp",
                Newer))
            .ToArray();

        var catalog = new SessionCatalog(dumps, investigations, comparisons);

        Assert.Equal(20, catalog.RecentDumps.Count);
        Assert.Equal("Newest", catalog.RecentDumps[0].ProcessName);
        Assert.DoesNotContain(catalog.RecentDumps, dump => dump.Path == "/dumps/0.dmp");
        Assert.Equal(20, catalog.RecentInvestigations.Count);
        Assert.Equal("Newest", catalog.RecentInvestigations[0].ProcessName);
        Assert.Equal(20, catalog.ComparisonPairs.Count);
        Assert.Equal(Newer, catalog.ComparisonPairs[0].LastComparedAt);
    }
}
