using System.Globalization;
using MemoryProfiler.App.ViewModels.Objects;
using MemoryProfiler.Contracts.Heap;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Objects;

public sealed class HeapObjectRowViewModelTests
{
    [Fact]
    public void RowFormatsAddressSizeAndGeneration()
    {
        var row = new HeapObjectRowViewModel(
            new HeapObjectInfo(0x0000_01A8_3210, 0x1000, "Example.Widget", 128, "Gen2"));

        Assert.Equal("0x000001A83210", row.AddressDisplay);
        Assert.Equal(FormatBytes(128), row.SizeDisplay);
        Assert.Equal("Gen2", row.GenerationDisplay);
    }

    [Fact]
    public void RowPadsShortAddressesToTwelveHexDigits()
    {
        var row = new HeapObjectRowViewModel(
            new HeapObjectInfo(0x1A832, 0x1000, "Example.Widget", 64, "Gen0"));

        Assert.Equal("0x00000001A832", row.AddressDisplay);
    }

    [Fact]
    public void RowKeepsLongAddressesUnTruncated()
    {
        var row = new HeapObjectRowViewModel(
            new HeapObjectInfo(0xABCD_EF01_2345_6789, 0x1000, "Example.Widget", 32, "LOH"));

        Assert.Equal("0xABCDEF0123456789", row.AddressDisplay);
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var unitIndex = 0;
        while (amount >= 1024 && unitIndex < units.Length - 1)
        {
            amount /= 1024;
            unitIndex++;
        }

        var format = amount >= 100 || unitIndex == 0 ? "N0" : "N1";
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }
}
