namespace MemoryProfiler.Storage.Storage;

public sealed record RecentDump
{
    public RecentDump(
        string path,
        string? processName,
        int? processId,
        string? runtimeVersion,
        DateTimeOffset capturedAt,
        long? objectCount,
        ulong? heapSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        ProcessName = processName;
        ProcessId = processId;
        RuntimeVersion = runtimeVersion;
        CapturedAt = capturedAt;
        ObjectCount = objectCount;
        HeapSize = heapSize;
    }

    public string Path { get; init; }

    public string? ProcessName { get; init; }

    public int? ProcessId { get; init; }

    public string? RuntimeVersion { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    public long? ObjectCount { get; init; }

    public ulong? HeapSize { get; init; }
}

public sealed record RecentInvestigation
{
    public RecentInvestigation(
        string path,
        string? processName,
        DateTimeOffset lastOpenedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        ProcessName = processName;
        LastOpenedAt = lastOpenedAt;
    }

    public string Path { get; init; }

    public string? ProcessName { get; init; }

    public DateTimeOffset LastOpenedAt { get; init; }
}

public sealed record ComparisonPair
{
    public ComparisonPair(
        string beforePath,
        string afterPath,
        DateTimeOffset lastComparedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterPath);
        BeforePath = beforePath;
        AfterPath = afterPath;
        LastComparedAt = lastComparedAt;
    }

    public string BeforePath { get; init; }

    public string AfterPath { get; init; }

    public DateTimeOffset LastComparedAt { get; init; }
}

public sealed record SessionCatalog
{
    private const int MaximumEntries = 20;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly IEqualityComparer<ComparisonPair> ComparisonComparer =
        new ComparisonPathComparer();

    public SessionCatalog(
        IReadOnlyList<RecentDump> recentDumps,
        IReadOnlyList<RecentInvestigation> recentInvestigations,
        IReadOnlyList<ComparisonPair> comparisonPairs)
    {
        ArgumentNullException.ThrowIfNull(recentDumps);
        ArgumentNullException.ThrowIfNull(recentInvestigations);
        ArgumentNullException.ThrowIfNull(comparisonPairs);
        RecentDumps = NormalizeDumps(recentDumps);
        RecentInvestigations = NormalizeInvestigations(recentInvestigations);
        ComparisonPairs = NormalizeComparisons(comparisonPairs);
    }

    public static SessionCatalog Empty { get; } = new([], [], []);

    public IReadOnlyList<RecentDump> RecentDumps { get; private init; }

    public IReadOnlyList<RecentInvestigation> RecentInvestigations { get; private init; }

    public IReadOnlyList<ComparisonPair> ComparisonPairs { get; private init; }

    public SessionCatalog WithRecentDump(RecentDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var items = NormalizeDumps(RecentDumps.Append(dump));
        return this with { RecentDumps = items };
    }

    public SessionCatalog WithRecentInvestigation(RecentInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var items = NormalizeInvestigations(RecentInvestigations.Append(investigation));
        return this with { RecentInvestigations = items };
    }

    public SessionCatalog WithComparison(ComparisonPair comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var items = NormalizeComparisons(ComparisonPairs.Append(comparison));
        return this with { ComparisonPairs = items };
    }

    private static RecentDump[] NormalizeDumps(IEnumerable<RecentDump> dumps) =>
        dumps
            .OrderByDescending(item => item.CapturedAt)
            .DistinctBy(item => item.Path, PathComparer)
            .Take(MaximumEntries)
            .ToArray();

    private static RecentInvestigation[] NormalizeInvestigations(
        IEnumerable<RecentInvestigation> investigations) =>
        investigations
            .OrderByDescending(item => item.LastOpenedAt)
            .DistinctBy(item => item.Path, PathComparer)
            .Take(MaximumEntries)
            .ToArray();

    private static ComparisonPair[] NormalizeComparisons(
        IEnumerable<ComparisonPair> comparisons) =>
        comparisons
            .OrderByDescending(item => item.LastComparedAt)
            .Distinct(ComparisonComparer)
            .Take(MaximumEntries)
            .ToArray();

    private sealed class ComparisonPathComparer : IEqualityComparer<ComparisonPair>
    {
        public bool Equals(ComparisonPair? x, ComparisonPair? y) =>
            ReferenceEquals(x, y) ||
            x is not null &&
            y is not null &&
            PathComparer.Equals(x.BeforePath, y.BeforePath) &&
            PathComparer.Equals(x.AfterPath, y.AfterPath);

        public int GetHashCode(ComparisonPair pair)
        {
            var hash = new HashCode();
            hash.Add(pair.BeforePath, PathComparer);
            hash.Add(pair.AfterPath, PathComparer);
            return hash.ToHashCode();
        }
    }
}
