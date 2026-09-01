using System.Globalization;
using Avalonia;
using MemoryProfiler.App.ViewModels.Overview;

namespace MemoryProfiler.App.ViewModels.Objects;

public sealed class GcRootRowViewModel
{
    public GcRootRowViewModel(
        int depth,
        bool isRoot,
        bool isTarget,
        string fieldDisplay,
        string kindDisplay,
        string addressDisplay,
        string typeNameDisplay,
        ulong endpointAddress,
        string endpointTypeName,
        bool canNavigate)
    {
        Depth = depth;
        IsRoot = isRoot;
        IsTarget = isTarget;
        FieldDisplay = fieldDisplay;
        KindDisplay = kindDisplay;
        AddressDisplay = addressDisplay;
        TypeNameDisplay = typeNameDisplay;
        EndpointAddress = endpointAddress;
        EndpointTypeName = endpointTypeName;
        CanNavigate = canNavigate;
    }

    public int Depth { get; }

    public bool IsRoot { get; }

    public bool IsTarget { get; }

    public string FieldDisplay { get; }

    public string KindDisplay { get; }

    public string AddressDisplay { get; }

    public string TypeNameDisplay { get; }

    public ulong EndpointAddress { get; }

    public string EndpointTypeName { get; }

    public bool CanNavigate { get; }

    public Thickness IndentMargin =>
        new(8 + Depth * 16, 0, 0, 0);

    public static string AddressDisplayFor(ulong address) =>
        address == 0
            ? "root"
            : MetricFormatting.Address(address);
}
