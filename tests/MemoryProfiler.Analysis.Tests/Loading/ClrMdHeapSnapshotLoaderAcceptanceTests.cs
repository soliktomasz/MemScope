using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using MemoryProfiler.Analysis.Loading;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Loading;

public sealed class ClrMdHeapSnapshotLoaderAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpProducesManagedHeapTypes()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-analysis-{Guid.NewGuid():N}.dmp");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveTargetFixture? fixture = null;

        try
        {
            fixture = await LiveTargetFixture.StartAsync();
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var client = new DiagnosticsClient(fixture.ProcessId);
            var captureStarted = DateTimeOffset.UtcNow;
            await client.WriteDumpAsync(
                DumpType.WithHeap,
                destination,
                WriteDumpFlags.None,
                timeout.Token);
            var captureFinished = DateTimeOffset.UtcNow;

            var snapshot = await new ClrMdHeapSnapshotLoader()
                .LoadAsync(destination, timeout.Token);

            Assert.Equal(destination, snapshot.Info.Path);
            Assert.True(
                snapshot.Info.ProcessId is null ||
                snapshot.Info.ProcessId == fixture.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Info.RuntimeVersion));
            Assert.InRange(
                snapshot.Info.CapturedAt,
                captureStarted.AddSeconds(-1),
                captureFinished.AddSeconds(1));
            Assert.True(snapshot.Info.ObjectCount > 0);
            Assert.True(snapshot.Info.HeapSize > 0);
            Assert.NotEmpty(snapshot.Types);
            Assert.Contains(snapshot.Types, type => type.Name == "System.String");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", ambientTempDir);
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            try
            {
                File.Delete(destination);
            }
            catch
            {
                // Preserve any capture or analysis failure; cleanup is best effort.
            }
        }
    }

    private sealed class LiveTargetFixture : IAsyncDisposable
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
}
