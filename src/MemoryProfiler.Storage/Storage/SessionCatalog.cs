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

    public SessionCatalog(
        IReadOnlyList<RecentDump> recentDumps,
        IReadOnlyList<RecentInvestigation> recentInvestigations,
        IReadOnlyList<ComparisonPair> comparisonPairs)
    {
        ArgumentNullException.ThrowIfNull(recentDumps);
        ArgumentNullException.ThrowIfNull(recentInvestigations);
        ArgumentNullException.ThrowIfNull(comparisonPairs);
        RecentDumps = recentDumps.ToArray();
        RecentInvestigations = recentInvestigations.ToArray();
        ComparisonPairs = comparisonPairs.ToArray();
    }

    public static SessionCatalog Empty { get; } = new([], [], []);

    public IReadOnlyList<RecentDump> RecentDumps { get; init; }

    public IReadOnlyList<RecentInvestigation> RecentInvestigations { get; init; }

    public IReadOnlyList<ComparisonPair> ComparisonPairs { get; init; }

    public SessionCatalog WithRecentDump(RecentDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var items = RecentDumps
            .Where(item => !PathComparer.Equals(item.Path, dump.Path))
            .Append(dump)
            .OrderByDescending(item => item.CapturedAt)
            .Take(MaximumEntries)
            .ToArray();
        return this with { RecentDumps = items };
    }

    public SessionCatalog WithRecentInvestigation(RecentInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var items = RecentInvestigations
            .Where(item => !PathComparer.Equals(item.Path, investigation.Path))
            .Append(investigation)
            .OrderByDescending(item => item.LastOpenedAt)
            .Take(MaximumEntries)
            .ToArray();
        return this with { RecentInvestigations = items };
    }

    public SessionCatalog WithComparison(ComparisonPair comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var items = ComparisonPairs
            .Where(item =>
                !PathComparer.Equals(item.BeforePath, comparison.BeforePath) ||
                !PathComparer.Equals(item.AfterPath, comparison.AfterPath))
            .Append(comparison)
            .OrderByDescending(item => item.LastComparedAt)
            .Take(MaximumEntries)
            .ToArray();
        return this with { ComparisonPairs = items };
    }
}
