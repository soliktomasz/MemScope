using MemoryProfiler.App.ViewModels.Objects;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class GcRootRowViewModelTests
{
    [Fact]
    public void FormatsARootRowWithoutAnEndpoint()
    {
        var row = new GcRootRowViewModel(
            depth: 0,
            isRoot: true,
            isTarget: false,
            fieldDisplay: "GC Root",
            kindDisplay: "Static field",
            addressDisplay: "root",
            typeNameDisplay: "MyApp.Program._cache",
            endpointAddress: 0,
            endpointTypeName: string.Empty,
            canNavigate: false);

        Assert.Equal(0, row.Depth);
        Assert.True(row.IsRoot);
        Assert.False(row.IsTarget);
        Assert.False(row.CanNavigate);
        Assert.Equal("GC Root", row.FieldDisplay);
        Assert.Equal("root", row.AddressDisplay);
        Assert.Equal(8, row.IndentMargin.Left);
    }

    [Fact]
    public void FormatsAHeapObjectRowWithMonoAddressAndDepthIndent()
    {
        var row = new GcRootRowViewModel(
            depth: 2,
            isRoot: false,
            isTarget: true,
            fieldDisplay: "_value",
            kindDisplay: "Field",
            addressDisplay: "0x0000000ABC1234",
            typeNameDisplay: "MyApp.CustomerDto",
            endpointAddress: 0xABC1234,
            endpointTypeName: "MyApp.CustomerDto",
            canNavigate: true);

        Assert.Equal(2, row.Depth);
        Assert.False(row.IsRoot);
        Assert.True(row.IsTarget);
        Assert.True(row.CanNavigate);
        Assert.Equal(0xABC1234UL, row.EndpointAddress);
        Assert.Equal("MyApp.CustomerDto", row.EndpointTypeName);
        Assert.Equal(40, row.IndentMargin.Left);
    }

    [Theory]
    [InlineData(0x1234UL, "0x000000001234")]
    [InlineData(0UL, "root")]
    public void AddressDisplayFormatsHexOrRoot(ulong address, string expected)
    {
        Assert.Equal(expected, GcRootRowViewModel.AddressDisplayFor(address));
    }
}
