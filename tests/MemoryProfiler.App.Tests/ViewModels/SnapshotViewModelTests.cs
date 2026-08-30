using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class SnapshotViewModelTests
{
    private static readonly HeapSnapshotInfo SampleInfo = new(
        Path.GetFullPath("sample.dmp"),
        "Sample.Process",
        4217,
        "10.0.0",
        new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero),
        12_345,
        4_000_000);

    private static HeapSnapshot Snapshot(params HeapTypeInfo[] types) =>
        new() { Info = SampleInfo, Types = types };

    private static HeapTypeInfo Type(string name, string assembly, long count, ulong size) =>
        new(0x1000, name, assembly, count, size, null);

    [Fact]
    public async Task LoadPublishesSnapshotAndPopulatesTheTypeBrowser()
    {
        var loader = new StubSnapshotLoader(
            Snapshot(
                Type("System.String", "System.Private.CoreLib", 381_235, 44_200_000),
                Type("MyApp.CacheEntry", "MyApp", 50_000, 118_400_000)));
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);

        await viewModel.LoadAsync("sample.dmp");

        Assert.True(viewModel.HasSnapshot);
        Assert.True(viewModel.IsReady);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
        Assert.Equal("Sample.Process (PID 4217)", viewModel.ProcessDescription);
        Assert.Equal("10.0.0", viewModel.RuntimeDisplay);
        Assert.Equal(12_345.ToString("N0", CultureInfo.CurrentCulture), viewModel.ObjectCountDisplay);
        Assert.Equal(FormatBytes(4_000_000), viewModel.HeapSizeDisplay);
        Assert.Equal(Path.GetFullPath("sample.dmp"), viewModel.SourcePath);
        Assert.Equal(2, viewModel.Types.TotalTypeCount);
        Assert.True(viewModel.ShowTable);
        Assert.Equal("sample.dmp", loader.Path);
    }

    [Fact]
    public async Task LoadExposesLoadingStateWhileAnalysisIsPending()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<HeapSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new StubSnapshotLoader(
            async cancellationToken =>
            {
                started.SetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            });
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);

        var load = viewModel.LoadAsync("sample.dmp");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsLoading);
        Assert.True(viewModel.ShowLoading);
        Assert.False(viewModel.HasSnapshot);
        Assert.False(viewModel.IsReady);

        completion.SetResult(
            Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24)));
        await load;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.IsReady);
    }

    [Fact]
    public async Task LoadFailureSurfacesAnErrorState()
    {
        var loader = new StubSnapshotLoader(
            _ => Task.FromException<HeapSnapshot>(
                new InvalidDataException("The dump has no CLR runtime.")));
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);

        await viewModel.LoadAsync("sample.dmp");

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasSnapshot);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.Contains("The dump has no CLR runtime.", viewModel.ErrorMessage);
        Assert.StartsWith("Unable to open the snapshot.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CancellationClearsLoadingWithoutAnError()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new StubSnapshotLoader(
            async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);

        using var cancellation = new CancellationTokenSource();
        var load = viewModel.LoadAsync("sample.dmp", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await load;

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.HasSnapshot);
    }

    [Fact]
    public async Task CloseCommandInvokesTheProvidedCloseCallback()
    {
        var closeInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new StubSnapshotLoader(
            Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24)));
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance,
            close: () =>
            {
                closeInvoked.SetResult();
                return Task.CompletedTask;
            });
        await viewModel.LoadAsync("sample.dmp");

        viewModel.CloseCommand.Execute(null);

        Assert.True(closeInvoked.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        var loader = new StubSnapshotLoader(
            async cancellationToken =>
            {
                loadToken = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });
        var viewModel = new SnapshotViewModel(loader, new StubHeapObjectRepository([]), ImmediateUiDispatcher.Instance);

        var load = viewModel.LoadAsync("sample.dmp");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();
        await load;

        Assert.True(loadToken.IsCancellationRequested);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task SelectingATypeLoadsItsInstances()
    {
        var repository = new StubHeapObjectRepository(
            [new HeapObjectInfo(0x2000, 0x1000, "System.String", 24, "Gen0")]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            repository,
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        var row = viewModel.Types.FilteredTypes.Single(type => type.TypeName == "System.String");
        viewModel.Types.SelectedType = row;

        Assert.Equal(0x1000UL, repository.RequestedMethodTable);
        Assert.True(viewModel.ObjectInstances.HasSelection);
        Assert.True(viewModel.ObjectInstances.ShowTable);
        var instance = Assert.Single(viewModel.ObjectInstances.Instances);
        Assert.Equal("System.String", instance.Instance.TypeName);
        Assert.Equal("Gen0", instance.GenerationDisplay);
    }

    [Fact]
    public async Task ClearingTheTypeSelectionReturnsInstancesToIdle()
    {
        var repository = new StubHeapObjectRepository(
            [new HeapObjectInfo(0x2000, 0x1000, "System.String", 24, "Gen0")]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            repository,
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        Assert.True(viewModel.ObjectInstances.ShowTable);

        viewModel.Types.SelectedType = null;

        Assert.False(viewModel.ObjectInstances.HasSelection);
        Assert.True(viewModel.ObjectInstances.ShowIdle);
        Assert.Empty(viewModel.ObjectInstances.Instances);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightInstancesLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken instancesToken = default;
        var repository = new StubHeapObjectRepository(
            async (_, _, cancellationToken) =>
            {
                instancesToken = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            repository,
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();

        Assert.True(instancesToken.IsCancellationRequested);
        Assert.False(viewModel.ObjectInstances.HasError);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var unitIndex = 0;
        while (amount >= 1024 && unitIndex < units.Length - 1)
        {
            amount /= 1024;
            unitIndex++;
        }

        var format = amount >= 100 || unitIndex == 0 ? "N0" : "N1";
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    private sealed class StubSnapshotLoader : IHeapSnapshotLoader
    {
        private readonly Func<CancellationToken, Task<HeapSnapshot>> _load;
        private readonly HeapSnapshot? _snapshot;

        public StubSnapshotLoader(HeapSnapshot snapshot)
            : this(_ => Task.FromResult(snapshot))
        {
            _snapshot = snapshot;
        }

        public StubSnapshotLoader(Func<CancellationToken, Task<HeapSnapshot>> load) => _load = load;

        public string? Path { get; private set; }

        public Task<HeapSnapshot> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Path = path;
            return _load(cancellationToken);
        }
    }

    private sealed class StubHeapObjectRepository : IHeapObjectRepository
    {
        private readonly Func<
            HeapSnapshot,
            ulong,
            CancellationToken,
            Task<IReadOnlyList<HeapObjectInfo>>> _getInstances;

        public StubHeapObjectRepository(IReadOnlyList<HeapObjectInfo> instances)
            : this((_, _, _) => Task.FromResult(instances))
        {
        }

        public StubHeapObjectRepository(
            Func<HeapSnapshot, ulong, CancellationToken, Task<IReadOnlyList<HeapObjectInfo>>> getInstances)
        {
            _getInstances = getInstances;
        }

        public ulong? RequestedMethodTable { get; private set; }

        public Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
            HeapSnapshot snapshot,
            ulong methodTable,
            CancellationToken cancellationToken = default)
        {
            RequestedMethodTable = methodTable;
            return _getInstances(snapshot, methodTable, cancellationToken);
        }
    }
}
