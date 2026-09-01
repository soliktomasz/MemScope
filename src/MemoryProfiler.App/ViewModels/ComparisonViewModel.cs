using System.Globalization;
using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.App.Errors;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels.Comparison;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels;

public sealed class ComparisonViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHeapSnapshotLoader _loader;
    private readonly ISnapshotComparisonService _comparisonService;
    private readonly IDumpFilePicker _filePicker;
    private readonly IDominatorTreeService? _dominatorService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<Task>? _close;
    private readonly Func<string, string, Task>? _comparisonCompleted;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncCommand _closeCommand;
    private readonly AsyncCommand _pickBeforeCommand;
    private readonly AsyncCommand _pickAfterCommand;
    private readonly AsyncCommand _compareCommand;
    private string _beforePath = string.Empty;
    private string _afterPath = string.Empty;
    private CancellationTokenSource? _compareCancellation;
    private int _compareVersion;
    private bool _isLoading;
    private bool _isComputingRetainedSizes;
    private double _progress;
    private string _statusText = string.Empty;
    private ProfilerError? _error;
    private string? _retainedSizeNote;
    private bool _hasCompared;
    private int _disposed;

    internal ComparisonViewModel(
        IHeapSnapshotLoader loader,
        ISnapshotComparisonService comparisonService,
        IUiDispatcher uiDispatcher,
        IDumpFilePicker filePicker,
        Func<Task>? close = null,
        IDominatorTreeService? dominatorService = null,
        Func<string, string, Task>? comparisonCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(comparisonService);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(filePicker);
        _loader = loader;
        _comparisonService = comparisonService;
        _uiDispatcher = uiDispatcher;
        _filePicker = filePicker;
        _dominatorService = dominatorService;
        _close = close;
        _comparisonCompleted = comparisonCompleted;
        _closeCommand = new AsyncCommand(close ?? (() => Task.CompletedTask));
        _pickBeforeCommand = new AsyncCommand(() => PickBeforeAsync());
        _pickAfterCommand = new AsyncCommand(() => PickAfterAsync());
        _compareCommand = new AsyncCommand(
            () => CompareAsync(),
            () => HasBefore && HasAfter && !IsLoading);
        Table.PropertyChanged += OnTablePropertyChanged;
    }

    public ComparisonTableViewModel Table { get; } = new();

    public System.Windows.Input.ICommand CloseCommand => _closeCommand;

    public System.Windows.Input.ICommand PickBeforeCommand => _pickBeforeCommand;

    public System.Windows.Input.ICommand PickAfterCommand => _pickAfterCommand;

    public System.Windows.Input.ICommand CompareCommand => _compareCommand;

    public string BeforePath
    {
        get => _beforePath;
        private set
        {
            if (SetProperty(ref _beforePath, value))
            {
                OnPropertyChanged(nameof(HasBefore));
                _compareCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string AfterPath
    {
        get => _afterPath;
        private set
        {
            if (SetProperty(ref _afterPath, value))
            {
                OnPropertyChanged(nameof(HasAfter));
                _compareCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasBefore => _beforePath.Length > 0;

    public bool HasAfter => _afterPath.Length > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                _compareCommand.NotifyCanExecuteChanged();
                NotifyDisplayStateChanged();
            }
        }
    }

    public double Progress => _progress;

    public bool ShowProgress => IsLoading;

    public string StatusText
    {
        get
        {
            if (_isComputingRetainedSizes)
            {
                var percent = (int)Math.Round(_progress * 100);
                return $"Computing retained sizes… {percent.ToString("N0", CultureInfo.CurrentCulture)}%";
            }

            return _statusText;
        }
    }

    public ProfilerError? Error => _error;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public bool HasError => Error is not null;

    public bool ShowError => HasError;

    public string RetainedSizeNote => _retainedSizeNote ?? string.Empty;

    public bool HasRetainedSizeNote => _retainedSizeNote is not null;

    public bool HasCompared => _hasCompared;

    public bool ShowChoosePrompt => !HasCompared && !IsLoading && !HasError;

    public bool ShowTable => HasCompared && Table.HasFilteredDeltas;

    public bool ShowNoChanges => HasCompared && Table.HasNoDeltas;

    public bool ShowNoFilteredDeltas => HasCompared && Table.HasNoFilteredDeltas;

    public async Task PickBeforeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await PickPathAsync(
            pick: () => _filePicker.PickAsync(),
            setPath: path => BeforePath = path,
            cancellationToken: cancellationToken);
    }

    public async Task PickAfterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await PickPathAsync(
            pick: () => _filePicker.PickAsync(),
            setPath: path => AfterPath = path,
            cancellationToken: cancellationToken);
    }

    public async Task LoadAsync(
        string beforePath,
        string afterPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await PublishAsync(() =>
        {
            BeforePath = beforePath;
            AfterPath = afterPath;
        }).ConfigureAwait(false);
        await CompareAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompareAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var beforePath = _beforePath;
        var afterPath = _afterPath;
        if (beforePath.Length == 0 || afterPath.Length == 0)
        {
            return;
        }

        // A new comparison supersedes any in-flight one: bump the version so a
        // stale result can never publish, and cancel the previous computation.
        var version = Interlocked.Increment(ref _compareVersion);
        CancelCompare();
        var cancellation = new CancellationTokenSource();
        _compareCancellation = cancellation;
        CancellationTokenSource? linked = null;
        var token = CancellationToken.None;
        try
        {
            // Created inside the try so a disposed _disposeCancellation (during
            // view closure) surfaces as a handled error instead of escaping
            // CompareAsync; the composition and cleanup are unchanged.
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.Token,
                _disposeCancellation.Token,
                cancellationToken);
            token = linked.Token;
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                SetError(null);
                _retainedSizeNote = null;
                _hasCompared = false;
                _statusText = "Loading before snapshot…";
                _progress = 0.05;
                OnPropertyChanged(nameof(RetainedSizeNote));
                OnPropertyChanged(nameof(HasRetainedSizeNote));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(HasCompared));
                IsLoading = true;
                NotifyDisplayStateChanged();
            }).ConfigureAwait(false);

            var before = await _loader.LoadAsync(beforePath, token).ConfigureAwait(false);
            var after = await _loader.LoadAsync(afterPath, token).ConfigureAwait(false);

            if (_dominatorService is not null)
            {
                (before, after) = await ComputeRetainedSizesAsync(
                    version, before, after, token).ConfigureAwait(false);
            }

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                _statusText = "Comparing…";
                OnPropertyChanged(nameof(StatusText));
            }).ConfigureAwait(false);

            // The merge is a cheap in-memory pass; it runs on the thread-pool
            // continuation, never on the UI thread.
            var comparison = _comparisonService.Compare(before, after);

            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                Table.SetDeltas(comparison.Deltas);
                _hasCompared = true;
                _statusText = string.Empty;
                OnPropertyChanged(nameof(HasCompared));
                OnPropertyChanged(nameof(StatusText));
                NotifyDisplayStateChanged();
            }).ConfigureAwait(false);
            if (version == Volatile.Read(ref _compareVersion))
            {
                await NotifyComparisonCompletedAsync(beforePath, afterPath)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            await PublishAsync(() => SetError(ProfilerErrorFactory.Create(
                ProfilerOperation.CompareSnapshots,
                exception))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer comparison or the view closure superseded this one.
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                SetError(ProfilerErrorFactory.Create(
                    ProfilerOperation.CompareSnapshots,
                    exception));
            }).ConfigureAwait(false);
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                IsLoading = false;
                _statusText = string.Empty;
                OnPropertyChanged(nameof(StatusText));
            }).ConfigureAwait(false);
            linked?.Dispose();
            if (ReferenceEquals(_compareCancellation, cancellation))
            {
                _compareCancellation = null;
            }

            try
            {
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already released by CancelCompare.
            }
        }
    }

    private async Task NotifyComparisonCompletedAsync(
        string beforePath,
        string afterPath)
    {
        if (_comparisonCompleted is null)
        {
            return;
        }

        try
        {
            await _comparisonCompleted(beforePath, afterPath).ConfigureAwait(false);
        }
        catch
        {
            // Session-history persistence is best effort and must not turn a
            // successful comparison into an analysis failure.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Table.PropertyChanged -= OnTablePropertyChanged;
        try
        {
            _disposeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Nothing to cancel.
        }

        CancelCompare();
        _disposeCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    // Computes dominators for both snapshots (reusing the per-snapshot cache),
    // reports combined progress, and returns enriched copies whose type lists
    // carry retained sizes so the comparison can fill the Retained Δ column.
    // A failure here is non-fatal: the comparison completes with retained
    // deltas unavailable and a quiet note, mirroring the type browser.
    private async Task<(HeapSnapshot Before, HeapSnapshot After)> ComputeRetainedSizesAsync(
        int version,
        HeapSnapshot before,
        HeapSnapshot after,
        CancellationToken cancellationToken)
    {
        try
        {
            var beforeRetained = await _dominatorService!
                .ComputeDominatorsAsync(
                    before,
                    new DominatorProgress(value =>
                        PublishProgress(version, 0.30 + 0.30 * value)),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var afterRetained = await _dominatorService
                .ComputeDominatorsAsync(
                    after,
                    new DominatorProgress(value =>
                        PublishProgress(version, 0.60 + 0.30 * value)),
                    cancellationToken)
                .ConfigureAwait(false);
            return (
                WithRetainedSizes(before, beforeRetained.TypeRetainedSizes),
                WithRetainedSizes(after, afterRetained.TypeRetainedSizes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The failure is non-fatal: the comparison completes without
            // retained deltas (the column shows N/A) with a quiet note.
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                _retainedSizeNote = "Retained sizes unavailable.";
                OnPropertyChanged(nameof(RetainedSizeNote));
                OnPropertyChanged(nameof(HasRetainedSizeNote));
            }).ConfigureAwait(false);
            return (before, after);
        }
        finally
        {
            await PublishAsync(() =>
            {
                if (version != Volatile.Read(ref _compareVersion))
                {
                    return;
                }

                _isComputingRetainedSizes = false;
                OnPropertyChanged(nameof(StatusText));
            }).ConfigureAwait(false);
        }
    }

    private async Task PickPathAsync(
        Func<Task<string?>> pick,
        Action<string> setPath,
        CancellationToken cancellationToken)
    {
        string? path;
        try
        {
            path = await pick().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
                SetError(ProfilerErrorFactory.Create(
                    ProfilerOperation.ChooseFile,
                    exception))).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await PublishAsync(() => setPath(path)).ConfigureAwait(false);

        // Comparing needs both sides: when the second path arrives, start the
        // comparison immediately; the Compare button re-runs or retries.
        if (HasBefore && HasAfter)
        {
            _ = CompareAsync(cancellationToken);
        }
    }

    private void PublishProgress(int version, double value)
    {
        _ = PublishAsync(() =>
        {
            if (version != Volatile.Read(ref _compareVersion))
            {
                return;
            }

            _isComputingRetainedSizes = true;
            _progress = Math.Clamp(value, 0.0, 0.9);
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(StatusText));
        });
    }

    private static HeapSnapshot WithRetainedSizes(
        HeapSnapshot snapshot,
        IReadOnlyList<TypeRetainedSize> retainedSizes)
    {
        var byMethodTable = new Dictionary<ulong, ulong>(retainedSizes.Count);
        foreach (var retained in retainedSizes)
        {
            byMethodTable[retained.MethodTable] = retained.RetainedSize;
        }

        return new HeapSnapshot
        {
            Info = snapshot.Info,
            Types = snapshot.Types
                .Select(type =>
                {
                    ulong? retainedSize =
                        byMethodTable.TryGetValue(type.MethodTable, out var value)
                            ? value
                            : null;
                    return type with { RetainedSize = retainedSize };
                })
                .ToArray(),
        };
    }

    private void CancelCompare()
    {
        var cancellation = _compareCancellation;
        _compareCancellation = null;
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

    private void OnTablePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        NotifyDisplayStateChanged();
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
        if (SetProperty(ref _error, error, nameof(Error)))
        {
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
            NotifyDisplayStateChanged();
        }
    }

    private void NotifyDisplayStateChanged()
    {
        OnPropertyChanged(nameof(ShowChoosePrompt));
        OnPropertyChanged(nameof(ShowTable));
        OnPropertyChanged(nameof(ShowNoChanges));
        OnPropertyChanged(nameof(ShowNoFilteredDeltas));
    }

    // Reports service progress on the background thread; the callback routes
    // through the dispatcher and the compare version, so stale progress can
    // never surface after a superseded comparison.
    private sealed class DominatorProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
