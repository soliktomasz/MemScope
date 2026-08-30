using MemoryProfiler.Diagnostics.Dumps;
using Xunit;

namespace MemoryProfiler.Diagnostics.Tests.Dumps;

public sealed class DumpCaptureServiceTests
{
    private static readonly DateTimeOffset CaptureTime =
        new(2026, 8, 28, 16, 25, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CaptureRejectsNonPositiveProcessIds(int processId)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.CaptureAsync(processId, Path.GetTempPath()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CaptureRejectsMissingDestination(string? destination)
    {
        var service = CreateService();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => service.CaptureAsync(4217, destination!));
    }

    [Fact]
    public async Task CaptureUsesSanitizedTimestampedFilenameAndReturnsFullPath()
    {
        var destination = Path.Combine(Path.GetTempPath(), "captures");
        var environment = new StubEnvironment("My/Api", CaptureTime);
        var writer = new StubWriter();
        var service = new DumpCaptureService(writer, environment);

        var result = await service.CaptureAsync(4217, destination);

        var expected = Path.GetFullPath(
            Path.Combine(destination, "My_Api-2026-08-28-162500.dmp"));
        Assert.Equal(expected, result);
        Assert.EndsWith(".partial.dmp", writer.Path, StringComparison.Ordinal);
        Assert.Equal((writer.Path, expected), Assert.Single(environment.Moves));
        Assert.Equal(Path.GetFullPath(destination), environment.CreatedDirectory);
    }

    [Fact]
    public async Task CaptureAddsNumericSuffixInsteadOfOverwriting()
    {
        var destination = Path.Combine(Path.GetTempPath(), "captures");
        var environment = new StubEnvironment("MyApi", CaptureTime);
        environment.ExistingPaths.UnionWith([
            Path.GetFullPath(Path.Combine(destination, "MyApi-2026-08-28-162500.dmp")),
            Path.GetFullPath(Path.Combine(destination, "MyApi-2026-08-28-162500-2.dmp"))]);
        var service = new DumpCaptureService(new StubWriter(), environment);

        var result = await service.CaptureAsync(4217, destination);

        Assert.Equal("MyApi-2026-08-28-162500-3.dmp", Path.GetFileName(result));
    }

    [Fact]
    public async Task CaptureFallsBackToProcessIdWhenNameHasNoUsableCharacters()
    {
        var environment = new StubEnvironment("///", CaptureTime);
        var service = new DumpCaptureService(new StubWriter(), environment);

        var result = await service.CaptureAsync(4217, Path.GetTempPath());

        Assert.Equal("process-4217-2026-08-28-162500.dmp", Path.GetFileName(result));
    }

    [Fact]
    public async Task CaptureForwardsProcessIdAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var writer = new StubWriter();
        var service = new DumpCaptureService(writer, StubEnvironment.Default);

        await service.CaptureAsync(4217, Path.GetTempPath(), cancellation.Token);

        Assert.Equal(4217, writer.ProcessId);
        Assert.Equal(cancellation.Token, writer.Token);
    }

    [Fact]
    public async Task CaptureRetriesAtomicMoveWhenFinalNameIsClaimedConcurrently()
    {
        var destination = Path.Combine(Path.GetTempPath(), "captures");
        var environment = StubEnvironment.Default;
        var basePath = Path.GetFullPath(
            Path.Combine(destination, "MyApi-2026-08-28-162500.dmp"));
        environment.MoveCollisions.Add(basePath);
        var writer = new StubWriter();
        var service = new DumpCaptureService(writer, environment);

        var result = await service.CaptureAsync(4217, destination);

        Assert.Equal("MyApi-2026-08-28-162500-2.dmp", Path.GetFileName(result));
        Assert.Contains(basePath, environment.ExistingPaths);
        Assert.Equal((writer.Path, result), environment.Moves[^1]);
    }

    [Fact]
    public async Task CaptureDeletesIncompleteOutputAndPreservesWriterFailure()
    {
        var failure = new IOException("capture failed");
        var environment = StubEnvironment.Default;
        var writer = new StubWriter((path, _) =>
        {
            environment.ExistingPaths.Add(path);
            return Task.FromException(failure);
        });
        var service = new DumpCaptureService(writer, environment);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => service.CaptureAsync(4217, Path.GetTempPath()));

        Assert.Same(failure, thrown);
        Assert.Equal([writer.Path], environment.DeletedPaths);
        Assert.EndsWith(".partial.dmp", writer.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureDeletesIncompleteOutputAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var environment = StubEnvironment.Default;
        var writer = new StubWriter((path, token) =>
        {
            environment.ExistingPaths.Add(path);
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        var service = new DumpCaptureService(writer, environment);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(4217, Path.GetTempPath(), cancellation.Token));

        Assert.Equal([writer.Path], environment.DeletedPaths);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceCaptureFailure()
    {
        var captureFailure = new IOException("capture failed");
        var environment = StubEnvironment.Default;
        environment.DeleteFailure = new UnauthorizedAccessException("delete failed");
        var writer = new StubWriter((path, _) =>
        {
            environment.ExistingPaths.Add(path);
            return Task.FromException(captureFailure);
        });
        var service = new DumpCaptureService(writer, environment);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => service.CaptureAsync(4217, Path.GetTempPath()));

        Assert.Same(captureFailure, thrown);
    }

    private static DumpCaptureService CreateService() =>
        new(new StubWriter(), StubEnvironment.Default);

    private sealed class StubWriter(
        Func<string, CancellationToken, Task>? write = null) : IDumpWriter
    {
        public int ProcessId { get; private set; }
        public string Path { get; private set; } = string.Empty;
        public CancellationToken Token { get; private set; }

        public Task WriteAsync(
            int processId,
            string path,
            CancellationToken cancellationToken)
        {
            ProcessId = processId;
            Path = path;
            Token = cancellationToken;
            return write?.Invoke(path, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class StubEnvironment(
        string processName,
        DateTimeOffset localNow) : IDumpCaptureEnvironment
    {
        public static StubEnvironment Default => new("MyApi", CaptureTime);

        public DateTimeOffset LocalNow => localNow;
        public Exception? DeleteFailure { get; set; }
        public string? CreatedDirectory { get; private set; }
        public HashSet<string> ExistingPaths { get; } = [];
        public HashSet<string> MoveCollisions { get; } = [];
        public List<string> DeletedPaths { get; } = [];
        public List<(string Source, string Destination)> Moves { get; } = [];

        public string GetProcessName(int processId) => processName;

        public void CreateDirectory(string path) => CreatedDirectory = path;

        public bool FileExists(string path) => ExistingPaths.Contains(path);

        public string CreateTemporaryPath(string directory) =>
            Path.Combine(directory, ".memscope-test.partial.dmp");

        public void MoveFile(string source, string destination)
        {
            Moves.Add((source, destination));
            if (ExistingPaths.Contains(destination) || MoveCollisions.Remove(destination))
            {
                ExistingPaths.Add(destination);
                throw new IOException("The destination already exists.");
            }

            ExistingPaths.Remove(source);
            ExistingPaths.Add(destination);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Add(path);
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            ExistingPaths.Remove(path);
        }
    }
}
