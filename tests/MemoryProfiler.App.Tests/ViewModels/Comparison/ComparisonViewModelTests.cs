using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Comparison;

public sealed class ComparisonViewModelTests
{
    private static readonly HeapSnapshotInfo BeforeInfo = new(
        Path.GetFullPath("before.dmp"),
        "Sample.Process",
        4217,
        "10.0.0",
        new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
        300,
        35_000_000);

    private static readonly HeapSnapshotInfo AfterInfo = new(
        Path.GetFullPath("after.dmp"),
        "Sample.Process",
        4217,
        "10.0.0",
        new DateTimeOffset(2026, 9, 2, 10, 5, 0, TimeSpan.Zero),
        550,
        55_000_000);

    private static HeapSnapshot BeforeSnapshot() =>
        new()
        {
            Info = BeforeInfo,
            Types =
            [
                new HeapTypeInfo(0x100, "System.Byte[]", "System.Private.CoreLib", 100, 134_217_728, null),
                new HeapTypeInfo(0x200, "System.String", "System.Private.CoreLib", 200, 20_000_000, null),
            ],
        };

    private static HeapSnapshot AfterSnapshot() =>
        new()
        {
            Info = AfterInfo,
            Types =
            [
                new HeapTypeInfo(0x100, "System.Byte[]", "System.Private.CoreLib", 300, 268_435_456, null),
                new HeapTypeInfo(0x200, "System.String", "System.Private.CoreLib", 200, 20_000_000, null),
                new HeapTypeInfo(0x300, "MyApp.Cache", "MyApp", 50, 5_000_000, null),
            ],
        };

    private static ComparisonViewModel CreateViewModel(
        IHeapSnapshotLoader loader,
        StubDumpFilePicker picker,
        IDominatorTreeService? dominatorService = null,
        Func<Task>? close = null) =>
        new(
            loader,
            new SnapshotComparisonService(),
            ImmediateUiDispatcher.Instance,
            picker,
            close,
            dominatorService);

    [Fact]
    public async Task PickingBothSnapshotsComparesAndPopulatesTheTable()
    {
        var picker = new StubDumpFilePicker("before.dmp", "after.dmp");
        var loader = new StubComparisonLoader(
            ("before.dmp", BeforeSnapshot()),
            ("after.dmp", AfterSnapshot()));
        await using var viewModel = CreateViewModel(loader, picker);

        await viewModel.PickBeforeAsync();

        Assert.Equal("before.dmp", viewModel.BeforePath);
        Assert.True(viewModel.HasBefore);
        Assert.False(viewModel.HasAfter);
        Assert.False(viewModel.CompareCommand.CanExecute(null));
        Assert.True(viewModel.ShowChoosePrompt);

        await viewModel.PickAfterAsync();

        Assert.True(viewModel.HasAfter);
        Assert.True(viewModel.HasCompared);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.ShowTable);
        Assert.Equal(["before.dmp", "after.dmp"], loader.LoadedPaths);

