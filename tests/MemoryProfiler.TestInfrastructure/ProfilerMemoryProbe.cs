namespace MemoryProfiler.TestInfrastructure;

public static class ProfilerMemoryProbe
{
    public static long MeasureRetainedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public static bool IsGrowthWithin(
        long before,
        long after,
        long peak,
        long fixedAllowanceBytes)
    {
        if (before < 0 || after < 0 || peak < 0 || fixedAllowanceBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(before),
                "Memory measurements and allowances must be non-negative.");
        }

        var retainedGrowth = Math.Max(0, after - before);
        var peakGrowth = Math.Max(0, peak - before);
        var variableAllowance = peakGrowth / 10;
        var allowedGrowth = fixedAllowanceBytes > long.MaxValue - variableAllowance
            ? long.MaxValue
            : fixedAllowanceBytes + variableAllowance;
        return retainedGrowth <= allowedGrowth;
    }
}
