// Cross-platform acceptance target for live diagnostics and dump analysis. Signals
// readiness on stdout, then allocates managed memory periodically until the process
// is terminated.
//
// Default mode (no arguments) keeps a bounded ring of 64 KiB chunks, matching the
// steady-state expectations of the session/dump/analysis acceptance tests.
//
// Leak mode (`--leak`) is used by the snapshot-comparison acceptance test: it
// pre-allocates a fixed baseline chunk set, signals READY, then waits on stdin for
// the line "LEAK" before growing an unbounded chunk list — so a before-dump captures
// the baseline and an after-dump captures the baseline plus the controlled leak.

const int ChunkSize = 64 * 1024;
const int BaselineChunkCount = 128;

var random = new Random(42);

if (args is ["--leak"])
{
    var baseline = new List<byte[]>(BaselineChunkCount);
    for (var i = 0; i < BaselineChunkCount; i++)
    {
        var chunk = new byte[ChunkSize];
        random.NextBytes(chunk);
        baseline.Add(chunk);
    }

    // Keep the graph behind an explicit static root. Stack-local reporting in
    // captured dumps varies by platform and JIT, while this target must expose
    // the same retention graph everywhere.
    var leaked = LiveAllocationHolder.Chunks = baseline;

    Console.Out.WriteLine("READY");
    Console.Out.Flush();

    if (Console.In.ReadLine() == "LEAK")
    {
        Console.Out.WriteLine("LEAKING");
        Console.Out.Flush();
        while (true)
        {
            var chunk = new byte[ChunkSize];
            random.NextBytes(chunk);
            leaked.Add(chunk);
            Thread.Sleep(50);
        }
    }

    while (true)
    {
        Thread.Sleep(100);
    }
}

var allocations = LiveAllocationHolder.Chunks = [];
Console.Out.WriteLine("READY");
Console.Out.Flush();

while (true)
{
    var chunk = new byte[ChunkSize];
    random.NextBytes(chunk);
    allocations.Add(chunk);
    if (allocations.Count > 1_024)
    {
        allocations.RemoveAt(0);
    }

    Thread.Sleep(50);
}

internal static class LiveAllocationHolder
{
    internal static List<byte[]>? Chunks;
}
