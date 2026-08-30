using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class ObjectReferenceRowViewModelTests
{
    [Fact]
    public void OutgoingFieldRowExposesTheTargetAsTheEndpoint()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_child",
                SourceTypeName: "MyApp.Container", TargetTypeName: "MyApp.Widget"),
            ReferenceDirection.Outgoing);

        Assert.True(row.CanNavigate);
        Assert.False(row.IsRoot);
        Assert.Equal(0x2000UL, row.EndpointAddress);
        Assert.Equal("MyApp.Widget", row.EndpointTypeName);
        Assert.Equal("_child", row.FieldDisplay);
        Assert.Equal("Field", row.KindDisplay);
        Assert.Equal("0x000000002000", row.AddressDisplay);
        Assert.Equal("MyApp.Widget", row.TypeNameDisplay);
    }

    [Fact]
    public void OutgoingArrayElementRowShowsArrayElementAsTheField()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0x1000, 0x2000, ReferenceKind.ArrayElement, null,
                SourceTypeName: "System.Byte[][]", TargetTypeName: "System.Byte[]"),
            ReferenceDirection.Outgoing);

        Assert.Equal("array element", row.FieldDisplay);
        Assert.Equal("Array element", row.KindDisplay);
        Assert.Equal("System.Byte[]", row.TypeNameDisplay);
        Assert.Equal(0x2000UL, row.EndpointAddress);
    }

    [Fact]
    public void IncomingFieldRowExposesTheSourceAsTheEndpoint()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0x3000, 0x1000, ReferenceKind.Field, "_owner",
                SourceTypeName: "MyApp.Owner", TargetTypeName: "MyApp.Widget"),
            ReferenceDirection.Incoming);

        Assert.True(row.CanNavigate);
        Assert.Equal(0x3000UL, row.EndpointAddress);
        Assert.Equal("MyApp.Owner", row.EndpointTypeName);
        Assert.Equal("_owner", row.FieldDisplay);
        Assert.Equal("Field", row.KindDisplay);
        Assert.Equal("0x000000003000", row.AddressDisplay);
        Assert.Equal("MyApp.Owner", row.TypeNameDisplay);
    }

    [Fact]
    public void IncomingHandleRootRowDisablesNavigationAndShowsRoot()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0, 0x1000, ReferenceKind.Handle, null,
                SourceTypeName: null, TargetTypeName: "MyApp.Widget"),
            ReferenceDirection.Incoming);

        Assert.True(row.IsRoot);
        Assert.False(row.CanNavigate);
        Assert.Equal(0UL, row.EndpointAddress);
        Assert.Equal("GC handle", row.KindDisplay);
        Assert.Equal("root", row.AddressDisplay);
        Assert.Equal("N/A", row.TypeNameDisplay);
        Assert.Equal("N/A", row.FieldDisplay);
    }

    [Fact]
    public void IncomingStaticFieldRootRowShowsStaticFieldKind()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0, 0x1000, ReferenceKind.StaticField, null,
                SourceTypeName: null, TargetTypeName: "MyApp.Widget"),
            ReferenceDirection.Incoming);

        Assert.True(row.IsRoot);
        Assert.False(row.CanNavigate);
        Assert.Equal("Static field", row.KindDisplay);
        Assert.Equal("root", row.AddressDisplay);
    }

    [Fact]
    public void MissingEndpointTypeNameFallsBackToNA()
    {
        var row = new ObjectReferenceRowViewModel(
            new ObjectReference(0x1000, 0x2000, ReferenceKind.Field, "_child"),
            ReferenceDirection.Outgoing);

        Assert.Equal("N/A", row.TypeNameDisplay);
    }
}
