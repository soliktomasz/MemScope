using System.Globalization;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.Models;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.App.ViewModels.Types;
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
    public async Task CopyCommandsWriteTheSelectedInvestigationValue()
    {
        var clipboard = new RecordingClipboardService();
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(Snapshot()),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance,
            clipboardService: clipboard);
        var type = new TypeRowViewModel(Type("Example.Widget", "Example", 1, 24));
        var instance = new HeapObjectRowViewModel(
            new HeapObjectInfo(0x1234, 0x1000, "Example.Widget", 24, "Gen0"));
        var root = new GcRootRowViewModel(
            0, true, false, "GC Root", "Static field", "root", "Example.Root",
            0, string.Empty, false, "complete root path");

        await ((AsyncCommand<TypeRowViewModel>)viewModel.CopyTypeNameCommand)
            .ExecuteAsync(type);
        Assert.Equal("Example.Widget", clipboard.Text);
        await ((AsyncCommand<object>)viewModel.CopyObjectAddressCommand)
            .ExecuteAsync(instance);
        Assert.Equal("0x000000001234", clipboard.Text);
        await ((AsyncCommand<GcRootRowViewModel>)viewModel.CopyGcRootPathCommand)
            .ExecuteAsync(root);
        Assert.Equal("complete root path", clipboard.Text);
    }

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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
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
    public async Task CancelLoadCommandStopsLoadingWithoutReportingAnError()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new StubSnapshotLoader(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return Snapshot();
        });
        await using var viewModel = new SnapshotViewModel(
            loader,
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        var load = viewModel.LoadAsync("sample.dmp");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.CancelLoadCommand.CanExecute(null));

        viewModel.CancelLoadCommand.Execute(null);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await load;

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        await viewModel.LoadAsync("sample.dmp");

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasSnapshot);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.ShowError);
        Assert.Equal(ProfilerErrorKind.ClrRuntimeNotFound, viewModel.Error!.Kind);
        Assert.DoesNotContain("The dump has no CLR runtime.", viewModel.ErrorMessage);
        Assert.Contains("The dump has no CLR runtime.", viewModel.Error.TechnicalDetails);
    }

    [Fact]
    public async Task RequestedCancellationReportsAnalysisCancelled()
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        using var cancellation = new CancellationTokenSource();
        var load = viewModel.LoadAsync("sample.dmp", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await load;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.Equal(ProfilerErrorKind.AnalysisCancelled, viewModel.Error!.Kind);
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
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
        var viewModel = new SnapshotViewModel(loader, new StubHeapObjectRepository([]), new StubObjectReferenceService([]), new StubGcRootService([]), ImmediateUiDispatcher.Instance);

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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
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
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();

        Assert.True(instancesToken.IsCancellationRequested);
        Assert.False(viewModel.ObjectInstances.HasError);
    }

    [Fact]
    public async Task ShowOutgoingReferencesCommandRoutesAnInstanceRowToTheReferencesPane()
    {
        var referenceService = new StubObjectReferenceService(
            [new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_child",
                "System.String", "MyApp.Widget")],
            []);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository(
                [new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        var instanceRow = Assert.Single(viewModel.ObjectInstances.Instances);

        viewModel.ShowOutgoingReferencesCommand.Execute(instanceRow);

        Assert.True(viewModel.ObjectReferences.HasSelection);
        Assert.True(viewModel.ObjectReferences.ShowTable);
        Assert.Equal("System.String", viewModel.ObjectReferences.ObjectTypeName);
        Assert.Equal("0x000000001000", viewModel.ObjectReferences.AddressDisplay);
        Assert.Equal(ReferenceDirection.Outgoing, viewModel.ObjectReferences.Direction);
        var row = Assert.Single(viewModel.ObjectReferences.References);
        Assert.Equal("MyApp.Widget", row.TypeNameDisplay);
    }

    [Fact]
    public async Task ShowIncomingReferencesCommandRoutesAnInstanceRowToTheReferencesPane()
    {
        var referenceService = new StubObjectReferenceService(
            [],
            [new ObjectReference(0x2000, 0x1000, ReferenceKind.Field, "_owner",
                "MyApp.Owner", "System.String")]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository(
                [new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        var instanceRow = Assert.Single(viewModel.ObjectInstances.Instances);

        viewModel.ShowIncomingReferencesCommand.Execute(instanceRow);

        Assert.True(viewModel.ObjectReferences.IsIncoming);
        Assert.Equal(ReferenceDirection.Incoming, viewModel.ObjectReferences.Direction);
        var row = Assert.Single(viewModel.ObjectReferences.References);
        Assert.Equal("MyApp.Owner", row.TypeNameDisplay);
    }

    [Fact]
    public async Task ShowPathToRootCommandRoutesAnInstanceRowToTheGcRootsPane()
    {
        var gcRootService = new StubGcRootService(
        [
            new GcRootInfo(0x1000, 0x1000, "Static field", "MyApp.Program._cache", Path: null),
        ]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository(
                [new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")]),
            new StubObjectReferenceService([]),
            gcRootService,
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        var instanceRow = Assert.Single(viewModel.ObjectInstances.Instances);

        Assert.True(viewModel.ShowPathToRootCommand.CanExecute(instanceRow));
        viewModel.ShowPathToRootCommand.Execute(instanceRow);

        Assert.True(viewModel.GcRoots.HasSelection);
        Assert.True(viewModel.GcRoots.ShowTable);
        Assert.Equal("System.String", viewModel.GcRoots.ObjectTypeName);
        Assert.Equal("0x000000001000", viewModel.GcRoots.AddressDisplay);
        Assert.Equal("1 path to root", viewModel.GcRoots.SummaryDisplay);
        Assert.Equal(2, viewModel.GcRoots.Rows.Count);
    }

    [Fact]
    public async Task ShowPathToRootCommandRoutesAPathRowEndpointBackToTheReferencesPane()
    {
        var referenceService = new StubObjectReferenceService(
            [new ObjectReference(0x3000, 0x4000, ReferenceKind.Field, "_child",
                "MyApp.Owner", "MyApp.Widget")],
            []);
        var gcRootService = new StubGcRootService(
        [
            new GcRootInfo(0x2000, 0x1000, "Static field", "MyApp.Program._cache",
            [
                new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_owner",
                    "MyApp.Cache", "MyApp.Owner"),
                new ObjectReference(0x3000, 0x1000, ReferenceKind.Field, "_target",
                    "MyApp.Owner", "System.String"),
            ]),
        ]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository(
                [new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")]),
            referenceService,
            gcRootService,
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        var instanceRow = Assert.Single(viewModel.ObjectInstances.Instances);

        viewModel.ShowPathToRootCommand.Execute(instanceRow);
        // The hop row for the intermediate object, not the inspected target.
        var ownerRow = Assert.Single(
            viewModel.GcRoots.Rows, row => row.EndpointAddress == 0x3000);
        Assert.Equal("MyApp.Owner", ownerRow.EndpointTypeName);

        // Inspecting a node in the path routes back through the references pane.
        viewModel.ShowOutgoingReferencesCommand.Execute(ownerRow);

        Assert.Equal("MyApp.Owner", viewModel.ObjectReferences.ObjectTypeName);
        Assert.Equal("0x000000003000", viewModel.ObjectReferences.AddressDisplay);
        Assert.Equal(ReferenceDirection.Outgoing, viewModel.ObjectReferences.Direction);
    }

    [Fact]
    public async Task ShowPathToRootCommandIsDisabledWithoutASnapshot()
    {
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        Assert.False(viewModel.ShowPathToRootCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReferenceRowNavigationDrillsIntoTheObjectAtTheOtherEnd()
    {
        var referenceService = new StubObjectReferenceService(
            [new ObjectReference(0x2000, 0x3000, ReferenceKind.Field, "_child",
                "MyApp.First", "MyApp.Second")],
            []);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        viewModel.ShowOutgoingReferencesCommand.Execute(
            new HeapObjectRowViewModel(
                new HeapObjectInfo(0x2000, 0x1000, "MyApp.First", 32, "Gen0")));
        var referenceRow = Assert.Single(viewModel.ObjectReferences.References);
        Assert.Equal("MyApp.Second", referenceRow.TypeNameDisplay);

        viewModel.ShowOutgoingReferencesCommand.Execute(referenceRow);

        Assert.Equal("MyApp.Second", viewModel.ObjectReferences.ObjectTypeName);
        Assert.Equal("0x000000003000", viewModel.ObjectReferences.AddressDisplay);
    }

    [Fact]
    public async Task BackAndForwardRestoreTheSelectedType()
    {
        var repository = new StubHeapObjectRepository(
            [new HeapObjectInfo(0x2000, 0x1000, "System.String", 24, "Gen0")]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            repository,
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");

        var type = Assert.Single(viewModel.Types.FilteredTypes);
        viewModel.Types.SelectedType = type;

        Assert.True(viewModel.GoBackCommand.CanExecute(null));
        Assert.False(viewModel.GoForwardCommand.CanExecute(null));

        viewModel.GoBackCommand.Execute(null);

        Assert.Null(viewModel.Types.SelectedType);
        Assert.True(viewModel.ObjectInstances.ShowIdle);
        Assert.True(viewModel.GoForwardCommand.CanExecute(null));

        viewModel.GoForwardCommand.Execute(null);

        Assert.Same(type, viewModel.Types.SelectedType);
        Assert.True(viewModel.ObjectInstances.ShowTable);
    }

    [Fact]
    public async Task ForwardRestoresTheSelectedTypeOnTheUiDispatcher()
    {
        var dispatcher = new TrackingUiDispatcher();
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            dispatcher);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = Assert.Single(viewModel.Types.FilteredTypes);
        viewModel.GoBackCommand.Execute(null);
        var selectionChanged = false;
        var selectionChangedOutsideDispatcher = false;
        viewModel.Types.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(viewModel.Types.SelectedType))
            {
                return;
            }

            selectionChanged = true;
            selectionChangedOutsideDispatcher = !dispatcher.IsDispatching;
        };

        viewModel.GoForwardCommand.Execute(null);

        Assert.True(selectionChanged);
        Assert.False(selectionChangedOutsideDispatcher);
    }

    [Fact]
    public async Task BackAndForwardRestoreDeepObjectReferenceNavigation()
    {
        var referenceService = new StubObjectReferenceService(
            [new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_child",
                "MyApp.Owner", "MyApp.Child")],
            []);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("MyApp.Owner", "MyApp", 1, 32))),
            new StubHeapObjectRepository([]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        var owner = new HeapObjectRowViewModel(
            new HeapObjectInfo(0x1000, 0x1000, "MyApp.Owner", 32, "Gen0"));

        viewModel.ShowOutgoingReferencesCommand.Execute(owner);
        var child = Assert.Single(viewModel.ObjectReferences.References);
        viewModel.ShowOutgoingReferencesCommand.Execute(child);
        Assert.Equal("0x000000002000", viewModel.ObjectReferences.AddressDisplay);

        viewModel.GoBackCommand.Execute(null);

        Assert.Equal("MyApp.Owner", viewModel.ObjectReferences.ObjectTypeName);
        Assert.Equal("0x000000001000", viewModel.ObjectReferences.AddressDisplay);

        viewModel.GoForwardCommand.Execute(null);

        Assert.Equal("MyApp.Child", viewModel.ObjectReferences.ObjectTypeName);
        Assert.Equal("0x000000002000", viewModel.ObjectReferences.AddressDisplay);
    }

    [Fact]
    public async Task RootReferenceRowCannotNavigate()
    {
        var referenceService = new StubObjectReferenceService(
            [],
            [new ObjectReference(0, 0x1000, ReferenceKind.Handle, null, null, "System.String")]);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository(
                [new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.Types.SelectedType = viewModel.Types.FilteredTypes.Single();
        var instanceRow = Assert.Single(viewModel.ObjectInstances.Instances);
        viewModel.ShowIncomingReferencesCommand.Execute(instanceRow);
        var rootRow = Assert.Single(viewModel.ObjectReferences.References);
        Assert.True(rootRow.IsRoot);
        Assert.False(rootRow.CanNavigate);

        Assert.False(viewModel.ShowOutgoingReferencesCommand.CanExecute(rootRow));
        Assert.False(viewModel.ShowIncomingReferencesCommand.CanExecute(rootRow));

        viewModel.ShowOutgoingReferencesCommand.Execute(rootRow);

        // The pane stays on the original object.
        Assert.Equal("System.String", viewModel.ObjectReferences.ObjectTypeName);
    }

    [Fact]
    public async Task LoadingANewSnapshotClearsTheReferencesPane()
    {
        var referenceService = new StubObjectReferenceService(
            [new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_child")],
            []);
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            referenceService,
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);
        await viewModel.LoadAsync("sample.dmp");
        viewModel.ShowOutgoingReferencesCommand.Execute(
            new HeapObjectRowViewModel(
                new HeapObjectInfo(0x1000, 0x1000, "System.String", 24, "Gen0")));
        Assert.True(viewModel.ObjectReferences.HasSelection);

        await viewModel.LoadAsync("sample.dmp");

        Assert.False(viewModel.ObjectReferences.HasSelection);
        Assert.True(viewModel.ObjectReferences.ShowIdle);
        Assert.Empty(viewModel.ObjectReferences.References);
    }

    [Fact]
    public async Task LoadComputesRetainedSizesAndFillsTheTypeBrowser()
    {
        var dominatorService = new StubDominatorService(
            new DominatorAnalysisResult(
                [],
                [new TypeRetainedSize(0x1000, "System.String", 44_200_000)]));
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 381_235, 44_200_000))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance,
            dominatorService: dominatorService);

        await viewModel.LoadAsync("sample.dmp");

        Assert.False(viewModel.IsComputingRetainedSizes);
        Assert.False(viewModel.HasRetainedSizeError);
        Assert.False(viewModel.ShowRetainedSizeProgress);
        var row = Assert.Single(viewModel.Types.FilteredTypes);
        Assert.True(row.IsRetainedSizeAvailable);
        Assert.Equal(FormatBytes(44_200_000), row.RetainedSizeDisplay);
    }

    [Fact]
    public async Task RetainedSizeComputationPublishesProgressWhileRunning()
    {
        var gate = new TaskCompletionSource<DominatorAnalysisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dominatorService = new StubDominatorService(async (progress, _) =>
        {
            progress?.Report(0.42);
            return await gate.Task;
        });
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance,
            dominatorService: dominatorService);

        await viewModel.LoadAsync("sample.dmp");

        Assert.True(viewModel.IsComputingRetainedSizes);
        Assert.True(viewModel.ShowRetainedSizeProgress);
        Assert.Equal(0.42, viewModel.RetainedSizeProgress, precision: 10);
        Assert.Contains("42%", viewModel.RetainedSizeStatusText);
        Assert.True(viewModel.CancelRetainedSizeCommand.CanExecute(null));

        // Await the fire-and-forget continuation's publish deterministically:
        // subscribe to the state change before completing the gate, then wait
        // for the completion source instead of sleeping.
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += OnPropertyChanged;
        gate.SetResult(new DominatorAnalysisResult([], []));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.PropertyChanged -= OnPropertyChanged;

        Assert.False(viewModel.IsComputingRetainedSizes);
        Assert.False(viewModel.ShowRetainedSizeProgress);

        void OnPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(SnapshotViewModel.IsComputingRetainedSizes) &&
                !viewModel.IsComputingRetainedSizes)
            {
                completed.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task RetainedSizeComputationFailureIsNonFatal()
    {
        var dominatorService = new StubDominatorService(
            (_, _) => Task.FromException<DominatorAnalysisResult>(
                new InvalidDataException("The dump has no CLR runtime.")));
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance,
            dominatorService: dominatorService);

        await viewModel.LoadAsync("sample.dmp");

        Assert.True(viewModel.HasSnapshot);
        Assert.True(viewModel.IsReady);
        Assert.True(viewModel.ShowTable);
        Assert.True(viewModel.HasRetainedSizeError);
        Assert.True(viewModel.ShowRetainedSizeProgress);
        Assert.StartsWith("Retained sizes unavailable.", viewModel.RetainedSizeStatusText);
        Assert.True(Assert.Single(viewModel.Types.FilteredTypes).IsRetainedSizeUnavailable);
    }

    [Fact]
    public async Task LoadWithoutDominatorServiceLeavesRetainedSizesUnavailable()
    {
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance);

        await viewModel.LoadAsync("sample.dmp");

        Assert.False(viewModel.IsComputingRetainedSizes);
        Assert.False(viewModel.HasRetainedSizeError);
        Assert.True(Assert.Single(viewModel.Types.FilteredTypes).IsRetainedSizeUnavailable);
    }

    [Fact]
    public async Task LoadingANewSnapshotDropsTheStaleRetainedSizeResult()
    {
        var gate = new TaskCompletionSource<DominatorAnalysisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dominatorService = new StubDominatorService((_, _) =>
        {
            calls++;
            return calls == 1
                ? gate.Task
                : Task.FromResult(new DominatorAnalysisResult(
                    [],
                    [new TypeRetainedSize(0x1000, "System.String", 500)]));
        });
        await using var viewModel = new SnapshotViewModel(
            new StubSnapshotLoader(
                Snapshot(Type("System.String", "System.Private.CoreLib", 1, 24))),
            new StubHeapObjectRepository([]),
            new StubObjectReferenceService([]),
            new StubGcRootService([]),
            ImmediateUiDispatcher.Instance,
            dominatorService: dominatorService);

        await viewModel.LoadAsync("sample.dmp");
        Assert.True(viewModel.IsComputingRetainedSizes);

        // The second load supersedes the first computation and publishes its
        // own result.
        await viewModel.LoadAsync("sample.dmp");
        Assert.False(viewModel.IsComputingRetainedSizes);
        Assert.Equal(
            FormatBytes(500),
            Assert.Single(viewModel.Types.FilteredTypes).RetainedSizeDisplay);

        // The stale computation finishing late must not overwrite the newer
        // result. Await its completion, then prove the guard: a broken version
        // check would publish the stale 999 almost immediately, so a bounded
        // spin that never observes it confirms the result was dropped.
        gate.SetResult(new DominatorAnalysisResult(
            [],
            [new TypeRetainedSize(0x1000, "System.String", 999)]));
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var row = viewModel.Types.FilteredTypes.Single();
        Assert.False(SpinWait.SpinUntil(
            () => row.RetainedSize == 999,
            TimeSpan.FromSeconds(1)));
        Assert.Equal(FormatBytes(500), row.RetainedSizeDisplay);
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

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            return Task.CompletedTask;
        }
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

    private sealed class StubObjectReferenceService : IObjectReferenceService
    {
        private readonly IReadOnlyList<ObjectReference> _outgoing;
        private readonly IReadOnlyList<ObjectReference> _incoming;

        public StubObjectReferenceService(IReadOnlyList<ObjectReference> references)
            : this(references, references)
        {
        }

        public StubObjectReferenceService(
            IReadOnlyList<ObjectReference> outgoing,
            IReadOnlyList<ObjectReference> incoming)
        {
            _outgoing = outgoing;
            _incoming = incoming;
        }

        public Task<IReadOnlyList<ObjectReference>> GetOutgoingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_outgoing);

        public Task<IReadOnlyList<ObjectReference>> GetIncomingReferencesAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_incoming);
    }

    private sealed class StubGcRootService(
        IReadOnlyList<GcRootInfo> roots) : IGcRootService
    {
        public Task<IReadOnlyList<GcRootInfo>> FindRootsAsync(
            HeapSnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(roots);
    }

    private sealed class StubDominatorService : IDominatorTreeService
    {
        private readonly Func<
            IProgress<double>?,
            CancellationToken,
            Task<DominatorAnalysisResult>> _compute;

        public StubDominatorService(DominatorAnalysisResult result)
            : this((_, _) => Task.FromResult(result))
        {
        }

        public StubDominatorService(
            Func<IProgress<double>?, CancellationToken, Task<DominatorAnalysisResult>> compute)
        {
            _compute = compute;
        }

        public Task<DominatorAnalysisResult> ComputeDominatorsAsync(
            HeapSnapshot snapshot,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            _compute(progress, cancellationToken);
    }

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        private int _dispatchDepth;

        public bool IsDispatching => _dispatchDepth > 0;

        public Task InvokeAsync(Action action)
        {
            _dispatchDepth++;
            try
            {
                action();
            }
            finally
            {
                _dispatchDepth--;
            }

            return Task.CompletedTask;
        }
    }
}
