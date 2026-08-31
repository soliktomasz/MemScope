namespace MemoryProfiler.App.Errors;

public static class ProfilerErrorFactory
{
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;
    private const int UnixNoSpaceLeft = 0x1C;

    public static ProfilerError Create(
        ProfilerOperation operation,
        Exception exception,
        int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var kind = Classify(operation, exception);
        var processDescription = processId is > 0
            ? $"process {processId.Value}"
            : "the selected process";
        var (title, message) = kind switch
        {
            ProfilerErrorKind.ProcessExited => (
                "Process exited",
                operation == ProfilerOperation.ObserveSession
                    ? $"The diagnostics session for {processDescription} ended because the process exited."
                    : $"Could not attach to {processDescription}. The process exited before the diagnostics session was established."),
            ProfilerErrorKind.AccessDenied => (
                "Access denied",
                $"MemScope does not have permission to access {Target(operation, processDescription)}."),
            ProfilerErrorKind.UnsupportedRuntime => (
                "Unsupported runtime",
                $"Could not attach to {processDescription}. Its .NET runtime is not supported."),
            ProfilerErrorKind.UnableToAttach => (
                "Unable to attach",
                $"Could not attach to {processDescription}. The diagnostics endpoint did not accept the connection."),
            ProfilerErrorKind.DumpCaptureFailed => (
                "Dump capture failed",
                $"The memory dump for {processDescription} could not be captured."),
            ProfilerErrorKind.DumpCorrupted => (
                "Dump corrupted",
                "The selected file is not a readable memory dump."),
            ProfilerErrorKind.ClrRuntimeNotFound => (
                "CLR runtime not found",
                "The dump does not contain a discoverable CLR runtime."),
            ProfilerErrorKind.SnapshotIncompatible => (
                "Snapshot incompatible",
                "The snapshot is not compatible with this version of MemScope."),
            ProfilerErrorKind.InsufficientDiskSpace => (
                "Insufficient disk space",
                "There is not enough free disk space to capture the memory dump."),
            ProfilerErrorKind.AnalysisCancelled => (
                "Analysis cancelled",
                "Snapshot analysis was cancelled before it completed."),
            _ => (
                "Operation failed",
                SafeFallback(operation)),
        };

        return new ProfilerError(kind, title, message, exception.ToString());
    }

    private static ProfilerErrorKind Classify(
        ProfilerOperation operation,
        Exception exception)
    {
        if (exception is OperationCanceledException &&
            operation is ProfilerOperation.OpenSnapshot or
                ProfilerOperation.AnalyzeSnapshot or
                ProfilerOperation.CompareSnapshots)
        {
            return ProfilerErrorKind.AnalysisCancelled;
        }

        if (Contains<UnauthorizedAccessException>(exception))
        {
            return ProfilerErrorKind.AccessDenied;
        }

        if (IsDiskFull(exception))
        {
            return ProfilerErrorKind.InsufficientDiskSpace;
        }

        if (operation == ProfilerOperation.Attach)
        {
            return exception switch
            {
                ArgumentException => ProfilerErrorKind.ProcessExited,
                NotSupportedException => ProfilerErrorKind.UnsupportedRuntime,
                _ => ProfilerErrorKind.UnableToAttach,
            };
        }

        if (operation == ProfilerOperation.ObserveSession)
        {
            return ProfilerErrorKind.ProcessExited;
        }

        if (operation == ProfilerOperation.CaptureDump)
        {
            return ProfilerErrorKind.DumpCaptureFailed;
        }

        if (operation is ProfilerOperation.OpenSnapshot or
            ProfilerOperation.AnalyzeSnapshot or
            ProfilerOperation.CompareSnapshots)
        {
            if (ContainsClrRuntimeMessage(exception))
            {
                return ProfilerErrorKind.ClrRuntimeNotFound;
            }

            return exception switch
            {
                InvalidDataException or BadImageFormatException =>
                    ProfilerErrorKind.DumpCorrupted,
                NotSupportedException =>
                    ProfilerErrorKind.SnapshotIncompatible,
                _ => ProfilerErrorKind.SnapshotIncompatible,
            };
        }

        return ProfilerErrorKind.Unexpected;
    }

    private static string Target(ProfilerOperation operation, string processDescription) =>
        operation switch
        {
            ProfilerOperation.Attach or ProfilerOperation.CaptureDump => processDescription,
            ProfilerOperation.OpenSnapshot or
                ProfilerOperation.AnalyzeSnapshot or
                ProfilerOperation.CompareSnapshots => "the selected snapshot",
            _ => "the requested resource",
        };

    private static string SafeFallback(ProfilerOperation operation) =>
        operation switch
        {
            ProfilerOperation.DiscoverProcesses =>
                "MemScope could not discover running .NET processes.",
            ProfilerOperation.ChooseFile =>
                "MemScope could not open the file picker.",
            ProfilerOperation.RestoreSessions =>
                "Recent sessions could not be restored.",
            ProfilerOperation.SaveSessions =>
                "Recent sessions could not be saved.",
            _ => "The requested operation could not be completed.",
        };

    private static bool Contains<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsClrRuntimeMessage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("CLR runtime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDiskFull(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not IOException)
            {
                continue;
            }

            var code = current.HResult & 0xFFFF;
            if (code is ErrorHandleDiskFull or ErrorDiskFull or UnixNoSpaceLeft)
            {
                return true;
            }
        }

        return false;
    }
}
