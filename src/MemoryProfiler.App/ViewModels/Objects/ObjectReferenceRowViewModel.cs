using System.Globalization;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class ObjectReferenceRowViewModel
{
    public ObjectReferenceRowViewModel(
        ObjectReference reference,
        ReferenceDirection direction)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Reference = reference;
        Direction = direction;

        var isRoot = direction == ReferenceDirection.Incoming &&
                     reference.SourceAddress == 0;
        IsRoot = isRoot;
        CanNavigate = !isRoot;
        EndpointAddress = direction == ReferenceDirection.Outgoing
            ? reference.TargetAddress
            : reference.SourceAddress;
        EndpointTypeName = direction == ReferenceDirection.Outgoing
            ? reference.TargetTypeName
            : reference.SourceTypeName;
        FieldDisplay = reference.Name ??
                       (reference.Kind == ReferenceKind.ArrayElement
                           ? "array element"
                           : "N/A");
        KindDisplay = reference.Kind switch
        {
            ReferenceKind.Field => "Field",
            ReferenceKind.ArrayElement => "Array element",
            ReferenceKind.StaticField => "Static field",
            ReferenceKind.Handle => "GC handle",
            _ => string.Empty,
        };
        AddressDisplay = isRoot
            ? "root"
            : "0x" + EndpointAddress.ToString("X12", CultureInfo.InvariantCulture);
        TypeNameDisplay = isRoot ? "N/A" : EndpointTypeName ?? "N/A";
    }

    public ObjectReference Reference { get; }

    public ReferenceDirection Direction { get; }

    public bool IsRoot { get; }

    public bool CanNavigate { get; }

    public ulong EndpointAddress { get; }

    public string EndpointTypeName { get; }

    public string FieldDisplay { get; }

    public string KindDisplay { get; }

    public string AddressDisplay { get; }

    public string TypeNameDisplay { get; }
}
