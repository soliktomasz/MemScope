namespace MemoryProfiler.Analysis.Values;

public sealed record ObjectValueReadOptions(
    int ArrayOffset = 0,
    int ArrayLimit = 500,
    int StringLimit = 4096)
{
    public void Validate()
    {
        if (ArrayOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArrayOffset),
                "Array offset must be non-negative.");
        }

        if (ArrayLimit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArrayLimit),
                "Array limit must be between 1 and 500.");
        }

        if (StringLimit is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StringLimit),
                "String limit must be between 1 and 1,048,576.");
        }
    }
}
