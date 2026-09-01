namespace GrowingMemoryTarget;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();

        while (true)
        {
            LeakHolder.Leak.Add(new LeakPayload(new byte[1024 * 1024]));
            await Task.Delay(500);
        }
    }
}

public sealed record LeakPayload(byte[] Bytes);

internal static class LeakHolder
{
    internal static List<LeakPayload> Leak { get; } = [];
}
