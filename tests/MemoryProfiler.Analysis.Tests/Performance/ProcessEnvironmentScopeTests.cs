using MemoryProfiler.TestInfrastructure;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Performance;

[Collection("Live diagnostics")]
public sealed class ProcessEnvironmentScopeTests
{
    [Fact]
    public async Task ConcurrentTempDirectoryScopesEnterSeriallyAndRestoreOriginalValue()
    {
        var original = Environment.GetEnvironmentVariable("TMPDIR");
        var first = await ProcessEnvironmentScope.EnterTempDirectoryAsync(
            "/tmp/memscope-scope-one");
        var secondTask = ProcessEnvironmentScope.EnterTempDirectoryAsync(
            "/tmp/memscope-scope-two");
        var secondCompletedBeforeRelease = secondTask.IsCompleted;

        await first.DisposeAsync();
        var second = await secondTask;
        var secondValue = Environment.GetEnvironmentVariable("TMPDIR");
        await second.DisposeAsync();

        Assert.False(secondCompletedBeforeRelease);
        Assert.Equal("/tmp/memscope-scope-two", secondValue);
        Assert.Equal(original, Environment.GetEnvironmentVariable("TMPDIR"));
    }
}
