# Top Retainers and Object Values Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose object-level retained-memory owners and immediate managed field values so a user can identify a cache object, measure the full graph it retains, and inspect its scalar state from a dump.

**Architecture:** Keep `DominatorTreeService` as the retained-memory source and retain its completed `DominatorAnalysisResult` in `SnapshotViewModel`. Add an on-demand `IHeapObjectValueService` over the existing dump-source seam, then compose its bounded field results with retained metrics in independent Top Retainers and Object Details view models.

**Tech Stack:** .NET 10, C#, ClrMD 4.0.732401, Avalonia 12.1.1 DataGrid/MVVM, xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-09-01-top-retainers-and-object-values-design.md`

## Global Constraints

- Display decoded values immediately and keep the warning `Dump values may contain credentials, personal data, or other secrets.` visible whenever Object Details is active.
- Decode immediate fields only; do not recursively expand object graphs or reconstruct JSON.
- Normal string reads stop at 4,096 characters; expanded reads stop at 1,048,576 characters.
- Array pages contain at most 500 elements and use stable zero-based indices.
- Never store decoded values in navigation history, session storage, logs, telemetry, or exception/status text.
- Keep all dump reads and million-object scans off the UI thread, cancellable, version-guarded, and disposable.
- Keep `DominatorTreeService` and `DominatorInfo` semantics unchanged.
- Use invariant text for decoded target values; continue using current-culture formatting for MemScope counts and sizes.
- Add no package or framework dependency.
- Preserve the existing Types, Instances, References, Paths to Root, Back, and Forward workflows.

---

### Task 1: Value contracts and the on-demand analysis boundary

**Files:**
- Create: `src/MemoryProfiler.Contracts/Heap/HeapValueKind.cs`
- Create: `src/MemoryProfiler.Contracts/Heap/HeapFieldValue.cs`
- Create: `src/MemoryProfiler.Contracts/Heap/HeapObjectValueResult.cs`
- Modify: `tests/MemoryProfiler.Contracts.Tests/ContractSerializationTests.cs`
- Create: `src/MemoryProfiler.Analysis/Values/ObjectValueReadOptions.cs`
- Create: `src/MemoryProfiler.Analysis/Values/IHeapObjectValueService.cs`
- Create: `src/MemoryProfiler.Analysis/Values/ClrMdHeapObjectValueService.cs`
- Modify: `src/MemoryProfiler.Analysis/Loading/ClrMdHeapSnapshotLoader.cs`
- Create: `tests/MemoryProfiler.Analysis.Tests/Values/ClrMdHeapObjectValueServiceTests.cs`

**Interfaces:**
- Produces: `HeapValueKind`, `HeapFieldValue`, and `HeapObjectValueResult` exactly as specified in the design.
- Produces: `ObjectValueReadOptions(int ArrayOffset = 0, int ArrayLimit = 500, int StringLimit = 4096)`.
- Produces: `IHeapObjectValueService.ReadAsync(HeapSnapshot, ulong, ObjectValueReadOptions, CancellationToken)`.
- Extends: internal `IHeapDumpSource.ReadObjectValues(ulong, ObjectValueReadOptions, CancellationToken)` with a default `NotSupportedException` implementation so unrelated test stubs continue to compile.

- [ ] **Step 1: Add failing serialization tests**

Add both contracts to `SerializableContracts` and add a structural-equality test for the result's field list:

```csharp
new HeapFieldValue(
    "_name",
    "System.String",
    HeapValueKind.String,
    "cache-a",
    null,
    null,
    IsTruncated: false,
    TotalLength: 7,
    UnavailableReason: null),
new HeapObjectValueResult(
    new HeapObjectInfo(0x2000, 0x1000, "Example.Cache", 64, "Gen2"),
    [
        new HeapFieldValue(
            "_count", "System.Int32", HeapValueKind.Primitive, "42",
            null, null, false, null, null),
    ],
    TotalFieldOrElementCount: 1,
    HasMoreElements: false),
```

Verify `HeapObjectValueResult.Equals` compares `Fields` element-by-element, matching `GcRootInfo`'s structural path equality.

- [ ] **Step 2: Run the contract tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Contracts.Tests --filter ContractSerializationTests
```

Expected: compilation fails because the three heap-value contracts do not exist.

- [ ] **Step 3: Implement the contracts**

Create the enum and records from the spec. Implement structural equality on `HeapObjectValueResult`:

```csharp
public bool Equals(HeapObjectValueResult? other) =>
    other is not null &&
    Object == other.Object &&
    TotalFieldOrElementCount == other.TotalFieldOrElementCount &&
    HasMoreElements == other.HasMoreElements &&
    Fields.SequenceEqual(other.Fields);

public override int GetHashCode() => HashCode.Combine(
    Object,
    TotalFieldOrElementCount,
    HasMoreElements,
    Fields.Count);
```

Run the contract tests again; expected: PASS.

- [ ] **Step 4: Write failing service-boundary tests**

Create a stub `IHeapDumpSource` whose new method records address/options and returns a controlled result. Cover forwarding, path normalization, already-cancelled requests, non-walkable heaps, disposal, zero addresses, and all option bounds:

```csharp
[Fact]
public async Task ReadAsyncForwardsBoundedOptionsAndDisposesDump()
{
    var expected = Result(Field("_count", "System.Int32", "42"));
    var source = new StubValueDumpSource(expected);
    var service = new ClrMdHeapObjectValueService(
        new StubHeapDumpSourceFactory(source));

var actual = await service.ReadAsync(
        Snapshot(),
        0x2000,
        new ObjectValueReadOptions(ArrayOffset: 500, ArrayLimit: 250, StringLimit: 8192));

    Assert.Same(expected, actual);
    Assert.Equal(0x2000UL, source.Address);
    Assert.Equal(new ObjectValueReadOptions(500, 250, 8192), source.Options);
    Assert.True(source.Disposed);
}
```

Define the test helpers in the same test class so no production helper is implied:

```csharp
private static HeapSnapshot Snapshot() => new()
{
    Info = new HeapSnapshotInfo(
        "sample.dmp", "Sample", 42, "10.0.0", DateTimeOffset.UtcNow, 1, 64),
    Types = [],
};

