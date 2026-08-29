using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace MemoryProfiler.Diagnostics.Sessions;

internal interface IEventPipeSessionAdapter : IAsyncDisposable
{
    int ProcessId { get; }

    event Action<CounterSample>? CounterSample;
    event Action<GcStartObservation>? GcStart;
    event Action<GcStopObservation>? GcStop;
    event Action<GcHeapStatsObservation>? GcHeapStats;
    event Action<GcSuspendBeginObservation>? GcSuspendBegin;
    event Action<GcRestartBeginObservation>? GcRestartBegin;

    void Process();

    void StopProcessing();

    Task StopAsync();
}

internal interface IEventPipeSessionFactory
{
    Task<IEventPipeSessionAdapter> CreateAsync(
        int processId,
        CancellationToken cancellationToken);
}

internal sealed class EventPipeSessionFactory : IEventPipeSessionFactory
{
    private static readonly IReadOnlyList<EventPipeProvider> Providers =
    [
        new EventPipeProvider(
            "System.Runtime",
            EventLevel.Informational,
            0,
            new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
        new EventPipeProvider(
            "Microsoft-Windows-DotNETRuntime",
            EventLevel.Informational,
            0x1)
    ];

    public async Task<IEventPipeSessionAdapter> CreateAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var client = new DiagnosticsClient(processId);
        try
        {
            var session = await client
                .StartEventPipeSessionAsync(
                    Providers,
                    requestRundown: false,
                    token: cancellationToken)
                .ConfigureAwait(false);
            return new EventPipeSessionAdapter(processId, session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            throw LiveDiagnosticsSessionFactory.TranslateConnectionFailure(processId, exception);
        }
    }

    private static bool IsConnectionFailure(Exception exception) => exception is
        NotSupportedException or
        UnsupportedProtocolException or
        DiagnosticsClientException or
        IOException or
        UnauthorizedAccessException or
        TimeoutException or
        EndOfStreamException;
}

internal sealed class EventPipeSessionAdapter : IEventPipeSessionAdapter
{
    private readonly EventPipeSession _session;
    private readonly EventPipeEventSource _source;

    public EventPipeSessionAdapter(int processId, EventPipeSession session)
    {
        ProcessId = processId;
        _session = session;
        _source = new EventPipeEventSource(session.EventStream);
        WireEventHandlers();
    }

    public int ProcessId { get; }

    public event Action<CounterSample>? CounterSample;
    public event Action<GcStartObservation>? GcStart;
    public event Action<GcStopObservation>? GcStop;
    public event Action<GcHeapStatsObservation>? GcHeapStats;
    public event Action<GcSuspendBeginObservation>? GcSuspendBegin;
    public event Action<GcRestartBeginObservation>? GcRestartBegin;

    public void Process() => _source.Process();

    public void StopProcessing() => _source.StopProcessing();

    public Task StopAsync()
    {
        try
        {
            _session.Stop();
        }
        catch
        {
            // Best-effort stop; the target may already be gone.
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _source.Dispose();
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private void WireEventHandlers()
    {
        _source.Dynamic.All += OnDynamicEvent;
        _source.Clr.GCStart += OnGcStart;
        _source.Clr.GCStop += OnGcStop;
        _source.Clr.GCHeapStats += OnGcHeapStats;
        _source.Clr.GCSuspendEEStart += OnGcSuspendBegin;
        _source.Clr.GCRestartEEStart += OnGcRestartBegin;
    }

    private void OnDynamicEvent(TraceEvent traceEvent)
    {
        if (traceEvent.EventName == "EventCounters" &&
            TryParseCounterSample(traceEvent, out var sample))
        {
            CounterSample?.Invoke(sample);
        }
    }

    private void OnGcStart(GCStartTraceData data) =>
        GcStart?.Invoke(new GcStartObservation(
            data.Count,
            data.Depth,
            data.Reason.ToString(),
            ToTimestamp(data.TimeStamp)));

    private void OnGcStop(GCEndTraceData data) =>
        GcStop?.Invoke(new GcStopObservation(data.Count, ToTimestamp(data.TimeStamp)));

    private void OnGcHeapStats(GCHeapStatsTraceData data) =>
        GcHeapStats?.Invoke(new GcHeapStatsObservation(
            ClampToUInt64(data.TotalHeapSize),
            ClampToUInt64(data.TotalPromoted),
            ToTimestamp(data.TimeStamp)));

    private void OnGcSuspendBegin(GCSuspendEETraceData data) =>
        GcSuspendBegin?.Invoke(new GcSuspendBeginObservation(
            data.Count,
            ToTimestamp(data.TimeStamp)));

    private void OnGcRestartBegin(GCNoUserDataTraceData data) =>
        GcRestartBegin?.Invoke(new GcRestartBeginObservation(ToTimestamp(data.TimeStamp)));

    private static DateTimeOffset ToTimestamp(DateTime timestamp) =>
        new(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));

    private static ulong ClampToUInt64(long value) => value > 0 ? (ulong)value : 0;

    private static bool TryParseCounterSample(TraceEvent traceEvent, out CounterSample sample)
    {
        sample = default;

        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> payload ||
            payload.TryGetValue("Payload", out var payloadValue) is false ||
            payloadValue is not IDictionary<string, object> counter ||
            counter.TryGetValue("Name", out var nameValue) is false ||
            nameValue is not string name)
        {
            return false;
        }

        var mean = TryReadDouble(counter, "Mean");
        var increment = TryReadDouble(counter, "Increment");
        var intervalSec = TryReadDouble(counter, "IntervalSec");

        if (mean is null && increment is null)
        {
            return false;
        }

        sample = new CounterSample(name, mean, increment, intervalSec, ToTimestamp(traceEvent.TimeStamp));
        return true;
    }

    private static double? TryReadDouble(IDictionary<string, object> values, string key)
    {
        if (values.TryGetValue(key, out var value) is false || value is null)
        {
            return null;
        }

        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            short number => number,
            ushort number => number,
            byte number => number,
            sbyte number => number,
            decimal number => (double)number,
            string text when double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
