namespace MemoryProfiler.Diagnostics.Dumps;

public interface IDumpCaptureService
{
    Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
