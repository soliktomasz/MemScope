using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Types;

public enum TypeSortColumn
{
    TypeName,
    AssemblyName,
    ObjectCount,
    ShallowSize,
    RetainedSize
}

public enum TypeSortDirection
{
    Ascending,
    Descending
}

public sealed class TypeBrowserViewModel : ViewModelBase
{
    public const string AllAssemblies = "All assemblies";

    private readonly List<TypeRowViewModel> _types = [];
    private ObservableCollection<TypeRowViewModel> _filteredTypes = [];
    private ReadOnlyObservableCollection<TypeRowViewModel> _filteredTypesView;
    private IReadOnlyList<string> _assemblyFilters = [AllAssemblies];
    private string _selectedAssemblyFilter = AllAssemblies;
    private string _searchText = string.Empty;
    private string _minimumSizeText = string.Empty;
    private TypeSortColumn _sortColumn = TypeSortColumn.ShallowSize;
    private TypeSortDirection _sortDirection = TypeSortDirection.Descending;
    private TypeRowViewModel? _selectedType;
    private readonly RelayCommand _sortByTypeNameCommand;
    private readonly RelayCommand _sortByAssemblyCommand;
    private readonly RelayCommand _sortByCountCommand;
    private readonly RelayCommand _sortByShallowSizeCommand;
    private readonly RelayCommand _sortByRetainedSizeCommand;

    public TypeBrowserViewModel()
    {
        _filteredTypesView = new ReadOnlyObservableCollection<TypeRowViewModel>(_filteredTypes);
        _sortByTypeNameCommand = new RelayCommand(() => SortBy(TypeSortColumn.TypeName), CanSort);
        _sortByAssemblyCommand = new RelayCommand(() => SortBy(TypeSortColumn.AssemblyName), CanSort);
        _sortByCountCommand = new RelayCommand(() => SortBy(TypeSortColumn.ObjectCount), CanSort);
        _sortByShallowSizeCommand = new RelayCommand(() => SortBy(TypeSortColumn.ShallowSize), CanSort);
        _sortByRetainedSizeCommand = new RelayCommand(() => SortBy(TypeSortColumn.RetainedSize), CanSort);
    }

    public ReadOnlyObservableCollection<TypeRowViewModel> FilteredTypes => _filteredTypesView;

    public IReadOnlyList<string> AssemblyFilters => _assemblyFilters;

    public TypeRowViewModel? SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedType is not null;

    public int TotalTypeCount => _types.Count;

    public int FilteredTypeCount => _filteredTypes.Count;

    public bool HasTypes => TotalTypeCount > 0;

    public bool HasNoTypes => !HasTypes;

    public bool HasFilteredTypes => FilteredTypeCount > 0;

    public bool HasNoFilteredTypes => HasTypes && !HasFilteredTypes;

