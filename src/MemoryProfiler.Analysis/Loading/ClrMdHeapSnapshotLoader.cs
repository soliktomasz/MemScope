using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using MemoryProfiler.Analysis.Values;
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

    IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress);

    IEnumerable<ObjectReference> EnumerateIncomingReferences(
        ulong targetAddress,
        CancellationToken cancellationToken);

    IEnumerable<ClrRootData> EnumerateRoots(CancellationToken cancellationToken);

    HeapObjectValueResult ReadObjectValues(
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Heap object value inspection is unavailable.");
}

internal readonly record struct ClrRootData(
    ulong ObjectAddress,
    ClrRootKind Kind,
    string? Name);

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

    public IEnumerable<ObjectReference> EnumerateOutgoingReferences(ulong sourceAddress)
    {
        var heap = _runtime.Heap;
        var source = heap.GetObject(sourceAddress);
        if (source.IsNull || !source.IsValid || source.IsFree)
        {
            yield break;
        }

        foreach (var reference in source.EnumerateReferencesWithFields(
            carefully: true,
            considerDependantHandles: false))
        {
            var target = reference.Object;
            if (target.IsNull || !target.IsValid || target.IsFree || target.Address == 0)
            {
                continue;
            }

            yield return new ObjectReference(
                sourceAddress,
                target.Address,
                reference.IsArrayElement ? ReferenceKind.ArrayElement : ReferenceKind.Field,
                reference.Field?.Name,
                SourceTypeName: source.Type?.Name,
                TargetTypeName: target.Type?.Name);
        }
    }

    public IEnumerable<ObjectReference> EnumerateIncomingReferences(
        ulong targetAddress,
        CancellationToken cancellationToken)
    {
        var heap = _runtime.Heap;
        foreach (var heapObject in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!heapObject.IsValid || heapObject.IsFree || heapObject.Address == 0)
            {
                continue;
            }

            foreach (var reference in heapObject.EnumerateReferencesWithFields(
                carefully: true,
                considerDependantHandles: false))
            {
                var candidate = reference.Object;
                if (candidate.IsNull || candidate.Address != targetAddress)
                {
                    continue;
                }

                yield return new ObjectReference(
                    heapObject.Address,
                    targetAddress,
                    reference.IsArrayElement ? ReferenceKind.ArrayElement : ReferenceKind.Field,
                    reference.Field?.Name,
                    SourceTypeName: heapObject.Type?.Name,
                    TargetTypeName: candidate.Type?.Name);
            }
        }

        foreach (var root in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootObject = root.Object;
            if (rootObject.IsNull || rootObject.Address != targetAddress)
            {
                continue;
            }

            yield return new ObjectReference(
                0,
                targetAddress,
                root.RootKind is ClrRootKind.StaticVar or ClrRootKind.ThreadStaticVar
                    ? ReferenceKind.StaticField
                    : ReferenceKind.Handle,
                Name: RootKindLabel(root.RootKind),
                SourceTypeName: null,
                TargetTypeName: rootObject.Type?.Name);
        }
    }

    public IEnumerable<ClrRootData> EnumerateRoots(CancellationToken cancellationToken)
    {
        // Static and thread-static field values are roots by definition, but
        // dumps do not always report them: the runtime's root set may only
        // carry a subset (on .NET Core, mutable statics frequently are absent
        // from dump root enumeration). Resolve the field values up front so
        // every static retention path is surfaced, and name any runtime
        // reported static roots with the same map.
        var (staticNames, threadStaticNames) = BuildStaticFieldNameMap(cancellationToken);
        var reported = new HashSet<ulong>();
        foreach (var root in _runtime.Heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootObject = root.Object;
            if (rootObject.IsNull ||
                !rootObject.IsValid ||
                rootObject.IsFree ||
                rootObject.Address == 0)
            {
                continue;
            }

            if (root.RootKind is ClrRootKind.StaticVar or ClrRootKind.ThreadStaticVar)
            {
                reported.Add(rootObject.Address);
            }

            yield return new ClrRootData(
                rootObject.Address,
                root.RootKind,
                Name: StaticName(staticNames, threadStaticNames, root));
        }

        foreach (var pair in staticNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reported.Contains(pair.Key))
            {
                yield return new ClrRootData(pair.Key, ClrRootKind.StaticVar, pair.Value);
            }
        }

        foreach (var pair in threadStaticNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reported.Contains(pair.Key))
            {
                yield return new ClrRootData(pair.Key, ClrRootKind.ThreadStaticVar, pair.Value);
            }
        }
    }

    private static string? StaticName(
        IReadOnlyDictionary<ulong, string> staticNames,
        IReadOnlyDictionary<ulong, string> threadStaticNames,
        ClrRoot root)
    {
        if (root.RootKind == ClrRootKind.ThreadStaticVar)
        {
            return threadStaticNames.TryGetValue(root.Object.Address, out var threadName)
                ? threadName
                : null;
        }

        if (root.RootKind == ClrRootKind.StaticVar)
        {
            return staticNames.TryGetValue(root.Object.Address, out var name)
                ? name
                : null;
        }

        return null;
    }

    // Static roots do not carry a field name in ClrMD, so the declaring
    // "Type.field" is resolved by matching static field values against the
    // object addresses. The same map feeds the merge of unreported statics.
    private (Dictionary<ulong, string> Static, Dictionary<ulong, string> ThreadStatic)
        BuildStaticFieldNameMap(CancellationToken cancellationToken)
    {
        var staticNames = new Dictionary<ulong, string>();
        var threadStaticNames = new Dictionary<ulong, string>();
        var domains = _runtime.AppDomains;
        var threads = _runtime.Threads;
        foreach (var module in _runtime.EnumerateModules())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var type in module.EnumerateTypesWithStaticFields())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var field in type.StaticFields)
                {
                    if (!field.IsObjectReference)
                    {
                        continue;
                    }

                    foreach (var domain in domains)
                    {
                        AddStaticName(
                            staticNames,
                            ReadObjectSafely(field, domain),
                            $"{type.Name}.{field.Name}");
                    }
                }

                foreach (var field in type.ThreadStaticFields)
                {
                    if (!field.IsObjectReference)
                    {
                        continue;
                    }

                    foreach (var thread in threads)
                    {
                        AddStaticName(
                            threadStaticNames,
                            ReadObjectSafely(field, thread),
                            $"{type.Name}.{field.Name}");
                    }
                }
            }
        }

        return (staticNames, threadStaticNames);
    }

    private static ClrObject ReadObjectSafely(ClrStaticField field, ClrAppDomain domain)
    {
        try
        {
            return field.ReadObject(domain);
        }
        catch
        {
            // Uninitialized or unreadable static fields carry no object.
            return default;
        }
    }

    private static ClrObject ReadObjectSafely(ClrThreadStaticField field, ClrThread thread)
    {
        try
        {
            return field.ReadObject(thread);
        }
        catch
        {
            // Uninitialized or unreadable thread-static fields carry no object.
            return default;
        }
    }

    private static void AddStaticName(
        Dictionary<ulong, string> names,
        ClrObject value,
        string name)
    {
        if (!value.IsNull && value.IsValid && value.Address != 0)
        {
            names.TryAdd(value.Address, name);
        }
    }

    internal static string RootKindLabel(ClrRootKind kind) =>
        kind switch
        {
            ClrRootKind.StaticVar => "Static field",
            ClrRootKind.ThreadStaticVar => "Thread static",
            ClrRootKind.Stack => "Stack",
            ClrRootKind.FinalizerQueue => "Finalizer queue",
            ClrRootKind.StrongHandle => "GC handle",
            ClrRootKind.PinnedHandle => "Pinned handle",
            ClrRootKind.RefCountedHandle => "Ref-counted handle",
            ClrRootKind.AsyncPinnedHandle => "Async pinned handle",
            ClrRootKind.SizedRefHandle => "Sized ref handle",
            _ => "GC root",
        };

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
