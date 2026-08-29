using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.Diagnostics.Sessions;

public sealed class LiveDiagnosticsSessionFactory : ILiveDiagnosticsSessionFactory
{
    private readonly IEventPipeSessionFactory _eventPipeSessionFactory;

    public LiveDiagnosticsSessionFactory()
        : this(new EventPipeSessionFactory())
    {
    }

    internal LiveDiagnosticsSessionFactory(IEventPipeSessionFactory eventPipeSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(eventPipeSessionFactory);
        _eventPipeSessionFactory = eventPipeSessionFactory;
    }

    public async Task<ILiveDiagnosticsSession> ConnectAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "The process identifier must be a positive integer.");
        }

        var adapter = await _eventPipeSessionFactory
            .CreateAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        return new LiveDiagnosticsSession(adapter);
    }

    internal static Exception TranslateConnectionFailure(int processId, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return exception;
        }

        if (exception is NotSupportedException or UnsupportedProtocolException)
        {
            return new NotSupportedException(
                $"The target process {processId} does not support live diagnostics sessions. " +
                "A compatible .NET runtime with a diagnostics endpoint is required.",
                exception);
        }

        if (exception is DiagnosticsClientException or
            IOException or
            UnauthorizedAccessException or
            TimeoutException or
            EndOfStreamException)
        {
            return new IOException(
                $"Unable to connect to process {processId} for live diagnostics.",
                exception);
        }

        return exception;
    }
}
