namespace LiveDiagnosticsTarget;

public enum CacheState
{
    Cold,
    Ready,
}

public sealed class CacheProbe
{
    public int Count = 42;
    public bool Enabled = true;
    public char Marker = 'M';
    public CacheState State = CacheState.Ready;
    public int? Limit = 12;
    public int? MissingLimit;
    public decimal Price = 1234.5m;
    public DateTime CreatedAt = new(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
    public TimeSpan Ttl = TimeSpan.FromMinutes(15);
    public Guid Identifier = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    public string Label = "memscope-value-sentinel";
    public string LongLabel = new('x', 5_000);
    public CacheChild Child = new() { Id = 7 };
    public CacheChild? Missing;
    public int[] Numbers = Enumerable.Range(0, 750).ToArray();
    public byte[][] Payload = Enumerable.Range(0, 32)
        .Select(_ => new byte[64 * 1024])
        .ToArray();
}

public sealed class CacheChild
{
    public int Id;
}