private static HeapFieldValue Field(string name, string type, string value) =>
    new(name, type, HeapValueKind.Primitive, value, null, null, false, null, null);

private static HeapObjectValueResult Result(params HeapFieldValue[] fields) =>
    new(new HeapObjectInfo(0x2000, 0x1000, "Example.Cache", 64, "Gen2"),
        fields, fields.Length, false);
```

Validation expectations:

```csharp
Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectValueReadOptions(-1, 500, 4096).Validate());
Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectValueReadOptions(0, 0, 4096).Validate());
Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectValueReadOptions(0, 501, 4096).Validate());
Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectValueReadOptions(0, 500, 0).Validate());
Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectValueReadOptions(0, 500, 1_048_577).Validate());
```

- [ ] **Step 5: Run the value-service tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter ClrMdHeapObjectValueServiceTests
```

Expected: compilation fails because the values namespace and dump-source method are absent.

- [ ] **Step 6: Implement the minimal service and seam**

Use the repository/service lifecycle already established by `ClrMdHeapObjectRepository`:

```csharp
public Task<HeapObjectValueResult> ReadAsync(
    HeapSnapshot snapshot,
    ulong objectAddress,
    ObjectValueReadOptions options,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Info.Path);
    ArgumentNullException.ThrowIfNull(options);
    options.Validate();
    if (objectAddress == 0)
    {
        throw new ArgumentOutOfRangeException(nameof(objectAddress));
    }

    cancellationToken.ThrowIfCancellationRequested();
    var path = Path.GetFullPath(snapshot.Info.Path);
    return Task.Run(
        () => Read(path, objectAddress, options, cancellationToken),
        cancellationToken);
}
```

`Read` opens the source in a `using`, rejects `!source.CanWalkHeap`, checks cancellation, and calls `ReadObjectValues`. Add this default member to `IHeapDumpSource`:

```csharp
HeapObjectValueResult ReadObjectValues(
    ulong objectAddress,
    ObjectValueReadOptions options,
    CancellationToken cancellationToken) =>
    throw new NotSupportedException("Heap object value inspection is unavailable.");
```

- [ ] **Step 7: Verify and commit**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Contracts.Tests --filter ContractSerializationTests
rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter ClrMdHeapObjectValueServiceTests
```

Expected: PASS. Commit:

```bash
rtk git add src/MemoryProfiler.Contracts/Heap src/MemoryProfiler.Analysis/Values src/MemoryProfiler.Analysis/Loading/ClrMdHeapSnapshotLoader.cs tests/MemoryProfiler.Contracts.Tests/ContractSerializationTests.cs tests/MemoryProfiler.Analysis.Tests/Values/ClrMdHeapObjectValueServiceTests.cs
rtk git commit -m "feat: add heap object value contracts"
```

---

### Task 2: ClrMD field decoding, paging, and live-dump acceptance

**Files:**
- Create: `src/MemoryProfiler.Analysis/Values/ClrMdHeapValueReader.cs`
- Create: `src/MemoryProfiler.Analysis/Values/HeapValueFormatting.cs`
- Modify: `src/MemoryProfiler.Analysis/Loading/ClrMdHeapSnapshotLoader.cs`
- Create: `tests/MemoryProfiler.Analysis.Tests/Values/HeapValueFormattingTests.cs`
- Create: `tests/MemoryProfiler.Analysis.Tests/Values/ClrMdHeapObjectValueServiceAcceptanceTests.cs`
- Create: `tests/MemoryProfiler.Diagnostics.Tests/LiveDiagnosticsTarget/CacheProbe.cs`
- Modify: `tests/MemoryProfiler.Diagnostics.Tests/LiveDiagnosticsTarget/Program.cs`
- Modify: `tests/MemoryProfiler.Analysis.Tests/LiveTargetFixture.cs`

**Interfaces:**
- Consumes: contracts and `IHeapDumpSource.ReadObjectValues` from Task 1.
- Produces: `ClrMdHeapValueReader.Read(ClrRuntime, ulong, ObjectValueReadOptions, CancellationToken)`.
- Produces: `LiveTargetFixture.StartAsync(LiveTargetMode mode)` while preserving the current parameterless and `bool leakPhase` overloads.

- [ ] **Step 1: Write failing pure formatting tests**

Cover invariant numeric text, escaped characters, enum fallback, and the common scalar structs:

```csharp
[Theory]
[InlineData('\n', "'\\n'")]
[InlineData('\t', "'\\t'")]
[InlineData('A', "'A'")]
public void CharacterTextEscapesControlCharacters(char value, string expected) =>
    Assert.Equal(expected, HeapValueFormatting.Character(value));

