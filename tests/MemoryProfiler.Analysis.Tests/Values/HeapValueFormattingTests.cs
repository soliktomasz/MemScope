using System.Globalization;
using MemoryProfiler.Analysis.Values;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Values;

public sealed class HeapValueFormattingTests
{
    [Theory]
    [InlineData('\n', "'\\n'")]
    [InlineData('\t', "'\\t'")]
    [InlineData('\r', "'\\r'")]
    [InlineData('\0', "'\\0'")]
    [InlineData('\\', "'\\\\'")]
    [InlineData('\'', "'\\''")]
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
            Assert.Equal("00:15:00", HeapValueFormatting.Scalar(TimeSpan.FromMinutes(15)));
            Assert.Equal("01234567-89ab-cdef-0123-456789abcdef",
                HeapValueFormatting.Scalar(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
