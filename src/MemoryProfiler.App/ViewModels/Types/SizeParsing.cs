using System.Globalization;

namespace MemoryProfiler.App.ViewModels.Types;

internal static class SizeParsing
{
    private static readonly (string Suffix, ulong Multiplier)[] Units =
    [
        ("tb", 1024UL * 1024 * 1024 * 1024),
        ("gb", 1024UL * 1024 * 1024),
        ("mb", 1024UL * 1024),
        ("kb", 1024UL),
        ("b", 1UL)
    ];

    public static bool TryParseBytes(string? input, out ulong bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        var unitIndex = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetter(text[i]))
            {
                unitIndex = i;
                break;
            }
        }

        var numberPart = text[..unitIndex].Trim();
        var unitPart = text[unitIndex..].Trim();
        if (!double.TryParse(
                numberPart,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var amount) ||
            !double.IsFinite(amount) ||
            amount < 0)
        {
            return false;
        }

        var multiplier = 1UL;
        if (unitPart.Length > 0)
        {
            var found = false;
            foreach (var (suffix, unitMultiplier) in Units)
            {
                if (unitPart.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    multiplier = unitMultiplier;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        // (double)ulong.MaxValue rounds to 2^64, and every double that rounds to
        // 2^64 is genuinely greater than ulong.MaxValue, so a >= comparison is an
        // exact boundary check. Using > here would let a value of exactly 2^64
        // through and wrap to 0 on the cast.
        var scaled = amount * multiplier;
        if (scaled >= (double)ulong.MaxValue)
        {
            return false;
        }

        bytes = (ulong)scaled;
        return true;
    }
}
