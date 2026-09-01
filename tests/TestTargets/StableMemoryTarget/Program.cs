using System.Globalization;

namespace StableMemoryTarget;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var count = args.Length == 0
            ? 100_000
            : int.Parse(args[0], NumberStyles.None, CultureInfo.InvariantCulture);
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Object count must be positive.");
        }

        StableHolder.Markers = Enumerable.Range(0, count)
            .Select(static _ => new StableMarker())
            .ToArray();

        Console.WriteLine("READY");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

public sealed class StableMarker;

internal static class StableHolder
{
    internal static StableMarker[] Markers { get; set; } = [];
}
