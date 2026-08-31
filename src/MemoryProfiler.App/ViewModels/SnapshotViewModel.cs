using System.ComponentModel;
using System.Globalization;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Navigation;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.App.ViewModels.Types;

namespace MemoryProfiler.App.ViewModels;

public sealed class SnapshotViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHeapSnapshotLoader _loader;
    private readonly IDominatorTreeService? _dominatorService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly AsyncCommand _closeCommand;
    private readonly RelayCommand<object> _showOutgoingReferencesCommand;
    private readonly RelayCommand<object> _showIncomingReferencesCommand;
    private readonly RelayCommand<object> _showPathToRootCommand;
    private readonly InvestigationNavigationService _navigation = new();
    private readonly RelayCommand _goBackCommand;
    private readonly RelayCommand _goForwardCommand;
    private HeapSnapshot? _snapshot;
    private string? _errorMessage;
    private bool _isLoading;
    private CancellationTokenSource? _retainedSizeCancellation;
    private int _retainedSizeVersion;
    private bool _isComputingRetainedSizes;
    private double _retainedSizeProgress;
    private string _retainedSizeStatusText = string.Empty;
    private int _disposed;
    private bool _suppressTypeNavigation;

    internal SnapshotViewModel(
        IHeapSnapshotLoader loader,
        IHeapObjectRepository objectRepository,
        IObjectReferenceService referenceService,
        IGcRootService gcRootService,
        IUiDispatcher uiDispatcher,
        Func<Task>? close = null,
        IDominatorTreeService? dominatorService = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(objectRepository);
        ArgumentNullException.ThrowIfNull(referenceService);
        ArgumentNullException.ThrowIfNull(gcRootService);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _loader = loader;
        _dominatorService = dominatorService;
        _uiDispatcher = uiDispatcher;
        _closeCommand = new AsyncCommand(close ?? (() => Task.CompletedTask));
        _showOutgoingReferencesCommand = new RelayCommand<object>(
            ShowOutgoingReferences,
            parameter => HasSnapshot && CanNavigateFrom(parameter));
        _showIncomingReferencesCommand = new RelayCommand<object>(
            ShowIncomingReferences,
            parameter => HasSnapshot && CanNavigateFrom(parameter));
        _showPathToRootCommand = new RelayCommand<object>(
            ShowPathToRoot,
            parameter => HasSnapshot && CanNavigateFrom(parameter));
        _goBackCommand = new RelayCommand(
            _navigation.GoBack,
            () => _navigation.CanGoBack);
        _goForwardCommand = new RelayCommand(
            _navigation.GoForward,
            () => _navigation.CanGoForward);
        ObjectInstances = new ObjectInstancesViewModel(objectRepository, uiDispatcher);
        ObjectReferences = new ObjectReferencesViewModel(referenceService, uiDispatcher);
        GcRoots = new GcRootsViewModel(gcRootService, uiDispatcher);
        Types.PropertyChanged += OnTypesPropertyChanged;
        _navigation.StateChanged += OnNavigationStateChanged;
    }

    public TypeBrowserViewModel Types { get; } = new();

    public ObjectInstancesViewModel ObjectInstances { get; }

    public ObjectReferencesViewModel ObjectReferences { get; }

    public GcRootsViewModel GcRoots { get; }

    public System.Windows.Input.ICommand CloseCommand => _closeCommand;

    public System.Windows.Input.ICommand ShowOutgoingReferencesCommand =>
        _showOutgoingReferencesCommand;

    public System.Windows.Input.ICommand ShowIncomingReferencesCommand =>
        _showIncomingReferencesCommand;

    public System.Windows.Input.ICommand ShowPathToRootCommand =>
        _showPathToRootCommand;

    public System.Windows.Input.ICommand GoBackCommand => _goBackCommand;

    public System.Windows.Input.ICommand GoForwardCommand => _goForwardCommand;

    public bool CanGoBack => _navigation.CanGoBack;

    public bool CanGoForward => _navigation.CanGoForward;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsReady));
                NotifyDisplayStateChanged();
            }
        }
    }

    public bool HasSnapshot => _snapshot is not null;

    public bool IsReady => HasSnapshot && !IsLoading;

    public string ErrorMessage => _errorMessage ?? string.Empty;

    public bool HasError => _errorMessage is not null;

    public bool ShowLoading => IsLoading;

    public bool ShowError => HasError;

    public bool ShowEmpty => IsReady && Types.HasNoTypes;

    public bool ShowNoFilteredTypes => IsReady && !Types.HasNoTypes && Types.HasNoFilteredTypes;

    public bool ShowTable => IsReady && Types.HasFilteredTypes;

    public bool IsComputingRetainedSizes => _isComputingRetainedSizes;

    public double RetainedSizeProgress => _retainedSizeProgress;

    public bool HasRetainedSizeError =>
        !_isComputingRetainedSizes && !string.IsNullOrEmpty(_retainedSizeStatusText);

    public bool ShowRetainedSizeProgress =>
        IsComputingRetainedSizes || HasRetainedSizeError;

    public string RetainedSizeStatusText
    {
        get
        {
            if (_isComputingRetainedSizes)
            {
                var percent = (int)Math.Round(_retainedSizeProgress * 100);
                return $"Computing retained sizes... {percent.ToString("N0", CultureInfo.CurrentCulture)}%";
            }

            return _retainedSizeStatusText;
        }
    }

    public string ProcessDescription
    {
        get
        {
            var info = _snapshot?.Info;
            if (info is null)
            {
                return string.Empty;
            }

            var processName = string.IsNullOrWhiteSpace(info.ProcessName)
                ? "Unknown process"
                : info.ProcessName;
            return info.ProcessId is > 0
                ? $"{processName} (PID {info.ProcessId})"
                : processName;
        }
    }

    public string RuntimeDisplay => _snapshot?.Info.RuntimeVersion ?? string.Empty;

    public string CapturedAtDisplay =>
        _snapshot is null
            ? string.Empty
            : _snapshot.Info.CapturedAt
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string ObjectCountDisplay =>
        _snapshot is null
            ? string.Empty
            : _snapshot.Info.ObjectCount.ToString("N0", CultureInfo.CurrentCulture);

    public string HeapSizeDisplay =>
        _snapshot is null ? string.Empty : MetricFormatting.Bytes(_snapshot.Info.HeapSize);

    public string SourcePath => _snapshot?.Info.Path ?? string.Empty;

    public async Task LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _loadCancellation.Token);

        // A new snapshot supersedes any in-flight retained-size computation:
        // cancel it up front (a version bump below keeps a stale result from
        // ever publishing) and clear the previous progress state.
        CancelRetainedSizeLoad();
        Interlocked.Increment(ref _retainedSizeVersion);

        await PublishAsync(() =>
        {
            _snapshot = null;
            SetError(null);
            _retainedSizeProgress = 0;
            _retainedSizeStatusText = string.Empty;
            _isComputingRetainedSizes = false;
            OnPropertyChanged(nameof(RetainedSizeProgress));
            OnPropertyChanged(nameof(RetainedSizeStatusText));
            OnPropertyChanged(nameof(IsComputingRetainedSizes));
            OnPropertyChanged(nameof(ShowRetainedSizeProgress));
            OnPropertyChanged(nameof(HasRetainedSizeError));
            OnPropertyChanged(nameof(HasSnapshot));
            IsLoading = true;
            NotifyDisplayStateChanged();
        }).ConfigureAwait(false);

        // A new snapshot replaces the previous analysis; any references still
        // shown belong to the old dump and must not survive it.
        await ObjectReferences.ClearAsync().ConfigureAwait(false);
        await GcRoots.ClearAsync().ConfigureAwait(false);

        try
        {
            var snapshot = await _loader.LoadAsync(path, linked.Token).ConfigureAwait(false);
            await PublishAsync(() =>
            {
                _snapshot = snapshot;
                Types.SetTypes(snapshot.Types);
                OnPropertyChanged(nameof(HasSnapshot));
                OnPropertyChanged(nameof(ProcessDescription));
                OnPropertyChanged(nameof(RuntimeDisplay));
                OnPropertyChanged(nameof(CapturedAtDisplay));
                OnPropertyChanged(nameof(ObjectCountDisplay));
                OnPropertyChanged(nameof(HeapSizeDisplay));
                OnPropertyChanged(nameof(SourcePath));
                NotifyDisplayStateChanged();
                _navigation.Reset(new TypesLocation());
            }).ConfigureAwait(false);

            // Retained sizes are computed in the background off the UI thread:
            // the type browser stays usable immediately and its Retained Size
            // column fills in as the dominator analysis progresses.
            if (_dominatorService is not null)
            {
                _ = ComputeRetainedSizesAsync(snapshot);
            }
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
            // Cancellation is expected when the user closes the snapshot during analysis.
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
                SetError($"Unable to open the snapshot. {exception.Message}")).ConfigureAwait(false);
        }
        finally
        {
            await PublishAsync(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _loadCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Nothing to cancel.
        }

        CancelRetainedSizeLoad();
        _navigation.StateChanged -= OnNavigationStateChanged;
        Types.PropertyChanged -= OnTypesPropertyChanged;
        _loadCancellation.Dispose();
        return DisposeChildrenAsync();
    }

    private async ValueTask DisposeChildrenAsync()
    {
        await ObjectInstances.DisposeAsync();
        await ObjectReferences.DisposeAsync();
        await GcRoots.DisposeAsync();
    }

    private void OnTypesPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(TypeBrowserViewModel.SelectedType))
        {
            if (!_suppressTypeNavigation)
            {
                var location = Types.SelectedType is { } selected
                    ? (InvestigationLocation)new TypeLocation(selected.MethodTable)
                    : new TypesLocation();
                _navigation.Navigate(location);
            }
        }

        NotifyDisplayStateChanged();
    }

    // Runs the dominator analysis in the background after a snapshot loads,
    // publishes progress through the UI dispatcher, and fills the type
    // browser's Retained Size column on completion. A version counter plus
    // cancellation guarantee a superseded computation never publishes.
    private async Task ComputeRetainedSizesAsync(HeapSnapshot snapshot)
    {
        var version = Interlocked.Increment(ref _retainedSizeVersion);
        var cancellation = new CancellationTokenSource();
        _retainedSizeCancellation = cancellation;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation.Token,
            _loadCancellation.Token);
        var token = linked.Token;
        try
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _retainedSizeVersion))
                {
                    return;
                }

                _retainedSizeProgress = 0;
                _retainedSizeStatusText = string.Empty;
                _isComputingRetainedSizes = true;
                OnPropertyChanged(nameof(RetainedSizeProgress));
                OnPropertyChanged(nameof(RetainedSizeStatusText));
                OnPropertyChanged(nameof(IsComputingRetainedSizes));
                OnPropertyChanged(nameof(ShowRetainedSizeProgress));
                OnPropertyChanged(nameof(HasRetainedSizeError));
            }).ConfigureAwait(false);

            var result = await _dominatorService!
                .ComputeDominatorsAsync(
                    snapshot,
                    new DominatorProgress(value => PublishRetainedSizeProgress(version, value)),
                    token)
                .ConfigureAwait(false);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _retainedSizeVersion))
                {
                    return;
                }

                Types.SetRetainedSizes(result.TypeRetainedSizes);
                _retainedSizeStatusText = string.Empty;
                _isComputingRetainedSizes = false;
                OnPropertyChanged(nameof(RetainedSizeStatusText));
                OnPropertyChanged(nameof(IsComputingRetainedSizes));
                OnPropertyChanged(nameof(ShowRetainedSizeProgress));
                OnPropertyChanged(nameof(HasRetainedSizeError));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A new snapshot or snapshot closure superseded the computation.
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _retainedSizeVersion))
                {
                    return;
                }

                // The failure is non-fatal: the snapshot and type browser stay
                // fully usable, only the retained column stays unavailable.
                _retainedSizeStatusText = $"Retained sizes unavailable. {exception.Message}";
                _isComputingRetainedSizes = false;
                OnPropertyChanged(nameof(RetainedSizeStatusText));
                OnPropertyChanged(nameof(IsComputingRetainedSizes));
                OnPropertyChanged(nameof(ShowRetainedSizeProgress));
                OnPropertyChanged(nameof(HasRetainedSizeError));
            }).ConfigureAwait(false);
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _retainedSizeVersion))
                {
                    return;
                }

                _isComputingRetainedSizes = false;
                OnPropertyChanged(nameof(IsComputingRetainedSizes));
                OnPropertyChanged(nameof(ShowRetainedSizeProgress));
                OnPropertyChanged(nameof(RetainedSizeStatusText));
            }).ConfigureAwait(false);
            linked.Dispose();

            // Release the cancellation source this computation created on every
            // completion path; a superseding load may already have canceled and
            // disposed it through CancelRetainedSizeLoad.
            if (ReferenceEquals(_retainedSizeCancellation, cancellation))
            {
                _retainedSizeCancellation = null;
            }

            try
            {
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already released by CancelRetainedSizeLoad.
            }
        }
    }

    private void PublishRetainedSizeProgress(int version, double value)
    {
        _ = PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _retainedSizeVersion))
            {
                return;
            }

            _retainedSizeProgress = value;
            OnPropertyChanged(nameof(RetainedSizeProgress));
            OnPropertyChanged(nameof(RetainedSizeStatusText));
        });
    }

    private void CancelRetainedSizeLoad()
    {
        var cancellation = _retainedSizeCancellation;
        _retainedSizeCancellation = null;
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

    private async Task RefreshInstancesAsync()
    {
        try
        {
            var snapshot = _snapshot;
            var type = Types.SelectedType?.Type;
            if (snapshot is null || type is null)
            {
                await ObjectInstances.ClearAsync().ConfigureAwait(false);
            }
            else
            {
                await ObjectInstances.ShowAsync(snapshot, type).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection or snapshot closure superseded this refresh.
        }
        catch (ObjectDisposedException)
        {
            // The snapshot was closed while the selection was changing.
        }
    }

    public void ShowOutgoingReferences(object? parameter) =>
        ShowReferences(parameter, ReferenceDirection.Outgoing);

    public void ShowIncomingReferences(object? parameter) =>
        ShowReferences(parameter, ReferenceDirection.Incoming);

    public void ShowPathToRoot(object? parameter)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !TryResolveEndpoint(parameter, out var address, out var typeName))
        {
            return;
        }

        _navigation.Navigate(new GcRootsLocation(address, typeName));
    }

    private async Task ShowPathToRootAsync(
        HeapSnapshot snapshot,
        string typeName,
        ulong address)
    {
        try
        {
            await GcRoots.ShowAsync(snapshot, typeName, address).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or snapshot closure superseded this one.
        }
        catch (ObjectDisposedException)
        {
            // The snapshot was closed while the command was executing.
        }
    }

    private void ShowReferences(object? parameter, ReferenceDirection direction)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !TryResolveEndpoint(parameter, out var address, out var typeName))
        {
            return;
        }

        _navigation.Navigate(new ObjectReferencesLocation(address, typeName, direction));
    }

    private void OnNavigationStateChanged(object? sender, EventArgs eventArgs)
    {
        _goBackCommand.NotifyCanExecuteChanged();
        _goForwardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        if (_navigation.CurrentLocation is { } location)
        {
            _ = ApplyNavigationAsync(location);
        }
    }

    private async Task ApplyNavigationAsync(InvestigationLocation location)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return;
        }

        try
        {
            switch (location)
            {
                case TypesLocation:
                    SetSelectedTypeWithoutNavigation(null);
                    await ObjectInstances.ClearAsync().ConfigureAwait(false);
                    await ObjectReferences.ClearAsync().ConfigureAwait(false);
                    await GcRoots.ClearAsync().ConfigureAwait(false);
                    break;
                case TypeLocation type:
                    await ObjectReferences.ClearAsync().ConfigureAwait(false);
                    await GcRoots.ClearAsync().ConfigureAwait(false);
                    SetSelectedTypeWithoutNavigation(Types.FindByMethodTable(type.MethodTable));
                    await RefreshInstancesAsync().ConfigureAwait(false);
                    break;
                case ObjectReferencesLocation references:
                    await GcRoots.ClearAsync().ConfigureAwait(false);
                    await ShowReferencesAsync(
                        snapshot,
                        references.ObjectTypeName,
                        references.ObjectAddress,
                        references.Direction).ConfigureAwait(false);
                    break;
                case GcRootsLocation roots:
                    await ShowPathToRootAsync(
                        snapshot,
                        roots.ObjectTypeName,
                        roots.ObjectAddress).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or snapshot closure superseded this one.
        }
        catch (ObjectDisposedException)
        {
            // The snapshot was closed while history was being restored.
        }
    }

    private void SetSelectedTypeWithoutNavigation(TypeRowViewModel? type)
    {
        _suppressTypeNavigation = true;
        try
        {
            Types.SelectedType = type;
        }
        finally
        {
            _suppressTypeNavigation = false;
        }
    }

    private async Task ShowReferencesAsync(
        HeapSnapshot snapshot,
        string typeName,
        ulong address,
        ReferenceDirection direction)
    {
        try
        {
            await ObjectReferences
                .ShowAsync(snapshot, typeName, address, direction)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or snapshot closure superseded this one.
        }
        catch (ObjectDisposedException)
        {
            // The snapshot was closed while the command was executing.
        }
    }

    private static bool CanNavigateFrom(object? parameter) =>
        parameter switch
        {
            HeapObjectRowViewModel => true,
            ObjectReferenceRowViewModel { CanNavigate: true } => true,
            GcRootRowViewModel { CanNavigate: true } => true,
            _ => false,
        };

    private static bool TryResolveEndpoint(
        object? parameter,
        out ulong address,
        out string typeName)
    {
        switch (parameter)
        {
            case HeapObjectRowViewModel instance:
                address = instance.Address;
                typeName = instance.Instance.TypeName;
                return true;
            case ObjectReferenceRowViewModel reference when reference.CanNavigate:
                address = reference.EndpointAddress;
                typeName = reference.EndpointTypeName;
                return true;
            case GcRootRowViewModel pathRow when pathRow.CanNavigate:
                address = pathRow.EndpointAddress;
                typeName = pathRow.EndpointTypeName;
                return true;
            default:
                address = 0;
                typeName = string.Empty;
                return false;
        }
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

    private void SetError(string? message)
    {
        if (SetProperty(ref _errorMessage, message, nameof(ErrorMessage)))
        {
            OnPropertyChanged(nameof(HasError));
            NotifyDisplayStateChanged();
        }
    }

    private void NotifyDisplayStateChanged()
    {
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoFilteredTypes));
        OnPropertyChanged(nameof(ShowTable));
    }

    // Reports service progress on the background thread; the callback routes
    // through the dispatcher and the load version, so stale progress can never
    // surface after a superseded computation.
    private sealed class DominatorProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
