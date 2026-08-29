using System.Diagnostics;
using MemoryProfiler.Diagnostics.Sessions;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Sessions;

public sealed class LiveDiagnosticsAcceptanceTests
{
    [Fact]
    public async Task ConnectsToALiveTargetAndReceivesManagedMemorySamples()
    {
        var fixture = await LiveDiagnosticsTargetFixture.StartAsync();
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            var factory = new LiveDiagnosticsSessionFactory();
            await using var session = await factory.ConnectAsync(fixture.ProcessId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            var receivedSample = false;
            await foreach (var metrics in session.ObserveMemoryAsync(timeout.Token))
            {
                if (metrics.ManagedHeapSize > 0)
                {
                    receivedSample = true;
                    break;
                }
            }

            Assert.True(
                receivedSample,
                "The live target did not produce a non-zero managed-memory sample.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambientTempDir);
            await fixture.DisposeAsync();
        }
    }
}

internal sealed class LiveDiagnosticsTargetFixture : IAsyncDisposable
{
    private readonly Process _process;

    private LiveDiagnosticsTargetFixture(Process process, string socketRoot)
    {
        _process = process;
        SocketRoot = socketRoot;
    }

    public int ProcessId => _process.Id;

    public string SocketRoot { get; }

    public static async Task<LiveDiagnosticsTargetFixture> StartAsync()
    {
        var socketRoot = ResolveShortSocketRoot();
        var targetAssembly = Path.Combine(AppContext.BaseDirectory, "LiveDiagnosticsTarget.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(targetAssembly);
        startInfo.Environment["TMPDIR"] = socketRoot;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the live diagnostics target.");
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

        return new LiveDiagnosticsTargetFixture(process, socketRoot);
    }

    private static string ResolveShortSocketRoot()
    {
        // Unix diagnostics endpoints place a socket under the temp directory, which is limited
        // to 108 characters. Use the short platform temp root (e.g. /tmp) so the endpoint path
        // always fits. On Windows the temp path is used as-is and length is not a concern.
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
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The target may have already exited.
        }

        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _process.Dispose();
    }
}
