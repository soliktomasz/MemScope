namespace MemoryProfiler.Diagnostics.Sessions;

public interface ILiveDiagnosticsSessionFactory
{
    Task<ILiveDiagnosticsSession> ConnectAsync(
        int processId,
        CancellationToken cancellationToken = default);
}
