namespace MemoryProfiler.App.Services;

internal interface IDumpDestinationPicker
{
    Task<string?> PickAsync();
}
