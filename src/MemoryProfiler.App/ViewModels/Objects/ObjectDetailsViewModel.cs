using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Values;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class ObjectDetailsViewModel : ViewModelBase, IAsyncDisposable
{
    public const string SensitiveValueWarning =
        "Dump values may contain credentials, personal data, or other secrets.";

    private const int ArrayPageSize = 500;
    private const int DefaultStringLimit = 4096;
    private const int ExpandedStringLimit = 1_048_576;

    private readonly IHeapObjectValueService _service;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncCommand _loadNextArrayPageCommand;
    private readonly AsyncCommand _showMoreStringsCommand;
    private readonly RelayCommand _cancelCommand;
    private ObservableCollection<HeapFieldValueRowViewModel> _fields = [];
    private ReadOnlyObservableCollection<HeapFieldValueRowViewModel> _fieldsView;
    private CancellationTokenSource? _loadCancellation;
    private HeapSnapshot? _snapshot;
    private ulong _objectAddress;
    private string _objectTypeName = string.Empty;
    private string _objectGeneration = "Unknown";
    private ulong _shallowSize;
    private ulong _retainedSize;
    private long _retainedObjectCount;
    private ulong _totalReachableBytes;
    private bool _hasRetainedMetrics;
    private bool _isLoadingValues;
    private bool _hasMoreElements;
    private int _currentArrayOffset;
    private int _objectVersion;
    private int _disposed;
    private ProfilerError? _error;
    private HeapFieldValueRowViewModel? _selectedField;
    private DominatorAnalysisResult? _dominatorResult;

    internal ObjectDetailsViewModel(
        IHeapObjectValueService service,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _service = service;
        _uiDispatcher = uiDispatcher;
        _fieldsView = new ReadOnlyObservableCollection<HeapFieldValueRowViewModel>(_fields);
        _cancelCommand = new RelayCommand(CancelLoad, () => IsLoadingValues);
        _loadNextArrayPageCommand = new AsyncCommand(LoadNextArrayPageAsync, () => CanLoadNextArrayPage);
        _showMoreStringsCommand = new AsyncCommand(ShowMoreStringsAsync, () => CanShowMoreStrings);
    }

    public string SensitiveValuesWarning => SensitiveValueWarning;

    public ReadOnlyObservableCollection<HeapFieldValueRowViewModel> Fields => _fieldsView;

    public HeapFieldValueRowViewModel? SelectedField
    {
        get => _selectedField;
        set => SetProperty(ref _selectedField, value);
    }

    public ulong ObjectAddress => _objectAddress;

    public string TypeName => _objectTypeName;

    public string AddressDisplay =>
        _objectAddress == 0
            ? string.Empty
            : MetricFormatting.Address(_objectAddress);

    public string GenerationDisplay => _objectGeneration;

    public bool HasSnapshot => _snapshot is not null;

    public bool HasRetainedMetrics => _hasRetainedMetrics;

    public string ShallowSizeDisplay =>
        _hasRetainedMetrics ? MetricFormatting.Bytes(_shallowSize) : string.Empty;

    public string RetainedSizeDisplay =>
        _hasRetainedMetrics ? MetricFormatting.Bytes(_retainedSize) : string.Empty;

    public string RetainedObjectCountDisplay =>
        _hasRetainedMetrics ? MetricFormatting.Count(_retainedObjectCount) : string.Empty;

    public string RetainedPercentageDisplay =>
        !_hasRetainedMetrics || _totalReachableBytes == 0
            ? string.Empty
            : $"{((double)_retainedSize / _totalReachableBytes * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

    public bool IsLoadingValues => _isLoadingValues;

    public bool HasValues => _fields.Count > 0;

    public bool HasMoreElements => _hasMoreElements;

    public bool HasAnyTruncatedString => _fields.Any(row => row.IsTruncated);

    public bool ShowLoading => IsLoadingValues;

    public bool ShowEmpty => HasSnapshot && !IsLoadingValues && !HasError && _fields.Count == 0;

    public bool ShowTable => HasSnapshot && !IsLoadingValues && !HasError && _fields.Count > 0;

    public bool ShowError => HasError;

    public bool CanLoadNextArrayPage => HasSnapshot && HasMoreElements && !IsLoadingValues;

    public bool CanShowMoreStrings => HasSnapshot && HasAnyTruncatedString && !IsLoadingValues;

    public ICommand LoadNextArrayPageCommand => _loadNextArrayPageCommand;

    public ICommand ShowMoreStringsCommand => _showMoreStringsCommand;

    public ICommand CancelCommand => _cancelCommand;

    public ProfilerError? Error => _error;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public bool HasError => Error is not null;

    public async Task ShowAsync(
        HeapSnapshot snapshot,
        ulong objectAddress,
        string objectTypeName,
        DominatorAnalysisResult? dominatorResult,
        DominatorInfo? knownDominator,
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
        var version = Interlocked.Increment(ref _objectVersion);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var token = linked.Token;
        _loadCancellation = linked;

        await PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _objectVersion))
            {
                return;
            }

            _snapshot = snapshot;
            _objectAddress = objectAddress;
            _objectTypeName = objectTypeName;
            _objectGeneration = "Unknown";
            _shallowSize = 0;
            _retainedSize = 0;
            _retainedObjectCount = 0;
            _hasRetainedMetrics = false;
            _dominatorResult = dominatorResult;
            SetError(null);
            _isLoadingValues = true;
            _hasMoreElements = false;
            _currentArrayOffset = 0;
            _fields = [];
            SelectedField = null;
            _fieldsView = new ReadOnlyObservableCollection<HeapFieldValueRowViewModel>(_fields);
            OnPropertyChanged(nameof(Fields));
            NotifyHeaderChanged();
            NotifyValueStateChanged();
        }).ConfigureAwait(false);

        var retainedTask = ResolveDominatorAsync(
            dominatorResult,
            objectAddress,
            knownDominator,
            token);
        var valuesTask = _service.ReadAsync(
            snapshot,
            objectAddress,
            new ObjectValueReadOptions(
                ArrayOffset: 0,
                ArrayLimit: 500,
                StringLimit: DefaultStringLimit),
            token);

        try
        {
            var dominator = await retainedTask.ConfigureAwait(false);
            await PublishRetainedAsync(version, dominator).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Dominator metrics are optional; value loading owns the loading state.
        }

        try
        {
            var result = await valuesTask.ConfigureAwait(false);
            await PublishValuesAsync(version, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await PublishAsync(() =>
            {
                if (version == Volatile.Read(ref _objectVersion))
                {
                    _isLoadingValues = false;
                    NotifyValueStateChanged();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _objectVersion))
                {
                    return;
                }

                SetError(ProfilerErrorFactory.Create(
                    ProfilerOperation.AnalyzeSnapshot,
                    exception));
                _isLoadingValues = false;
                NotifyValueStateChanged();
            }).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync()
    {
        CancelLoad();
        Interlocked.Increment(ref _objectVersion);
        await PublishAsync(() =>
        {
            _snapshot = null;
            _objectAddress = 0;
            _objectTypeName = string.Empty;
            _objectGeneration = "Unknown";
            _shallowSize = 0;
            _retainedSize = 0;
            _retainedObjectCount = 0;
            _totalReachableBytes = 0;
            _hasRetainedMetrics = false;
            SetError(null);
            _isLoadingValues = false;
            _hasMoreElements = false;
            _currentArrayOffset = 0;
            _fields = [];
            SelectedField = null;
            _fieldsView = new ReadOnlyObservableCollection<HeapFieldValueRowViewModel>(_fields);
            OnPropertyChanged(nameof(Fields));
            NotifyHeaderChanged();
            NotifyValueStateChanged();
        }).ConfigureAwait(false);
    }

    internal async Task SetDominatorResultAsync(DominatorAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var version = Volatile.Read(ref _objectVersion);
        await PublishAsync(() =>
        {
            if (version == Volatile.Read(ref _objectVersion))
            {
                _dominatorResult = result;
            }
        }).ConfigureAwait(false);

        if (version == Volatile.Read(ref _objectVersion))
        {
            var dominator = result.Dominators.FirstOrDefault(
                item => item.ObjectAddress == _objectAddress);
            await PublishRetainedAsync(version, dominator).ConfigureAwait(false);
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

    private static Task<DominatorInfo?> ResolveDominatorAsync(
        DominatorAnalysisResult? dominatorResult,
        ulong objectAddress,
        DominatorInfo? knownDominator,
        CancellationToken cancellationToken)
    {
        if (knownDominator is not null)
        {
            return Task.FromResult<DominatorInfo?>(knownDominator);
        }

        if (dominatorResult is null)
        {
            return Task.FromResult<DominatorInfo?>(null);
        }

        return Task.Run(
            () => dominatorResult.Dominators
                .FirstOrDefault(item => item.ObjectAddress == objectAddress),
            cancellationToken);
    }

    private async Task PublishRetainedAsync(int version, DominatorInfo? dominator)
    {
        dominator ??= _dominatorResult?.Dominators.FirstOrDefault(
            item => item.ObjectAddress == _objectAddress);
        ulong totalReachable = 0;
        if (_dominatorResult is not null)
        {
            foreach (var item in _dominatorResult.Dominators)
            {
                checked
                {
                    totalReachable += item.ShallowSize;
                }
            }
        }

        await PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _objectVersion))
            {
                return;
            }

            _totalReachableBytes = totalReachable;
            if (dominator is null)
            {
                _hasRetainedMetrics = false;
                _shallowSize = 0;
                _retainedSize = 0;
                _retainedObjectCount = 0;
            }
            else
            {
                _hasRetainedMetrics = true;
                _shallowSize = dominator.ShallowSize;
                _retainedSize = dominator.RetainedSize;
                _retainedObjectCount = dominator.RetainedObjectCount;
            }

            NotifyHeaderChanged();
        }).ConfigureAwait(false);
    }

    private async Task PublishValuesAsync(int version, HeapObjectValueResult result)
    {
        await PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _objectVersion))
            {
                return;
            }

            _objectAddress = result.Object.Address;
            _objectTypeName = result.Object.TypeName;
            _objectGeneration = result.Object.Generation;
            _shallowSize = result.Object.Size;
            _currentArrayOffset = result.Fields.Count;
            _hasMoreElements = result.HasMoreElements;
            var rows = result.Fields
                .Select(field => new HeapFieldValueRowViewModel(field))
                .ToList();
            _fields = new ObservableCollection<HeapFieldValueRowViewModel>(rows);
            _fieldsView = new ReadOnlyObservableCollection<HeapFieldValueRowViewModel>(_fields);
            _isLoadingValues = false;
            SetError(null);
            OnPropertyChanged(nameof(Fields));
            NotifyHeaderChanged();
            NotifyValueStateChanged();
        }).ConfigureAwait(false);
    }

    private async Task LoadNextArrayPageAsync()
    {
        var snapshot = _snapshot;
        var address = _objectAddress;
        if (snapshot is null || address == 0)
        {
            return;
        }

        var version = Volatile.Read(ref _objectVersion);
        var nextOffset = _currentArrayOffset;
        var token = _loadCancellation?.Token ?? CancellationToken.None;
        await PublishAsync(() =>
        {
            if (version == Volatile.Read(ref _objectVersion))
            {
                _isLoadingValues = true;
                NotifyValueStateChanged();
            }
        }).ConfigureAwait(false);
        try
        {
            var result = await _service.ReadAsync(
                snapshot,
                address,
                new ObjectValueReadOptions(
                    ArrayOffset: nextOffset,
                    ArrayLimit: ArrayPageSize,
                    StringLimit: DefaultStringLimit), token).ConfigureAwait(false);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _objectVersion) ||
                    result.Object.Address != _objectAddress)
                {
                    return;
                }

                foreach (var field in result.Fields)
                {
                    _fields.Add(new HeapFieldValueRowViewModel(field));
                }

                _currentArrayOffset += result.Fields.Count;
                _hasMoreElements = result.HasMoreElements;
                NotifyValueStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer selection or disposal superseded this page.
        }
        catch (ObjectDisposedException)
        {
            // The pane was disposed while the page was loading.
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version == Volatile.Read(ref _objectVersion))
                {
                    _isLoadingValues = false;
                    NotifyValueStateChanged();
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task ShowMoreStringsAsync()
    {
        var snapshot = _snapshot;
        var address = _objectAddress;
        if (snapshot is null || address == 0)
        {
            return;
        }

        var version = Volatile.Read(ref _objectVersion);
        var token = _loadCancellation?.Token ?? CancellationToken.None;
        await PublishAsync(() =>
        {
            if (version == Volatile.Read(ref _objectVersion))
            {
                _isLoadingValues = true;
                NotifyValueStateChanged();
            }
        }).ConfigureAwait(false);
        try
        {
            var result = await _service.ReadAsync(
                snapshot,
                address,
                new ObjectValueReadOptions(
                    ArrayOffset: 0,
                    ArrayLimit: ArrayPageSize,
                    StringLimit: ExpandedStringLimit), token).ConfigureAwait(false);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _objectVersion) ||
                    result.Object.Address != _objectAddress)
                {
                    return;
                }

                var rows = result.Fields
                    .Select(field => new HeapFieldValueRowViewModel(field))
                    .ToList();
                _fields = new ObservableCollection<HeapFieldValueRowViewModel>(rows);
                _fieldsView = new ReadOnlyObservableCollection<HeapFieldValueRowViewModel>(_fields);
                _currentArrayOffset = result.Fields.Count;
                _hasMoreElements = result.HasMoreElements;
                OnPropertyChanged(nameof(Fields));
                NotifyValueStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer selection or disposal superseded this reload.
        }
        catch (ObjectDisposedException)
        {
            // The pane was disposed while the strings were being reloaded.
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version == Volatile.Read(ref _objectVersion))
                {
                    _isLoadingValues = false;
                    NotifyValueStateChanged();
                }
            }).ConfigureAwait(false);
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
        OnPropertyChanged(nameof(ShowError));
    }

    private void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(ObjectAddress));
        OnPropertyChanged(nameof(TypeName));
        OnPropertyChanged(nameof(AddressDisplay));
        OnPropertyChanged(nameof(GenerationDisplay));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(HasRetainedMetrics));
        OnPropertyChanged(nameof(ShallowSizeDisplay));
        OnPropertyChanged(nameof(RetainedSizeDisplay));
        OnPropertyChanged(nameof(RetainedObjectCountDisplay));
        OnPropertyChanged(nameof(RetainedPercentageDisplay));
    }

    private void NotifyValueStateChanged()
    {
        OnPropertyChanged(nameof(IsLoadingValues));
        OnPropertyChanged(nameof(HasValues));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowTable));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(HasMoreElements));
        OnPropertyChanged(nameof(HasAnyTruncatedString));
        OnPropertyChanged(nameof(CanLoadNextArrayPage));
        OnPropertyChanged(nameof(CanShowMoreStrings));
        _cancelCommand.NotifyCanExecuteChanged();
        _loadNextArrayPageCommand.NotifyCanExecuteChanged();
        _showMoreStringsCommand.NotifyCanExecuteChanged();
    }
}
