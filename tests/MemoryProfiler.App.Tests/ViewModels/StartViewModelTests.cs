using System.Collections.Specialized;
using MemoryProfiler.App.ViewModels;
using MemoryProfiler.Contracts.Processes;
using MemoryProfiler.Diagnostics.Processes;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels;

public sealed class StartViewModelTests
{
    [Theory]
    [InlineData("10.0.1", ".NET 10.0.1")]
    [InlineData(null, "Runtime unavailable")]
    public void ProcessRowsProvideReadableRuntimeLabels(
        string? runtimeVersion,
        string expectedLabel)
    {
        var row = new ProcessRowViewModel(
            new ProcessInfo(12451, "MyApi", runtimeVersion));

        Assert.Equal(expectedLabel, row.RuntimeDisplay);
    }

    [Fact]
    public async Task RefreshProjectsProcessesAndUsesPidOrderByDefault()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(12883, "WorkerService", "9.0.0"),
            new ProcessInfo(12451, "MyApi", "10.0.1"));
        using var picker = new ProcessPickerViewModel(discovery);

        await picker.RefreshAsync();

        Assert.Collection(
            picker.Processes,
            process => Assert.Equal(12451, process.ProcessId),
            process => Assert.Equal(12883, process.ProcessId));
        Assert.True(picker.HasProcesses);
        Assert.False(picker.IsLoading);
        Assert.False(picker.HasError);
    }

    [Fact]
    public async Task ColumnSortingUsesNaturalRuntimeOrderAndTogglesDirection()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(1, "Unknown", null),
            new ProcessInfo(2, "Ten", "10.0.0"),
            new ProcessInfo(3, "Nine", "9.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();

        picker.SortBy(ProcessSortColumn.Runtime);

        Assert.Equal(["Nine", "Ten", "Unknown"], picker.Processes.Select(row => row.Name));
        Assert.Equal("Runtime ascending", picker.RuntimeSortDescription);

        picker.SortBy(ProcessSortColumn.Runtime);

        Assert.Equal(["Ten", "Nine", "Unknown"], picker.Processes.Select(row => row.Name));
        Assert.Equal("Runtime descending", picker.RuntimeSortDescription);
    }

    [Fact]
    public async Task NameSortingIsCaseInsensitiveAndStableByProcessId()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(20, "worker", "10.0.0"),
            new ProcessInfo(30, "Api", "10.0.0"),
            new ProcessInfo(10, "api", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();

        picker.SortBy(ProcessSortColumn.Name);

        Assert.Equal([10, 30, 20], picker.Processes.Select(row => row.ProcessId));
    }

    [Fact]
    public async Task SortingMovesRowsWithoutResettingTheSelectedProcess()
    {
        var discovery = StubDiscovery.Returning(
            new ProcessInfo(20, "Api", "10.0.0"),
            new ProcessInfo(10, "Worker", "10.0.0"));
        using var picker = new ProcessPickerViewModel(discovery);
        await picker.RefreshAsync();
        var selected = picker.Processes.Single(row => row.ProcessId == 10);
        picker.SelectedProcess = selected;
        var collectionActions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)picker.Processes).CollectionChanged += (_, eventArgs) =>
            collectionActions.Add(eventArgs.Action);

        picker.SortBy(ProcessSortColumn.Name);

        Assert.Same(selected, picker.SelectedProcess);
        Assert.NotEmpty(collectionActions);
        Assert.All(
            collectionActions,
            action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
    }

    [Fact]
    public async Task RefreshExposesLoadingStateWhileDiscoveryIsPending()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ProcessInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new StubDiscovery(_ => completion.Task);
        using var picker = new ProcessPickerViewModel(discovery);

        var refresh = picker.RefreshAsync();

        Assert.False(refresh.IsCompleted);
        Assert.True(picker.IsLoading);
        Assert.False(picker.RefreshCommand.CanExecute(null));

        completion.SetResult([]);
        await refresh;

        Assert.False(picker.IsLoading);
        Assert.True(picker.IsEmpty);
        Assert.True(picker.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshFailureBecomesAnInlineRetryableError()
    {
        var discovery = new StubDiscovery(
            _ => Task.FromException<IReadOnlyList<ProcessInfo>>(
                new InvalidOperationException("Diagnostics service unavailable.")));
        using var picker = new ProcessPickerViewModel(discovery);

        await picker.RefreshAsync();

        Assert.True(picker.HasError);
        Assert.Contains("Diagnostics service unavailable.", picker.ErrorMessage);
        Assert.True(picker.RefreshCommand.CanExecute(null));
        Assert.Empty(picker.Processes);
    }

    [Fact]
    public async Task ANewRefreshCancelsAndCannotBeOverwrittenByThePreviousRefresh()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var call = 0;
        var discovery = new StubDiscovery(async cancellationToken =>
        {
            call++;
            if (call == 1)
            {
                firstToken = cancellationToken;
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [new ProcessInfo(42, "Current", "10.0.0")];
        });
        using var picker = new ProcessPickerViewModel(discovery);

        var firstRefresh = picker.RefreshAsync();
        await firstStarted.Task;
        var secondRefresh = picker.RefreshAsync();

        await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.True(firstToken.IsCancellationRequested);
        var process = Assert.Single(picker.Processes);
        Assert.Equal("Current", process.Name);
    }

    [Fact]
    public async Task RefreshCoordinatorDoesNotPublishALeaseThatAnotherLeaseCanDispose()
    {
        using var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        var second = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(first.Token.IsCancellationRequested);
        first.Dispose();

        using var third = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(second.Token.IsCancellationRequested);
        Assert.True(third.IsCurrent);
        second.Dispose();
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotPreventAReplacementRefresh()
    {
        var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        using var registration = first.Token.Register(
            () => throw new InvalidOperationException("Callback failed."));
        RefreshLease? second = null;

        try
        {
            var exception = await Record.ExceptionAsync(
                async () => second = await coordinator.BeginAsync(CancellationToken.None));

            Assert.Null(exception);
            Assert.NotNull(second);
            Assert.True(second.IsCurrent);
        }
        finally
        {
            registration.Dispose();
            first.Dispose();
            second?.Dispose();
            coordinator.Dispose();
        }
    }

    [Fact]
    public async Task CancellationCallbackCanRetireItsLeaseDuringReplacement()
    {
        using var coordinator = new RefreshCoordinator();
        var first = await coordinator.BeginAsync(CancellationToken.None);
        var firstToken = first.Token;
        using var registration = firstToken.Register(first.Dispose);

        using var second = await coordinator.BeginAsync(CancellationToken.None);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public async Task ReplacementWaitsForAnAcceptedStateMutationToFinish()
    {
        using var coordinator = new RefreshCoordinator();
        using var first = await coordinator.BeginAsync(CancellationToken.None);
        using var mutationStarted = new ManualResetEventSlim();
        using var releaseMutation = new ManualResetEventSlim();
        var mutation = Task.Run(async () =>
            await first.TryRunIfCurrentAsync(() =>
            {
                mutationStarted.Set();
                releaseMutation.Wait();
            }));
        Assert.True(mutationStarted.Wait(TimeSpan.FromSeconds(5)));

        var replacement = coordinator.BeginAsync(CancellationToken.None).AsTask();

        Assert.False(replacement.IsCompleted);
        releaseMutation.Set();
        Assert.True(await mutation);
        using var second = await replacement;
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public async Task AttachShowsThePickerBeforeDiscoveryCompletes()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ProcessInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new StubDiscovery(_ => completion.Task);
        using var start = new StartViewModel(new ProcessPickerViewModel(discovery));

        var showPicker = start.ShowProcessPickerAsync();

        Assert.True(start.IsProcessPickerVisible);
        Assert.True(start.ProcessPicker.IsLoading);

        completion.SetResult([]);
        await showPicker;
    }

    private sealed class StubDiscovery(
        Func<CancellationToken, Task<IReadOnlyList<ProcessInfo>>> discover) : IDotNetProcessDiscovery
    {
        public static StubDiscovery Returning(params ProcessInfo[] processes) =>
            new(_ => Task.FromResult<IReadOnlyList<ProcessInfo>>(processes));

        public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(
            CancellationToken cancellationToken = default) =>
            discover(cancellationToken);
    }
}
