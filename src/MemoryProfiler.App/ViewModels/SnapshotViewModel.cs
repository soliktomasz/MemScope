using System.ComponentModel;
using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.App.ViewModels.Types;

namespace MemoryProfiler.App.ViewModels;

public sealed class SnapshotViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHeapSnapshotLoader _loader;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly AsyncCommand _closeCommand;
    private readonly RelayCommand<object> _showOutgoingReferencesCommand;
    private readonly RelayCommand<object> _showIncomingReferencesCommand;
    private readonly RelayCommand<object> _showPathToRootCommand;
    private HeapSnapshot? _snapshot;
    private string? _errorMessage;
    private bool _isLoading;
    private int _disposed;

    internal SnapshotViewModel(
        IHeapSnapshotLoader loader,
        IHeapObjectRepository objectRepository,
        IObjectReferenceService referenceService,
        IGcRootService gcRootService,
        IUiDispatcher uiDispatcher,
        Func<Task>? close = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(objectRepository);
        ArgumentNullException.ThrowIfNull(referenceService);
        ArgumentNullException.ThrowIfNull(gcRootService);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _loader = loader;
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
        ObjectInstances = new ObjectInstancesViewModel(objectRepository, uiDispatcher);
        ObjectReferences = new ObjectReferencesViewModel(referenceService, uiDispatcher);
        GcRoots = new GcRootsViewModel(gcRootService, uiDispatcher);
        Types.PropertyChanged += OnTypesPropertyChanged;
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

        await PublishAsync(() =>
        {
            _snapshot = null;
            SetError(null);
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
            }).ConfigureAwait(false);
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
            _ = RefreshInstancesAsync();
        }

        NotifyDisplayStateChanged();
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

        _ = ShowPathToRootAsync(snapshot, typeName, address);
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

        _ = ShowReferencesAsync(snapshot, typeName, address, direction);
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
}