[Fact]
public void ScalarTextIsInvariant()
{
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
        Assert.Equal("1234.5", HeapValueFormatting.Scalar(1234.5d));
        Assert.Equal("2026-09-01T12:30:00.0000000Z",
            HeapValueFormatting.Scalar(
                new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc)));
    }
    finally
    {
        CultureInfo.CurrentCulture = previous;
    }
}
```

- [ ] **Step 2: Write the failing live-dump acceptance test**

Add `LiveTargetMode.ObjectValues`. In that mode retain one `LiveDiagnosticsTarget.CacheProbe` through a static holder. The probe contains exact controlled fields:

```csharp
public sealed class CacheProbe
{
    public int Count = 42;
    public bool Enabled = true;
    public char Marker = 'M';
    public CacheState State = CacheState.Ready;
    public int? Limit = 12;
    public int? MissingLimit;
    public decimal Price = 1234.5m;
    public DateTime CreatedAt = new(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
    public TimeSpan Ttl = TimeSpan.FromMinutes(15);
    public Guid Identifier = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    public string Label = "memscope-value-sentinel";
    public string LongLabel = new('x', 5_000);
    public CacheChild Child = new() { Id = 7 };
    public CacheChild? Missing;
    public int[] Numbers = Enumerable.Range(0, 750).ToArray();
    public byte[][] Payload = Enumerable.Range(0, 32)
        .Select(_ => new byte[64 * 1024])
        .ToArray();
}

public enum CacheState { Cold, Ready }
```

Capture/load the dump, locate `CacheProbe` through `ClrMdHeapObjectRepository`, then assert:

```csharp
var values = await service.ReadAsync(snapshot, probe.Address, new(), timeout.Token);
Assert.Equal("42", Field(values, "Count").ValueText);
Assert.Equal("True", Field(values, "Enabled").ValueText);
Assert.Equal("'M'", Field(values, "Marker").ValueText);
Assert.Equal("Ready (1)", Field(values, "State").ValueText);
Assert.Equal("12", Field(values, "Limit").ValueText);
Assert.Equal(HeapValueKind.Null, Field(values, "MissingLimit").Kind);
Assert.Equal("1234.5", Field(values, "Price").ValueText);
Assert.Equal("2026-09-01T12:30:00.0000000Z", Field(values, "CreatedAt").ValueText);
Assert.Equal("00:15:00", Field(values, "Ttl").ValueText);
Assert.Equal("01234567-89ab-cdef-0123-456789abcdef",
    Field(values, "Identifier").ValueText);
Assert.Equal("memscope-value-sentinel", Field(values, "Label").ValueText);
Assert.True(Field(values, "LongLabel").IsTruncated);
Assert.Equal(5_000, Field(values, "LongLabel").TotalLength);
Assert.NotNull(Field(values, "Child").ReferencedObjectAddress);
Assert.Equal(HeapValueKind.Null, Field(values, "Missing").Kind);
```

Repeat the probe read with `new ObjectValueReadOptions(StringLimit: 1_048_576)` and assert `LongLabel` contains all 5,000 characters with `IsTruncated == false`.

Follow the `Numbers` reference and read offsets `0` and `500`; assert the pages contain `[0]..[499]` and `[500]..[749]` and `HasMoreElements` changes from `true` to `false`.

- [ ] **Step 3: Run focused tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter "HeapValueFormattingTests|ClrMdHeapObjectValueServiceAcceptanceTests"
```

Expected: formatting types, target mode, and concrete decoder are missing.

- [ ] **Step 4: Implement the pure formatter**

Use `CultureInfo.InvariantCulture`; use `"R"` for floating point, `"O"` for `DateTime`, `"c"` for `TimeSpan`, and `"D"` for `Guid`. Escape `\\`, `'`, `\0`, `\n`, `\r`, and `\t`; encode other control characters as `\\uXXXX`.

```csharp
internal static string Scalar<T>(T value) where T : IFormattable =>
    value switch
    {
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
        _ => value.ToString(null, CultureInfo.InvariantCulture),
    };
```

- [ ] **Step 5: Implement `ClrMdHeapValueReader`**

Resolve the object using `runtime.Heap.GetObject(address)`. Reject `IsNull`, `!IsValid`, `IsFree`, missing type, or method table zero. Return header metadata using `ClrMdHeapObjectRepository.GenerationLabel`.

For non-arrays, iterate `type.Fields` in metadata order and use `ClrField.ElementType`:

```csharp
return field.ElementType switch
{
    ClrElementType.Boolean => Primitive(field, obj.ReadField<bool>(field)),
    ClrElementType.Char => Character(field, obj.ReadField<char>(field)),
    ClrElementType.Int8 => Primitive(field, obj.ReadField<sbyte>(field)),
    ClrElementType.UInt8 => Primitive(field, obj.ReadField<byte>(field)),
    ClrElementType.Int16 => Primitive(field, obj.ReadField<short>(field)),
    ClrElementType.UInt16 => Primitive(field, obj.ReadField<ushort>(field)),
    ClrElementType.Int32 => PrimitiveOrEnum(field, obj.ReadField<int>(field)),
    ClrElementType.UInt32 => PrimitiveOrEnum(field, obj.ReadField<uint>(field)),
    ClrElementType.Int64 => PrimitiveOrEnum(field, obj.ReadField<long>(field)),
    ClrElementType.UInt64 => PrimitiveOrEnum(field, obj.ReadField<ulong>(field)),
    ClrElementType.Float => Primitive(field, obj.ReadField<float>(field)),
    ClrElementType.Double => Primitive(field, obj.ReadField<double>(field)),
    ClrElementType.String => String(field, obj, options.StringLimit),
    ClrElementType.Class or ClrElementType.Object or
        ClrElementType.Array or ClrElementType.SZArray => Reference(field, obj),
    ClrElementType.Struct => WellKnownStructOrUnavailable(field, obj),
    _ => Unavailable(field, "Unsupported value type"),
};
```

Determine enum status from `field.Type?.BaseType?.Name == "System.Enum"`. For strings, call `ReadStringField(field, options.StringLimit)` and read the referenced string object's `_stringLength` field (fallback `m_stringLength`) for exact `TotalLength`; never infer an unbounded allocation from corrupt metadata. Catch per-field `ArgumentException`, `InvalidOperationException`, `IOException`, and `ClrDiagnosticsException`, returning the controlled reason `Value could not be read` without exception text.

For arrays, use `ClrObject.AsArray`, clamp the page to `Length`, and switch on `type.ComponentType?.ElementType`. Use `ClrArray.ReadValues<T>(offset, count)` for supported scalar components; use `ReadValues<ulong>` plus `runtime.Heap.GetObject` for reference components. Check cancellation between emitted elements.

Read `decimal`, `DateTime`, `TimeSpan`, and `Guid` through `ClrObject.ReadValueTypeField`. Keep runtime-layout knowledge inside `TryReadDecimal`, `TryReadDateTime`, `TryReadTimeSpan`, and `TryReadGuid`:

- `decimal`: `_flags`, `_hi32`, and `_lo64`; reconstruct the low/mid/high words, sign, and scale through the public `decimal` constructor.
- `DateTime`: `_dateData`; separate the 62-bit ticks from the two kind bits and construct the matching `DateTimeKind`.
- `TimeSpan`: `_ticks`; construct `TimeSpan.FromTicks`.
- `Guid`: `_a`, `_b`, `_c`, and bytes `_d` through `_k`; construct the public 11-argument `Guid` value.
- `Nullable<T>`: read `hasValue`; return `Null` when false, otherwise decode the immediate `value` field only when `T` is one of the supported scalar/enum/common-struct types.

Every layout reader first verifies all expected fields and returns `Unavailable("Unsupported value type")` if the runtime layout differs. Do not recurse into arbitrary structs.

- [ ] **Step 6: Wire the concrete source and target mode**

Implement in `ClrMdHeapDumpSource`:

```csharp
public HeapObjectValueResult ReadObjectValues(
    ulong objectAddress,
    ObjectValueReadOptions options,
    CancellationToken cancellationToken) =>
    ClrMdHeapValueReader.Read(_runtime, objectAddress, options, cancellationToken);
```

Add `CacheProbe.cs`, retain it in `--object-values` mode, signal `READY` only after the complete graph exists, and keep it alive through a static `ValueInspectionHolder.Probe`. Extend `LiveTargetFixture` without changing existing call-site behavior.

- [ ] **Step 7: Verify decoder behavior and commit**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter "HeapValueFormattingTests|ClrMdHeapObjectValueServiceAcceptanceTests|ClrMdHeapObjectValueServiceTests"
```

Expected: PASS on the current platform. Commit:

```bash
rtk git add src/MemoryProfiler.Analysis/Values src/MemoryProfiler.Analysis/Loading/ClrMdHeapSnapshotLoader.cs tests/MemoryProfiler.Analysis.Tests/Values tests/MemoryProfiler.Analysis.Tests/LiveTargetFixture.cs tests/MemoryProfiler.Diagnostics.Tests/LiveDiagnosticsTarget
rtk git commit -m "feat: decode heap object field values"
```

---

### Task 3: Top Retainers application model

**Files:**
- Create: `src/MemoryProfiler.App/ViewModels/Retainers/TopRetainerRowViewModel.cs`
- Create: `src/MemoryProfiler.App/ViewModels/Retainers/TopRetainersViewModel.cs`
- Create: `tests/MemoryProfiler.App.Tests/ViewModels/Retainers/TopRetainerRowViewModelTests.cs`
- Create: `tests/MemoryProfiler.App.Tests/ViewModels/Retainers/TopRetainersViewModelTests.cs`

**Interfaces:**
- Produces: `TopRetainersViewModel.BeginLoadingAsync()`, `SetResultAsync(DominatorAnalysisResult, CancellationToken)`, `SetUnavailableAsync()`, and `ClearAsync()`.
- Produces: `SearchText`, `ApplySearchAsync(CancellationToken)`, `ApplySearchCommand`, `Retainers`, `SelectedRetainer`, `LoadMoreCommand`, and mutually exclusive loading/unavailable/empty/table states.
- Produces: `TopRetainerRowViewModel.Info`, `Address`, formatted metrics, and `RetainedPercentage`.

- [ ] **Step 1: Write failing row tests**

Use a 1,000-byte reachable heap and a 400-byte dominator:

```csharp
var row = new TopRetainerRowViewModel(
    new DominatorInfo(0x2000, "MyApp.Cache", 64, 400, 12),
    totalReachableBytes: 1_000);

Assert.Equal("MyApp.Cache", row.TypeName);
Assert.Equal("0x000000002000", row.AddressDisplay);
Assert.Equal("64 B", row.ShallowSizeDisplay);
Assert.Equal("400 B", row.RetainedSizeDisplay);
Assert.Equal("12", row.RetainedObjectCountDisplay);
Assert.Equal("40.0%", row.RetainedPercentageDisplay);
```

- [ ] **Step 2: Write failing windowing and search tests**

Create 1,201 ordered `DominatorInfo` records. Assert the first publication contains 500 rows, each `LoadMoreCommand` adds at most 500, type search is ordinal-ignore-case, hexadecimal address search matches the canonical address, a superseded search cannot publish, and `ClearAsync` drops the result and selection.

```csharp
await viewModel.SetResultAsync(Result(1_201));
Assert.Equal(500, viewModel.Retainers.Count);
viewModel.LoadMoreCommand.Execute(null);
Assert.Equal(1_000, viewModel.Retainers.Count);

viewModel.SearchText = "cacheentry1199";
await viewModel.ApplySearchAsync();
Assert.Single(viewModel.Retainers);
```

- [ ] **Step 3: Run tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "TopRetainerRowViewModelTests|TopRetainersViewModelTests"
```

Expected: compilation fails because the Retainers view models do not exist.

- [ ] **Step 4: Implement row formatting and bounded publication**

Compute reachable bytes once with checked shallow-size accumulation over `result.Dominators`; zero produces `0.0%`. Keep the underlying `IReadOnlyList<DominatorInfo>` and only create row view models for the current 500-row window.

Search inside `Task.Run` with a linked cancellation token and version counter:

```csharp
private static bool Matches(DominatorInfo item, string search)
{
    if (string.IsNullOrWhiteSpace(search))
    {
        return true;
    }

    return item.TypeName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
           MetricFormatting.Address(item.ObjectAddress)
               .Contains(search, StringComparison.OrdinalIgnoreCase);
}
```

Publish a replacement `ReadOnlyObservableCollection<TopRetainerRowViewModel>` through `IUiDispatcher`; stale refreshes return without touching state. `LoadMoreCommand` appends only the next bounded window on the UI dispatcher.

`BeginLoadingAsync` clears stale rows and publishes loading state before dominator work starts. `SetUnavailableAsync` clears rows, publishes the nonfatal unavailable state, and keeps the rest of the snapshot usable. `SetResultAsync` stores the result and delegates to `ApplySearchAsync` with an empty initial search.

- [ ] **Step 5: Verify and commit**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "TopRetainerRowViewModelTests|TopRetainersViewModelTests"
```

Expected: PASS. Commit:

```bash
rtk git add src/MemoryProfiler.App/ViewModels/Retainers tests/MemoryProfiler.App.Tests/ViewModels/Retainers
rtk git commit -m "feat: add top retainers model"
```

---

### Task 4: Object Details state machine and sensitive-value lifecycle

**Files:**
- Create: `src/MemoryProfiler.App/ViewModels/Objects/HeapFieldValueRowViewModel.cs`
- Create: `src/MemoryProfiler.App/ViewModels/Objects/ObjectDetailsViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/InvestigationClipboard.cs`
- Create: `tests/MemoryProfiler.App.Tests/ViewModels/Objects/HeapFieldValueRowViewModelTests.cs`
- Create: `tests/MemoryProfiler.App.Tests/ViewModels/Objects/ObjectDetailsViewModelTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/InvestigationClipboardTests.cs`

**Interfaces:**
- Consumes: `IHeapObjectValueService`, `DominatorAnalysisResult`, `IUiDispatcher`, and explicit clipboard service.
- Produces: `ObjectDetailsViewModel.ShowAsync(HeapSnapshot, ulong, string, DominatorAnalysisResult?, DominatorInfo?, CancellationToken)`.
- Produces: `ClearAsync`, `LoadNextArrayPageCommand`, `ShowMoreStringsCommand`, `CancelCommand`, and independent retained/value state properties.
- Produces: `InvestigationClipboard.CopyFieldValueAsync(HeapFieldValueRowViewModel, CancellationToken)`.

- [ ] **Step 1: Write failing field-row tests**

Cover primitive, string, null, unavailable, and navigable reference rows:

```csharp
var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
    "_child", "MyApp.Child", HeapValueKind.ObjectReference, null,
    0x3000, "MyApp.Child", false, null, null));

