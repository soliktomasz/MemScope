using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Diagnostics.Dumps;
using MemoryProfiler.Diagnostics.Tests.Sessions;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Dumps;

public sealed class DumpCaptureAcceptanceTests
{
    [Fact]
    public async Task CapturedDumpCanImmediatelyBeOpenedByClrMd()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"memscope-dump-{Guid.NewGuid():N}");
        var ambientTempDir = Environment.GetEnvironmentVariable("TMPDIR");
        LiveDiagnosticsTargetFixture? fixture = null;

        try
        {
            Directory.CreateDirectory(destination);
            fixture = await LiveDiagnosticsTargetFixture.StartAsync();
            Environment.SetEnvironmentVariable("TMPDIR", fixture.SocketRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var service = new DumpCaptureService();

            var dumpPath = await service.CaptureAsync(
                fixture.ProcessId,
                destination,
                timeout.Token);

            using var dataTarget = DataTarget.LoadDump(dumpPath);
            Assert.NotEmpty(dataTarget.ClrVersions);
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
                Directory.Delete(destination, recursive: true);
            }
            catch
            {
                // Preserve any capture or ClrMD failure; test cleanup is best effort.
            }
        }
    }
}
