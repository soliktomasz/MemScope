using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Loading;

public sealed class ClrMdHeapSnapshotLoader : IHeapSnapshotLoader
{
    private readonly IHeapDumpSourceFactory _sourceFactory;

    public ClrMdHeapSnapshotLoader()
        : this(new ClrMdHeapDumpSourceFactory())
    {
    }

    internal ClrMdHeapSnapshotLoader(IHeapDumpSourceFactory sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
    }

    public Task<HeapSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        return Task.Run(
            () => Load(fullPath, cancellationToken),
            cancellationToken);
    }

    private HeapSnapshot Load(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = _sourceFactory.Open(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (!source.CanWalkHeap)
        {
            throw new InvalidDataException(
                "The dump was captured while the GC heap was not walkable.");
        }

        var types = new Dictionary<ulong, TypeAccumulator>();
        long objectCount = 0;
        ulong heapSize = 0;

        foreach (var heapObject in source.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!heapObject.IsValid ||
                heapObject.IsFree ||
                heapObject.MethodTable == 0 ||
                string.IsNullOrWhiteSpace(heapObject.TypeName))
            {
                continue;
            }

            checked
            {
                objectCount++;
                heapSize += heapObject.Size;
            }

            if (!types.TryGetValue(heapObject.MethodTable, out var type))
            {
                type = new TypeAccumulator(
                    heapObject.MethodTable,
                    heapObject.TypeName,
                    heapObject.AssemblyName);
                types.Add(heapObject.MethodTable, type);
            }

            type.Add(heapObject.Size);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new HeapSnapshot
        {
            Info = new HeapSnapshotInfo(
                path,
                source.ProcessName,
                source.ProcessId,
                source.RuntimeVersion,
                source.CapturedAt,
                objectCount,
                heapSize),
            Types = types.Values
                .Select(type => type.ToInfo())
                .OrderByDescending(type => type.ShallowSize)
                .ThenBy(type => type.Name, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private sealed class TypeAccumulator(
        ulong methodTable,
        string name,
        string? assemblyName)
    {
        private long _objectCount;
        private ulong _shallowSize;

        public void Add(ulong size)
        {
            checked
            {
                _objectCount++;
                _shallowSize += size;
            }
        }

        public HeapTypeInfo ToInfo() => new(
            methodTable,
            name,
            assemblyName,
            _objectCount,
            _shallowSize,
            RetainedSize: null);
    }
}

internal readonly record struct HeapObjectData(
    ulong MethodTable,
    string? TypeName,
    string? AssemblyName,
    ulong Size,
    ulong Address = 0,
    bool IsValid = true,
    bool IsFree = false);

internal interface IHeapDumpSourceFactory
{
    IHeapDumpSource Open(string path);
}

internal interface IHeapDumpSource : IDisposable
{
    string? ProcessName { get; }

    int? ProcessId { get; }

    string RuntimeVersion { get; }

    DateTimeOffset CapturedAt { get; }

    bool CanWalkHeap { get; }

    IEnumerable<HeapObjectData> EnumerateObjects();

    Generation? GetGeneration(ulong address);
}

internal sealed class ClrMdHeapDumpSourceFactory : IHeapDumpSourceFactory
{
    public IHeapDumpSource Open(string path) => new ClrMdHeapDumpSource(path);
}

internal sealed class ClrMdHeapDumpSource : IHeapDumpSource
{
    private readonly DataTarget _dataTarget;
    private readonly ClrRuntime _runtime;

    public ClrMdHeapDumpSource(string path)
    {
        _dataTarget = DataTarget.LoadDump(path);
        try
        {
            var clrInfo = _dataTarget.ClrVersions.FirstOrDefault()
                ?? throw new InvalidDataException(
                    "The dump does not contain a discoverable CLR runtime.");
            _runtime = clrInfo.CreateRuntime();
            var processInfo = (_dataTarget.DataReader as IProcessInfoProvider)
                ?.GetProcessInfo();
            ProcessName = GetProcessName(processInfo?.ImagePath);
            var processId = processInfo?.ProcessId;
            ProcessId = processId is > 0 and <= int.MaxValue
                ? (int)processId.Value
                : NormalizeProcessId(_dataTarget.DataReader.ProcessId);
            RuntimeVersion = clrInfo.Version.ToString();
            CapturedAt = processInfo?.DumpTimestampUtc ?? File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            _dataTarget.Dispose();
            throw;
        }
    }

    public string? ProcessName { get; }

    public int? ProcessId { get; }

    public string RuntimeVersion { get; }

    public DateTimeOffset CapturedAt { get; }

    public bool CanWalkHeap => _runtime.Heap.CanWalkHeap;

    public IEnumerable<HeapObjectData> EnumerateObjects()
    {
        foreach (var heapObject in _runtime.Heap.EnumerateObjects())
        {
            var type = heapObject.Type;
            yield return new HeapObjectData(
                type?.MethodTable ?? 0,
                type?.Name,
                type?.Module?.AssemblyName,
                heapObject.Size,
                heapObject.Address,
                heapObject.IsValid,
                heapObject.IsFree);
        }
    }

    public Generation? GetGeneration(ulong address)
    {
        var segment = _runtime.Heap.GetSegmentByAddress(address);
        return segment?.GetGeneration(address);
    }

    public void Dispose() => _dataTarget.Dispose();

    private static string? GetProcessName(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var processName = Path.GetFileNameWithoutExtension(imagePath);
        return string.IsNullOrWhiteSpace(processName) ? null : processName;
    }

    private static int? NormalizeProcessId(int processId) =>
        processId > 0 ? processId : null;
}