Assert.Equal("0x000000003000", row.ReferencedAddressDisplay);
Assert.Equal("MyApp.Child @ 0x000000003000", row.ValueDisplay);
Assert.True(row.CanNavigate);
Assert.True(row.CanCopyValue);
```

Assert unavailable rows display `Unavailable`, expose a controlled tooltip, cannot navigate, and do not copy the failure reason as a target value.

- [ ] **Step 2: Write failing Object Details state tests**

Use a blocking stub value service and controlled dominator result. Prove retained metrics publish before the service completes, values appear automatically, and the exact warning is always exposed:

```csharp
var load = viewModel.ShowAsync(
    Snapshot,
    0x2000,
    "MyApp.Cache",
    Result(CacheDominator),
    CacheDominator);
await service.Started;

Assert.Equal("430 MB", viewModel.RetainedSizeDisplay);
Assert.True(viewModel.IsLoadingValues);
Assert.Equal(
    "Dump values may contain credentials, personal data, or other secrets.",
    viewModel.SensitiveValuesWarning);
```

Define `CacheDominator` as `new DominatorInfo(0x2000, "MyApp.Cache", 64, 430UL * 1024 * 1024, 12_345)`, `Result` as a `DominatorAnalysisResult` containing that object, and `Snapshot` as the same minimal `HeapSnapshot` shape used by the existing object-pane tests.

Also cover:

- A stale first object cannot replace a completed second object.
- `ClearAsync` immediately removes decoded strings and cancels the service.
- `DisposeAsync` clears rows and cancels work.
- One unavailable field coexists with successful fields.
- Missing dominator metrics do not block values.
- Failed values leave metrics visible and publish a `ProfilerError` without value text.
- `LoadNextArrayPageCommand` requests offsets `500`, `1000`, and stops at `HasMoreElements == false`.
- `ShowMoreStringsCommand` repeats the read with `StringLimit == 1_048_576` and replaces, rather than duplicates, rows.

- [ ] **Step 3: Run tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "HeapFieldValueRowViewModelTests|ObjectDetailsViewModelTests|InvestigationClipboardTests"
```

