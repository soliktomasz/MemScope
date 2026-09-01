namespace MemoryProfiler.TestInfrastructure;

public sealed class ProcessEnvironmentScope : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _originalTempDirectory;
    private int _disposed;

    private ProcessEnvironmentScope(string? originalTempDirectory)
    {
        _originalTempDirectory = originalTempDirectory;
    }

    public static async Task<ProcessEnvironmentScope> EnterTempDirectoryAsync(
        string? tempDirectory,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var original = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", tempDirectory);
            return new ProcessEnvironmentScope(original);
        }
        catch
        {
            Gate.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", _originalTempDirectory);
        }
        finally
        {
            Gate.Release();
        }

        return ValueTask.CompletedTask;
    }
}
