namespace MemoryProfiler.Diagnostics.Processes;

internal interface IProcessHandleSource
{
    IProcessHandle Open(int processId);
}

internal interface IProcessHandle : IDisposable
{
    string Name { get; }

    DateTimeOffset StartTime { get; }

    bool HasExited { get; }

    IEnumerable<RuntimeModuleMetadata> Modules { get; }

    void Refresh();
}

internal sealed class SystemProcessHandleSource : IProcessHandleSource
{
    public IProcessHandle Open(int processId) =>
        new SystemProcessHandle(System.Diagnostics.Process.GetProcessById(processId));
}

internal sealed class SystemProcessHandle(System.Diagnostics.Process process) : IProcessHandle
{
    public string Name => process.ProcessName;

    public DateTimeOffset StartTime => process.StartTime;

    public bool HasExited => process.HasExited;

    public IEnumerable<RuntimeModuleMetadata> Modules => process.Modules
        .Cast<System.Diagnostics.ProcessModule>()
        .Select(module => new RuntimeModuleMetadata(
            module.ModuleName,
            EmptyAsNull(module.FileVersionInfo.ProductVersion) ??
            EmptyAsNull(module.FileVersionInfo.FileVersion)));

    public void Refresh() => process.Refresh();

    public void Dispose() => process.Dispose();

    private static string? EmptyAsNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
