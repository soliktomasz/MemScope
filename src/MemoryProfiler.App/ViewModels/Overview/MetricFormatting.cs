using System.Globalization;

namespace MemoryProfiler.App.ViewModels.Overview;

internal static class MetricFormatting
{
    public static string Bytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var unitIndex = 0;
        while (amount >= 1024 && unitIndex < units.Length - 1)
        {
            amount /= 1024;
            unitIndex++;
        }

        var format = unitIndex switch
        {
            0 => "N0",
            1 or 2 => "#,0.#",
            _ => "#,0.##",
        };
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    public static string Count(long value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Address(ulong value) =>
        "0x" + value.ToString("X12", CultureInfo.InvariantCulture);

    public static string BytesPerSecond(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return "0 B/s";
        }

        return $"{Bytes((ulong)value)}/s";
    }

    // Signed byte formatting for deltas: "+118 MB", "-12 MB", "0 B". The sign
    // is dropped at zero; -(value + 1) + 1 is exact for every long, so the
    // magnitude never wraps at long.MinValue.
    public static string SignedBytes(long value)
    {
        var negative = value < 0;
        var magnitude = negative ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var sign = negative ? "-" : value > 0 ? "+" : string.Empty;
        return $"{sign}{Bytes(magnitude)}";
    }
}
