using System.ComponentModel;
using System.Windows.Input;
using MemoryProfiler.Analysis.Dominators;
using MemoryProfiler.Analysis.Loading;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Analysis.References;
using MemoryProfiler.Analysis.Roots;
using MemoryProfiler.App.Services;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Sessions;

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
    private readonly IDumpFilePicker? _dumpFilePicker;
    private readonly AsyncCommand _attachToProcessCommand;
    private readonly AsyncCommand _attachSelectedProcessCommand;
    private readonly AsyncCommand _openDumpCommand;
    private bool _isProcessPickerVisible;
    private LiveSessionViewModel? _liveSession;
    private SnapshotViewModel? _snapshot;
    private string? _dumpErrorMessage;
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
        IDominatorTreeService? dominatorService = null)
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
        _attachToProcessCommand = new AsyncCommand(ShowProcessPickerAsync);
        _attachSelectedProcessCommand = new AsyncCommand(
            StartLiveSessionAsync,
            () => ProcessPicker.SelectedProcess is not null && LiveSession is null);
        _openDumpCommand = new AsyncCommand(
            OpenDumpAsync,
            () => Snapshot is null &&
                  _dumpFilePicker is not null &&
                  _snapshotLoader is not null &&
                  _objectRepository is not null &&
                  _referenceService is not null &&
                  _gcRootService is not null &&
                  _dominatorService is not null);
        ProcessPicker.PropertyChanged += OnProcessPickerPropertyChanged;
    }

    public ProcessPickerViewModel ProcessPicker { get; }

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
            _openDumpCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsStartVisible => LiveSession is null && Snapshot is null;

    public bool IsLiveSessionVisible => LiveSession is not null && Snapshot is null;

    public bool IsSnapshotVisible => Snapshot is not null;

    public bool IsProcessPickerVisible
    {
        get => _isProcessPickerVisible;
        private set => SetProperty(ref _isProcessPickerVisible, value);
    }

    public ICommand OpenDumpCommand => _openDumpCommand;

    public string DumpErrorMessage => _dumpErrorMessage ?? string.Empty;

    public bool HasDumpError => _dumpErrorMessage is not null;

    public async Task ShowProcessPickerAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        IsProcessPickerVisible = true;
        await ProcessPicker.RefreshAsync();
    }

    public async Task StartLiveSessionAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var selectedProcess = ProcessPicker.SelectedProcess;
        if (selectedProcess is null || LiveSession is not null)
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
            analyzeSnapshot: AnalyzeCapturedDumpAsync);
        LiveSession = session;
        await session.StartAsync();
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

    public async Task OpenDumpAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (Snapshot is not null ||
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
            path = await _dumpFilePicker.PickAsync();
        }
        catch (Exception exception)
        {
            await _uiDispatcher.InvokeAsync(() =>
                SetDumpError($"Unable to open the dump picker. {exception.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenSnapshotAsync(path);
    }

    public async Task AnalyzeCapturedDumpAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Snapshot is not null)
        {
            return;
        }

        // Keep the live session running behind the snapshot; the user returns to
        // it when the snapshot is closed. A failed analysis must not lose the
        // live diagnostics session.
        await OpenSnapshotAsync(path);
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

    private async Task OpenSnapshotAsync(string path)
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
        await snapshot.LoadAsync(path);
    }

    private void SetDumpError(string? message)
    {
        if (SetProperty(ref _dumpErrorMessage, message, nameof(DumpErrorMessage)))
        {
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
    }

    private void OnProcessPickerPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ProcessPickerViewModel.SelectedProcess))
        {
            _attachSelectedProcessCommand.NotifyCanExecuteChanged();
        }
    }
}
