namespace RootedObjectTarget;

public static class Program
{
    public static async Task Main()
    {
        RootHolder.Root = RootNode.CreateChain(64);

        Console.WriteLine("READY");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

public sealed class RootNode
{
    public RootNode? Next { get; private set; }

    public RootPayload? Payload { get; private set; }

    public static RootNode CreateChain(int length)
    {
        var root = new RootNode();
        var current = root;
        for (var index = 1; index < length; index++)
        {
            current.Next = new RootNode();
            current = current.Next;
        }

        current.Payload = new RootPayload();
        return root;
    }
}

public sealed class RootPayload;

internal static class RootHolder
{
    internal static RootNode? Root;
}
