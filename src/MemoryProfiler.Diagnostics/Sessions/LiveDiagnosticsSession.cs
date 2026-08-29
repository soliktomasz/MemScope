using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MemoryProfiler.Contracts.Live;

namespace MemoryProfiler.Diagnostics.Sessions;

internal sealed class LiveDiagnosticsSession : ILiveDiagnosticsSession
{
    private readonly IEventPipeSessionAdapter _adapter;
    private readonly Channel<MemoryMetrics> _memoryChannel;
    private readonly Channel<GcEvent> _gcChannel;
    private readonly CancellationTokenSource _parserCancellation = new();
    private readonly Task _parserCompletion;
    private readonly OutputCompletion _completion;
    private readonly MemoryMetricsAccumulator _memoryAccumulator = new();
    private readonly GcCorrelator _gcCorrelator = new();
    private int _memoryObserverCount;
    private int _gcObserverCount;
    private int _disposed;

    public LiveDiagnosticsSession(IEventPipeSessionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;

        _memoryChannel = Channel.CreateBounded<MemoryMetrics>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _gcChannel = Channel.CreateUnbounded<GcEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _completion = new OutputCompletion(_memoryChannel.Writer, _gcChannel.Writer);

        adapter.CounterSample += OnCounterSample;
        adapter.GcStart += _gcCorrelator.AddGcStart;
        adapter.GcStop += OnGcStop;
        adapter.GcHeapStats += OnGcHeapStats;
        adapter.GcSuspendBegin += _gcCorrelator.AddSuspensionBegin;
        adapter.GcRestartBegin += _gcCorrelator.AddRestartBegin;

        _parserCancellation.Token.Register(_adapter.StopProcessing);
        _parserCompletion = MonitorParserAsync();
    }

    public int ProcessId => _adapter.ProcessId;

    public IAsyncEnumerable<MemoryMetrics> ObserveMemoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _memoryObserverCount, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one memory observer can be active per live diagnostics session.");
        }

        return ObserveMemoryCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<MemoryMetrics> ObserveMemoryCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var metrics in _memoryChannel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return metrics;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _memoryObserverCount, 0);
        }
    }

    public IAsyncEnumerable<GcEvent> ObserveGcEventsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _gcObserverCount, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one GC event observer can be active per live diagnostics session.");
        }

        return ObserveGcEventsCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<GcEvent> ObserveGcEventsCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var gcEvent in _gcChannel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return gcEvent;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _gcObserverCount, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _parserCancellation.Cancel();
        try
        {
            await _adapter.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort stop; the target may already be gone.
        }

        try
        {
            await _parserCompletion.ConfigureAwait(false);
        }
        catch
        {
            // The parser monitor never faults; guard against adapter defects.
        }

        _completion.Complete(null);
        _parserCancellation.Dispose();
        try
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Releasing an already terminated target must not surface shutdown races.
        }
    }

    private void OnCounterSample(CounterSample sample)
    {
        var snapshot = _memoryAccumulator.Add(sample);
        if (snapshot is not null)
        {
            _memoryChannel.Writer.TryWrite(snapshot);
        }

        _gcCorrelator.AddHeapSize(sample);
    }

    private void OnGcStop(GcStopObservation observation)
    {
        var gcEvent = _gcCorrelator.AddGcStop(observation);
        if (gcEvent is not null)
        {
            _gcChannel.Writer.TryWrite(gcEvent);
        }
    }

    private void OnGcHeapStats(GcHeapStatsObservation observation)
    {
        _gcCorrelator.AddGcHeapStats(observation);
        _memoryAccumulator.AddPromotedBytes(observation.PromotedBytes);
    }

    private async Task MonitorParserAsync()
    {
        Exception? failure = null;
        try
        {
            await Task.Run(_adapter.Process).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        _completion.Complete(failure is null
            ? null
            : new IOException("The live diagnostics session ended unexpectedly.", failure));
    }
}

internal sealed class OutputCompletion
{
    private readonly object _gate = new();
    private readonly ChannelWriter<MemoryMetrics> _memoryWriter;
    private readonly ChannelWriter<GcEvent> _gcWriter;
    private bool _completed;

    public OutputCompletion(
        ChannelWriter<MemoryMetrics> memoryWriter,
        ChannelWriter<GcEvent> gcWriter)
    {
        _memoryWriter = memoryWriter;
        _gcWriter = gcWriter;
    }

    public void Complete(Exception? error)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _memoryWriter.TryComplete(error);
            _gcWriter.TryComplete(error);
        }
    }
}
