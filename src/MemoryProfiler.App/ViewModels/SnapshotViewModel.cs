using System.Globalization;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.App.ViewModels.Types;

namespace MemoryProfiler.App.ViewModels;

public sealed class SnapshotViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHeapSnapshotLoader _loader;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly AsyncCommand _closeCommand;
    private HeapSnapshot? _snapshot;
    private string? _errorMessage;
    private bool _isLoading;
    private int _disposed;

    internal SnapshotViewModel(
        IHeapSnapshotLoader loader,
        IUiDispatcher uiDispatcher,
        Func<Task>? close = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _loader = loader;
        _uiDispatcher = uiDispatcher;
        _closeCommand = new AsyncCommand(close ?? (() => Task.CompletedTask));
        Types.PropertyChanged += (_, _) => NotifyDisplayStateChanged();
    }

    public TypeBrowserViewModel Types { get; } = new();

    public System.Windows.Input.ICommand CloseCommand => _closeCommand;

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
        return ValueTask.CompletedTask;
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
