using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.TestInfrastructure;

public sealed class HeapDumpFixture : IAsyncDisposable
{
    private HeapDumpFixture(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static async Task<HeapDumpFixture> CaptureAsync(
        int processId,
        string socketRoot,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(socketRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"memscope-workload-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", socketRoot);
            await new DiagnosticsClient(processId).WriteDumpAsync(
                DumpType.WithHeap,
                path,
                WriteDumpFlags.None,
                cancellationToken);
            return new HeapDumpFixture(path);
        }
        catch
        {
            DeleteBestEffort(path);
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambientTempDir);
        }
    }

    public ValueTask DisposeAsync()
    {
        DeleteBestEffort(Path);
        return ValueTask.CompletedTask;
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the capture, analysis, or test failure.
        }
    }
}
