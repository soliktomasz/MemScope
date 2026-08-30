using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MemoryProfiler.App.ViewModels.Types;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Comparison;

public enum DeltaSortColumn
{
    TypeName,
    CountDelta,
    SizeDelta,
    RetainedDelta
}

public enum DeltaSortDirection
{
    Ascending,
    Descending
}

public sealed class ComparisonTableViewModel : ViewModelBase
{
    private readonly List<TypeDeltaRowViewModel> _deltas = [];
    private ObservableCollection<TypeDeltaRowViewModel> _filteredDeltas = [];
    private ReadOnlyObservableCollection<TypeDeltaRowViewModel> _filteredDeltasView;
    private bool _showGrowingOnly;
    private bool _showNewTypes;
    private bool _showDisappearedTypes;
    private string _minimumDeltaText = string.Empty;
    private DeltaSortColumn _sortColumn = DeltaSortColumn.SizeDelta;
    private DeltaSortDirection _sortDirection = DeltaSortDirection.Descending;
    private readonly RelayCommand _sortByTypeNameCommand;
    private readonly RelayCommand _sortByCountDeltaCommand;
    private readonly RelayCommand _sortBySizeDeltaCommand;
    private readonly RelayCommand _sortByRetainedDeltaCommand;

    public ComparisonTableViewModel()
    {
        _filteredDeltasView = new ReadOnlyObservableCollection<TypeDeltaRowViewModel>(_filteredDeltas);
        _sortByTypeNameCommand = new RelayCommand(() => SortBy(DeltaSortColumn.TypeName), CanSort);
        _sortByCountDeltaCommand = new RelayCommand(() => SortBy(DeltaSortColumn.CountDelta), CanSort);
        _sortBySizeDeltaCommand = new RelayCommand(() => SortBy(DeltaSortColumn.SizeDelta), CanSort);
        _sortByRetainedDeltaCommand = new RelayCommand(() => SortBy(DeltaSortColumn.RetainedDelta), CanSort);
    }

    public ReadOnlyObservableCollection<TypeDeltaRowViewModel> FilteredDeltas => _filteredDeltasView;

    public int TotalDeltaCount => _deltas.Count;

    public int FilteredDeltaCount => _filteredDeltas.Count;

    public bool HasDeltas => TotalDeltaCount > 0;

    public bool HasNoDeltas => !HasDeltas;

    public bool HasFilteredDeltas => FilteredDeltaCount > 0;

    public bool HasNoFilteredDeltas => HasDeltas && !HasFilteredDeltas;

    public string ShownSummary =>
        HasDeltas
            ? $"{FilteredDeltaCount.ToString("N0", CultureInfo.CurrentCulture)} of {TotalDeltaCount.ToString("N0", CultureInfo.CurrentCulture)} types"
            : string.Empty;

    public bool ShowGrowingOnly
    {
        get => _showGrowingOnly;
        set
        {
            if (SetProperty(ref _showGrowingOnly, value))
            {
                Rebuild();
            }
        }
    }

    public bool ShowNewTypes
    {
        get => _showNewTypes;
        set
        {
            if (SetProperty(ref _showNewTypes, value))
            {
                Rebuild();
            }
        }
    }

    public bool ShowDisappearedTypes
    {
        get => _showDisappearedTypes;
        set
        {
            if (SetProperty(ref _showDisappearedTypes, value))
            {
                Rebuild();
            }
        }
    }

