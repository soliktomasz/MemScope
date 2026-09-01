namespace MemoryProfiler.App.Errors;

public enum ProfilerErrorKind
{
    ProcessExited,
    AccessDenied,
    UnsupportedRuntime,
    UnableToAttach,
    DumpCaptureFailed,
    DumpCorrupted,
    ClrRuntimeNotFound,
    SnapshotIncompatible,
    InsufficientDiskSpace,
    AnalysisCancelled,
    Unexpected,
}

public sealed record ProfilerError(
    ProfilerErrorKind Kind,
    string Title,
    string Message,
    string TechnicalDetails)
{
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);
}

public enum ProfilerOperation
{
    DiscoverProcesses,
    Attach,
    ObserveSession,
    CaptureDump,
    OpenSnapshot,
    AnalyzeSnapshot,
    CompareSnapshots,
    ChooseFile,
    RestoreSessions,
    SaveSessions,
}
