using System.Collections.ObjectModel;
using System.Windows.Input;
using MemoryProfiler.App.Services;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.App.ViewModels.GcTimeline;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Sessions;

namespace MemoryProfiler.App.ViewModels;

public sealed class LiveSessionViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumMetricSamples = 3_600;
    private const int MaximumGcEvents = 3_600;

    private readonly ILiveDiagnosticsSessionFactory _sessionFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly int _maximumMetricSamples;
    private readonly ObservableCollection<MemoryMetrics> _metricHistory = [];
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly object _captureGate = new();
    private readonly AsyncCommand _disconnectCommand;
    private readonly AsyncCommand _closeCommand;
    private readonly AsyncCommand _captureSnapshotCommand;
    private readonly AsyncCommand _analyzeSnapshotCommand;
    private readonly RelayCommand _cancelCaptureCommand;
    private readonly IDumpCaptureService? _dumpCaptureService;
    private readonly IDumpDestinationPicker? _dumpDestinationPicker;
    private readonly Func<string, Task>? _analyzeSnapshot;
    private ILiveDiagnosticsSession? _session;
    private Task? _runTask;
    private Task? _disposeTask;
    private Task? _captureTask;
    private CancellationTokenSource? _captureCancellation;
    private bool _isConnecting;
    private bool _isLive;
    private bool _isDisconnected;
    private bool _hasMetrics;
    private string? _errorMessage;
    private bool _isCapturing;
    private string _captureStatusMessage = string.Empty;
    private string _captureErrorMessage = string.Empty;
    private string _capturedDumpPath = string.Empty;
    private int _disposed;

    internal LiveSessionViewModel(
        int processId,
        string processName,
        ILiveDiagnosticsSessionFactory sessionFactory,
        IUiDispatcher uiDispatcher,
        int maximumMetricSamples = MaximumMetricSamples,
        int maximumGcEvents = MaximumGcEvents,
        Func<Task>? closeSession = null,
        IDumpCaptureService? dumpCaptureService = null,
        IDumpDestinationPicker? dumpDestinationPicker = null,
        Func<string, Task>? analyzeSnapshot = null)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        if (maximumMetricSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMetricSamples));
        }

        if (maximumGcEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGcEvents));
        }

        ProcessId = processId;
        ProcessName = processName;
        _sessionFactory = sessionFactory;
        _uiDispatcher = uiDispatcher;
        _maximumMetricSamples = maximumMetricSamples;
        _dumpCaptureService = dumpCaptureService;
        _dumpDestinationPicker = dumpDestinationPicker;
        _analyzeSnapshot = analyzeSnapshot;
        MetricHistory = new ReadOnlyObservableCollection<MemoryMetrics>(_metricHistory);
        GcTimeline = new GcTimelineViewModel(maximumGcEvents);
        _disconnectCommand = new AsyncCommand(DisconnectAsync, () => IsConnecting || IsLive);
        _closeCommand = new AsyncCommand(
            closeSession ?? DisconnectAsync,
            () => IsDisconnected || HasError);
        _captureSnapshotCommand = new AsyncCommand(
            CaptureSnapshotAsync,
            () => CanCaptureSnapshot);
        _analyzeSnapshotCommand = new AsyncCommand(
            AnalyzeSnapshotAsync,
            () => CanAnalyzeSnapshot);
        _cancelCaptureCommand = new RelayCommand(CancelCapture, () => IsCapturing);
    }

    public int ProcessId { get; }

    public string ProcessName { get; }

    public string ProcessDescription => $"{ProcessName} (PID {ProcessId})";

    public HeapSummaryViewModel Heap { get; } = new();

    public AllocationRateViewModel Allocation { get; } = new();

    public GenerationSummaryViewModel Generations { get; } = new();

    public GcTimelineViewModel GcTimeline { get; }

    public ReadOnlyObservableCollection<MemoryMetrics> MetricHistory { get; }

    public ICommand DisconnectCommand => _disconnectCommand;

    public ICommand CloseCommand => _closeCommand;

    public ICommand CaptureSnapshotCommand => _captureSnapshotCommand;

    public ICommand AnalyzeSnapshotCommand => _analyzeSnapshotCommand;

    public ICommand CancelCaptureCommand => _cancelCaptureCommand;

    public bool CanAnalyzeSnapshot =>
        !IsCapturing &&
        CapturedDumpPath.Length > 0 &&
        _analyzeSnapshot is not null;

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(CanCaptureSnapshot));
                OnPropertyChanged(nameof(CanAnalyzeSnapshot));
                _captureSnapshotCommand.NotifyCanExecuteChanged();
                _analyzeSnapshotCommand.NotifyCanExecuteChanged();
                _cancelCaptureCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanCaptureSnapshot =>
        IsLive &&
        !IsCapturing &&
        _dumpCaptureService is not null &&
        _dumpDestinationPicker is not null;

    public bool HasCaptureStatus => CaptureStatusMessage.Length > 0;

    public string CaptureStatusMessage
    {
        get => _captureStatusMessage;
        private set
        {
            if (SetProperty(ref _captureStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasCaptureStatus));
            }
        }
    }

    public bool HasCaptureError => CaptureErrorMessage.Length > 0;

    public string CaptureErrorMessage
    {
        get => _captureErrorMessage;
        private set
        {
            if (SetProperty(ref _captureErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasCaptureError));
            }
        }
    }

    public string CapturedDumpPath
    {
        get => _capturedDumpPath;
        private set
        {
            if (SetProperty(ref _capturedDumpPath, value))
            {
                OnPropertyChanged(nameof(CanAnalyzeSnapshot));
                _analyzeSnapshotCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set => SetState(ref _isConnecting, value, nameof(IsConnecting));
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetState(ref _isLive, value, nameof(IsLive));
    }

    public bool IsDisconnected
    {
        get => _isDisconnected;
        private set => SetState(ref _isDisconnected, value, nameof(IsDisconnected));
    }

    public bool HasMetrics
    {
        get => _hasMetrics;
        private set
        {
            if (SetProperty(ref _hasMetrics, value))
            {
                OnPropertyChanged(nameof(IsAwaitingMetrics));
            }
        }
    }

    public bool IsAwaitingMetrics => IsLive && !HasMetrics;

    public bool HasError => _errorMessage is not null;

    public string ErrorMessage => _errorMessage ?? string.Empty;

    public Task StartAsync()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_runTask is not null)
            {
                return _runTask;
            }

            IsConnecting = true;
            IsDisconnected = false;
            SetError(null);
            _runTask = RunAsync();
            return _runTask;
        }
    }

    private async Task RunAsync()
    {
        var connected = false;
        var cancellationToken = _sessionCancellation.Token;
        try
        {
            var session = await _sessionFactory
                .ConnectAsync(ProcessId, cancellationToken)
                .ConfigureAwait(false);
            _session = session;
            cancellationToken.ThrowIfCancellationRequested();
            connected = true;

            await PublishAsync(() =>
            {
                IsConnecting = false;
                IsLive = true;
            }).ConfigureAwait(false);

            var memoryObservation = ObserveMemoryAsync(session, cancellationToken);
            var gcObservation = ObserveGcEventsAsync(session, cancellationToken);
            await Task.WhenAll(memoryObservation, gcObservation).ConfigureAwait(false);

            await PublishAsync(SetDisconnected).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PublishAsync(SetDisconnected).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                SetDisconnected();
                SetError(connected
                    ? $"The live diagnostics session ended unexpectedly. {exception.Message}"
                    : $"Unable to start live diagnostics. {exception.Message}");
            }).ConfigureAwait(false);
        }
        finally
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveMemoryAsync(
        ILiveDiagnosticsSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var metrics in session
                           .ObserveMemoryAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await PublishAsync(() => ApplyMetrics(metrics)).ConfigureAwait(false);
        }
    }

    private async Task ObserveGcEventsAsync(
        ILiveDiagnosticsSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var gcEvent in session
                           .ObserveGcEventsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await PublishAsync(() => GcTimeline.Apply(gcEvent)).ConfigureAwait(false);
        }
    }

    internal void ApplyMetrics(MemoryMetrics metrics)
    {
        Heap.Apply(metrics);
        Allocation.Apply(metrics);
        Generations.Apply(metrics);
        _metricHistory.Add(metrics);
        while (_metricHistory.Count > _maximumMetricSamples)
        {
            _metricHistory.RemoveAt(0);
        }

        HasMetrics = true;
    }

    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var captureTask = CancelActiveCapture();
        await _sessionCancellation.CancelAsync().ConfigureAwait(false);
        Task? runTask;
        lock (_lifecycleGate)
        {
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }
        else
        {
            await PublishAsync(SetDisconnected).ConfigureAwait(false);
        }

        if (captureTask is not null)
        {
            await captureTask.ConfigureAwait(false);
        }
    }

    public async Task CaptureSnapshotAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !IsLive ||
            _dumpCaptureService is null ||
            _dumpDestinationPicker is null)
        {
            return;
        }

        string? destinationDirectory;
        try
        {
            destinationDirectory = await _dumpDestinationPicker.PickAsync();
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                CapturedDumpPath = string.Empty;
                CaptureStatusMessage = string.Empty;
                CaptureErrorMessage = $"Unable to choose a snapshot destination. {exception.Message}";
            }).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return;
        }

        CancellationTokenSource captureCancellation;
        Task captureTask;
        lock (_captureGate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                _captureTask is not null ||
                !CanCaptureSnapshot)
            {
                return;
            }

            captureCancellation = new CancellationTokenSource();
            _captureCancellation = captureCancellation;
            captureTask = CaptureCoreAsync(
                destinationDirectory,
                captureCancellation.Token);
            _captureTask = captureTask;
        }

        try
        {
            await captureTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_captureGate)
            {
                if (ReferenceEquals(_captureTask, captureTask))
                {
                    _captureTask = null;
                    _captureCancellation = null;
                }
            }

            captureCancellation.Dispose();
        }
    }

    public void CancelCapture()
    {
        CancellationTokenSource? cancellation;
        lock (_captureGate)
        {
            cancellation = _captureCancellation;
        }

        RequestCancellation(cancellation);
    }

    public async Task AnalyzeSnapshotAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !CanAnalyzeSnapshot ||
            _analyzeSnapshot is null)
        {
            return;
        }

        var path = CapturedDumpPath;
        await PublishAsync(() => CaptureErrorMessage = string.Empty).ConfigureAwait(false);
        try
        {
            await _analyzeSnapshot(path);
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                CaptureErrorMessage =
                    $"Unable to open the snapshot. {exception.Message}";
            }).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Volatile.Write(ref _disposed, 1);
            _disposeTask = DisposeCoreAsync(_runTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? runTask)
    {
        var captureTask = CancelActiveCapture();
        await _sessionCancellation.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }
        else
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }

        if (captureTask is not null)
        {
            await captureTask.ConfigureAwait(false);
        }

        _sessionCancellation.Dispose();
    }

    private async Task CaptureCoreAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await PublishAsync(() =>
        {
            CapturedDumpPath = string.Empty;
            CaptureErrorMessage = string.Empty;
            CaptureStatusMessage = "Capturing snapshot";
            IsCapturing = true;
        }).ConfigureAwait(false);

        try
        {
            var path = await _dumpCaptureService!
                .CaptureAsync(ProcessId, destinationDirectory, cancellationToken)
                .ConfigureAwait(false);
            await PublishAsync(() =>
            {
                CapturedDumpPath = path;
                CaptureStatusMessage = "Snapshot saved";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PublishAsync(() => CaptureStatusMessage = string.Empty)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await PublishAsync(() =>
            {
                CaptureStatusMessage = string.Empty;
                CaptureErrorMessage = $"Unable to capture snapshot. {exception.Message}";
            }).ConfigureAwait(false);
        }
        finally
        {
            await PublishAsync(() => IsCapturing = false).ConfigureAwait(false);
        }
    }

    private Task? CancelActiveCapture()
    {
        CancellationTokenSource? cancellation;
        Task? captureTask;
        lock (_captureGate)
        {
            cancellation = _captureCancellation;
            captureTask = _captureTask;
        }

        RequestCancellation(cancellation);
        return captureTask;
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Capture completion won the race and already retired this source.
        }
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

    private async Task DisposeSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetDisconnected()
    {
        IsConnecting = false;
        IsLive = false;
        IsDisconnected = true;
    }

    private void SetState(ref bool field, bool value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            _disconnectCommand.NotifyCanExecuteChanged();
            _closeCommand.NotifyCanExecuteChanged();
            _captureSnapshotCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCaptureSnapshot));
            OnPropertyChanged(nameof(IsAwaitingMetrics));
        }
    }

    private void SetError(string? message)
    {
        if (SetProperty(ref _errorMessage, message, nameof(ErrorMessage)))
        {
            OnPropertyChanged(nameof(HasError));
            _closeCommand.NotifyCanExecuteChanged();
        }
    }
}