Expected: compilation fails because Object Details types and field-value clipboard support are absent.

- [ ] **Step 4: Implement the row and state machine**

Follow `ObjectReferencesViewModel`'s linked cancellation, captured-token, version, and single-publication pattern. Start the retained lookup and value read concurrently when no `knownDominator` is supplied:

```csharp
var retainedTask = knownDominator is not null
    ? Task.FromResult<DominatorInfo?>(knownDominator)
    : Task.Run(
        () => dominatorResult?.Dominators
            .FirstOrDefault(item => item.ObjectAddress == objectAddress),
        linkedToken);
var valuesTask = _service.ReadAsync(
    snapshot,
    objectAddress,
    new ObjectValueReadOptions(),
    linkedToken);
```

Publish retained metrics as soon as `retainedTask` completes. Publish decoded rows only when the version still matches. Clear the previous collection before starting a new object. Use `checked` when deriving the reachable-heap denominator and `MetricFormatting` for memory/count/address display.

Expanded-string reloads replace matching rows by field name; array pages append after verifying the returned object's address and expected offset. Never place `ValueText` inside an error message.

- [ ] **Step 5: Add explicit field copying**

Copy `row.CopyText`, where primitives/strings use `ValueDisplay`, references use the canonical address, null uses `null`, and unavailable rows are disabled:

