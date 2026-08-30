using System.Diagnostics;

namespace MemoryProfiler.Analysis.Tests;

internal sealed class LiveTargetFixture : IAsyncDisposable
{
    private readonly Process _process;

    private LiveTargetFixture(Process process, string socketRoot)
    {
        _process = process;
        SocketRoot = socketRoot;
    }

    public int ProcessId => _process.Id;

    public string SocketRoot { get; }

    public static async Task<LiveTargetFixture> StartAsync()
    {
        var socketRoot = ResolveShortSocketRoot();
        var targetAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "LiveDiagnosticsTarget.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(targetAssembly);
        startInfo.Environment["TMPDIR"] = socketRoot;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start the live diagnostics target.");
        try
        {
            var readyLine = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            if (readyLine != "READY")
            {
                var error = await process.StandardError
                    .ReadToEndAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException(
                    $"The live diagnostics target did not signal readiness. stderr: {error}");
            }

            return new LiveTargetFixture(process, socketRoot);
        }
        catch
        {
            await StopAndDisposeAsync(process);
            throw;
        }
    }

    private static string ResolveShortSocketRoot()
    {
        var ambient = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", null);
            return Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambient);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAndDisposeAsync(_process);
    }

    private static async Task StopAndDisposeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The target may have already exited.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            process.Dispose();
        }
    }
}
