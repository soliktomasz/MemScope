using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Values;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class ObjectDetailsViewModelTests
{
    private static readonly DominatorInfo CacheDominator =
        new(0x2000, "MyApp.Cache", 64, 430UL * 1024 * 1024, 12_345);

    private static DominatorAnalysisResult Result() =>
        new([CacheDominator], []);

    private static HeapSnapshot Snapshot() =>
        new()
        {
            Info = new HeapSnapshotInfo(
                Path.GetFullPath("sample.dmp"), "Sample", 42, "10.0.0",
                DateTimeOffset.UtcNow, 1, 64),
            Types = [],
        };

    private static HeapFieldValue Primitive(string name, string type, string value) =>
        new(name, type, HeapValueKind.Primitive, value, null, null, false, null, null);

    private static HeapObjectValueResult ObjectResult(params HeapFieldValue[] fields) =>
        new(new HeapObjectInfo(0x2000, 0x1000, "MyApp.Cache", 64, "Gen2"),
            fields, fields.Length, false);

    private static HeapObjectValueResult ResultObj(
        ulong address,
        string typeName,
        string value) =>
        new(
            new HeapObjectInfo(address, 0x1000, typeName, 64, "Gen2"),
            [Primitive("_count", "System.Int32", value)],
            1,
            false);

    [Fact]
    public async Task RetainedMetricsPublishBeforeValuesAndWarningIsAlwaysExposed()
    {
        var service = new BlockingValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var load = viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", Result(), CacheDominator);
        await service.Started;

        Assert.Equal("430 MB", viewModel.RetainedSizeDisplay);
        Assert.True(viewModel.IsLoadingValues);
        Assert.Equal(
            "Dump values may contain credentials, personal data, or other secrets.",
            viewModel.SensitiveValuesWarning);

        service.Complete(ObjectResult(Primitive("_count", "System.Int32", "42")));
        await load;

        Assert.False(viewModel.IsLoadingValues);
        Assert.True(viewModel.ShowTable);
        var row = Assert.Single(viewModel.Fields);
        Assert.Equal("_count", row.Name);
    }

    [Fact]
    public async Task UnavailableFieldCoexistsWithSuccessfulFields()
    {
        var service = new ControllableValueService(
            _ => new HeapObjectValueResult(
                new HeapObjectInfo(0x2000, 0x1000, "MyApp.Cache", 64, "Gen2"),
                [
                    Primitive("_count", "System.Int32", "42"),
                    new HeapFieldValue("_state", "MyApp.State", HeapValueKind.Unavailable,
                        null, null, null, false, null, "Unsupported value type"),
                ],
                2,
                false));
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", null, null);

        Assert.Equal(2, viewModel.Fields.Count);
        Assert.True(viewModel.Fields[0].CanCopyValue);
        Assert.False(viewModel.Fields[1].CanCopyValue);
        Assert.Equal("Unavailable", viewModel.Fields[1].ValueDisplay);
    }

    [Fact]
    public async Task MissingDominatorMetricsDoNotBlockValues()
    {
        var service = new ControllableValueService(
            _ => ObjectResult(Primitive("_count", "System.Int32", "42")));
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache",
            new DominatorAnalysisResult([], []),
            knownDominator: null);

        Assert.False(viewModel.HasRetainedMetrics);
        Assert.Single(viewModel.Fields);
        Assert.True(viewModel.ShowTable);
    }

    [Fact]
    public async Task FailedValuesLeaveMetricsVisibleAndPublishAnError()
    {
        var service = new ControllableValueService(
            _ => throw new InvalidDataException("The dump is corrupt."));
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", Result(), CacheDominator);

        Assert.True(viewModel.HasRetainedMetrics);
        Assert.Equal("430 MB", viewModel.RetainedSizeDisplay);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.DoesNotContain("cache-a", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearAsyncRemovesRowsAndCancelsAnInFlightRead()
    {
        var service = new BlockingValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var load = viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", Result(), CacheDominator);
        await service.Started;

        await viewModel.ClearAsync();
        await load;

        Assert.Empty(viewModel.Fields);
        Assert.False(viewModel.HasSnapshot);
        Assert.False(viewModel.IsLoadingValues);
    }

    [Fact]
    public async Task CurrentLoadCancellationClearsLoadingState()
    {
        var service = new BlockingValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        using var cancellation = new CancellationTokenSource();

        var load = viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", null, null, cancellation.Token);
        await service.Started;

        cancellation.Cancel();
        await load;

        Assert.False(viewModel.IsLoadingValues);
    }

    [Fact]
    public async Task DisposeAsyncClearsRowsAndCancelsWork()
    {
        var service = new BlockingValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);

        var load = viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", Result(), CacheDominator);
        await service.Started;

        await viewModel.DisposeAsync();
        await load;

        Assert.Empty(viewModel.Fields);
    }

    [Fact]
    public async Task NewerNavigationSupersedesAnEarlierLoad()
    {
        var service = new GatedValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        service.Gate(0x1000);
        service.Gate(0x2000);

        var first = viewModel.ShowAsync(
            Snapshot(), 0x1000, "MyApp.First", null, null);
        var second = viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Second", null, null);

        service.Complete(0x2000, ResultObj(0x2000, "MyApp.Second", "42"));
        await second;

        // The superseded first load completes late and must not overwrite.
        service.Complete(0x1000, ResultObj(0x1000, "MyApp.First", "7"));
        await first;

        Assert.Equal("MyApp.Second", viewModel.TypeName);
        Assert.Equal("42", viewModel.Fields[0].ValueDisplay);
    }

    [Fact]
    public async Task LoadNextArrayPageRequestsAscendingOffsets()
    {
        var offsets = new List<int>();
        var service = new ControllableValueService(options =>
        {
            offsets.Add(options.ArrayOffset);
            var count = Math.Min(500, 1500 - options.ArrayOffset);
            return new HeapObjectValueResult(
                new HeapObjectInfo(0x3000, 0x1000, "System.Int32[]", 64, "Gen0"),
                Enumerable.Range(options.ArrayOffset, count).Select(index =>
                    new HeapFieldValue($"[{index}]", "System.Int32",
                        HeapValueKind.ArrayElement, index.ToString(),
                        null, null, false, null, null)).ToArray(),
                1500,
                options.ArrayOffset + count < 1500);
        });
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), 0x3000, "System.Int32[]", null, null);
        Assert.Equal(500, viewModel.Fields.Count);
        Assert.True(viewModel.HasMoreElements);

        await ((AsyncCommand)viewModel.LoadNextArrayPageCommand).ExecuteAsync();
        Assert.Equal(1_000, viewModel.Fields.Count);
        Assert.True(viewModel.HasMoreElements);

        await ((AsyncCommand)viewModel.LoadNextArrayPageCommand).ExecuteAsync();
        Assert.Equal(1_500, viewModel.Fields.Count);
        Assert.False(viewModel.HasMoreElements);

        Assert.Equal([0, 500, 1_000], offsets);
    }

    [Fact]
    public async Task ArrayPageLoadUsesCancellationAndPublishesLoadingState()
    {
        var service = new CancellablePageValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), 0x3000, "System.Int32[]", null, null);
        var page = ((AsyncCommand)viewModel.LoadNextArrayPageCommand).ExecuteAsync();
        await service.PageStarted;

        Assert.True(viewModel.IsLoadingValues);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.CancelCommand.Execute(null);
        await page;

        Assert.True(service.PageToken.IsCancellationRequested);
        Assert.False(viewModel.IsLoadingValues);
    }

    [Fact]
    public async Task ShowMoreStringsReplacesTruncatedRows()
    {
        var service = new ControllableValueService(options =>
            options.StringLimit == 1_048_576
                ? ObjectResult(new HeapFieldValue("_name", "System.String",
                    HeapValueKind.String, new string('x', 5000), null, null, false, 5000, null))
                : ObjectResult(new HeapFieldValue("_name", "System.String",
                    HeapValueKind.String, new string('x', 4096), null, null, true, 5000, null)));
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), 0x2000, "MyApp.Cache", null, null);
        Assert.True(viewModel.Fields[0].IsTruncated);
        Assert.True(viewModel.CanShowMoreStrings);

        await ((AsyncCommand)viewModel.ShowMoreStringsCommand).ExecuteAsync();
        Assert.Single(viewModel.Fields);
        Assert.Equal(5000, viewModel.Fields[0].ValueDisplay!.Length);
        Assert.False(viewModel.Fields[0].IsTruncated);
    }

    [Fact]
    public async Task StringExpansionUsesCancellationAndPublishesLoadingState()
    {
        var service = new CancellableStringValueService();
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), 0x2000, "MyApp.Cache", null, null);
        var expansion = ((AsyncCommand)viewModel.ShowMoreStringsCommand).ExecuteAsync();
        await service.ExpansionStarted;

        Assert.True(viewModel.IsLoadingValues);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.CancelCommand.Execute(null);
        var completed = await Task.WhenAny(expansion, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(expansion, completed);
        Assert.True(service.ExpansionToken.IsCancellationRequested);
        Assert.False(viewModel.IsLoadingValues);
    }

    [Fact]
    public async Task SensitiveValuesAreReleasedWhenThePaneClears()
    {
        var service = new ControllableValueService(_ => ObjectResult(
            new HeapFieldValue("_key", "System.String", HeapValueKind.String,
                new string('s', 256 * 1024), null, null, false, 256 * 1024, null)));
        var viewModel = new ObjectDetailsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), 0x2000, "MyApp.Cache", null, null);
        var reference = CaptureRowReference(viewModel);

        // Navigate to another object, then clear, so the first value has no
        // surviving strong reference.
        await viewModel.ShowAsync(Snapshot(), 0x3000, "MyApp.Other", null, null);
        await viewModel.ClearAsync();
        ForceFullCollection();

        Assert.False(reference.IsAlive);
        Assert.DoesNotContain("secret-sentinel", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CaptureRowReference(ObjectDetailsViewModel viewModel) =>
        new(viewModel.Fields[0]);

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class BlockingValueService : IHeapObjectValueService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HeapObjectValueResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Complete(HeapObjectValueResult result) => _completion.TrySetResult(result);

        public async Task<HeapObjectValueResult> ReadAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ControllableValueService : IHeapObjectValueService
    {
        private readonly Func<ObjectValueReadOptions, HeapObjectValueResult> _resolver;

        public ControllableValueService(Func<ObjectValueReadOptions, HeapObjectValueResult> resolver)
        {
            _resolver = resolver;
        }

        public Task<HeapObjectValueResult> ReadAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(_resolver(options));
            }
            catch (Exception exception)
            {
                return Task.FromException<HeapObjectValueResult>(exception);
            }
        }
    }

    private sealed class GatedValueService : IHeapObjectValueService
    {
        private readonly Dictionary<ulong, TaskCompletionSource<HeapObjectValueResult>> _gates =
            new();

        public void Gate(ulong address) =>
            _gates[address] = new TaskCompletionSource<HeapObjectValueResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(ulong address, HeapObjectValueResult result) =>
            _gates[address].TrySetResult(result);

        public Task<HeapObjectValueResult> ReadAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken = default) =>
            _gates[objectAddress].Task;
    }

    private sealed class CancellablePageValueService : IHeapObjectValueService
    {
        private readonly TaskCompletionSource _pageStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PageStarted => _pageStarted.Task;
        public CancellationToken PageToken { get; private set; }

        public async Task<HeapObjectValueResult> ReadAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options.ArrayOffset > 0)
            {
                PageToken = cancellationToken;
                _pageStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HeapObjectValueResult(
                new HeapObjectInfo(objectAddress, 0x1000, "System.Int32[]", 64, "Gen0"),
                [Primitive("[0]", "System.Int32", "0")], 2, options.ArrayOffset == 0);
        }
    }

    private sealed class CancellableStringValueService : IHeapObjectValueService
    {
        private readonly TaskCompletionSource _expansionStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExpansionStarted => _expansionStarted.Task;
        public CancellationToken ExpansionToken { get; private set; }

        public async Task<HeapObjectValueResult> ReadAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            ObjectValueReadOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options.StringLimit > 4096)
            {
                ExpansionToken = cancellationToken;
                _expansionStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }

            return ObjectResult(new HeapFieldValue(
                "_name",
                "System.String",
                HeapValueKind.String,
                new string('x', 4096),
                null,
                null,
                true,
                5000,
                null));
        }
    }
}