```csharp
public Task CopyFieldValueAsync(
    HeapFieldValueRowViewModel row,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(row);
    if (!row.CanCopyValue)
    {
        return Task.CompletedTask;
    }

    return service.SetTextAsync(row.CopyText, cancellationToken);
}
```

- [ ] **Step 6: Verify and commit**

Run the focused command from Step 3. Expected: PASS. Commit:

```bash
rtk git add src/MemoryProfiler.App/ViewModels/Objects src/MemoryProfiler.App/ViewModels/InvestigationClipboard.cs tests/MemoryProfiler.App.Tests/ViewModels/Objects tests/MemoryProfiler.App.Tests/ViewModels/InvestigationClipboardTests.cs
rtk git commit -m "feat: add object details inspection"
```

---

### Task 5: Snapshot, navigation, and composition integration

**Files:**
- Modify: `src/MemoryProfiler.App/Navigation/InvestigationLocation.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/SnapshotViewModel.cs`
- Modify: `src/MemoryProfiler.App/ViewModels/StartViewModel.cs`
- Modify: `src/MemoryProfiler.App/App.axaml.cs`
- Modify: `tests/MemoryProfiler.App.Tests/Navigation/InvestigationNavigationServiceTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/SnapshotViewModelTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/StartViewModelTests.cs`

**Interfaces:**
- Produces: `ObjectDetailsLocation(ulong ObjectAddress, string ObjectTypeName)`.
- Produces: `SnapshotViewModel.TopRetainers`, `ObjectDetails`, `ShowTypesCommand`, `ShowTopRetainersCommand`, `InspectObjectCommand`, `CopyFieldValueCommand`, `IsInstancesPaneVisible`, and `IsObjectDetailsPaneVisible`.
- Consumes: `IHeapObjectValueService` supplied by production composition.

- [ ] **Step 1: Write failing navigation tests**

Add Object Details between a type and references and assert Back/Forward equality uses only address/type:

```csharp
var details = new ObjectDetailsLocation(0x2000, "MyApp.Cache");
navigation.Navigate(new TypeLocation(0x1000));
navigation.Navigate(details);
navigation.Navigate(new ObjectReferencesLocation(
    0x2000, "MyApp.Cache", ReferenceDirection.Outgoing));

navigation.GoBack();
Assert.Equal(details, navigation.CurrentLocation);
```

Use reflection or positional-pattern assertions to prove the record has no values, metrics, or collection property.

- [ ] **Step 2: Write failing SnapshotViewModel integration tests**

Cover these flows:

```csharp
// Dominator completion feeds both consumers and remains available for details.
await dominator.CompleteAsync(Result(CacheDominator));
Assert.Equal(CacheDominator, viewModel.TopRetainers.Retainers[0].Info);

// Any supported row opens the same Object Details address.
await viewModel.InspectObjectAsync(new HeapObjectRowViewModel(CacheObject));
Assert.Equal(0x2000UL, viewModel.ObjectDetails.ObjectAddress);
Assert.Equal(new ObjectDetailsLocation(0x2000, "MyApp.Cache"), viewModel.CurrentLocation);
```

Add cases for a Top Retainer row, reference row, and non-root GC path row. Verify a root-only row cannot inspect address zero. Verify loading a new snapshot clears Top Retainers/Object Details and decoded values. Verify disposal cancels both child view models.

- [ ] **Step 3: Write failing composition tests**

Assert Open Dump remains disabled without `IHeapObjectValueService`, production-style construction passes it into `SnapshotViewModel`, and comparison remains unaffected.

- [ ] **Step 4: Run focused tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "InvestigationNavigationServiceTests|SnapshotViewModelTests|StartViewModelTests"
```

Expected: failures identify missing location, dependencies, commands, and result publication.

- [ ] **Step 5: Integrate retained results and inspection routing**

Store the completed result:

```csharp
private DominatorAnalysisResult? _dominatorResult;