    public string ShownSummary =>
        HasTypes
            ? $"{FilteredTypeCount.ToString("N0", CultureInfo.CurrentCulture)} of {TotalTypeCount.ToString("N0", CultureInfo.CurrentCulture)} types"
            : string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public string MinimumSizeText
    {
        get => _minimumSizeText;
        set
        {
            if (SetProperty(ref _minimumSizeText, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public string SelectedAssemblyFilter
    {
        get => _selectedAssemblyFilter;
        set
        {
            var filter = _assemblyFilters.Contains(value) ? value : AllAssemblies;
            if (SetProperty(ref _selectedAssemblyFilter, filter))
            {
                Rebuild();
            }
        }
    }

    public TypeSortColumn SortColumn => _sortColumn;

    public TypeSortDirection SortDirection => _sortDirection;

    public ICommand SortByTypeNameCommand => _sortByTypeNameCommand;

    public ICommand SortByAssemblyCommand => _sortByAssemblyCommand;

    public ICommand SortByCountCommand => _sortByCountCommand;

    public ICommand SortByShallowSizeCommand => _sortByShallowSizeCommand;

    public ICommand SortByRetainedSizeCommand => _sortByRetainedSizeCommand;

    public string TypeNameSortDescription => GetSortDescription(TypeSortColumn.TypeName, "Type");

    public string AssemblySortDescription => GetSortDescription(TypeSortColumn.AssemblyName, "Assembly");

    public string CountSortDescription => GetSortDescription(TypeSortColumn.ObjectCount, "Count");

    public string ShallowSizeSortDescription => GetSortDescription(TypeSortColumn.ShallowSize, "Shallow size");

    public string RetainedSizeSortDescription => GetSortDescription(TypeSortColumn.RetainedSize, "Retained size");

    public string TypeNameHeader => GetSortHeader(TypeSortColumn.TypeName, "Type");

    public string AssemblyHeader => GetSortHeader(TypeSortColumn.AssemblyName, "Assembly");

    public string CountHeader => GetSortHeader(TypeSortColumn.ObjectCount, "Count");

    public string ShallowSizeHeader => GetSortHeader(TypeSortColumn.ShallowSize, "Shallow Size");

    public string RetainedSizeHeader => GetSortHeader(TypeSortColumn.RetainedSize, "Retained Size");

    public void SetTypes(IReadOnlyList<HeapTypeInfo> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types.Clear();
        foreach (var type in types)
        {
            _types.Add(new TypeRowViewModel(type));
        }

        _assemblyFilters = BuildAssemblyFilters();
        _selectedAssemblyFilter = AllAssemblies;
        _searchText = string.Empty;
        _minimumSizeText = string.Empty;
        _sortColumn = TypeSortColumn.ShallowSize;
        _sortDirection = TypeSortDirection.Descending;
        SelectedType = null;
        Rebuild();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(MinimumSizeText));
        OnPropertyChanged(nameof(SelectedAssemblyFilter));
        NotifySourceChanged();
    }

    // Fills in the retained sizes computed by the dominator analysis without
    // disturbing the search, assembly, or minimum-size filters or the current
    // selection: the type rows are updated in place and re-sorted only.
    public void SetRetainedSizes(IReadOnlyList<TypeRetainedSize> retainedSizes)
    {
        ArgumentNullException.ThrowIfNull(retainedSizes);
        var byMethodTable = new Dictionary<ulong, ulong>(retainedSizes.Count);
        foreach (var retained in retainedSizes)
        {
            byMethodTable[retained.MethodTable] = retained.RetainedSize;
        }

        foreach (var row in _types)
        {
            if (byMethodTable.TryGetValue(row.MethodTable, out var retainedSize))
            {
                row.SetRetainedSize(retainedSize);
            }
        }

        Rebuild();
    }

    public void SortBy(TypeSortColumn column)
    {
        if (!CanSort())
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == TypeSortDirection.Ascending
                ? TypeSortDirection.Descending
                : TypeSortDirection.Ascending;
        }
        else
        {
            _sortColumn = column;
            _sortDirection = TypeSortDirection.Ascending;
        }

        Rebuild();
        NotifySortChanged();
    }

    private bool CanSort() => HasTypes;

    private IReadOnlyList<string> BuildAssemblyFilters()
    {
        var names = _types
            .Select(row => row.AssemblyName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? [AllAssemblies] : [AllAssemblies, .. names];
    }

    private void Rebuild()
    {
        _filteredTypes = new ObservableCollection<TypeRowViewModel>(ApplySort(_types.Where(Matches)));
        _filteredTypesView = new ReadOnlyObservableCollection<TypeRowViewModel>(_filteredTypes);
        OnPropertyChanged(nameof(FilteredTypes));

        if (SelectedType is not null && !_filteredTypes.Contains(SelectedType))
        {
            SelectedType = null;
        }

        OnPropertyChanged(nameof(FilteredTypeCount));
        OnPropertyChanged(nameof(HasFilteredTypes));
        OnPropertyChanged(nameof(HasNoFilteredTypes));
        OnPropertyChanged(nameof(ShownSummary));
    }

    private bool Matches(TypeRowViewModel row)
    {
        if (SelectedAssemblyFilter != AllAssemblies &&
            !string.Equals(
                row.AssemblyName,
                SelectedAssemblyFilter,
                StringComparison.Ordinal))
        {
            return false;
        }

        var search = _searchText.Trim();
        if (search.Length > 0 &&
            row.TypeName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (SizeParsing.TryParseBytes(_minimumSizeText, out var minimumSize) &&
            row.Type.ShallowSize < minimumSize)
        {
            return false;
        }

        return true;
    }

    private IEnumerable<TypeRowViewModel> ApplySort(IEnumerable<TypeRowViewModel> rows)
    {
        var ascending = _sortDirection == TypeSortDirection.Ascending;
        return _sortColumn switch
        {
            TypeSortColumn.TypeName => ascending
                ? rows
                    .OrderBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.MethodTable)
                : rows
                    .OrderByDescending(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.MethodTable),
            TypeSortColumn.AssemblyName => ascending
                ? rows
                    .OrderBy(row => row.AssemblyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                : rows
                    .OrderByDescending(row => row.AssemblyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase),
            TypeSortColumn.ObjectCount => ascending
                ? rows
                    .OrderBy(row => row.Type.ObjectCount)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                : rows
                    .OrderByDescending(row => row.Type.ObjectCount)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase),
            TypeSortColumn.ShallowSize => ascending
                ? rows
                    .OrderBy(row => row.Type.ShallowSize)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                : rows
                    .OrderByDescending(row => row.Type.ShallowSize)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase),
            TypeSortColumn.RetainedSize => ascending
                ? rows
                    .OrderBy(row => row.RetainedSize is null)
                    .ThenBy(row => row.RetainedSize)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase)
                : rows
                    .OrderBy(row => row.RetainedSize is null)
                    .ThenByDescending(row => row.RetainedSize)
                    .ThenBy(row => row.TypeName, StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(_sortColumn))
        };
    }

    private string GetSortDescription(TypeSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {_sortDirection.ToString().ToLowerInvariant()}"
            : $"Sort by {label}";

    private string GetSortHeader(TypeSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {(_sortDirection == TypeSortDirection.Ascending ? "↑" : "↓")}"
            : label;

    private void NotifySourceChanged()
    {
        OnPropertyChanged(nameof(AssemblyFilters));
        OnPropertyChanged(nameof(SelectedAssemblyFilter));
        OnPropertyChanged(nameof(TotalTypeCount));
        OnPropertyChanged(nameof(HasTypes));
        OnPropertyChanged(nameof(HasNoTypes));
        OnPropertyChanged(nameof(ShownSummary));
        NotifySortChanged();
    }

    private void NotifySortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(TypeNameSortDescription));
        OnPropertyChanged(nameof(TypeNameHeader));
        OnPropertyChanged(nameof(AssemblySortDescription));
        OnPropertyChanged(nameof(AssemblyHeader));
        OnPropertyChanged(nameof(CountSortDescription));
        OnPropertyChanged(nameof(CountHeader));
        OnPropertyChanged(nameof(ShallowSizeSortDescription));
        OnPropertyChanged(nameof(ShallowSizeHeader));
        OnPropertyChanged(nameof(RetainedSizeSortDescription));
        OnPropertyChanged(nameof(RetainedSizeHeader));
    }
}
