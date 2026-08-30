using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class ObjectReferencesViewModelTests
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

    private static ObjectReference RootRef(ReferenceKind kind, ulong target) =>
        new(0, target, kind, null, null, "MyApp.Widget");

    [Fact]
    public async Task StartsInTheIdleState()
    {
        await using var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService([], []),
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
        Assert.Empty(viewModel.References);
    }

    [Fact]
    public async Task ShowOutgoingPublishesRowsHeaderAndSummary()
    {
        var service = new StubObjectReferenceService(
            [
                FieldRef(0x1000, 0x3000, "_second", "MyApp.Container", "MyApp.Second"),
                FieldRef(0x1000, 0x2000, "_first", "MyApp.Container", "MyApp.First"),
            ],
            []);
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.ShowTable);
        Assert.False(viewModel.ShowLoading);
        Assert.Equal("MyApp.Container", viewModel.ObjectTypeName);
        Assert.Equal("0x000000001000", viewModel.AddressDisplay);
        Assert.Equal(ReferenceDirection.Outgoing, viewModel.Direction);
        Assert.True(viewModel.IsOutgoing);
        Assert.False(viewModel.IsIncoming);
        Assert.Equal(2, viewModel.References.Count);
        Assert.Equal("0x000000003000", viewModel.References[0].AddressDisplay);
        Assert.Equal("MyApp.Second", viewModel.References[0].TypeNameDisplay);
        Assert.Equal(0x1000UL, service.OutgoingCalls.Single());
        Assert.Empty(service.IncomingCalls);
        Assert.Equal(
            $"{2.ToString("N0", CultureInfo.CurrentCulture)} outgoing references",
            viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowIncomingPublishesRowsAndDirection()
    {
        var service = new StubObjectReferenceService(
            [],
            [
                FieldRef(0x2000, 0x1000, "_owner", "MyApp.Owner", "MyApp.Widget"),
                RootRef(ReferenceKind.Handle, 0x1000),
            ]);
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Widget", 0x1000, ReferenceDirection.Incoming);

        Assert.True(viewModel.IsIncoming);
        Assert.Equal("Incoming", viewModel.DirectionLabel);
        Assert.Equal(2, viewModel.References.Count);
        Assert.True(viewModel.References[0].CanNavigate);
        Assert.True(viewModel.References[1].IsRoot);
        Assert.Equal(0x1000UL, service.IncomingCalls.Single());
        Assert.Empty(service.OutgoingCalls);
        Assert.Equal(
            $"{2.ToString("N0", CultureInfo.CurrentCulture)} incoming references",
            viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowExposesLoadingWhileReferencesArePending()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<ObjectReference>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubObjectReferenceService(
            async (_, cancellationToken) =>
            {
                started.SetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            },
            (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([]));
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var show = viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsLoading);
        Assert.True(viewModel.ShowLoading);
        Assert.False(viewModel.ShowTable);
        Assert.Equal("MyApp.Container", viewModel.ObjectTypeName);

        completion.SetResult([FieldRef(0x1000, 0x2000, "_child")]);
        await show;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.ShowTable);
        Assert.Single(viewModel.References);
    }

    [Fact]
    public async Task ShowFailureSurfacesAnErrorState()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService(
                (_, _) => Task.FromException<IReadOnlyList<ObjectReference>>(
                    new InvalidDataException("The dump is corrupt.")),
                (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([])),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.False(viewModel.ShowTable);
        Assert.Contains("The dump is corrupt.", viewModel.ErrorMessage);
        Assert.StartsWith("Unable to load references.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ShowWithNoReferencesExposesTheEmptyState()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService([], []),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.ShowEmpty);
        Assert.False(viewModel.ShowTable);
        Assert.Equal(string.Empty, viewModel.SummaryDisplay);
    }

    [Fact]
    public async Task ShowSwapsTheReferencesCollectionInBatchedNotifications()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService(
                [
                    FieldRef(0x1000, 0x2000, "_first"),
                    FieldRef(0x1000, 0x3000, "_second"),
                    FieldRef(0x1000, 0x4000, "_third"),
                ],
                []),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;
        var initialCollection = viewModel.References;
        var referencesNotifications = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ObjectReferencesViewModel.References))
            {
                referencesNotifications++;
            }
        };

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);

        // One clear while loading plus one swap when rows arrive, never one
        // notification per row.
        Assert.Equal(2, referencesNotifications);
        Assert.NotSame(initialCollection, viewModel.References);
        Assert.Equal(3, viewModel.References.Count);
    }

    [Fact]
    public async Task NewerNavigationSupersedesAnEarlierLoad()
    {
        var gates = new Dictionary<ulong, TaskCompletionSource<IReadOnlyList<ObjectReference>>>();
        var service = new StubObjectReferenceService(
            (address, _) =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<ObjectReference>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                gates[address] = completion;
                return completion.Task;
            },
            (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([]));
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var first = viewModel.ShowAsync(
            Snapshot(), "MyApp.First", 0x1000, ReferenceDirection.Outgoing);
        var second = viewModel.ShowAsync(
            Snapshot(), "MyApp.Second", 0x2000, ReferenceDirection.Outgoing);

        gates[0x2000].SetResult([FieldRef(0x2000, 0x3000, "_child")]);
        await second;

        // The superseded load completes late and must not overwrite the newer rows.
        gates[0x1000].SetResult([FieldRef(0x1000, 0x5000, "_child")]);
        await first;

        Assert.Equal("MyApp.Second", viewModel.ObjectTypeName);
        var row = Assert.Single(viewModel.References);
        Assert.Equal(0x2000UL, row.Reference.SourceAddress);
    }

    [Fact]
    public async Task SupersededLoadCancellationDoesNotSurfaceAnError()
    {
        var gates = new Dictionary<ulong, TaskCompletionSource<IReadOnlyList<ObjectReference>>>();
        var service = new StubObjectReferenceService(
            (address, cancellationToken) =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<ObjectReference>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                gates[address] = completion;
                cancellationToken.Register(
                    () => completion.TrySetException(
                        new OperationCanceledException(cancellationToken)));
                return completion.Task;
            },
            (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([]));
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var first = viewModel.ShowAsync(
            Snapshot(), "MyApp.First", 0x1000, ReferenceDirection.Outgoing);
        var second = viewModel.ShowAsync(
            Snapshot(), "MyApp.Second", 0x2000, ReferenceDirection.Outgoing);
        gates[0x2000].SetResult([FieldRef(0x2000, 0x3000, "_child")]);
        await second;
        await first;

        Assert.Equal("MyApp.Second", viewModel.ObjectTypeName);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.ShowError);
        Assert.True(viewModel.ShowTable);
    }

    [Fact]
    public async Task ClearReturnsToIdleAndCancelsAnInFlightLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recordedTokens = new List<CancellationToken>();
        var service = new StubObjectReferenceService(
            async (_, cancellationToken) =>
            {
                recordedTokens.Add(cancellationToken);
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            },
            (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([]));
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        var show = viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.ClearAsync();
        await show;

        Assert.True(recordedTokens.Single().IsCancellationRequested);
        Assert.False(viewModel.HasSelection);
        Assert.True(viewModel.ShowIdle);
        Assert.False(viewModel.HasError);
        Assert.Empty(viewModel.References);
        Assert.Equal(string.Empty, viewModel.ObjectTypeName);
    }

    [Fact]
    public async Task DirectionCommandsReloadTheSameObjectInTheOtherDirection()
    {
        var service = new StubObjectReferenceService(
            [FieldRef(0x1000, 0x2000, "_child", "MyApp.Container", "MyApp.Widget")],
            [FieldRef(0x2000, 0x1000, "_owner", "MyApp.Owner", "MyApp.Widget")]);
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await viewModel.ShowAsync(
            Snapshot(), "MyApp.Widget", 0x1000, ReferenceDirection.Outgoing);
        Assert.True(viewModel.IsOutgoing);

        await ((AsyncCommand)viewModel.ShowOutgoingCommand).ExecuteAsync();
        Assert.Equal(2, service.OutgoingCalls.Count);
        Assert.Equal(0x1000UL, service.OutgoingCalls[1]);

        await ((AsyncCommand)viewModel.ShowIncomingCommand).ExecuteAsync();
        Assert.True(viewModel.IsIncoming);
        Assert.Equal("MyApp.Widget", viewModel.ObjectTypeName);
        Assert.Equal("0x000000001000", viewModel.AddressDisplay);
        Assert.Equal(0x1000UL, service.IncomingCalls.Single());
    }

    [Fact]
    public async Task DirectionCommandsAreDisabledWithoutASelection()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService([], []),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        Assert.False(viewModel.ShowOutgoingCommand.CanExecute(null));
        Assert.False(viewModel.ShowIncomingCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightLoad()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        var service = new StubObjectReferenceService(
            async (_, cancellationToken) =>
            {
                loadToken = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            },
            (_, _) => Task.FromResult<IReadOnlyList<ObjectReference>>([]));
        var viewModel = new ObjectReferencesViewModel(service, ImmediateUiDispatcher.Instance);

        var show = viewModel.ShowAsync(
            Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();
        await show;

        Assert.True(loadToken.IsCancellationRequested);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ShowAfterDisposeThrows()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService([], []),
            ImmediateUiDispatcher.Instance);
        await viewModel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => viewModel.ShowAsync(
                Snapshot(), "MyApp.Container", 0x1000, ReferenceDirection.Outgoing));
    }

    [Fact]
    public async Task ShowRejectsAZeroObjectAddress()
    {
        var viewModel = new ObjectReferencesViewModel(
            new StubObjectReferenceService([], []),
            ImmediateUiDispatcher.Instance);
        await using var _ = viewModel;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => viewModel.ShowAsync(
                Snapshot(), "MyApp.Container", 0, ReferenceDirection.Outgoing));
    }

    private sealed class StubObjectReferenceService : IObjectReferenceService
    {
        private readonly Func<ulong, CancellationToken, Task<IReadOnlyList<ObjectReference>>> _outgoing;
        private readonly Func<ulong, CancellationToken, Task<IReadOnlyList<ObjectReference>>> _incoming;

        public StubObjectReferenceService(
            IReadOnlyList<ObjectReference> outgoing,
            IReadOnlyList<ObjectReference> incoming)
            : this(
                (_, _) => Task.FromResult(outgoing),
                (_, _) => Task.FromResult(incoming))
        {
        }

        public StubObjectReferenceService(
            Func<ulong, CancellationToken, Task<IReadOnlyList<ObjectReference>>> outgoing,
            Func<ulong, CancellationToken, Task<IReadOnlyList<ObjectReference>>> incoming)
        {
            _outgoing = outgoing;
            _incoming = incoming;
        }

        public List<ulong> OutgoingCalls { get; } = [];

        public List<ulong> IncomingCalls { get; } = [];

        public Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default)
        {
            OutgoingCalls.Add(objectAddress);
            return _outgoing(objectAddress, cancellationToken);
        }

        public Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default)
        {
            IncomingCalls.Add(objectAddress);
            return _incoming(objectAddress, cancellationToken);
        }
    }
}
