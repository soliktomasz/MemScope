namespace GcPressureTarget;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();

        while (true)
        {
            for (var index = 0; index < 10_000; index++)
            {
                GC.KeepAlive(new byte[1024]);
            }

            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            await Task.Yield();
        }
    }
}