// In the retained-size success publication:
_dominatorResult = result;
Types.SetRetainedSizes(result.TypeRetainedSizes);
await TopRetainers.SetResultAsync(result, token).ConfigureAwait(false);
```

Call `TopRetainers.BeginLoadingAsync` when retained-size computation starts and `TopRetainers.SetUnavailableAsync` in the nonfatal failure path. Cancellation caused by a replacement snapshot calls `ClearAsync` rather than publishing unavailable state.

Do not await child work while holding the UI dispatcher callback; publish the result reference on the dispatcher, then call the child asynchronously. On new load/dispose, null `_dominatorResult`, clear Top Retainers, and clear Object Details before releasing the snapshot.

Add an analysis-mode enum local to the App project:

```csharp
public enum SnapshotAnalysisMode
{
    Types,
    TopRetainers,
}
```

`ShowTypesCommand` and `ShowTopRetainersCommand` set `AnalysisMode`; this UI preference does not enter navigation history. `InspectObjectCommand` resolves addresses from `TopRetainerRowViewModel`, `HeapObjectRowViewModel`, `ObjectReferenceRowViewModel`, `HeapFieldValueRowViewModel`, and navigable `GcRootRowViewModel`, then navigates to `ObjectDetailsLocation`.

Handle `ObjectDetailsLocation` in `ApplyNavigationAsync` by switching the lower-left pane to Object Details and calling `ShowAsync` with the current snapshot/result. Existing Type/Reference/Root locations keep their current behavior.

- [ ] **Step 6: Wire production composition**

Add `IHeapObjectValueService? objectValueService = null` after the dominator dependency in internal constructors, require it for Open Dump, and construct `new ClrMdHeapObjectValueService()` in `App.axaml.cs`. Update test constructors with named arguments when positional ambiguity would result.

- [ ] **Step 7: Verify and commit**

Run the focused command from Step 4. Expected: PASS. Commit:

```bash
rtk git add src/MemoryProfiler.App/Navigation/InvestigationLocation.cs src/MemoryProfiler.App/ViewModels/SnapshotViewModel.cs src/MemoryProfiler.App/ViewModels/StartViewModel.cs src/MemoryProfiler.App/App.axaml.cs tests/MemoryProfiler.App.Tests/Navigation/InvestigationNavigationServiceTests.cs tests/MemoryProfiler.App.Tests/ViewModels/SnapshotViewModelTests.cs tests/MemoryProfiler.App.Tests/ViewModels/StartViewModelTests.cs
rtk git commit -m "feat: integrate object details navigation"
```

---

### Task 6: Top Retainers and Object Details Avalonia surfaces

**Files:**
- Modify: `src/MemoryProfiler.App/Views/SnapshotView.axaml`
- Modify: `src/MemoryProfiler.App/Views/SnapshotView.axaml.cs`
- Create: `tests/MemoryProfiler.App.Tests/Views/TopRetainersAndObjectDetailsViewTests.cs`

**Interfaces:**
- Consumes: `SnapshotViewModel.AnalysisMode`, Top Retainers, Object Details, inspection/navigation, copying, paging, and string-expansion commands.
- Produces: accessible Types/Top Retainers mode controls and the lower-left Object Details surface.

- [ ] **Step 1: Write failing Avalonia view tests**

Instantiate `SnapshotView` and inspect named controls/descendants. Assert:

- Two accessible mode buttons named `Show heap types` and `Show top retainers`.
- A Top Retainers DataGrid with exactly six columns: Type, Address, Shallow size, Retained size, Retained objects, Retained heap.
- An Object Details warning with the exact approved text.
- A field DataGrid with Field, Declared type, Kind, Value, and Referenced address.
- **Show more**, **Load more elements**, and **Cancel** buttons bind to their matching commands.
- DataGrids are read-only, keyboard-focusable, resizable, and use explicit `ItemsSource`/`SelectedItem` bindings.

```csharp
[Fact]
public void ObjectDetailsDisplaysTheSensitiveValueWarning()
{
    var view = new SnapshotView();
    var warning = view.FindControl<TextBlock>("SensitiveValuesWarning");
    Assert.Equal(
        "Dump values may contain credentials, personal data, or other secrets.",
        warning?.Text);
}
```

- [ ] **Step 2: Run view tests and verify RED**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests --filter TopRetainersAndObjectDetailsViewTests
```

Expected: named controls and tables are absent.

- [ ] **Step 3: Add the mode switch and Top Retainers table**

Place two existing `direction-button`-style buttons beside the upper section label. Bind active classes to `IsTypesMode`/`IsTopRetainersMode`. Keep the current type filters/table in a Types-only grid and add a Top Retainers grid with complete loading, unavailable, empty, and table states. Bind selection to `TopRetainers.SelectedRetainer`; double-click calls `InspectObjectCommand` with that row.

Add a Top Retainers search row with a `TextBox` bound to `TopRetainers.SearchText`, an **Apply** button bound to `TopRetainers.ApplySearchCommand`, and a **Load more** button bound to `TopRetainers.LoadMoreCommand`. The search placeholder is `Filter by type or address` and its automation name is `Filter top retainers`.

Use these exact columns:

```xml
<DataGridTextColumn Header="Type" Width="*" Binding="{Binding TypeName}" />
<DataGridTextColumn Header="Address" Width="150" Binding="{Binding AddressDisplay}" />
<DataGridTextColumn Header="Shallow size" Width="115" Binding="{Binding ShallowSizeDisplay}" />
<DataGridTextColumn Header="Retained size" Width="115" Binding="{Binding RetainedSizeDisplay}" />
<DataGridTextColumn Header="Retained objects" Width="125" Binding="{Binding RetainedObjectCountDisplay}" />
<DataGridTextColumn Header="Retained heap" Width="110" Binding="{Binding RetainedPercentageDisplay}" />
```

