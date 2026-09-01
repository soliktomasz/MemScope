namespace LargeObjectHeapTarget;

public static class Program
{
    public static async Task Main()
    {
        LargeObjectHolder.Arrays = Enumerable.Range(0, 32)
            .Select(static _ => new byte[100_000])
            .ToArray();

        Console.WriteLine("READY");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

internal static class LargeObjectHolder
{
    internal static byte[][] Arrays { get; set; } = [];
}
