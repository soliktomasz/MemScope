using System.Globalization;

namespace MemoryProfiler.Analysis.Values;

internal static class HeapValueFormatting
{
    public static string Character(char value)
    {
        var escaped = value switch
        {
            '\\' => "\\\\",
            '\'' => "\\'",
            '\0' => "\\0",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(value) => $"\\u{(int)value:X4}",
            _ => value.ToString(),
        };

        return $"'{escaped}'";
    }

    public static string Scalar<T>(T value) where T : IFormattable =>
        value switch
        {
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            _ => value.ToString(null, CultureInfo.InvariantCulture),
        };
}