- [ ] **Step 4: Add Object Details to the lower-left pane**

Keep the existing Instances grid under `IsInstancesPaneVisible`. Add Object Details under `IsObjectDetailsPaneVisible`; do not create a fourth horizontal pane. The header displays type/address/generation, followed by four memory metrics and the persistent warning. Bind the field DataGrid to `ObjectDetails.Fields` and its selection to `SelectedField`.

Add context-menu entries:

```xml
<MenuItem Header="Copy Value"
          Command="{Binding CopyFieldValueCommand}"
          CommandParameter="{Binding ObjectDetails.SelectedField}" />
<MenuItem Header="Inspect Referenced Object"
          Command="{Binding InspectObjectCommand}"
          CommandParameter="{Binding ObjectDetails.SelectedField}" />
<MenuItem Header="Show Outgoing References"
          Command="{Binding ShowOutgoingReferencesCommand}"
          CommandParameter="{Binding ObjectDetails.SelectedField}" />
```

Add inline loading, cancellation, controlled error, empty-fields, **Show more**, and **Load more elements** states. Double-click navigates only when `CanNavigate` is true.

- [ ] **Step 5: Verify App tests and commit**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.App.Tests
```

Expected: all App tests pass. Commit:

```bash
rtk git add src/MemoryProfiler.App/Views/SnapshotView.axaml src/MemoryProfiler.App/Views/SnapshotView.axaml.cs tests/MemoryProfiler.App.Tests/Views/TopRetainersAndObjectDetailsViewTests.cs
rtk git commit -m "feat: add top retainers and object details UI"
```

---

### Task 7: Privacy, performance, and full integration verification

**Files:**
- Modify: `tests/MemoryProfiler.Analysis.Tests/Values/ClrMdHeapObjectValueServiceAcceptanceTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/Objects/ObjectDetailsViewModelTests.cs`
- Modify: `tests/MemoryProfiler.App.Tests/ViewModels/Retainers/TopRetainersViewModelTests.cs`
- Modify: `AGENTS.md` only if the verified suite count changed and the project convention requires updating it.

**Interfaces:**
- Consumes: the completed feature from Tasks 1–6.
- Produces: regression evidence for sensitive-value release, bounded presentation, real retained attribution, and the complete solution.

- [ ] **Step 1: Add a cache-attribution acceptance assertion**

In the object-values acceptance test, also compute dominators for the same snapshot. Locate `LiveDiagnosticsTarget.CacheProbe` and assert:

```csharp
var probeDominator = Assert.Single(
    dominators.Dominators,
    item => item.ObjectAddress == probe.Address);
Assert.True(probeDominator.RetainedSize >= 1_048_576);
Assert.True(probeDominator.RetainedObjectCount >= 32);
```

This proves the Top Retainers metric and decoded field values refer to the same concrete cache owner.

- [ ] **Step 2: Add sensitive-value release regression tests**

Load a unique 256 KiB synthetic string through Object Details, retain only a `WeakReference` to the row/value, navigate to another object, force two full collections, and assert the old value is collectible. Also assert `ObjectDetails.ErrorMessage`, navigation locations, and command state contain neither the synthetic value nor its prefix.

```csharp
await viewModel.ClearAsync();
ForceFullCollection();
Assert.False(valueReference.IsAlive);
Assert.DoesNotContain("secret-sentinel", viewModel.ErrorMessage, StringComparison.Ordinal);
```

Define the test-only collection helper explicitly:

```csharp
private static void ForceFullCollection()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
```

- [ ] **Step 3: Add bounded-row regression tests**

Feed one million lightweight `DominatorInfo` records, warm up once, publish the result, and assert exactly 500 row view models exist before Load More. Search for one late address and assert the result remains bounded. Use `ProfilerMemoryProbe` to assert row-view-model overhead remains below 16 MiB beyond the supplied dominator result.

- [ ] **Step 4: Run focused verification**

Run:

```bash
rtk dotnet test tests/MemoryProfiler.Contracts.Tests
rtk dotnet test tests/MemoryProfiler.Analysis.Tests --filter "ObjectValue|HeapValue|Dominator"
rtk dotnet test tests/MemoryProfiler.App.Tests --filter "TopRetainer|ObjectDetails|SnapshotViewModel|InvestigationNavigation"
```

Expected: all commands exit 0 with no failed tests.

- [ ] **Step 5: Format and inspect**

Run:

```bash
rtk dotnet format MemoryProfiler.sln
rtk dotnet format MemoryProfiler.sln --verify-no-changes
rtk git diff --check
rtk git status --short
```

Inspect the focused diff and confirm no decoded target value is logged, persisted, or included in exception/status construction. Confirm all arrays/strings are bounded before allocation.

- [ ] **Step 6: Run the complete solution verification**

Run:

```bash
rtk dotnet build MemoryProfiler.sln
rtk dotnet test MemoryProfiler.sln
```

Expected: build exit code 0; all tests pass, including the serialized `Live diagnostics` acceptance collection.

- [ ] **Step 7: Commit final regression coverage**

If Steps 1–3 added changes not committed with the preceding feature tasks:

```bash
rtk git add tests/MemoryProfiler.Analysis.Tests/Values/ClrMdHeapObjectValueServiceAcceptanceTests.cs tests/MemoryProfiler.App.Tests/ViewModels/Objects/ObjectDetailsViewModelTests.cs tests/MemoryProfiler.App.Tests/ViewModels/Retainers/TopRetainersViewModelTests.cs
rtk git commit -m "test: verify retained object value inspection"
```

Do not push, create a branch, or open a pull request unless explicitly requested.
