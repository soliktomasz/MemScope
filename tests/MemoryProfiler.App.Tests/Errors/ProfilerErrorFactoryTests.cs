using MemoryProfiler.App.Errors;
using Xunit;

namespace MemoryProfiler.App.Tests.Errors;

public sealed class ProfilerErrorFactoryTests
{
    public static TheoryData<ProfilerOperation, Exception, ProfilerErrorKind, string> Cases =>
        new()
        {
            {
                ProfilerOperation.Attach,
                new ArgumentException("Process 8124 is no longer running."),
                ProfilerErrorKind.ProcessExited,
                "Process exited"
            },
            {
                ProfilerOperation.ObserveSession,
                new IOException("Transport closed."),
                ProfilerErrorKind.ProcessExited,
                "Process exited"
            },
            {
                ProfilerOperation.Attach,
                new UnauthorizedAccessException("Diagnostics socket denied access."),
                ProfilerErrorKind.AccessDenied,
                "Access denied"
            },
            {
                ProfilerOperation.Attach,
                new NotSupportedException("Runtime protocol 99 is unsupported."),
                ProfilerErrorKind.UnsupportedRuntime,
                "Unsupported runtime"
            },
            {
                ProfilerOperation.Attach,
                new IOException("Diagnostics endpoint unavailable."),
                ProfilerErrorKind.UnableToAttach,
                "Unable to attach"
            },
            {
                ProfilerOperation.CaptureDump,
                new IOException("Writer stopped."),
                ProfilerErrorKind.DumpCaptureFailed,
                "Dump capture failed"
            },
            {
                ProfilerOperation.OpenSnapshot,
                new InvalidDataException("Invalid dump header."),
                ProfilerErrorKind.DumpCorrupted,
                "Dump corrupted"
            },
            {
                ProfilerOperation.OpenSnapshot,
                new InvalidDataException("CLR runtime lookup failed at internal address 0x123."),
                ProfilerErrorKind.ClrRuntimeNotFound,
                "CLR runtime not found"
            },
            {
                ProfilerOperation.CompareSnapshots,
                new NotSupportedException("Architecture mismatch."),
                ProfilerErrorKind.SnapshotIncompatible,
                "Snapshot incompatible"
            },
            {
                ProfilerOperation.CaptureDump,
                new DiskFullIOException("No space left on device."),
                ProfilerErrorKind.InsufficientDiskSpace,
                "Insufficient disk space"
            },
            {
                ProfilerOperation.AnalyzeSnapshot,
                new OperationCanceledException("Analysis stopped."),
                ProfilerErrorKind.AnalysisCancelled,
                "Analysis cancelled"
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void CreateMapsExpectedFailureToSafeUserFacingError(
        ProfilerOperation operation,
        Exception exception,
        ProfilerErrorKind expectedKind,
        string expectedTitle)
    {
        var error = ProfilerErrorFactory.Create(operation, exception, processId: 8124);

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal(expectedTitle, error.Title);
        Assert.DoesNotContain(exception.Message, error.Message, StringComparison.Ordinal);
        Assert.Contains(exception.Message, error.TechnicalDetails, StringComparison.Ordinal);
        Assert.True(error.HasTechnicalDetails);
    }

    [Fact]
    public void CreateIncludesProcessContextWithoutPuttingExceptionTextInPrimaryMessage()
    {
        var error = ProfilerErrorFactory.Create(
            ProfilerOperation.Attach,
            new IOException("Secret transport detail."),
            processId: 8124);

        Assert.Contains("8124", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret transport detail.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Secret transport detail.", error.TechnicalDetails, StringComparison.Ordinal);
    }

    private sealed class DiskFullIOException : IOException
    {
        public DiskFullIOException(string message)
            : base(message)
        {
            HResult = unchecked((int)0x80070070);
        }
    }
}
