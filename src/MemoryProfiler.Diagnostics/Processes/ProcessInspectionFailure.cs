using System.ComponentModel;
using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.Diagnostics.Processes;

internal static class ProcessInspectionFailure
{
    public static bool IsExpected(Exception exception) => exception is
        ArgumentException or
        DiagnosticsClientException or
        IOException or
        InvalidOperationException or
        NotSupportedException or
        TimeoutException or
        UnauthorizedAccessException or
        Win32Exception;
}