    public string MinimumDeltaText
    {
        get => _minimumDeltaText;
        set
        {
            if (SetProperty(ref _minimumDeltaText, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public DeltaSortColumn SortColumn => _sortColumn;

    public DeltaSortDirection SortDirection => _sortDirection;

    public ICommand SortByTypeNameCommand => _sortByTypeNameCommand;

    public ICommand SortByCountDeltaCommand => _sortByCountDeltaCommand;

    public ICommand SortBySizeDeltaCommand => _sortBySizeDeltaCommand;

    public ICommand SortByRetainedDeltaCommand => _sortByRetainedDeltaCommand;

    public string TypeNameSortDescription => GetSortDescription(DeltaSortColumn.TypeName, "Type");

    public string CountDeltaSortDescription => GetSortDescription(DeltaSortColumn.CountDelta, "Count delta");

    public string SizeDeltaSortDescription => GetSortDescription(DeltaSortColumn.SizeDelta, "Size delta");

    public string RetainedDeltaSortDescription => GetSortDescription(DeltaSortColumn.RetainedDelta, "Retained delta");

    public string TypeNameHeader => GetSortHeader(DeltaSortColumn.TypeName, "Type");

    public string CountDeltaHeader => GetSortHeader(DeltaSortColumn.CountDelta, "Count Δ");

    public string SizeDeltaHeader => GetSortHeader(DeltaSortColumn.SizeDelta, "Size Δ");

    public string RetainedDeltaHeader => GetSortHeader(DeltaSortColumn.RetainedDelta, "Retained Δ");

    public void SetDeltas(IReadOnlyList<TypeMemoryDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        _deltas.Clear();
        foreach (var delta in deltas)
        {
            _deltas.Add(new TypeDeltaRowViewModel(delta));
        }

        // A new comparison starts from the defaults: biggest growth first, no
        // filters active.
        _showGrowingOnly = false;
        _showNewTypes = false;
        _showDisappearedTypes = false;
        _minimumDeltaText = string.Empty;
        _sortColumn = DeltaSortColumn.SizeDelta;
        _sortDirection = DeltaSortDirection.Descending;
        Rebuild();
        OnPropertyChanged(nameof(ShowGrowingOnly));
        OnPropertyChanged(nameof(ShowNewTypes));
        OnPropertyChanged(nameof(ShowDisappearedTypes));
        OnPropertyChanged(nameof(MinimumDeltaText));
        NotifySourceChanged();
    }

    public void SortBy(DeltaSortColumn column)
    {
        if (!CanSort())
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == DeltaSortDirection.Ascending
                ? DeltaSortDirection.Descending
                : DeltaSortDirection.Ascending;
        }
        else
        {
            _sortColumn = column;
            _sortDirection = DeltaSortDirection.Ascending;
        }

        Rebuild();
        NotifySortChanged();
    }

    private bool CanSort() => HasDeltas;

    private void Rebuild()
    {
        _filteredDeltas = new ObservableCollection<TypeDeltaRowViewModel>(ApplySort(_deltas.Where(Matches)));
        _filteredDeltasView = new ReadOnlyObservableCollection<TypeDeltaRowViewModel>(_filteredDeltas);
        OnPropertyChanged(nameof(FilteredDeltas));
        OnPropertyChanged(nameof(FilteredDeltaCount));
        OnPropertyChanged(nameof(HasFilteredDeltas));
        OnPropertyChanged(nameof(HasNoFilteredDeltas));
        OnPropertyChanged(nameof(ShownSummary));
    }

    private bool Matches(TypeDeltaRowViewModel row)
    {
        if (_showGrowingOnly && row.SizeDelta <= 0)
        {
            return false;
        }

        if (_showNewTypes && row.CountBefore != 0)
        {
            return false;
        }

        if (_showDisappearedTypes && row.CountAfter != 0)
        {
            return false;
        }

        if (SizeParsing.TryParseBytes(_minimumDeltaText, out var minimumDelta))
        {
            // Hide types whose size changed by less than the threshold; the
            // parsed value is capped so the long comparisons cannot wrap.
            var threshold = Math.Min(minimumDelta, (ulong)long.MaxValue);
            if (row.SizeDelta >= 0
                ? row.SizeDelta < (long)threshold
                : row.SizeDelta > -(long)threshold)
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerable<TypeDeltaRowViewModel> ApplySort(IEnumerable<TypeDeltaRowViewModel> rows)
    {
        var ascending = _sortDirection == DeltaSortDirection.Ascending;
        return _sortColumn switch
        {
            DeltaSortColumn.TypeName => ascending
                ? rows
                    .OrderBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal)
                : rows
                    .OrderByDescending(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal),
            DeltaSortColumn.CountDelta => ascending
                ? rows
                    .OrderBy(row => row.CountDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal)
                : rows
                    .OrderByDescending(row => row.CountDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal),
            DeltaSortColumn.SizeDelta => ascending
                ? rows
                    .OrderBy(row => row.SizeDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal)
                : rows
                    .OrderByDescending(row => row.SizeDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal),
            DeltaSortColumn.RetainedDelta => ascending
                ? rows
                    .OrderBy(row => row.RetainedDelta is null)
                    .ThenBy(row => row.RetainedDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal)
                : rows
                    .OrderBy(row => row.RetainedDelta is null)
                    .ThenByDescending(row => row.RetainedDelta)
                    .ThenBy(row => row.TypeName, StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(_sortColumn))
        };
    }

    private string GetSortDescription(DeltaSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {_sortDirection.ToString().ToLowerInvariant()}"
            : $"Sort by {label}";

    private string GetSortHeader(DeltaSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {(_sortDirection == DeltaSortDirection.Ascending ? "↑" : "↓")}"
            : label;

    private void NotifySourceChanged()
    {
        OnPropertyChanged(nameof(TotalDeltaCount));
        OnPropertyChanged(nameof(HasDeltas));
        OnPropertyChanged(nameof(HasNoDeltas));
        OnPropertyChanged(nameof(ShownSummary));
        // The sort commands gate on HasDeltas, so a source replacement must
        // re-query their CanExecute state.
        _sortByTypeNameCommand.NotifyCanExecuteChanged();
        _sortByCountDeltaCommand.NotifyCanExecuteChanged();
        _sortBySizeDeltaCommand.NotifyCanExecuteChanged();
        _sortByRetainedDeltaCommand.NotifyCanExecuteChanged();
        NotifySortChanged();
    }

    private void NotifySortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(TypeNameSortDescription));
        OnPropertyChanged(nameof(TypeNameHeader));
        OnPropertyChanged(nameof(CountDeltaSortDescription));
        OnPropertyChanged(nameof(CountDeltaHeader));
        OnPropertyChanged(nameof(SizeDeltaSortDescription));
        OnPropertyChanged(nameof(SizeDeltaHeader));
        OnPropertyChanged(nameof(RetainedDeltaSortDescription));
        OnPropertyChanged(nameof(RetainedDeltaHeader));
    }
}
