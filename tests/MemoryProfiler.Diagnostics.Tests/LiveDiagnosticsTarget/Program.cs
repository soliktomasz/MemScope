// Cross-platform acceptance target for the live diagnostics session. Signals readiness on
// stdout, then allocates managed memory periodically until the process is terminated.
var allocations = new List<byte[]>();
var random = new Random(42);

Console.Out.WriteLine("READY");
Console.Out.Flush();

while (true)
{
    var chunk = new byte[64 * 1024];
    random.NextBytes(chunk);
    allocations.Add(chunk);
    if (allocations.Count > 1_024)
    {
        allocations.RemoveAt(0);
    }

    Thread.Sleep(50);
}
