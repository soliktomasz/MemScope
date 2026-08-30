using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class ObjectInstancesViewModelTests
{
    private static readonly HeapSnapshotInfo SampleInfo = new(
        Path.GetFullPath("sample.dmp"),
        "Sample.Process",
        4217,
        "10.0.0",
        new DateTimeOffset(2026, 8, 28, 16, 25, 0, TimeSpan.Zero),
        12_345,
        4_000_000);

    private static HeapSnapshot Snapshot() =>
        new() { Info = SampleInfo, Types = [] };

    private static HeapTypeInfo Type(ulong methodTable, string name) =>
        new(methodTable, name, "MyApp", 2, 160, null);

    private static HeapObjectInfo Instance(ulong address, string typeName, ulong size, string generation) =>
        new(address, 0x1000, typeName, size, generation);

    [Fact]
    public async Task StartsInTheIdleState()
    {
        await using var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);

        Assert.False(viewModel.HasSelection);
        Assert.True(viewModel.ShowIdle);
        Assert.False(viewModel.ShowLoading);
        Assert.False(viewModel.ShowError);
        Assert.False(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.TypeName);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
        Assert.Empty(viewModel.Instances);
    }

    [Fact]
    public async Task ShowPublishesRowsTypeNameAndSummary()
    {
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(
            [
                Instance(0x1000, "MyApp.Widget", 64, "Gen0"),
                Instance(0x2000, "MyApp.Widget", 96, "Gen2"),
            ]),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.ShowTable);
        Assert.False(viewModel.ShowLoading);
        Assert.False(viewModel.ShowIdle);
        Assert.Equal("MyApp.Widget", viewModel.TypeName);
        Assert.Equal(2, viewModel.Instances.Count);
        Assert.Equal("0x000000001000", viewModel.Instances[0].AddressDisplay);
        Assert.Equal("Gen2", viewModel.Instances[1].GenerationDisplay);
        Assert.Equal(
            $"{2.ToString("N0", CultureInfo.CurrentCulture)} instances · {FormatBytes(160)}",
            viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowExposesLoadingWhileInstancesArePending()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<HeapObjectInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(async (_, _, cancellationToken) =>
            {
                started.SetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            }),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var show = viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsLoading);
        Assert.True(viewModel.ShowLoading);
        Assert.False(viewModel.ShowTable);
        Assert.Equal("MyApp.Widget", viewModel.TypeName);

        completion.SetResult([Instance(0x1000, "MyApp.Widget", 64, "Gen0")]);
        await show;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.ShowTable);
    }

    [Fact]
    public async Task ShowFailureSurfacesAnErrorState()
    {
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(
                _ => Task.FromException<IReadOnlyList<HeapObjectInfo>>(
                    new InvalidDataException("The dump is corrupt."))),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.False(viewModel.ShowTable);
        Assert.Contains("The dump is corrupt.", viewModel.ErrorMessage);
        Assert.StartsWith("Unable to load instances.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ShowWithNoInstancesExposesTheEmptyState()
    {
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowSwapsTheInstancesCollectionInOneNotification()
    {
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(
            [
                Instance(0x1000, "MyApp.Widget", 64, "Gen0"),
                Instance(0x2000, "MyApp.Widget", 96, "Gen2"),
                Instance(0x3000, "MyApp.Widget", 128, "Gen1"),
            ]),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        var initialCollection = viewModel.Instances;
        var instancesNotifications = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ObjectInstancesViewModel.Instances))
            {
                instancesNotifications++;
            }
        };

        await viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));

        // The rows arrive as one collection swap, never one notification per row.
        Assert.Equal(1, instancesNotifications);
        Assert.NotSame(initialCollection, viewModel.Instances);
        Assert.Equal(3, viewModel.Instances.Count);
    }

    [Fact]
    public async Task ClearReturnsToIdleAndCancelsAnInFlightLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recordedTokens = new List<CancellationToken>();
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(async (_, _, cancellationToken) =>
            {
                recordedTokens.Add(cancellationToken);
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var show = viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.ClearAsync();
        await show;

        Assert.True(recordedTokens.Single().IsCancellationRequested);
        Assert.False(viewModel.HasSelection);
        Assert.True(viewModel.ShowIdle);
        Assert.False(viewModel.HasError);
        Assert.Empty(viewModel.Instances);
        Assert.Equal(string.Empty, viewModel.TypeName);
    }

    [Fact]
    public async Task NewerSelectionSupersedesAnEarlierLoad()
    {
        var gates = new Dictionary<ulong, TaskCompletionSource<IReadOnlyList<HeapObjectInfo>>>();
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository((_, methodTable, _) =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<HeapObjectInfo>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                gates[methodTable] = completion;
                return completion.Task;
            }),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var first = viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.First"));
        var second = viewModel.ShowAsync(Snapshot(), Type(0x2000, "MyApp.Second"));

        gates[0x2000].SetResult([Instance(0x2000, "MyApp.Second", 32, "Gen1")]);
        await second;

        // The superseded load completes late and must not overwrite the newer rows.
        gates[0x1000].SetResult([Instance(0x1000, "MyApp.First", 64, "Gen0")]);
        await first;

        Assert.Equal("MyApp.Second", viewModel.TypeName);
        var row = Assert.Single(viewModel.Instances);
        Assert.Equal(0x2000UL, row.Address);
        Assert.Equal("MyApp.Second", row.Instance.TypeName);
    }

    [Fact]
    public async Task SupersededLoadCancellationDoesNotSurfaceAnError()
    {
        var gates = new Dictionary<ulong, TaskCompletionSource<IReadOnlyList<HeapObjectInfo>>>();
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository((_, methodTable, cancellationToken) =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<HeapObjectInfo>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                gates[methodTable] = completion;
                cancellationToken.Register(
                    () => completion.TrySetException(
                        new OperationCanceledException(cancellationToken)));
                return completion.Task;
            }),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var first = viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.First"));
        var second = viewModel.ShowAsync(Snapshot(), Type(0x2000, "MyApp.Second"));
        gates[0x2000].SetResult([Instance(0x2000, "MyApp.Second", 32, "Gen1")]);
        await second;
        await first;

        Assert.Equal("MyApp.Second", viewModel.TypeName);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.ShowError);
        Assert.True(viewModel.ShowTable);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository(async (_, _, cancellationToken) =>
            {
                loadToken = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }),
            ImmediateUiDispatcher.Instance);

        var show = viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();
        await show;

        Assert.True(loadToken.IsCancellationRequested);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ShowAfterDisposeThrows()
    {
        var viewModel = new ObjectInstancesViewModel(
            new StubHeapObjectRepository([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => viewModel.ShowAsync(Snapshot(), Type(0x1000, "MyApp.Widget")));
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

        public StubHeapObjectRepository(
            Func<CancellationToken, Task<IReadOnlyList<HeapObjectInfo>>> getInstances)
            : this((_, _, cancellationToken) => getInstances(cancellationToken))
        {
        }

        public Task<IReadOnlyList<HeapObjectInfo>> GetInstancesAsync(
            HeapSnapshot snapshot,
            ulong methodTable,
            CancellationToken cancellationToken = default) =>
            _getInstances(snapshot, methodTable, cancellationToken);
    }
}
