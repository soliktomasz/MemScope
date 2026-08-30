namespace MemoryProfiler.App.Services;

internal interface IDumpFilePicker
{
    Task<string?> PickAsync();
}
