using MemoryProfiler.Diagnostics.Sessions;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class LiveDiagnosticsSessionFactoryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-42)]
    public async Task ConnectAsyncRejectsNonPositiveProcessIdsBeforeConnecting(int processId)
    {
        var sessionFactory = new RecordingSessionFactory();
        var factory = new LiveDiagnosticsSessionFactory(sessionFactory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => factory.ConnectAsync(processId));

        Assert.Equal(0, sessionFactory.CreateCalls);
    }

    [Fact]
    public async Task ConnectAsyncDoesNotReturnUntilTheEventPipeSessionIsStarted()
    {
        var sessionFactory = new BlockingSessionFactory();
        var factory = new LiveDiagnosticsSessionFactory(sessionFactory);

        var connectTask = factory.ConnectAsync(42);
        await Task.Delay(100);
        Assert.False(connectTask.IsCompleted);

        sessionFactory.Release();
        var session = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, session.ProcessId);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsyncPropagatesCancellation()
    {
        var sessionFactory = new BlockingSessionFactory();
        var factory = new LiveDiagnosticsSessionFactory(sessionFactory);
        using var cancellation = new CancellationTokenSource();

        var connectTask = factory.ConnectAsync(42, cancellation.Token);
        await Task.Delay(100);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
    }

    [Fact]
    public void UnsupportedRuntimeIsTranslatedToNotSupportedException()
    {
        var exception = LiveDiagnosticsSessionFactory.TranslateConnectionFailure(
            42, new UnsupportedProtocolException("Protocol mismatch."));

        var notSupported = Assert.IsType<NotSupportedException>(exception);
        Assert.Contains("42", notSupported.Message);
        Assert.IsType<UnsupportedProtocolException>(notSupported.InnerException);
    }

    [Fact]
    public void TransportFailureIsTranslatedToIOExceptionWithProcessId()
    {
        var exception = LiveDiagnosticsSessionFactory.TranslateConnectionFailure(
            42, new ServerNotAvailableException("Endpoint is gone."));

        var ioException = Assert.IsType<IOException>(exception);
        Assert.Contains("42", ioException.Message);
        Assert.IsType<ServerNotAvailableException>(ioException.InnerException);
    }

    [Fact]
    public void NotSupportedExceptionFromTheClientIsTranslated()
    {
        var exception = LiveDiagnosticsSessionFactory.TranslateConnectionFailure(
            42, new NotSupportedException("Command unsupported."));

        Assert.IsType<NotSupportedException>(exception);
    }

    [Fact]
    public void OperationCanceledExceptionIsTranslatedToItself()
    {
        var original = new OperationCanceledException();

        var translated = LiveDiagnosticsSessionFactory.TranslateConnectionFailure(42, original);

        Assert.Same(original, translated);
    }

    private sealed class RecordingSessionFactory : IEventPipeSessionFactory
    {
        public int CreateCalls { get; private set; }

        public Task<IEventPipeSessionAdapter> CreateAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromException<IEventPipeSessionAdapter>(
                new InvalidOperationException("Unexpected connection attempt."));
        }
    }

    private sealed class BlockingSessionFactory : IEventPipeSessionFactory
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCalls { get; private set; }

        public async Task<IEventPipeSessionAdapter> CreateAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            await _release.Task.WaitAsync(cancellationToken);
            return new StubEventPipeSessionAdapter();
        }

        public void Release() => _release.TrySetResult();
    }
}
