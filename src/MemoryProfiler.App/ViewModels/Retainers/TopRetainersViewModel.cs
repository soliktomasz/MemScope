using System.Collections.ObjectModel;
using System.Windows.Input;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Retainers;

public sealed class TopRetainersViewModel : ViewModelBase, IAsyncDisposable
{
    public const int WindowSize = 500;

    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncCommand _applySearchCommand;
    private readonly RelayCommand _loadMoreCommand;
    private ObservableCollection<TopRetainerRowViewModel> _rows = [];
    private ReadOnlyObservableCollection<TopRetainerRowViewModel> _rowsView;
    private IReadOnlyList<DominatorInfo> _source = [];
    private IReadOnlyList<DominatorInfo> _filtered = [];
    private DominatorAnalysisResult? _result;
    private int _materializedCount;
    private ulong _totalReachableBytes;
    private string _searchText = string.Empty;
    private TopRetainerRowViewModel? _selectedRetainer;
    private CancellationTokenSource? _searchCancellation;
    private bool _isLoading;
    private bool _isUnavailable;
    private bool _hasResult;
    private int _version;
    private int _disposed;

    internal TopRetainersViewModel(IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _uiDispatcher = uiDispatcher;
        _rowsView = new ReadOnlyObservableCollection<TopRetainerRowViewModel>(_rows);
        _applySearchCommand = new AsyncCommand(() => ApplySearchAsync(), () => _hasResult);
        _loadMoreCommand = new RelayCommand(LoadMore, () => CanLoadMore);
    }

    public ReadOnlyObservableCollection<TopRetainerRowViewModel> Retainers => _rowsView;

    public TopRetainerRowViewModel? SelectedRetainer
    {
        get => _selectedRetainer;
        set => SetProperty(ref _selectedRetainer, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? string.Empty);
    }

    public ICommand ApplySearchCommand => _applySearchCommand;

    public ICommand LoadMoreCommand => _loadMoreCommand;

    public int TotalRetainerCount => _result?.Dominators.Count ?? 0;

    public int VisibleRetainerCount => _rows.Count;

    public bool IsLoading => _isLoading;

    public bool IsUnavailable => _isUnavailable;

    public bool HasResult => _hasResult;

    public bool ShowLoading => _isLoading;

    public bool ShowUnavailable => _isUnavailable;

    public bool ShowIdle => !_hasResult && !_isLoading && !_isUnavailable;

    public bool ShowEmpty => _hasResult && !_isLoading && !_isUnavailable && _rows.Count == 0;

    public bool ShowTable => _hasResult && !_isLoading && !_isUnavailable && _rows.Count > 0;

    public bool CanLoadMore => _hasResult && _filtered.Count > _materializedCount;

    public async Task BeginLoadingAsync()
    {
        CancelSearch();
        Interlocked.Increment(ref _version);
        await PublishAsync(() =>
        {
            _isLoading = true;
            _isUnavailable = false;
            _hasResult = false;
            _result = null;
            _source = [];
            _filtered = [];
            _materializedCount = 0;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            SelectedRetainer = null;
            ReplaceRows([]);
            NotifyStateChanged();
        }).ConfigureAwait(false);
    }

    public async Task SetResultAsync(
        DominatorAnalysisResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        _totalReachableBytes = ComputeReachableBytes(result);
        await PublishAsync(() =>
        {
            _result = result;
            _source = result.Dominators;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            SelectedRetainer = null;
        }).ConfigureAwait(false);

        await ApplySearchCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetUnavailableAsync()
    {
        CancelSearch();
        Interlocked.Increment(ref _version);
        await PublishAsync(() =>
        {
            _isLoading = false;
            _isUnavailable = true;
            _hasResult = false;
            _result = null;
            _source = [];
            _filtered = [];
            _materializedCount = 0;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            SelectedRetainer = null;
            ReplaceRows([]);
            NotifyStateChanged();
        }).ConfigureAwait(false);
    }

    public async Task ClearAsync()
    {
        CancelSearch();
        Interlocked.Increment(ref _version);
        await PublishAsync(() =>
        {
            _isLoading = false;
            _isUnavailable = false;
            _hasResult = false;
            _result = null;
            _source = [];
            _filtered = [];
            _materializedCount = 0;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            SelectedRetainer = null;
            ReplaceRows([]);
            NotifyStateChanged();
        }).ConfigureAwait(false);
    }

    public Task ApplySearchAsync(CancellationToken cancellationToken = default) =>
        ApplySearchCoreAsync(cancellationToken);

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

        CancelSearch();
        _disposeCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ApplySearchCoreAsync(CancellationToken cancellationToken)
    {
        CancelSearch();
        var version = Interlocked.Increment(ref _version);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var token = linked.Token;
        _searchCancellation = linked;

        var source = _source;
        var search = _searchText.Trim();

        try
        {
            var filtered = await Task.Run(
                () => Filter(source, search, token),
                token).ConfigureAwait(false);
            var materialized = Math.Min(WindowSize, filtered.Count);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _version))
                {
                    return;
                }

                _filtered = filtered;
                _materializedCount = materialized;
                _hasResult = true;
                _isLoading = false;
                _isUnavailable = false;
                ReplaceRows(BuildRows(filtered, materialized));
                NotifyStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search, clear, or disposal superseded this one.
        }
        finally
        {
            try
            {
                linked.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already released by CancelSearch.
            }

            if (ReferenceEquals(_searchCancellation, linked))
            {
                _searchCancellation = null;
            }
        }
    }

    private void LoadMore()
    {
        if (!CanLoadMore)
        {
            return;
        }

        _materializedCount = Math.Min(_filtered.Count, _materializedCount + WindowSize);
        ReplaceRows(BuildRows(_filtered, _materializedCount));
        NotifyStateChanged();
    }

    private void ReplaceRows(IEnumerable<TopRetainerRowViewModel> rows)
    {
        _rows = new ObservableCollection<TopRetainerRowViewModel>(rows);
        _rowsView = new ReadOnlyObservableCollection<TopRetainerRowViewModel>(_rows);
        OnPropertyChanged(nameof(Retainers));
        OnPropertyChanged(nameof(VisibleRetainerCount));
    }

    private List<TopRetainerRowViewModel> BuildRows(IReadOnlyList<DominatorInfo> filtered, int count)
    {
        var max = Math.Min(count, filtered.Count);
        var rows = new List<TopRetainerRowViewModel>(max);
        for (var index = 0; index < max; index++)
        {
            rows.Add(new TopRetainerRowViewModel(filtered[index], _totalReachableBytes));
        }

        return rows;
    }

    private static IReadOnlyList<DominatorInfo> Filter(
        IReadOnlyList<DominatorInfo> source,
        string search,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return source;
        }

        var matches = new List<DominatorInfo>();
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(item, search))
            {
                matches.Add(item);
            }
        }

        return matches;
    }

    private static bool Matches(DominatorInfo item, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return item.TypeName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               MetricFormatting.Address(item.ObjectAddress)
                   .Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ComputeReachableBytes(DominatorAnalysisResult result)
    {
        ulong total = 0;
        foreach (var dominator in result.Dominators)
        {
            checked
            {
                total += dominator.ShallowSize;
            }
        }

        return total;
    }

    private void CancelSearch()
    {
        var cancellation = _searchCancellation;
        _searchCancellation = null;
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

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowTable));
        OnPropertyChanged(nameof(TotalRetainerCount));
        OnPropertyChanged(nameof(CanLoadMore));
        _applySearchCommand.NotifyCanExecuteChanged();
        _loadMoreCommand.NotifyCanExecuteChanged();
    }
}
