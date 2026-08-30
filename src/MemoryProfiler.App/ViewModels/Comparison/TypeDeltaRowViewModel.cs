using System.Globalization;
using MemoryProfiler.App.ViewModels.Overview;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.App.ViewModels.Comparison;

public sealed class TypeDeltaRowViewModel
{
    public TypeDeltaRowViewModel(TypeMemoryDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Delta = delta;
    }

    public TypeMemoryDelta Delta { get; }

    public string TypeName => Delta.TypeName;

    public long CountBefore => Delta.CountBefore;

    public long CountAfter => Delta.CountAfter;

    public long CountDelta => Delta.CountDelta;

    public long SizeDelta => Delta.SizeDelta;

    public long? RetainedDelta => Delta.RetainedSizeDelta;

    public bool IsNewType => Delta.CountBefore == 0 && Delta.CountAfter > 0;

    public bool IsDisappearedType => Delta.CountAfter == 0;

    public bool IsRetainedDeltaAvailable => Delta.RetainedSizeDelta is not null;

    public bool IsRetainedDeltaUnavailable => !IsRetainedDeltaAvailable;

    public string CountDeltaDisplay
    {
        get
        {
            var text = Delta.CountDelta.ToString("N0", CultureInfo.CurrentCulture);
            return Delta.CountDelta > 0 ? $"+{text}" : text;
        }
    }

    public string SizeDeltaDisplay => MetricFormatting.SignedBytes(Delta.SizeDelta);

    public string RetainedDeltaDisplay =>
        Delta.RetainedSizeDelta is null
            ? "N/A"
            : MetricFormatting.SignedBytes(Delta.RetainedSizeDelta.Value);
}
