using System.Collections.ObjectModel;
using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.Models;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class ObjectReferencesViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IObjectReferenceService _service;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncCommand _showOutgoingCommand;
    private readonly AsyncCommand _showIncomingCommand;
    private ObservableCollection<ObjectReferenceRowViewModel> _references = [];
    private ReadOnlyObservableCollection<ObjectReferenceRowViewModel> _referencesView;
    private CancellationTokenSource? _loadCancellation;
    private HeapSnapshot? _snapshot;
    private string _objectTypeName = string.Empty;
    private ulong _objectAddress;
    private ReferenceDirection _direction;
    private ProfilerError? _error;
    private bool _isLoading;
    private int _loadVersion;
    private int _disposed;

    internal ObjectReferencesViewModel(
        IObjectReferenceService service,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _service = service;
        _uiDispatcher = uiDispatcher;
        _referencesView = new ReadOnlyObservableCollection<ObjectReferenceRowViewModel>(_references);
        _showOutgoingCommand = new AsyncCommand(
            ShowOutgoingAsync,
            () => HasSelection);
        _showIncomingCommand = new AsyncCommand(
            ShowIncomingAsync,
            () => HasSelection);
    }

    public ReadOnlyObservableCollection<ObjectReferenceRowViewModel> References => _referencesView;

    public string ObjectTypeName => _objectTypeName;

    public string AddressDisplay =>
        _objectAddress == 0
            ? string.Empty
            : MetricFormatting.Address(_objectAddress);

    public ReferenceDirection Direction => _direction;

    public string DirectionLabel =>
        _direction == ReferenceDirection.Outgoing ? "Outgoing" : "Incoming";

    public bool IsOutgoing => _direction == ReferenceDirection.Outgoing;

    public bool IsIncoming => _direction == ReferenceDirection.Incoming;

    public string EmptyHint =>
        _direction == ReferenceDirection.Outgoing
            ? "This object does not reference any other objects on the managed heap."
            : "Nothing on the managed heap references this object.";

    public string SummaryDisplay =>
        _objectAddress == 0 || _references.Count == 0
            ? string.Empty
            : $"{MetricFormatting.Count(_references.Count)} {DirectionLabel.ToLowerInvariant()} references";

    public bool HasSelection => _objectAddress != 0;

    public bool IsLoading => _isLoading;

    public ProfilerError? Error => _error;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public bool HasError => Error is not null;

    public bool ShowIdle => !HasSelection && !IsLoading && !HasError;

    public bool ShowLoading => IsLoading;

    public bool ShowError => HasError;

    public bool ShowEmpty => HasSelection && !IsLoading && !HasError && _references.Count == 0;

    public bool ShowTable => HasSelection && !IsLoading && !HasError && _references.Count > 0;

    public System.Windows.Input.ICommand ShowOutgoingCommand => _showOutgoingCommand;

    public System.Windows.Input.ICommand ShowIncomingCommand => _showIncomingCommand;

    public async Task ShowAsync(
        HeapSnapshot snapshot,
        string objectTypeName,
        ulong objectAddress,
        ReferenceDirection direction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (objectAddress == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectAddress),
                "Object address must be non-zero.");
        }

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

            _snapshot = snapshot;
            _objectTypeName = objectTypeName;
            _objectAddress = objectAddress;
            _direction = direction;
            SetError(null);
            _isLoading = true;
            // Drop the previous object's rows before the new load publishes any
            // state, so a failed or pending load can never show stale results
            // or a summary that contradicts the inspected object.
            _references = [];
            _referencesView = new ReadOnlyObservableCollection<ObjectReferenceRowViewModel>(_references);
            OnPropertyChanged(nameof(References));
            OnPropertyChanged(nameof(ObjectTypeName));
            OnPropertyChanged(nameof(AddressDisplay));
            OnPropertyChanged(nameof(DirectionLabel));
            OnPropertyChanged(nameof(IsOutgoing));
            OnPropertyChanged(nameof(IsIncoming));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SummaryDisplay));
            NotifyDisplayStateChanged();
            NotifyDirectionCommandsCanExecuteChanged();
        }).ConfigureAwait(false);

        try
        {
            var references = direction == ReferenceDirection.Outgoing
                ? await _service
                    .GetOutgoingReferencesAsync(snapshot, objectAddress, linkedToken)
                    .ConfigureAwait(false)
                : await _service
                    .GetIncomingReferencesAsync(snapshot, objectAddress, linkedToken)
                    .ConfigureAwait(false);

            // Build the rows off the UI thread. The collection is swapped in a
            // single published action so the pane raises exactly one binding
            // notification per load.
            var rows = new List<ObjectReferenceRowViewModel>(references.Count);
            foreach (var reference in references)
            {
                rows.Add(new ObjectReferenceRowViewModel(reference, direction));
            }

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                _references = new ObservableCollection<ObjectReferenceRowViewModel>(rows);
                _referencesView = new ReadOnlyObservableCollection<ObjectReferenceRowViewModel>(_references);
                _isLoading = false;
                OnPropertyChanged(nameof(References));
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
            _snapshot = null;
            _objectTypeName = string.Empty;
            _objectAddress = 0;
            SetError(null);
            _isLoading = false;
            _references = [];
            _referencesView = new ReadOnlyObservableCollection<ObjectReferenceRowViewModel>(_references);
            OnPropertyChanged(nameof(References));
            OnPropertyChanged(nameof(ObjectTypeName));
            OnPropertyChanged(nameof(AddressDisplay));
            OnPropertyChanged(nameof(DirectionLabel));
            OnPropertyChanged(nameof(IsOutgoing));
            OnPropertyChanged(nameof(IsIncoming));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SummaryDisplay));
            NotifyDisplayStateChanged();
            NotifyDirectionCommandsCanExecuteChanged();
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

    private async Task ShowOutgoingAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is null || _objectAddress == 0)
        {
            return;
        }

        try
        {
            await ShowAsync(
                snapshot,
                _objectTypeName,
                _objectAddress,
                ReferenceDirection.Outgoing).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or disposal superseded this load.
        }
        catch (ObjectDisposedException)
        {
            // The pane was disposed while the command was executing.
        }
    }

    private async Task ShowIncomingAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is null || _objectAddress == 0)
        {
            return;
        }

        try
        {
            await ShowAsync(
                snapshot,
                _objectTypeName,
                _objectAddress,
                ReferenceDirection.Incoming).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or disposal superseded this load.
        }
        catch (ObjectDisposedException)
        {
            // The pane was disposed while the command was executing.
        }
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
    }

    private void NotifyDirectionCommandsCanExecuteChanged()
    {
        _showOutgoingCommand.NotifyCanExecuteChanged();
        _showIncomingCommand.NotifyCanExecuteChanged();
    }
}
