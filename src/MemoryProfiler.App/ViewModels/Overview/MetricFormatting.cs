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

        var format = amount >= 100 || unitIndex == 0 ? "N0" : "N1";
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

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
