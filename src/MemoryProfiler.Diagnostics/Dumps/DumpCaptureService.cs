using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

namespace MemoryProfiler.Diagnostics.Dumps;

public sealed class DumpCaptureService : IDumpCaptureService
{
    private readonly IDumpWriter _writer;
    private readonly IDumpCaptureEnvironment _environment;

    public DumpCaptureService()
        : this(new DiagnosticsClientDumpWriter(), new SystemDumpCaptureEnvironment())
    {
    }

    internal DumpCaptureService(
        IDumpWriter writer,
        IDumpCaptureEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);
        _writer = writer;
        _environment = environment;
    }

    public async Task<string> CaptureAsync(
        int processId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "The process identifier must be a positive integer.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDirectory = Path.GetFullPath(destinationDirectory);
        _environment.CreateDirectory(fullDirectory);
        var processName = SanitizeProcessName(_environment.GetProcessName(processId));
        if (processName.Length == 0)
        {
            processName = $"process-{processId}";
        }

        var timestamp = _environment.LocalNow.ToString(
            "yyyy-MM-dd-HHmmss",
            CultureInfo.InvariantCulture);
        var temporaryPath = _environment.CreateTemporaryPath(fullDirectory);

        try
        {
            await _writer
                .WriteAsync(processId, temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return MoveToAvailablePath(
                temporaryPath,
                fullDirectory,
                processName,
                timestamp);
        }
        catch
        {
            TryDeleteIncompleteFile(temporaryPath);
            throw;
        }
    }

    private string MoveToAvailablePath(
        string temporaryPath,
        string directory,
        string processName,
        string timestamp)
    {
        var baseName = $"{processName}-{timestamp}";
        var suffix = 1;
        while (true)
        {
            var suffixText = suffix == 1 ? string.Empty : $"-{suffix}";
            var path = Path.Combine(directory, $"{baseName}{suffixText}.dmp");
            try
            {
                _environment.MoveFile(temporaryPath, path);
                return path;
            }
            catch (IOException) when (_environment.FileExists(path))
            {
                suffix++;
            }
        }
    }

    private void TryDeleteIncompleteFile(string path)
    {
        try
        {
            if (_environment.FileExists(path))
            {
                _environment.DeleteFile(path);
            }
        }
        catch
        {
            // Preserve the capture failure; cleanup is best effort.
        }
    }

    private static string SanitizeProcessName(string processName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(processName
            .Select(character =>
                invalidCharacters.Contains(character) ||
                character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar
                    ? '_'
                    : character)
            .ToArray());
        return sanitized.Trim(' ', '.', '_');
    }
}

internal interface IDumpWriter
{
    Task WriteAsync(
        int processId,
        string path,
        CancellationToken cancellationToken);
}

internal sealed class DiagnosticsClientDumpWriter : IDumpWriter
{
    public Task WriteAsync(
        int processId,
        string path,
        CancellationToken cancellationToken)
    {
        var client = new DiagnosticsClient(processId);
        return client.WriteDumpAsync(
            DumpType.WithHeap,
            path,
            WriteDumpFlags.None,
            cancellationToken);
    }
}

internal interface IDumpCaptureEnvironment
{
    DateTimeOffset LocalNow { get; }

    string GetProcessName(int processId);

    void CreateDirectory(string path);

    string CreateTemporaryPath(string directory);

    bool FileExists(string path);

    void MoveFile(string source, string destination);

    void DeleteFile(string path);
}

internal sealed class SystemDumpCaptureEnvironment : IDumpCaptureEnvironment
{
    public DateTimeOffset LocalNow => DateTimeOffset.Now;

    public string GetProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string CreateTemporaryPath(string directory)
    {
        while (true)
        {
            var path = Path.Combine(
                directory,
                $".memscope-{Guid.NewGuid():N}.partial.dmp");
            try
            {
                using var reservation = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Retry the practically impossible random-name collision.
            }
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public void MoveFile(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    public void DeleteFile(string path) => File.Delete(path);
}
