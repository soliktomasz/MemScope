using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Overview;

public sealed class MetricFormattingTests
{
    [Theory]
    [InlineData(824UL, "824 B")]
    [InlineData(1_024UL, "1 KB")]
    [InlineData(14_541UL, "14.2 KB")]
    [InlineData(102_912UL, "100.5 KB")]
    [InlineData(1_048_576UL, "1 MB")]
    [InlineData(51_065_651UL, "48.7 MB")]
    [InlineData(105_381_888UL, "100.5 MB")]
    [InlineData(1_073_741_824UL, "1 GB")]
    [InlineData(1_406_604_329UL, "1.31 GB")]
    [InlineData(107_642_617_856UL, "100.25 GB")]
    [InlineData(1_099_511_627_776UL, "1 TB")]
    [InlineData(110_226_040_684_544UL, "100.25 TB")]
    public void BytesUsesCompactBinaryUnits(ulong value, string expected) =>
        Assert.Equal(expected, WithCulture(() => MetricFormatting.Bytes(value)));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1_406_604_329L, "+1.31 GB")]
    [InlineData(-1_406_604_329L, "-1.31 GB")]
    public void SignedBytesPreservesTheSign(long value, string expected) =>
        Assert.Equal(expected, WithCulture(() => MetricFormatting.SignedBytes(value)));

    [Fact]
    public void CountUsesCurrentCultureGrouping() =>
        Assert.Equal("1,234,567", WithCulture(() => MetricFormatting.Count(1_234_567)));

    [Theory]
    [InlineData(0x1234UL, "0x000000001234")]
    [InlineData(0xABCDEF0123456789UL, "0xABCDEF0123456789")]
    public void AddressUsesCanonicalUppercaseHex(ulong value, string expected) =>
        Assert.Equal(expected, MetricFormatting.Address(value));

    private static string WithCulture(Func<string> format)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
