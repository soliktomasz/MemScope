using System.Collections.ObjectModel;
using System.Windows.Input;
using MemoryProfiler.Contracts.Processes;
using MemoryProfiler.Diagnostics.Processes;

namespace MemoryProfiler.App.ViewModels;

public enum ProcessSortColumn
{
    ProcessId,
    Name,
    Runtime
}

public enum ProcessSortDirection
{
    Ascending,
    Descending
}

public sealed class ProcessPickerViewModel : ViewModelBase, IDisposable
{
    private readonly IDotNetProcessDiscovery _discovery;
    private readonly ObservableCollection<ProcessRowViewModel> _processes = [];
    private readonly AsyncCommand _refreshCommand;
    private readonly RelayCommand _sortByProcessIdCommand;
    private readonly RelayCommand _sortByNameCommand;
    private readonly RelayCommand _sortByRuntimeCommand;
    private readonly RefreshCoordinator _refreshCoordinator = new();
    private bool _isLoading;
    private string? _errorMessage;
    private ProcessRowViewModel? _selectedProcess;
    private ProcessSortColumn _sortColumn = ProcessSortColumn.ProcessId;
    private ProcessSortDirection _sortDirection = ProcessSortDirection.Ascending;
    private bool _isDisposed;

    public ProcessPickerViewModel(IDotNetProcessDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
        Processes = new ReadOnlyObservableCollection<ProcessRowViewModel>(_processes);
        _refreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsLoading);
        _sortByProcessIdCommand = new RelayCommand(
            () => SortBy(ProcessSortColumn.ProcessId),
            CanSort);
        _sortByNameCommand = new RelayCommand(
            () => SortBy(ProcessSortColumn.Name),
            CanSort);
        _sortByRuntimeCommand = new RelayCommand(
            () => SortBy(ProcessSortColumn.Runtime),
            CanSort);
    }

    public ReadOnlyObservableCollection<ProcessRowViewModel> Processes { get; }

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand SortByProcessIdCommand => _sortByProcessIdCommand;

    public ICommand SortByNameCommand => _sortByNameCommand;

    public ICommand SortByRuntimeCommand => _sortByRuntimeCommand;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowLoadingPlaceholders));
            NotifyCommandsChanged();
        }
    }

    public string ErrorMessage => _errorMessage ?? string.Empty;

    public bool HasError => _errorMessage is not null;

    public bool HasProcesses => _processes.Count > 0;

    public bool IsEmpty => !IsLoading && !HasError && !HasProcesses;

    public bool ShowLoadingPlaceholders => IsLoading && !HasProcesses;

    public ProcessRowViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public ProcessSortColumn SortColumn => _sortColumn;

    public ProcessSortDirection SortDirection => _sortDirection;

    public string ProcessIdSortDescription => GetSortDescription(
        ProcessSortColumn.ProcessId,
        "PID");

    public string ProcessIdHeader => GetSortHeader(ProcessSortColumn.ProcessId, "PID");

    public string NameSortDescription => GetSortDescription(
        ProcessSortColumn.Name,
        "Name");

    public string NameHeader => GetSortHeader(ProcessSortColumn.Name, "Name");

    public string RuntimeSortDescription => GetSortDescription(
        ProcessSortColumn.Runtime,
        "Runtime");

    public string RuntimeHeader => GetSortHeader(ProcessSortColumn.Runtime, "Runtime");

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        using var refresh = await _refreshCoordinator.BeginAsync(cancellationToken);
        await refresh.TryRunIfCurrentAsync(() =>
        {
            SetError(null);
            IsLoading = true;
        });

        try
        {
            var discovered = await _discovery
                .GetProcessesAsync(refresh.Token);
            await refresh.TryRunIfCurrentAsync(
                () => ReplaceProcesses(
                    discovered.Select(process => new ProcessRowViewModel(process))));
        }
        catch (OperationCanceledException) when (refresh.Token.IsCancellationRequested)
        {
            // Cancellation is an expected result when a refresh is replaced or the view closes.
        }
        catch (Exception exception)
        {
            await refresh.TryRunIfCurrentAsync(
                () => SetError($"Unable to discover .NET processes. {exception.Message}"));
        }
        finally
        {
            await refresh.TryRunIfCurrentAsync(() => IsLoading = false);
        }
    }

    public void SortBy(ProcessSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == ProcessSortDirection.Ascending
                ? ProcessSortDirection.Descending
                : ProcessSortDirection.Ascending;
        }
        else
        {
            _sortColumn = column;
            _sortDirection = ProcessSortDirection.Ascending;
        }

        ApplyCurrentSort();
        NotifySortChanged();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _refreshCoordinator.Dispose();
    }

    private bool CanSort() => HasProcesses && !IsLoading;

    private void ReplaceProcesses(IEnumerable<ProcessRowViewModel> processes)
    {
        _processes.Clear();
        foreach (var process in processes)
        {
            _processes.Add(process);
        }

        ApplyCurrentSort();
        SelectedProcess = null;
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingPlaceholders));
        NotifyCommandsChanged();
    }

    private void ApplyCurrentSort()
    {
        IEnumerable<ProcessRowViewModel> sorted = _sortColumn switch
        {
            ProcessSortColumn.ProcessId => SortByProcessId(),
            ProcessSortColumn.Name => SortByName(),
            ProcessSortColumn.Runtime => SortByRuntime(),
            _ => throw new ArgumentOutOfRangeException()
        };

        var snapshot = sorted.ToArray();
        for (var targetIndex = 0; targetIndex < snapshot.Length; targetIndex++)
        {
            var currentIndex = _processes.IndexOf(snapshot[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _processes.Move(currentIndex, targetIndex);
            }
        }
    }

    private IEnumerable<ProcessRowViewModel> SortByProcessId() =>
        _sortDirection == ProcessSortDirection.Ascending
            ? _processes.OrderBy(process => process.ProcessId)
            : _processes.OrderByDescending(process => process.ProcessId);

    private IEnumerable<ProcessRowViewModel> SortByName() =>
        _sortDirection == ProcessSortDirection.Ascending
            ? _processes
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId)
            : _processes
                .OrderByDescending(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId);

    private IEnumerable<ProcessRowViewModel> SortByRuntime()
    {
        var knownRuntimesFirst = _processes
            .OrderBy(process => process.ParsedRuntimeVersion is null);

        return _sortDirection == ProcessSortDirection.Ascending
            ? knownRuntimesFirst
                .ThenBy(process => process.ParsedRuntimeVersion)
                .ThenBy(process => process.ProcessId)
            : knownRuntimesFirst
                .ThenByDescending(process => process.ParsedRuntimeVersion)
                .ThenBy(process => process.ProcessId);
    }

    private void SetError(string? message)
    {
        if (!SetProperty(ref _errorMessage, message, nameof(ErrorMessage)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string GetSortDescription(ProcessSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {_sortDirection.ToString().ToLowerInvariant()}"
            : $"Sort by {label}";

    private string GetSortHeader(ProcessSortColumn column, string label) =>
        _sortColumn == column
            ? $"{label} {(_sortDirection == ProcessSortDirection.Ascending ? "↑" : "↓")}"
            : label;

    private void NotifySortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(ProcessIdSortDescription));
        OnPropertyChanged(nameof(ProcessIdHeader));
        OnPropertyChanged(nameof(NameSortDescription));
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(RuntimeSortDescription));
        OnPropertyChanged(nameof(RuntimeHeader));
    }

    private void NotifyCommandsChanged()
    {
        _refreshCommand.NotifyCanExecuteChanged();
        _sortByProcessIdCommand.NotifyCanExecuteChanged();
        _sortByNameCommand.NotifyCanExecuteChanged();
        _sortByRuntimeCommand.NotifyCanExecuteChanged();
    }
}
