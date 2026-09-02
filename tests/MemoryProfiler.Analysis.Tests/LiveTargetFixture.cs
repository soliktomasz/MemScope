using System.Diagnostics;

namespace MemoryProfiler.Analysis.Tests;

internal enum LiveTargetMode
{
    Default,
    LeakPhase,
    ObjectValues,
}

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

    public static Task<LiveTargetFixture> StartAsync() =>
        StartAsync(LiveTargetMode.Default);

    // Leak phase arms the target for the snapshot-comparison acceptance test:
    // it pre-allocates a fixed baseline chunk set, signals READY, then blocks on
    // stdin until StartLeakAsync sends the "LEAK" signal. A before-dump captured
    // right after StartAsync therefore contains exactly the baseline.
    public static Task<LiveTargetFixture> StartAsync(bool leakPhase) =>
        StartAsync(leakPhase ? LiveTargetMode.LeakPhase : LiveTargetMode.Default);

    public static async Task<LiveTargetFixture> StartAsync(LiveTargetMode mode)
    {
        var socketRoot = ResolveShortSocketRoot();
        var targetAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "LiveDiagnosticsTarget.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = mode == LiveTargetMode.LeakPhase,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(targetAssembly);
        if (mode == LiveTargetMode.LeakPhase)
        {
            startInfo.ArgumentList.Add("--leak");
        }
        else if (mode == LiveTargetMode.ObjectValues)
        {
            startInfo.ArgumentList.Add("--object-values");
        }

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

    // Signals the leak-mode target to start growing its unbounded chunk list and
    // waits until it confirms the leak loop is running, so a dump captured right
    // after this call contains leaked chunks. The target's stdout can carry
    // unrelated lines ([createdump] progress from a prior dump capture), so the
    // confirmation is searched for rather than expected on the next line.
    public async Task StartLeakAsync()
    {
        await _process.StandardInput.WriteLineAsync("LEAK");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var line = await _process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(timeout.Token);
            if (line == "LEAKING")
            {
                return;
            }
        }

        throw new TimeoutException(
            "The leak-mode target did not confirm the leak loop.");
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
