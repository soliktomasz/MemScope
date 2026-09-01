using System.Collections.ObjectModel;
using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class GcRootsViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IGcRootService _service;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private ObservableCollection<GcRootRowViewModel> _rows = [];
    private ReadOnlyObservableCollection<GcRootRowViewModel> _rowsView;
    private CancellationTokenSource? _loadCancellation;
    private HeapSnapshot? _snapshot;
    private string _objectTypeName = string.Empty;
    private ulong _objectAddress;
    private int _rootCount;
    private ProfilerError? _error;
    private bool _isLoading;
    private int _loadVersion;
    private int _disposed;

    internal GcRootsViewModel(
        IGcRootService service,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _service = service;
        _uiDispatcher = uiDispatcher;
        _rowsView = new ReadOnlyObservableCollection<GcRootRowViewModel>(_rows);
    }

    public ReadOnlyObservableCollection<GcRootRowViewModel> Rows => _rowsView;

    public string ObjectTypeName => _objectTypeName;

    public string AddressDisplay =>
        _objectAddress == 0
            ? string.Empty
            : MetricFormatting.Address(_objectAddress);

    public string SummaryDisplay
    {
        get
        {
            if (_objectAddress == 0)
            {
                return string.Empty;
            }

            return _rootCount switch
            {
                0 => string.Empty,
                1 => "1 path to root",
                var count => $"{MetricFormatting.Count(count)} paths to root",
            };
        }
    }

    public string EmptyHint =>
        "No path from a GC root to this object was found within the search limit.";

    public bool HasSelection => _objectAddress != 0;

    public bool IsLoading => _isLoading;

    public ProfilerError? Error => _error;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public bool HasError => Error is not null;

    public bool ShowIdle => !HasSelection && !IsLoading && !HasError;

    public bool ShowLoading => IsLoading;

    public bool ShowError => HasError;

    public bool ShowEmpty => HasSelection && !IsLoading && !HasError && _rootCount == 0;

    public bool ShowTable => HasSelection && !IsLoading && !HasError && _rootCount > 0;

    public async Task ShowAsync(
        HeapSnapshot snapshot,
        string objectTypeName,
        ulong objectAddress,
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
            SetError(null);
            _isLoading = true;
            _rootCount = 0;
            // Drop the previous object's rows before the new load publishes any
            // state, so a failed or pending load can never show stale results
            // or a summary that contradicts the inspected object.
            _rows = [];
            _rowsView = new ReadOnlyObservableCollection<GcRootRowViewModel>(_rows);
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(ObjectTypeName));
            OnPropertyChanged(nameof(AddressDisplay));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SummaryDisplay));
            NotifyDisplayStateChanged();
        }).ConfigureAwait(false);

        try
        {
            var roots = await _service
                .FindRootsAsync(snapshot, objectAddress, linkedToken)
                .ConfigureAwait(false);

            // Build the rows off the UI thread. The collection is swapped in a
            // single published action so the pane raises exactly one binding
            // notification per load.
            var rows = Flatten(roots, objectTypeName, objectAddress);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _loadVersion))
                {
                    return;
                }

                _rows = new ObservableCollection<GcRootRowViewModel>(rows);
                _rowsView = new ReadOnlyObservableCollection<GcRootRowViewModel>(_rows);
                _rootCount = roots.Count;
                _isLoading = false;
                OnPropertyChanged(nameof(Rows));
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
            _rootCount = 0;
            _rows = [];
            _rowsView = new ReadOnlyObservableCollection<GcRootRowViewModel>(_rows);
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(ObjectTypeName));
            OnPropertyChanged(nameof(AddressDisplay));
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

    private static List<GcRootRowViewModel> Flatten(
        IReadOnlyList<GcRootInfo> roots,
        string objectTypeName,
        ulong objectAddress)
    {
        var rows = new List<GcRootRowViewModel>();
        foreach (var root in roots)
        {
            var path = root.Path;
            var headTypeName = path is { Count: > 0 }
                ? path[0].SourceTypeName
                : objectTypeName;
            var rootTypeDisplay = string.IsNullOrWhiteSpace(root.Name)
                ? headTypeName
                : root.Name;

            // The root itself is not a heap object: it identifies where the
            // path starts and is not navigable.
            rows.Add(new GcRootRowViewModel(
                depth: 0,
                isRoot: true,
                isTarget: false,
                fieldDisplay: "GC Root",
                kindDisplay: root.Kind,
                addressDisplay: "root",
                typeNameDisplay: rootTypeDisplay ?? "N/A",
                endpointAddress: 0,
                endpointTypeName: string.Empty,
                canNavigate: false));

            // The object the root references directly, then every hop down to
            // the queried object. A root that references the object directly
            // contributes only the target row.
            var hopCount = path?.Count ?? 0;
            for (var index = 0; index <= hopCount; index++)
            {
                var isTarget = index == hopCount;
                if (index == 0)
                {
                    rows.Add(new GcRootRowViewModel(
                        depth: 1,
                        isRoot: false,
                        isTarget: isTarget,
                        fieldDisplay: string.IsNullOrWhiteSpace(root.Name)
                            ? root.Kind
                            : root.Name,
                        kindDisplay: root.Kind,
                        addressDisplay: GcRootRowViewModel.AddressDisplayFor(root.RootAddress),
                        typeNameDisplay: headTypeName ?? "N/A",
                        endpointAddress: root.RootAddress,
                        endpointTypeName: headTypeName ?? string.Empty,
                        canNavigate: true));
                    continue;
                }

                var edge = path![index - 1];
                rows.Add(new GcRootRowViewModel(
                    depth: index + 1,
                    isRoot: false,
                    isTarget: isTarget,
                    fieldDisplay: edge.Name ??
                                  (edge.Kind == ReferenceKind.ArrayElement
                                      ? "array element"
                                      : "N/A"),
                    kindDisplay: KindLabel(edge.Kind),
                    addressDisplay: GcRootRowViewModel.AddressDisplayFor(edge.TargetAddress),
                    typeNameDisplay: edge.TargetTypeName ?? "N/A",
                    endpointAddress: edge.TargetAddress,
                    endpointTypeName: edge.TargetTypeName ?? string.Empty,
                    canNavigate: true));
            }
        }

        return rows;
    }

    private static string KindLabel(ReferenceKind kind) =>
        kind switch
        {
            ReferenceKind.Field => "Field",
            ReferenceKind.ArrayElement => "Array element",
            _ => string.Empty,
        };

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
}
