using System.Globalization;
using MemoryProfiler.App.ViewModels.Types;
using Xunit;

namespace MemoryProfiler.App.Tests.ViewModels.Types;

public sealed class SizeParsingTests
{
    [Theory]
    [InlineData("18446744073709551615")] // ulong.MaxValue rounds to 2^64 as a double
    [InlineData("18446744073709551616")] // 2^64
    [InlineData("16 EB")]                // 16 * 2^60 == 2^64
    [InlineData("1e30")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("12 XYZ")]
    [InlineData("")]
    [InlineData("   ")]
    public void OutOfRangeOrInvalidInputsAreRejected(string input)
    {
        var parsed = SizeParsing.TryParseBytes(input, out var bytes);

        Assert.False(parsed);
        Assert.Equal(0UL, bytes);
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("512", 512UL)]
    [InlineData("1 KB", 1024UL)]
    [InlineData("2 mb", 2_097_152UL)]
    [InlineData("1 GB", 1_073_741_824UL)]
    [InlineData("9223372036854775808", 9223372036854775808UL)] // 2^63 is exactly representable
    public void ValidInputsParseToBytes(string input, ulong expected)
    {
        var parsed = SizeParsing.TryParseBytes(input, out var bytes);

        Assert.True(parsed);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void FractionalValuesUseTheCurrentCultureDecimalSeparator()
    {
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

        var parsed = SizeParsing.TryParseBytes($"1{separator}5 KB", out var bytes);

        Assert.True(parsed);
        Assert.Equal(1536UL, bytes);
    }
}
