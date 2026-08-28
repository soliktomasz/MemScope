using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.Diagnostics.Processes;

internal sealed class DiagnosticsProcessSource : IProcessDiagnosticsSource
{
    private readonly IProcessEndpointProbe _endpointProbe;
    private readonly IProcessHandleSource _processHandles;

    public DiagnosticsProcessSource()
        : this(new DiagnosticsClientEndpointProbe(), new SystemProcessHandleSource())
    {
    }

    internal DiagnosticsProcessSource(IProcessEndpointProbe endpointProbe)
        : this(endpointProbe, new SystemProcessHandleSource())
    {
    }

    internal DiagnosticsProcessSource(
        IProcessEndpointProbe endpointProbe,
        IProcessHandleSource processHandles)
    {
        ArgumentNullException.ThrowIfNull(endpointProbe);
        ArgumentNullException.ThrowIfNull(processHandles);
        _endpointProbe = endpointProbe;
        _processHandles = processHandles;
    }

    public IEnumerable<int> GetPublishedProcessIds() => DiagnosticsClient.GetPublishedProcesses();

    public async ValueTask<DiscoveredProcess> InspectAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = _processHandles.Open(processId);
        var processName = process.Name;
        var startTime = process.StartTime;
        await _endpointProbe
            .ValidateAsync(processId, cancellationToken)
            .ConfigureAwait(false);

        process.Refresh();
        if (process.HasExited || process.StartTime != startTime)
        {
            throw new InvalidOperationException(
                $"Process {processId} exited or its PID was reused during inspection.");
        }

        string? runtimeVersion;

        try
        {
            runtimeVersion = SelectRuntimeVersion(process.Modules);
        }
        catch (Exception exception) when (ProcessInspectionFailure.IsExpected(exception))
        {
            runtimeVersion = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DiscoveredProcess(processName, runtimeVersion);
    }

    internal static string? SelectRuntimeVersion(
        IEnumerable<RuntimeModuleMetadata> modules)
    {
        foreach (var module in modules)
        {
            if (!module.Name.Contains("coreclr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return NormalizeVersion(module.Version);
        }

        return null;
    }

    private static string? NormalizeVersion(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var numericVersion = value.Split([' ', '+', '-'], 2)[0];
        return Version.TryParse(numericVersion, out var version) && version.Build >= 0
            ? new Version(version.Major, version.Minor, version.Build).ToString()
            : value;
    }
}

internal sealed record RuntimeModuleMetadata(string Name, string? Version);
