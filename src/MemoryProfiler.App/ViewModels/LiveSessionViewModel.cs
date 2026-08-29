using System.Collections.ObjectModel;
using System.Windows.Input;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Live;
using MemoryProfiler.Diagnostics.Sessions;

namespace MemoryProfiler.App.ViewModels;

public sealed class LiveSessionViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumMetricSamples = 3_600;

    private readonly ILiveDiagnosticsSessionFactory _sessionFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly int _maximumMetricSamples;
    private readonly ObservableCollection<MemoryMetrics> _metricHistory = [];
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly AsyncCommand _disconnectCommand;
    private readonly AsyncCommand _closeCommand;
    private ILiveDiagnosticsSession? _session;
    private Task? _runTask;
    private Task? _disposeTask;
    private bool _isConnecting;
    private bool _isLive;
    private bool _isDisconnected;
    private bool _hasMetrics;
    private string? _errorMessage;
    private int _disposed;

    internal LiveSessionViewModel(
        int processId,
        string processName,
        ILiveDiagnosticsSessionFactory sessionFactory,
        IUiDispatcher uiDispatcher,
        int maximumMetricSamples = MaximumMetricSamples,
        Func<Task>? closeSession = null)
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

        ProcessId = processId;
        ProcessName = processName;
        _sessionFactory = sessionFactory;
        _uiDispatcher = uiDispatcher;
        _maximumMetricSamples = maximumMetricSamples;
        MetricHistory = new ReadOnlyObservableCollection<MemoryMetrics>(_metricHistory);
        _disconnectCommand = new AsyncCommand(DisconnectAsync, () => IsConnecting || IsLive);
        _closeCommand = new AsyncCommand(
            closeSession ?? DisconnectAsync,
            () => IsDisconnected || HasError);
    }

    public int ProcessId { get; }

    public string ProcessName { get; }

    public string ProcessDescription => $"{ProcessName} (PID {ProcessId})";

    public HeapSummaryViewModel Heap { get; } = new();

    public AllocationRateViewModel Allocation { get; } = new();

    public GenerationSummaryViewModel Generations { get; } = new();

    public ReadOnlyObservableCollection<MemoryMetrics> MetricHistory { get; }

    public ICommand DisconnectCommand => _disconnectCommand;

    public ICommand CloseCommand => _closeCommand;

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
            var gcObservation = DrainGcEventsAsync(session, cancellationToken);
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

    private static async Task DrainGcEventsAsync(
        ILiveDiagnosticsSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in session
                           .ObserveGcEventsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
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
        await _sessionCancellation.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }
        else
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }

        _sessionCancellation.Dispose();
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
