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
}
