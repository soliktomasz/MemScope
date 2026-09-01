namespace HighAllocationTarget;

public static class Program
{
    public static async Task Main()
    {
        var workingSet = new Queue<byte[]>(capacity: 256);
        Console.WriteLine("READY");
        Console.Out.Flush();

        while (true)
        {
            for (var index = 0; index < 10_000; index++)
            {
                workingSet.Enqueue(new byte[4 * 1024]);
                if (workingSet.Count > 256)
                {
                    workingSet.Dequeue();
                }
            }

            await Task.Yield();
        }
    }
}
