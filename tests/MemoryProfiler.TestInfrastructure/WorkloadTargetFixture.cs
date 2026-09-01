using System.Diagnostics;

namespace MemoryProfiler.TestInfrastructure;

public sealed class WorkloadTargetFixture : IAsyncDisposable
{
    private readonly Process _process;

    private WorkloadTargetFixture(Process process, string socketRoot)
    {
        _process = process;
        SocketRoot = socketRoot;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public string SocketRoot { get; }

    public static async Task<WorkloadTargetFixture> StartAsync(
        string assemblyName,
        IEnumerable<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        cancellationToken.ThrowIfCancellationRequested();

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Workload target assembly '{assemblyName}' was not found.",
                assemblyPath);
        }

        var socketRoot = await ResolveShortSocketRootAsync(cancellationToken);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.Environment["TMPDIR"] = socketRoot;
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start workload target '{assemblyName}'.");

        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var readyLine = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
            if (readyLine != "READY")
            {
                var error = await process.StandardError.ReadToEndAsync(startupTimeout.Token);
                throw new InvalidOperationException(
                    $"Workload target '{assemblyName}' did not signal readiness. stderr: {error}");
            }

            return new WorkloadTargetFixture(process, socketRoot);
        }
        catch
        {
            await StopAndDisposeAsync(process);
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(StopAndDisposeAsync(_process));

    private static async Task<string> ResolveShortSocketRootAsync(
        CancellationToken cancellationToken)
    {
        await using var environment = await ProcessEnvironmentScope
            .EnterTempDirectoryAsync(tempDirectory: null, cancellationToken);
        return Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    }

    private static async Task StopAndDisposeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The target may already have exited.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // Preserve the original startup/test failure when shutdown stalls.
        }
        finally
        {
            process.Dispose();
        }
    }
}
