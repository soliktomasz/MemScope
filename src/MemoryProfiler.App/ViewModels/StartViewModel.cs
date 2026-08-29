using System.ComponentModel;
using System.Windows.Input;
using MemoryProfiler.Diagnostics.Sessions;

namespace MemoryProfiler.App.ViewModels;

public sealed class StartViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly ILiveDiagnosticsSessionFactory _sessionFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly AsyncCommand _attachToProcessCommand;
    private readonly AsyncCommand _attachSelectedProcessCommand;
    private bool _isProcessPickerVisible;
    private LiveSessionViewModel? _liveSession;
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
    {
        ArgumentNullException.ThrowIfNull(processPicker);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ProcessPicker = processPicker;
        _sessionFactory = sessionFactory;
        _uiDispatcher = uiDispatcher;
        _attachToProcessCommand = new AsyncCommand(ShowProcessPickerAsync);
        _attachSelectedProcessCommand = new AsyncCommand(
            StartLiveSessionAsync,
            () => ProcessPicker.SelectedProcess is not null && LiveSession is null);
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

    public bool IsStartVisible => LiveSession is null;

    public bool IsLiveSessionVisible => LiveSession is not null;

    public bool IsProcessPickerVisible
    {
        get => _isProcessPickerVisible;
        private set => SetProperty(ref _isProcessPickerVisible, value);
    }

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
            closeSession: CloseLiveSessionAsync);
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
    }

    private void OnProcessPickerPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ProcessPickerViewModel.SelectedProcess))
        {
            _attachSelectedProcessCommand.NotifyCanExecuteChanged();
        }
    }
}
