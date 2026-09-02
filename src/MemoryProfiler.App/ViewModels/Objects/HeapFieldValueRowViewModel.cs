using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class HeapFieldValueRowViewModel
{
    private readonly HeapFieldValue _field;

    public HeapFieldValueRowViewModel(HeapFieldValue field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
    }

    public HeapFieldValue Field => _field;

    public string Name => _field.Name;

    public string DeclaredTypeName => _field.DeclaredTypeName;

    public HeapValueKind Kind => _field.Kind;

    public string KindDisplay => _field.Kind switch
    {
        HeapValueKind.Primitive => "Primitive",
        HeapValueKind.Enum => "Enum",
        HeapValueKind.String => "String",
        HeapValueKind.ObjectReference => "Object reference",
        HeapValueKind.ArrayElement => "Array element",
        HeapValueKind.Null => "Null",
        HeapValueKind.Unavailable => "Unavailable",
        _ => string.Empty,
    };

    public string ValueDisplay => _field.Kind switch
    {
        HeapValueKind.ObjectReference => ValueReferenceDisplay,
        HeapValueKind.Null => "null",
        HeapValueKind.Unavailable => "Unavailable",
        _ => _field.ValueText ?? string.Empty,
    };

    public string ValueReferenceDisplay =>
        $"{_field.ReferencedObjectTypeName ?? "N/A"} @ {ReferencedAddressDisplay}";

    public ulong? ReferencedObjectAddress => _field.ReferencedObjectAddress;

    public string? ReferencedObjectTypeName => _field.ReferencedObjectTypeName;

    public string ReferencedAddressDisplay =>
        _field.ReferencedObjectAddress is ulong address
            ? MetricFormatting.Address(address)
            : string.Empty;

    public bool CanNavigate =>
        _field.Kind == HeapValueKind.ObjectReference &&
        _field.ReferencedObjectAddress is ulong address &&
        address != 0;

    public bool CanCopyValue => _field.Kind is not HeapValueKind.Unavailable;

    public string CopyText => _field.Kind switch
    {
        HeapValueKind.ObjectReference => ReferencedAddressDisplay,
        HeapValueKind.Null => "null",
        _ => _field.ValueText ?? string.Empty,
    };

    public bool IsTruncated => _field.IsTruncated;

    public int? TotalLength => _field.TotalLength;

    public bool IsUnavailable => _field.Kind == HeapValueKind.Unavailable;

    public string UnavailableTooltip =>
        _field.UnavailableReason ?? "Unavailable";
}
