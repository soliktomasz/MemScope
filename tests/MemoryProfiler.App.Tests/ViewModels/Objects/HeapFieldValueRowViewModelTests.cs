using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class HeapFieldValueRowViewModelTests
{
    [Fact]
    public void ReferenceRowsExposeNavigationAndCanonicalAddress()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "_child", "MyApp.Child", HeapValueKind.ObjectReference, null,
            0x3000, "MyApp.Child", false, null, null));

        Assert.Equal("0x000000003000", row.ReferencedAddressDisplay);
        Assert.Equal("MyApp.Child @ 0x000000003000", row.ValueDisplay);
        Assert.True(row.CanNavigate);
        Assert.True(row.CanCopyValue);
        Assert.Equal("0x000000003000", row.CopyText);
    }

    [Fact]
    public void NullRowsDisplayNullAndCannotNavigate()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "_missing", "MyApp.Child", HeapValueKind.Null, null,
            null, null, false, null, null));

        Assert.Equal("null", row.ValueDisplay);
        Assert.False(row.CanNavigate);
        Assert.True(row.CanCopyValue);
        Assert.Equal("null", row.CopyText);
    }

    [Fact]
    public void PrimitiveRowsDisplayTheInvariantValue()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "_count", "System.Int32", HeapValueKind.Primitive, "42",
            null, null, false, null, null));

        Assert.Equal("42", row.ValueDisplay);
        Assert.Equal("Primitive", row.KindDisplay);
        Assert.False(row.CanNavigate);
        Assert.True(row.CanCopyValue);
        Assert.Equal("42", row.CopyText);
    }

    [Fact]
    public void StringRowsExposeTruncationMetadata()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "_name", "System.String", HeapValueKind.String, "cache-a",
            null, null, true, 7, null));

        Assert.Equal("cache-a", row.ValueDisplay);
        Assert.True(row.IsTruncated);
        Assert.Equal(7, row.TotalLength);
        Assert.Equal("cache-a", row.CopyText);
    }

    [Fact]
    public void UnavailableRowsCannotNavigateOrCopyTheFailureReason()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "_payload", "System.Object", HeapValueKind.Unavailable, null,
            null, null, false, null, "Unsupported value type"));

        Assert.Equal("Unavailable", row.ValueDisplay);
        Assert.False(row.CanNavigate);
        Assert.False(row.CanCopyValue);
        Assert.Equal("Unsupported value type", row.UnavailableTooltip);
    }

    [Fact]
    public void ArrayElementRowsDisplayTheirIndexAndValue()
    {
        var row = new HeapFieldValueRowViewModel(new HeapFieldValue(
            "[5]", "System.Int32", HeapValueKind.ArrayElement, "99",
            null, null, false, null, null));

        Assert.Equal("[5]", row.Name);
        Assert.Equal("99", row.ValueDisplay);
        Assert.Equal("Array element", row.KindDisplay);
        Assert.False(row.CanNavigate);
    }
}
