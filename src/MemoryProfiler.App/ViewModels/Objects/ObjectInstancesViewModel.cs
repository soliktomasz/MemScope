using System.Collections.ObjectModel;
using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class ObjectInstancesViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHeapObjectRepository _repository;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly RelayCommand _cancelCommand;
    private ObservableCollection<HeapObjectRowViewModel> _instances = [];
    private ReadOnlyObservableCollection<HeapObjectRowViewModel> _instancesView;
    private CancellationTokenSource? _loadCancellation;
    private HeapTypeInfo? _type;
    private ulong _totalSize;
    private ProfilerError? _error;
    private bool _isLoading;
    private int _loadVersion;
    private int _disposed;
    private HeapObjectRowViewModel? _selectedInstance;

    internal ObjectInstancesViewModel(
        IHeapObjectRepository repository,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _repository = repository;
        _uiDispatcher = uiDispatcher;
        _instancesView = new ReadOnlyObservableCollection<HeapObjectRowViewModel>(_instances);
        _cancelCommand = new RelayCommand(CancelLoad, () => IsLoading);
    }

    public ReadOnlyObservableCollection<HeapObjectRowViewModel> Instances => _instancesView;

    public HeapObjectRowViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set => SetProperty(ref _selectedInstance, value);
    }

    public string TypeName => _type?.Name ?? string.Empty;

    public string SummaryDisplay =>
        _type is null || _instances.Count == 0
            ? string.Empty
            : $"{MetricFormatting.Count(_instances.Count)} instances · {MetricFormatting.Bytes(_totalSize)}";

    public bool HasSelection => _type is not null;

    public bool IsLoading => _isLoading;

    public ProfilerError? Error => _error;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public bool HasError => Error is not null;

    public bool ShowIdle => !HasSelection && !IsLoading && !HasError;

    public bool ShowLoading => IsLoading;

    public bool ShowError => HasError;

    public bool ShowEmpty => HasSelection && !IsLoading && !HasError && _instances.Count == 0;

    public bool ShowTable => HasSelection && !IsLoading && !HasError && _instances.Count > 0;

    public System.Windows.Input.ICommand CancelCommand => _cancelCommand;

    public async Task ShowAsync(
        HeapSnapshot snapshot,
        HeapTypeInfo type,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);

        CancelLoad();
        var version = Interlocked.Increment(ref _loadVersion);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        // Capture the token struct up front: the source may be disposed while
        // the load is in flight, and CancellationTokenSource.Token throws
        // ObjectDisposedException after dispose, which would break the
        // cancellation filter below.
        var linkedToken = linked.Token;
        _loadCancellation = linked;

        await PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            _type = type;
            SetError(null);
            _isLoading = true;
            // Drop the previous type's rows before the new load publishes any
            // state, so a failed or pending load can never show stale results
            // or a summary that contradicts the selected type name.
            _instances = [];
            SelectedInstance = null;
            _instancesView = new ReadOnlyObservableCollection<HeapObjectRowViewModel>(_instances);
            _totalSize = 0;
            OnPropertyChanged(nameof(Instances));
            OnPropertyChanged(nameof(SummaryDisplay));
            OnPropertyChanged(nameof(TypeName));
            OnPropertyChanged(nameof(HasSelection));
            NotifyDisplayStateChanged();
        }).ConfigureAwait(false);

        try
        {
            var instances = await _repository
                .GetInstancesAsync(snapshot, type.MethodTable, linkedToken)
                .ConfigureAwait(false);

            // Build the rows off the UI thread. The collection is swapped in a
            // single published action so a type with hundreds of thousands of
            // instances raises exactly one binding notification instead of one
            // per row.
            var rows = new List<HeapObjectRowViewModel>(instances.Count);
            ulong totalSize = 0;
            foreach (var instance in instances)
            {
                rows.Add(new HeapObjectRowViewModel(instance));
                checked
                {
                    totalSize += instance.Size;
                }
            }

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                _instances = new ObservableCollection<HeapObjectRowViewModel>(rows);
                _instancesView = new ReadOnlyObservableCollection<HeapObjectRowViewModel>(_instances);
                _totalSize = totalSize;
                _isLoading = false;
                OnPropertyChanged(nameof(Instances));
                OnPropertyChanged(nameof(SummaryDisplay));
                NotifyDisplayStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
        {
            // A newer selection, deselection, or disposal superseded this load.
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                SetError(ProfilerErrorFactory.Create(
                    ProfilerOperation.AnalyzeSnapshot,
                    exception));
                _isLoading = false;
                NotifyDisplayStateChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                _isLoading = false;
                NotifyDisplayStateChanged();
            }).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        CancelLoad();
        Interlocked.Increment(ref _loadVersion);

        await PublishAsync(() =>
        {
            _type = null;
            SetError(null);
            _isLoading = false;
            _instances = [];
            _instancesView = new ReadOnlyObservableCollection<HeapObjectRowViewModel>(_instances);
            _totalSize = 0;
            OnPropertyChanged(nameof(Instances));
            OnPropertyChanged(nameof(TypeName));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SummaryDisplay));
            NotifyDisplayStateChanged();
        }).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _disposeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Nothing to cancel.
        }

        CancelLoad();
        _disposeCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private void CancelLoad()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The token source was already released.
        }

        cancellation.Dispose();
    }

    private async Task PublishAsync(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                action();
            }
        }).ConfigureAwait(false);
    }

    private void SetError(ProfilerError? error)
    {
        if (error == _error)
        {
            return;
        }

        _error = error;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }

    private void NotifyDisplayStateChanged()
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowTable));
        _cancelCommand.NotifyCanExecuteChanged();
    }
}
