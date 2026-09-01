using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class GcRootsViewModelTests
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

    private static ObjectReference FieldRef(
        ulong source,
        ulong target,
        string? name,
        string? sourceType = null,
        string? targetType = null) =>
        new(source, target, ReferenceKind.Field, name, sourceType, targetType);

    private static ObjectReference ArrayRef(
        ulong source,
        ulong target,
        string? sourceType = null,
        string? targetType = null) =>
        new(source, target, ReferenceKind.ArrayElement, null, sourceType, targetType);

    [Fact]
    public async Task StartsInTheIdleState()
    {
        await using var viewModel = new GcRootsViewModel(
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        Assert.False(viewModel.HasSelection);
        Assert.True(viewModel.ShowIdle);
        Assert.False(viewModel.ShowLoading);
        Assert.False(viewModel.ShowError);
        Assert.False(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.ObjectTypeName);
        Assert.Equal(string.Empty, viewModel.AddressDisplay);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task ShowFlattensEachPathIntoDepthIndentedRows()
    {
        var service = new StubGcRootService(
        [
            new GcRootInfo(
                0x1000,
                0x4000,
                "Static field",
                "MyApp.Program._cache",
                [
                    FieldRef(0x1000, 0x2000, "_entries",
                        "MyApp.Cache", "System.Collections.Generic.Dictionary"),
                    ArrayRef(0x2000, 0x4000,
                        "System.Collections.Generic.Dictionary", "MyApp.CustomerDto"),
                ]),
        ]);
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.CustomerDto", 0x4000);

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.ShowTable);
        Assert.False(viewModel.ShowLoading);
        Assert.Equal("MyApp.CustomerDto", viewModel.ObjectTypeName);
        Assert.Equal("0x000000004000", viewModel.AddressDisplay);
        Assert.Equal("1 path to root", viewModel.SummaryDisplay);

        Assert.Collection(
            viewModel.Rows,
            root =>
            {
                Assert.Equal(0, root.Depth);
                Assert.True(root.IsRoot);
                Assert.False(root.CanNavigate);
                Assert.Equal("GC Root", root.FieldDisplay);
                Assert.Equal("Static field", root.KindDisplay);
                Assert.Equal("root", root.AddressDisplay);
                Assert.Equal("MyApp.Program._cache", root.TypeNameDisplay);
            },
            head =>
            {
                Assert.Equal(1, head.Depth);
                Assert.False(head.IsRoot);
                Assert.True(head.CanNavigate);
                Assert.Equal("MyApp.Program._cache", head.FieldDisplay);
                Assert.Equal("Static field", head.KindDisplay);
                Assert.Equal("0x000000001000", head.AddressDisplay);
                Assert.Equal("MyApp.Cache", head.TypeNameDisplay);
                Assert.Equal(0x1000UL, head.EndpointAddress);
                Assert.Equal("MyApp.Cache", head.EndpointTypeName);
            },
            dictionary =>
            {
                Assert.Equal(2, dictionary.Depth);
                Assert.Equal("_entries", dictionary.FieldDisplay);
                Assert.Equal("Field", dictionary.KindDisplay);
                Assert.Equal("0x000000002000", dictionary.AddressDisplay);
                Assert.Equal("System.Collections.Generic.Dictionary", dictionary.TypeNameDisplay);
                Assert.False(dictionary.IsTarget);
            },
            target =>
            {
                Assert.Equal(3, target.Depth);
                Assert.Equal("array element", target.FieldDisplay);
                Assert.Equal("Array element", target.KindDisplay);
                Assert.Equal("0x000000004000", target.AddressDisplay);
                Assert.Equal("MyApp.CustomerDto", target.TypeNameDisplay);
                Assert.True(target.IsTarget);
                Assert.Equal(0x4000UL, target.EndpointAddress);
            });
    }

    [Fact]
    public async Task ShowRendersADirectRootWithASynthesizedTargetRow()
    {
        var service = new StubGcRootService(
        [
            new GcRootInfo(0x4000, 0x4000, "Pinned handle", null, Path: null),
        ]);
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0x4000);

        Assert.Equal("1 path to root", viewModel.SummaryDisplay);
        Assert.Collection(
            viewModel.Rows,
            root =>
            {
                Assert.True(root.IsRoot);
                Assert.Equal("GC Root", root.FieldDisplay);
                Assert.Equal("Pinned handle", root.KindDisplay);
                Assert.Equal("MyApp.Widget", root.TypeNameDisplay);
            },
            target =>
            {
                Assert.Equal(1, target.Depth);
                Assert.True(target.IsTarget);
                Assert.True(target.CanNavigate);
                Assert.Equal("Pinned handle", target.FieldDisplay);
                Assert.Equal("0x000000004000", target.AddressDisplay);
                Assert.Equal("MyApp.Widget", target.TypeNameDisplay);
            });
    }

    [Fact]
    public async Task ShowPublishesMultiplePathsAndPluralSummary()
    {
        var service = new StubGcRootService(
        [
            new GcRootInfo(0x1000, 0x3000, "Stack", null, Path: null),
            new GcRootInfo(0x2000, 0x3000, "Static field", "MyApp.Program._cache",
                [
                    FieldRef(0x2000, 0x3000, "_value",
                        "MyApp.Cache", "MyApp.CustomerDto"),
                ]),
        ]);
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.CustomerDto", 0x3000);

        Assert.Equal(
            "2 paths to root",
            viewModel.SummaryDisplay);
        // Each path contributes its root row and at least one object row.
        Assert.Equal(5, viewModel.Rows.Count);
        Assert.Equal(2, viewModel.Rows.Count(row => row.IsRoot));
    }

    [Fact]
    public async Task ShowExposesLoadingStateWhileTheSearchIsPending()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<GcRootInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubGcRootService(
            async cancellationToken =>
            {
                started.SetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            });
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var show = viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0x1000);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsLoading);
        Assert.True(viewModel.ShowLoading);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);

        completion.SetResult(
        [
            new GcRootInfo(0x1000, 0x1000, "Stack", null, Path: null),
        ]);
        await show;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.ShowTable);
        Assert.Equal("1 path to root", viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowFailureSurfacesAnErrorState()
    {
        var service = new StubGcRootService(
            _ => Task.FromException<IReadOnlyList<GcRootInfo>>(
                new InvalidDataException("The dump has no CLR runtime.")));
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0x1000);

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.False(viewModel.ShowTable);
        Assert.Contains("The dump has no CLR runtime.", viewModel.Error!.TechnicalDetails);
        Assert.Equal("CLR runtime not found", viewModel.Error.Title);
    }

    [Fact]
    public async Task ShowReportsAnEmptyStateWhenNoRootKeepsTheObjectAlive()
    {
        var service = new StubGcRootService([]);
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0x1000);

        Assert.True(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task CancellationNeverAppearsAsAnError()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new StubGcRootService(
            token => Task.FromCanceled<IReadOnlyList<GcRootInfo>>(token));
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        cancellation.Cancel();
        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Widget", 0x1000, cancellation.Token);

        Assert.False(viewModel.HasError);
        Assert.False(viewModel.ShowError);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task SupersededShowDiscardsTheStaleLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<GcRootInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new StubGcRootService(
            async cancellationToken =>
            {
                // First load blocks until the test releases it and deliberately
                // ignores cancellation so the stale result can be observed.
                // Subsequent loads return immediately without touching the
                // completion sources.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    return await completion.Task;
                }

                return [];
            });
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var first = viewModel.ShowAsync(Snapshot(), "MyApp.First", 0x1000);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.ShowAsync(Snapshot(), "MyApp.Second", 0x2000);

        completion.SetResult(
        [
            new GcRootInfo(0x1000, 0x1000, "Stack", null, Path: null),
        ]);
        await first;

        Assert.Equal("MyApp.Second", viewModel.ObjectTypeName);
        Assert.Equal(0x2000UL.ToString("X12", CultureInfo.InvariantCulture),
            viewModel.AddressDisplay.Replace("0x", string.Empty));
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
        Assert.False(viewModel.ShowTable);
    }

    [Fact]
    public async Task ClearReturnsToTheIdleState()
    {
        var service = new StubGcRootService(
        [
            new GcRootInfo(0x1000, 0x1000, "Stack", null, Path: null),
        ]);
        var viewModel = new GcRootsViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0x1000);
        Assert.True(viewModel.ShowTable);

        await viewModel.ClearAsync();

        Assert.False(viewModel.HasSelection);
        Assert.True(viewModel.ShowIdle);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task ShowRejectsAZeroObjectAddress()
    {
        await using var viewModel = new GcRootsViewModel(
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => viewModel.ShowAsync(Snapshot(), "MyApp.Widget", 0));
    }

    private sealed class StubGcRootService : IGcRootService
    {
        private IReadOnlyList<GcRootInfo> _roots = [];
        private readonly Func<CancellationToken, Task<IReadOnlyList<GcRootInfo>>>? _handler;

        public StubGcRootService(IReadOnlyList<GcRootInfo> roots)
        {
            _roots = roots;
        }

        public StubGcRootService(
            Func<CancellationToken, Task<IReadOnlyList<GcRootInfo>>> handler)
        {
            _handler = handler;
        }

        public Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            _handler is not null
                ? _handler(cancellationToken)
                : Task.FromResult(_roots);
    }
}