        // Biggest growth first: System.Byte[] grew by 20 MB, MyApp.Cache is
        // new (+5 MB), System.String is unchanged.
        Assert.Collection(
            viewModel.Table.FilteredDeltas,
            row => Assert.Equal("System.Byte[]", row.TypeName),
            row => Assert.Equal("MyApp.Cache", row.TypeName),
            row => Assert.Equal("System.String", row.TypeName));
        var bytes = viewModel.Table.FilteredDeltas[0];
        Assert.Equal("+200", bytes.CountDeltaDisplay);
        Assert.Equal("+128 MB", bytes.SizeDeltaDisplay);
        Assert.Equal("N/A", bytes.RetainedDeltaDisplay);
        Assert.False(viewModel.HasRetainedSizeNote);
    }

    [Fact]
    public async Task PickingWithoutAFileSelectionLeavesTheComparisonIdle()
    {
        var picker = new StubDumpFilePicker((string?)null);
        await using var viewModel = CreateViewModel(
            new StubComparisonLoader(("before.dmp", BeforeSnapshot())),
            picker);

        await viewModel.PickBeforeAsync();

        Assert.False(viewModel.HasBefore);
        Assert.False(viewModel.HasCompared);
        Assert.True(viewModel.ShowChoosePrompt);
        Assert.Equal(1, picker.PickCount);
    }

    [Fact]
    public async Task LoadingStoredPathsComparesAndReportsThePairOnce()
    {
        var completedPairs = new List<(string Before, string After)>();
        var loader = new StubComparisonLoader(
            ("before.dmp", BeforeSnapshot()),
            ("after.dmp", AfterSnapshot()));
        await using var viewModel = new ComparisonViewModel(
            loader,
            new SnapshotComparisonService(),
            ImmediateUiDispatcher.Instance,
            new StubDumpFilePicker(),
            comparisonCompleted: (before, after) =>
            {
                completedPairs.Add((before, after));
                return Task.CompletedTask;
            });

        await viewModel.LoadAsync("before.dmp", "after.dmp");

        Assert.True(viewModel.HasCompared);
        Assert.Collection(
            completedPairs,
            pair =>
            {
                Assert.Equal("before.dmp", pair.Before);
                Assert.Equal("after.dmp", pair.After);
            });
    }

    [Fact]
    public async Task FailedStoredComparisonDoesNotReportThePair()
    {
        var callbackCount = 0;
        var loader = new StubComparisonLoader(
            (_, _) => Task.FromException<HeapSnapshot>(
                new InvalidDataException("Not a managed dump.")));
        await using var viewModel = new ComparisonViewModel(
            loader,
            new SnapshotComparisonService(),
            ImmediateUiDispatcher.Instance,
            new StubDumpFilePicker(),
            comparisonCompleted: (_, _) =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });

        await viewModel.LoadAsync("before.dmp", "after.dmp");

        Assert.True(viewModel.HasError);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task LoadFailureSurfacesAnErrorState()
    {
        var picker = new StubDumpFilePicker("before.dmp", "after.dmp");
        var loader = new StubComparisonLoader(
            (_, _) => Task.FromException<HeapSnapshot>(
                new InvalidDataException("The dump has no CLR runtime.")));
        await using var viewModel = CreateViewModel(loader, picker);

        await viewModel.PickBeforeAsync();
        await viewModel.PickAfterAsync();

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasCompared);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.False(viewModel.ShowChoosePrompt);
        Assert.Equal(ProfilerErrorKind.ClrRuntimeNotFound, viewModel.Error!.Kind);
        Assert.DoesNotContain("The dump has no CLR runtime.", viewModel.ErrorMessage);
        Assert.Contains("The dump has no CLR runtime.", viewModel.Error.TechnicalDetails);
    }

    [Fact]
    public async Task SupersededComparisonNeverPublishes()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new StubDumpFilePicker("first-before.dmp", "first-after.dmp", "second-before.dmp");
        var loader = new StubComparisonLoader(
            (path, cancellationToken) => path == "first-before.dmp"
                ? BlockingLoad(firstStarted, firstRelease, cancellationToken)
                : Task.FromResult(
                    path == "first-after.dmp"
                        ? AfterSnapshot()
                        : Snapshot("second-before.dmp", count: 7, size: 700)));
        await using var viewModel = CreateViewModel(loader, picker);

        await viewModel.PickBeforeAsync();
        await viewModel.PickAfterAsync(); // starts the first (blocked) comparison
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Re-picking the before snapshot supersedes the blocked comparison.
        await viewModel.PickBeforeAsync();

        Assert.True(viewModel.HasCompared);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
        Assert.Collection(
            viewModel.Table.FilteredDeltas,
            row =>
            {
                Assert.Equal("System.Byte[]", row.TypeName);
                Assert.Equal("+293", row.CountDeltaDisplay);
            },
            row => Assert.Equal("System.String", row.TypeName),
            row => Assert.Equal("MyApp.Cache", row.TypeName));

        // Releasing the stale first load must not republish anything.
        firstRelease.SetResult();
        Assert.Equal(3, viewModel.Table.FilteredDeltaCount);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task DominatorsEnrichTheComparisonWithRetainedDeltas()
    {
        var picker = new StubDumpFilePicker("before.dmp", "after.dmp");
        var loader = new StubComparisonLoader(
            ("before.dmp", BeforeSnapshot()),
            ("after.dmp", AfterSnapshot()));
        var dominators = new StubDominatorService(
            snapshot => new DominatorAnalysisResult(
                [],
                snapshot.Info.Path == BeforeInfo.Path
                    ? [new TypeRetainedSize(0x100, "System.Byte[]", 134_217_728)]
                    : [new TypeRetainedSize(0x100, "System.Byte[]", 268_435_456)]));
        await using var viewModel = CreateViewModel(loader, picker, dominators);

        await viewModel.PickBeforeAsync();
        await viewModel.PickAfterAsync();

        Assert.True(viewModel.HasCompared);
        Assert.False(viewModel.HasRetainedSizeNote);
        var bytes = viewModel.Table.FilteredDeltas[0];
        Assert.Equal("+128 MB", bytes.RetainedDeltaDisplay);
        Assert.True(bytes.IsRetainedDeltaAvailable);
    }

    [Fact]
    public async Task DominatorFailureIsNonFatalAndKeepsTheComparison()
    {
        var picker = new StubDumpFilePicker("before.dmp", "after.dmp");
        var loader = new StubComparisonLoader(
            ("before.dmp", BeforeSnapshot()),
            ("after.dmp", AfterSnapshot()));
        var dominators = new StubDominatorService(
            _ => throw new InvalidDataException("The dump was captured while the GC heap was not walkable."));
        await using var viewModel = CreateViewModel(loader, picker, dominators);

        await viewModel.PickBeforeAsync();
        await viewModel.PickAfterAsync();

        Assert.True(viewModel.HasCompared);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.HasRetainedSizeNote);
        Assert.StartsWith("Retained sizes unavailable.", viewModel.RetainedSizeNote);
        Assert.Equal("N/A", viewModel.Table.FilteredDeltas[0].RetainedDeltaDisplay);
    }

    [Fact]
    public async Task CloseCommandInvokesTheProvidedCloseCallback()
    {
        var closeInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = CreateViewModel(
            new StubComparisonLoader(("before.dmp", BeforeSnapshot())),
            new StubDumpFilePicker(),
            close: () =>
            {
                closeInvoked.SetResult();
                return Task.CompletedTask;
            });

        viewModel.CloseCommand.Execute(null);

        Assert.True(closeInvoked.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightComparison()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        var picker = new StubDumpFilePicker("before.dmp", "after.dmp");
        var loader = new StubComparisonLoader(
            async (path, cancellationToken) =>
            {
                if (path != "before.dmp")
                {
                    return AfterSnapshot();
                }

                loadToken = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });
        var viewModel = CreateViewModel(loader, picker);

        await viewModel.PickBeforeAsync();
        await viewModel.PickAfterAsync(); // starts the blocked comparison
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();

        Assert.True(loadToken.IsCancellationRequested);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task LoadAcceptsExternalCancellationAndReportsIt()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new StubComparisonLoader(
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });
        await using var viewModel = CreateViewModel(
            loader,
            new StubDumpFilePicker());
        using var cancellation = new CancellationTokenSource();

        var load = viewModel.LoadAsync("before.dmp", "after.dmp", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await load;

        Assert.True(viewModel.HasError);
        Assert.Equal(ProfilerErrorKind.AnalysisCancelled, viewModel.Error!.Kind);
    }

    private static async Task<HeapSnapshot> BlockingLoad(
        TaskCompletionSource started,
        TaskCompletionSource release,
        CancellationToken cancellationToken)
    {
        started.SetResult();
        await release.Task.WaitAsync(cancellationToken);
        return BeforeSnapshot();
    }

    private static HeapSnapshot Snapshot(string path, long count, ulong size) =>
        new()
        {
            Info = new HeapSnapshotInfo(
                Path.GetFullPath(path),
                "Sample.Process",
                4217,
                "10.0.0",
                new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
                count,
                size),
            Types =
            [
                new HeapTypeInfo(0x100, "System.Byte[]", "System.Private.CoreLib", count, size, null),
            ],
        };

    private sealed class StubComparisonLoader : IHeapSnapshotLoader
    {
        private readonly Dictionary<string, HeapSnapshot> _snapshots = [];
        private readonly Func<string, CancellationToken, Task<HeapSnapshot>>? _load;

        public StubComparisonLoader(params (string Path, HeapSnapshot Snapshot)[] snapshots)
        {
            _snapshots = snapshots.ToDictionary(
                pair => Path.GetFullPath(pair.Path),
                pair => pair.Snapshot,
                StringComparer.Ordinal);
        }

        public StubComparisonLoader(
            Func<string, CancellationToken, Task<HeapSnapshot>> load)
        {
            _load = load;
        }

        public List<string> LoadedPaths { get; } = [];

        public Task<HeapSnapshot> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            LoadedPaths.Add(path);
            if (_load is not null)
            {
                return _load(path, cancellationToken);
            }

            return Task.FromResult(
                _snapshots.TryGetValue(Path.GetFullPath(path), out var snapshot)
                    ? snapshot
                    : throw new KeyNotFoundException($"No stub snapshot for {path}."));
        }
    }

    private sealed class StubDumpFilePicker : IDumpFilePicker
    {
        private readonly Queue<string?> _results;

        public StubDumpFilePicker(params string?[] results) =>
            _results = new Queue<string?>(results);

        public int PickCount { get; private set; }

        public Task<string?> PickAsync()
        {
            PickCount++;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : null);
        }
    }

    private sealed class StubDominatorService(
        Func<HeapSnapshot, DominatorAnalysisResult> compute) : IDominatorTreeService
    {
        public Task<DominatorAnalysisResult> ComputeDominatorsAsync(
            HeapSnapshot snapshot,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(compute(snapshot));
    }
}
