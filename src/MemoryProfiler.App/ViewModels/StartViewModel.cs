using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MemoryProfiler.Analysis.Comparison;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.Errors;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Sessions;
using MemoryProfiler.Contracts.Heap;
using MemoryProfiler.Storage.Storage;

namespace MemoryProfiler.App.ViewModels;

public sealed class StartViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly ILiveDiagnosticsSessionFactory _sessionFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IDumpCaptureService? _dumpCaptureService;
    private readonly IDumpDestinationPicker? _dumpDestinationPicker;
    private readonly IHeapSnapshotLoader? _snapshotLoader;
    private readonly IHeapObjectRepository? _objectRepository;
    private readonly IObjectReferenceService? _referenceService;
    private readonly IGcRootService? _gcRootService;
    private readonly IDominatorTreeService? _dominatorService;
    private readonly ISnapshotComparisonService? _comparisonService;
    private readonly IDumpFilePicker? _dumpFilePicker;
    private readonly ISessionRepository? _sessionRepository;
    private readonly SemaphoreSlim _sessionHistoryGate = new(1, 1);
    private readonly CancellationTokenSource _sessionHistoryCancellation = new();
    private readonly ObservableCollection<RecentSessionRowViewModel> _recentSessions = [];
    private readonly AsyncCommand _attachToProcessCommand;
    private readonly AsyncCommand _attachSelectedProcessCommand;
    private readonly AsyncCommand _openDumpCommand;
    private readonly AsyncCommand _compareSnapshotsCommand;
    private bool _isProcessPickerVisible;
    private LiveSessionViewModel? _liveSession;
    private SnapshotViewModel? _snapshot;
    private ComparisonViewModel? _comparison;
    private ProfilerError? _dumpError;
    private SessionCatalog _sessionCatalog = SessionCatalog.Empty;
    private bool _isSessionHistoryLoading;
    private ProfilerError? _sessionHistoryError;
    private bool _isDisposed;

    public StartViewModel(ProcessPickerViewModel processPicker)
        : this(
            processPicker,
            new LiveDiagnosticsSessionFactory(),
            AvaloniaUiDispatcher.Instance)
    {
    }

    internal StartViewModel(
        ProcessPickerViewModel processPicker,
        ILiveDiagnosticsSessionFactory sessionFactory,
        IUiDispatcher uiDispatcher)
        : this(processPicker, sessionFactory, uiDispatcher, null, null)
    {
    }

    internal StartViewModel(
        ProcessPickerViewModel processPicker,
        ILiveDiagnosticsSessionFactory sessionFactory,
        IUiDispatcher uiDispatcher,
        IDumpCaptureService? dumpCaptureService,
        IDumpDestinationPicker? dumpDestinationPicker)
        : this(
            processPicker,
            sessionFactory,
            uiDispatcher,
            dumpCaptureService,
            dumpDestinationPicker,
            null,
            null)
    {
    }

    internal StartViewModel(
        ProcessPickerViewModel processPicker,
        ILiveDiagnosticsSessionFactory sessionFactory,
        IUiDispatcher uiDispatcher,
        IDumpCaptureService? dumpCaptureService,
        IDumpDestinationPicker? dumpDestinationPicker,
        IHeapSnapshotLoader? snapshotLoader,
        IDumpFilePicker? dumpFilePicker,
        IHeapObjectRepository? objectRepository = null,
        IObjectReferenceService? referenceService = null,
        IGcRootService? gcRootService = null,
        IDominatorTreeService? dominatorService = null,
        ISnapshotComparisonService? comparisonService = null,
        ISessionRepository? sessionRepository = null)
    {
        ArgumentNullException.ThrowIfNull(processPicker);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ProcessPicker = processPicker;
        _sessionFactory = sessionFactory;
        _uiDispatcher = uiDispatcher;
        _dumpCaptureService = dumpCaptureService;
        _dumpDestinationPicker = dumpDestinationPicker;
        _snapshotLoader = snapshotLoader;
        _dumpFilePicker = dumpFilePicker;
        _objectRepository = objectRepository;
        _referenceService = referenceService;
        _gcRootService = gcRootService;
        _dominatorService = dominatorService;
        _comparisonService = comparisonService;
        _sessionRepository = sessionRepository;
        RecentSessions = new ReadOnlyObservableCollection<RecentSessionRowViewModel>(
            _recentSessions);
        _attachToProcessCommand = new AsyncCommand(() => ShowProcessPickerAsync());
        _attachSelectedProcessCommand = new AsyncCommand(
            () => StartLiveSessionAsync(),
            () => ProcessPicker.SelectedProcess is not null &&
                  LiveSession is null &&
                  Snapshot is null &&
                  Comparison is null);
        _openDumpCommand = new AsyncCommand(
            () => OpenDumpAsync(),
            () => Snapshot is null &&
                  Comparison is null &&
                  _dumpFilePicker is not null &&
                  _snapshotLoader is not null &&
                  _objectRepository is not null &&
                  _referenceService is not null &&
                  _gcRootService is not null &&
                  _dominatorService is not null);
        _compareSnapshotsCommand = new AsyncCommand(
            ShowComparisonAsync,
            () => Comparison is null &&
                  LiveSession is null &&
                  Snapshot is null &&
                  _dumpFilePicker is not null &&
                  _snapshotLoader is not null &&
                  _comparisonService is not null);
        ProcessPicker.PropertyChanged += OnProcessPickerPropertyChanged;
    }

    public ProcessPickerViewModel ProcessPicker { get; }

    public ReadOnlyObservableCollection<RecentSessionRowViewModel> RecentSessions { get; }

    public bool IsSessionHistoryLoading
    {
        get => _isSessionHistoryLoading;
        private set
        {
            if (SetProperty(ref _isSessionHistoryLoading, value))
            {
                OnPropertyChanged(nameof(IsRecentSessionsEmpty));
            }
        }
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool IsRecentSessionsEmpty =>
        !IsSessionHistoryLoading && !HasRecentSessions && !HasSessionHistoryError;

    public ProfilerError? SessionHistoryError => _sessionHistoryError;

    public string SessionHistoryErrorMessage => SessionHistoryError?.Message ?? string.Empty;

    public bool HasSessionHistoryError => SessionHistoryError is not null;

    public ICommand AttachToProcessCommand => _attachToProcessCommand;

    public ICommand AttachSelectedProcessCommand => _attachSelectedProcessCommand;

    public LiveSessionViewModel? LiveSession
    {
        get => _liveSession;
        private set
        {
            if (!SetProperty(ref _liveSession, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsStartVisible));
            OnPropertyChanged(nameof(IsLiveSessionVisible));
            OnPropertyChanged(nameof(IsComparisonVisible));
            _attachSelectedProcessCommand.NotifyCanExecuteChanged();
        }
    }

    public SnapshotViewModel? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsStartVisible));
            OnPropertyChanged(nameof(IsLiveSessionVisible));
            OnPropertyChanged(nameof(IsSnapshotVisible));
            OnPropertyChanged(nameof(IsComparisonVisible));
            _openDumpCommand.NotifyCanExecuteChanged();
            _compareSnapshotsCommand.NotifyCanExecuteChanged();
        }
    }

    public ComparisonViewModel? Comparison
    {
        get => _comparison;
        private set
        {
            if (!SetProperty(ref _comparison, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsStartVisible));
            OnPropertyChanged(nameof(IsLiveSessionVisible));
            OnPropertyChanged(nameof(IsSnapshotVisible));
            OnPropertyChanged(nameof(IsComparisonVisible));
            _compareSnapshotsCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsStartVisible => LiveSession is null && Snapshot is null && Comparison is null;

    public bool IsLiveSessionVisible => LiveSession is not null && Snapshot is null && Comparison is null;

    public bool IsSnapshotVisible => Snapshot is not null && Comparison is null;

    public bool IsComparisonVisible => Comparison is not null;

    public bool IsProcessPickerVisible
    {
        get => _isProcessPickerVisible;
        private set => SetProperty(ref _isProcessPickerVisible, value);
    }

    public ICommand OpenDumpCommand => _openDumpCommand;

    public ICommand CompareSnapshotsCommand => _compareSnapshotsCommand;

    public ProfilerError? DumpError => _dumpError;

    public string DumpErrorMessage => DumpError?.Message ?? string.Empty;

    public bool HasDumpError => DumpError is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_sessionRepository is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionHistoryCancellation.Token);
        await _uiDispatcher.InvokeAsync(() =>
        {
            SetSessionHistoryError(null);
            IsSessionHistoryLoading = true;
        });

        try
        {
            await _sessionHistoryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var catalog = await _sessionRepository
                    .LoadAsync(linked.Token)
                    .ConfigureAwait(false);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    _sessionCatalog = catalog;
                    RebuildRecentSessions();
                });
            }
            finally
            {
                _sessionHistoryGate.Release();
            }
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _uiDispatcher.InvokeAsync(() =>
                SetSessionHistoryError(ProfilerErrorFactory.Create(
                    ProfilerOperation.RestoreSessions,
                    exception)));
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => IsSessionHistoryLoading = false);
        }
    }

    public async Task ShowProcessPickerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        IsProcessPickerVisible = true;
        await ProcessPicker.RefreshAsync(cancellationToken);
    }

    public async Task StartLiveSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var selectedProcess = ProcessPicker.SelectedProcess;
        if (selectedProcess is null || LiveSession is not null || Comparison is not null)
        {
            return;
        }

        var session = new LiveSessionViewModel(
            selectedProcess.ProcessId,
            selectedProcess.Name,
            _sessionFactory,
            _uiDispatcher,
            closeSession: CloseLiveSessionAsync,
            dumpCaptureService: _dumpCaptureService,
            dumpDestinationPicker: _dumpDestinationPicker,
            analyzeSnapshot: path => AnalyzeCapturedDumpAsync(path),
            snapshotCaptured: path => RecordCapturedDumpAsync(path, selectedProcess));
        LiveSession = session;
        await session.StartAsync(cancellationToken);
    }

    public async Task CloseLiveSessionAsync()
    {
        var session = LiveSession;
        if (session is null)
        {
            return;
        }

        await session.DisposeAsync();
        if (ReferenceEquals(LiveSession, session))
        {
            LiveSession = null;
        }
    }

    public async Task OpenDumpAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (Snapshot is not null ||
            Comparison is not null ||
            _dumpFilePicker is null ||
            _snapshotLoader is null ||
            _objectRepository is null ||
            _referenceService is null)
        {
            return;
        }

        SetDumpError(null);

        string? path;
        try
        {
            path = await _dumpFilePicker.PickAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await _uiDispatcher.InvokeAsync(() =>
                SetDumpError(ProfilerErrorFactory.Create(
                    ProfilerOperation.ChooseFile,
                    exception)));
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenSnapshotAsync(path, cancellationToken);
    }

    public async Task AnalyzeCapturedDumpAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || Snapshot is not null)
        {
            return;
        }

        // Keep the live session running behind the snapshot; the user returns to
        // it when the snapshot is closed. A failed analysis must not lose the
        // live diagnostics session.
        await OpenSnapshotAsync(path, cancellationToken);
    }

    public async Task CloseSnapshotAsync()
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            return;
        }

        await snapshot.DisposeAsync();
        if (ReferenceEquals(Snapshot, snapshot))
        {
            Snapshot = null;
        }
    }

    public Task ShowComparisonAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (Comparison is not null ||
            _snapshotLoader is null ||
            _comparisonService is null ||
            _dumpFilePicker is null)
        {
            return Task.CompletedTask;
        }

        var comparison = new ComparisonViewModel(
            _snapshotLoader,
            _comparisonService,
            _uiDispatcher,
            _dumpFilePicker,
            close: CloseComparisonAsync,
            dominatorService: _dominatorService,
            comparisonCompleted: RecordComparisonAsync);
        Comparison = comparison;
        return Task.CompletedTask;
    }

    public async Task CloseComparisonAsync()
    {
        var comparison = Comparison;
        if (comparison is null)
        {
            return;
        }

        await comparison.DisposeAsync();
        if (ReferenceEquals(Comparison, comparison))
        {
            Comparison = null;
        }
    }

    private async Task OpenSnapshotAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (_snapshotLoader is null ||
            _objectRepository is null ||
            _referenceService is null ||
            _gcRootService is null)
        {
            return;
        }

        var snapshot = new SnapshotViewModel(
            _snapshotLoader,
            _objectRepository,
            _referenceService,
            _gcRootService,
            _uiDispatcher,
            CloseSnapshotAsync,
            _dominatorService);
        Snapshot = snapshot;
        await snapshot.LoadAsync(path, cancellationToken);
        if (snapshot.SnapshotInfo is { } info)
        {
            await RecordSnapshotAsync(info).WaitAsync(cancellationToken);
        }
    }

    private Task RecordCapturedDumpAsync(string path, ProcessRowViewModel process) =>
        UpdateCatalogAsync(catalog => catalog.WithRecentDump(new RecentDump(
            path,
            process.Name,
            process.ProcessId,
            process.RuntimeVersion,
            DateTimeOffset.UtcNow,
            null,
            null)));

    private Task RecordSnapshotAsync(HeapSnapshotInfo info) =>
        UpdateCatalogAsync(catalog => catalog
            .WithRecentDump(new RecentDump(
                info.Path,
                info.ProcessName,
                info.ProcessId,
                info.RuntimeVersion,
                info.CapturedAt,
                info.ObjectCount,
                info.HeapSize))
            .WithRecentInvestigation(new RecentInvestigation(
                info.Path,
                info.ProcessName,
                DateTimeOffset.UtcNow)));

    private Task RecordComparisonAsync(string beforePath, string afterPath) =>
        UpdateCatalogAsync(catalog => catalog.WithComparison(new ComparisonPair(
            beforePath,
            afterPath,
            DateTimeOffset.UtcNow)));

    private async Task UpdateCatalogAsync(
        Func<SessionCatalog, SessionCatalog> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_sessionRepository is null || _isDisposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionHistoryCancellation.Token);
        try
        {
            await _sessionHistoryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var updated = update(_sessionCatalog);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    _sessionCatalog = updated;
                    RebuildRecentSessions();
                });
                await _sessionRepository
                    .SaveAsync(updated, linked.Token)
                    .ConfigureAwait(false);
                await _uiDispatcher.InvokeAsync(() => SetSessionHistoryError(null));
            }
            finally
            {
                _sessionHistoryGate.Release();
            }
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _uiDispatcher.InvokeAsync(() =>
                SetSessionHistoryError(ProfilerErrorFactory.Create(
                    ProfilerOperation.SaveSessions,
                    exception)));
        }
    }

    private void RebuildRecentSessions()
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var investigationPaths = new HashSet<string>(pathComparer);
        var rows = new List<RecentSessionRowViewModel>();
        foreach (var investigation in _sessionCatalog.RecentInvestigations)
        {
            investigationPaths.Add(investigation.Path);
            rows.Add(CreateSnapshotRow(
                investigation.Path,
                investigation.ProcessName,
                investigation.LastOpenedAt));
        }

        foreach (var dump in _sessionCatalog.RecentDumps)
        {
            if (!investigationPaths.Contains(dump.Path))
            {
                rows.Add(CreateSnapshotRow(dump.Path, dump.ProcessName, dump.CapturedAt));
            }
        }

        foreach (var comparison in _sessionCatalog.ComparisonPairs)
        {
            var beforeName = Path.GetFileName(comparison.BeforePath);
            var afterName = Path.GetFileName(comparison.AfterPath);
            rows.Add(new RecentSessionRowViewModel(
                RecentSessionKind.Comparison,
                "Snapshot comparison",
                $"{beforeName} to {afterName}",
                $"{comparison.BeforePath}{Environment.NewLine}{comparison.AfterPath}",
                comparison.LastComparedAt,
                () => OpenRecentComparisonAsync(comparison)));
        }

        _recentSessions.Clear();
        foreach (var row in rows.OrderByDescending(row => row.Timestamp))
        {
            _recentSessions.Add(row);
        }

        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(IsRecentSessionsEmpty));
    }

    private RecentSessionRowViewModel CreateSnapshotRow(
        string path,
        string? processName,
        DateTimeOffset timestamp) =>
        new(
            RecentSessionKind.Snapshot,
            string.IsNullOrWhiteSpace(processName) ? Path.GetFileName(path) : processName,
            "Snapshot",
            path,
            timestamp,
            () => OpenRecentSnapshotAsync(path));

    private async Task OpenRecentSnapshotAsync(string path)
    {
        if (!IsStartVisible)
        {
            return;
        }

        await OpenSnapshotAsync(path);
    }

    private async Task OpenRecentComparisonAsync(ComparisonPair comparison)
    {
        if (!IsStartVisible)
        {
            return;
        }

        await ShowComparisonAsync();
        if (Comparison is { } viewModel)
        {
            await viewModel.LoadAsync(comparison.BeforePath, comparison.AfterPath);
        }
    }

    private void SetSessionHistoryError(ProfilerError? error)
    {
        if (SetProperty(
                ref _sessionHistoryError,
                error,
                nameof(SessionHistoryError)))
        {
            OnPropertyChanged(nameof(SessionHistoryErrorMessage));
            OnPropertyChanged(nameof(HasSessionHistoryError));
            OnPropertyChanged(nameof(IsRecentSessionsEmpty));
        }
    }

    private void SetDumpError(ProfilerError? error)
    {
        if (SetProperty(ref _dumpError, error, nameof(DumpError)))
        {
            OnPropertyChanged(nameof(DumpErrorMessage));
            OnPropertyChanged(nameof(HasDumpError));
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _sessionHistoryCancellation.CancelAsync();
        ProcessPicker.PropertyChanged -= OnProcessPickerPropertyChanged;
        ProcessPicker.Dispose();
        if (LiveSession is not null)
        {
            await LiveSession.DisposeAsync();
            LiveSession = null;
        }

        if (Snapshot is not null)
        {
            await Snapshot.DisposeAsync();
            Snapshot = null;
        }

        if (Comparison is not null)
        {
            await Comparison.DisposeAsync();
            Comparison = null;
        }

        await _sessionHistoryGate.WaitAsync();
        _sessionHistoryGate.Release();
        _sessionHistoryGate.Dispose();
        _sessionHistoryCancellation.Dispose();
    }

    private void OnProcessPickerPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ProcessPickerViewModel.SelectedProcess))
        {
            _attachSelectedProcessCommand.NotifyCanExecuteChanged();
        }
    }
}
