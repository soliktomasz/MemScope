using MemoryProfiler.Contracts.Processes;

namespace MemoryProfiler.App.ViewModels;

public sealed class ProcessRowViewModel
{
    public ProcessRowViewModel(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ProcessId = process.ProcessId;
        Name = process.Name;
        RuntimeVersion = process.RuntimeVersion;
        ParsedRuntimeVersion = Version.TryParse(process.RuntimeVersion, out var version)
            ? version
            : null;
    }

    public int ProcessId { get; }

    public string Name { get; }

    public string? RuntimeVersion { get; }

    public string RuntimeDisplay => RuntimeVersion is null
        ? "Runtime unavailable"
        : $".NET {RuntimeVersion}";

    internal Version? ParsedRuntimeVersion { get; }
}
